# Edge Services (WAF + edge TLS + caching) — staging

Scaleway Edge Services sits in front of the Kapsule gateway Load Balancer. The LB shell, its
flexible IP, and the **entire edge pipeline topology are Terraform-owned**
(`../environment/main.tf`: `scaleway_lb.gateway` + `scaleway_lb_ip.gateway`,
`scaleway_edge_services_pipeline` / `_dns_stage` / `_tls_stage` / `_cache_stage` /
`_waf_stage` / `_backend_stage` / `_head_stage`). Plan: **professional** (per environment
project). The Kapsule cloud-controller-manager creates the 443 frontend/backend on the adopted
LB via the Service annotations (see cluster-ops/README.md "Gateway LB adoption").

## Pipelines

Each pipeline is the chain `DNS custom stage (head) -> TLS (managed LE cert) -> cache -> WAF ->
backend (LB)`. There is one pipeline per public hostname:

| | staging-app | staging-back-office |
|---|---|---|
| custom FQDN (head) | app.staging.europeanplatform.net | back-office.staging.europeanplatform.net |
| CNAME target | `<pipeline-id>.svc.edge.scw.cloud` | `<pipeline-id>.svc.edge.scw.cloud` |
| origin frontend | LB 443 (CCM-created) | LB 8443 (`scaleway_lb_frontend.back_office`, TF-created) |
| WAF | enable (blocking), paranoia 1 | enable (blocking), paranoia 1 |
| cache | fallback TTL 3600s | fallback TTL 3600s |

Pipeline ids are stable (TF-managed); read them from `terraform state` or
`scw edge-services pipeline list`. The parked "auto" DNS stages reserve each pipeline's
`*.svc.edge.scw.cloud` endpoint and double as smoke-test hosts.

### Head stages — the one reference outside the API

The pipeline HEAD (entry point) is managed by `scaleway_edge_services_head_stage` with
`replace_triggered_by` the custom DNS stage, so any DNS-stage replacement re-attaches the head
automatically inside one apply. Caveat: the provider's head-stage Read is a no-op, so an
edge-side head drop NOT caused by Terraform is invisible to `terraform plan` — the
`edge-health` command (developer-cli) is the check that catches it:

```bash
SCW_SECRET_KEY=<key> dotnet run --project developer-cli -- edge-health
```

## Backend stages — live-derived API quirks (all found the hard way)

- The edge keeps only ONE active backend stage per LB frontend — hence the dedicated 8443
  frontend for the back-office pipeline (TF-owned; the LB is plain TCP passthrough, Envoy
  terminates TLS, so the extra frontend needs no certificate).
- A backend stage's `frontend_id` CANNOT be updated in place (API 400) — taint and recreate the
  stage instead.
- With `is_ssl` the `domain_name` is mandatory and becomes BOTH the origin SNI and the Host
  header Envoy routes by (no pass-through; see the parked wildcard notes in the decision log).
- Stage evaluation is PATCH-triggered: freshly created stages can sit `inactive` until an
  update re-evaluates them.

## WAF mode

Blocking (`mode = "enable"` on `scaleway_edge_services_waf_stage.gateway`) at paranoia 1:
requests matching the managed OWASP CRS ruleset are rejected at the edge before reaching the
cluster. Known false positives are excepted with `exclusion_rules` by CRS rule ID in Terraform —
never by flipping the mode out of band. Revisit together with CrowdSec (production).

## Known risk: direct requests to the gateway Load Balancer bypass the edge

Edge Services connects to the gateway Load Balancer's public flexible IP, and nothing restricts
that IP to Edge Services: anyone who learns it can connect straight to Envoy
(`curl --resolve app.<domain>:443:<lb-ip> ...`) and skip the WAF, edge TLS, and caching. Edge
Services offers no way to authenticate itself to the origin today — no client certificate
(authenticated origin pulls), no custom origin header — and publishes no egress IP ranges, so
neither an origin mTLS check nor an LB ACL allowlist can be built on supported features. The
backend is TCP passthrough, so the LB could only filter on source IP anyway.

What the bypass does NOT skip: Envoy still terminates TLS, every endpoint enforces its own
authentication and authorization, `/internal-api/*` stays blocked at the app-gateway, and the
staff identity is never taken from request headers. The loss is the WAF layer of defence in
depth, not an authentication bypass, and the LB IP is not published in DNS.

Status: accepted for staging. The question is open with Scaleway (is `GET
/edge-services/v1beta1/nodes` a stable egress list; is Edge → private LB over VPC supported or
coming — the SDK already carries `private_network_id` on the backend; are authenticated origin
pulls on the roadmap). Decide before production: a private-network origin if Scaleway ships it,
otherwise an LB source-IP allowlist (`loadBalancerSourceRanges` on the gateway Service plus a
`scaleway_lb_acl` on the Terraform-owned back-office frontend) with a scheduled drift check
against the edge node list.

## DNS (user action)

```
app.staging.europeanplatform.net.         CNAME <app-pipeline-id>.svc.edge.scw.cloud.
back-office.staging.europeanplatform.net. CNAME <back-office-pipeline-id>.svc.edge.scw.cloud.
```

See the [DNS guide](../docs/dns-guide.md) for the full record set (web + email) and
verification steps.

## Maintenance page (cluster-down fallback)

Bucket `<prefix>-<environment>-maintenance` (bucket + website config + index.html object) is
Terraform-managed in `../environment/main.tf` with the page at
`../environment/maintenance/index.html`; it serves at the `maintenance_website_endpoint` output
(`<bucket>.s3-website.<region>.scw.cloud`).

The LB serves it as `failover_host` when ALL LB backends are down (node health-check failure —
full cluster outage, not application-level errors). Re-run after cluster churn (node
replacement, LB adoption changes):

```bash
SCW_SECRET_KEY=<scw profile key> ./set-gateway-lb-failover.sh
```

Limitation: the failover host only applies to the HTTP backend behind the LB's :80 frontend. The
edge pipelines reach the cluster through the TCP passthrough 443/8443 frontends, whose Envoy TCP
backend cannot carry a failover host, so edge visitors see the edge's origin error during a
cluster outage — not this page.

The script (idempotent, derives the region and failover host from the environment module's
outputs and all IDs live from the Service's `scw-loadbalancer-id` annotation) annotates the Service for HTTP backends (the CCM creates TCP by default and the API
rejects failover_host on TCP), then merges the failover host into a full backend PUT (the API
validates the request body, so forward_protocol must be present). Not in Terraform: the backend
is CCM-owned (pool = node IPs, ID derived from the Service UID) and a second owner would fight
the controller and reference dead IDs after recreation.
