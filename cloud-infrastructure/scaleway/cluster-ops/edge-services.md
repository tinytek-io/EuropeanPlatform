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
| WAF | log_only, paranoia 1 | log_only, paranoia 1 |
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

`log_only` at paranoia 1 while staging is young: evaluate what the WAF flags, then flip to
`enable` (`scw edge-services waf-stage update`). Revisit together with CrowdSec (production).

## DNS (user action)

```
app.staging.europeanplatform.net.         CNAME <app-pipeline-id>.svc.edge.scw.cloud.
back-office.staging.europeanplatform.net. CNAME <back-office-pipeline-id>.svc.edge.scw.cloud.
```

See the [DNS guide](../docs/dns-guide.md) for the full record set (web + email) and
verification steps.

## Maintenance page (cluster-down fallback)

Bucket `pp-stage-maintenance` (bucket + website config + index.html object) is Terraform-managed
in `../environment/main.tf` with the page at `../environment/maintenance/index.html`; it serves at
https://pp-stage-maintenance.s3-website.fr-par.scw.cloud/.

The LB serves it as `failover_host` when ALL LB backends are down (node health-check failure —
full cluster outage, not application-level errors). Re-run after cluster churn (node
replacement, LB adoption changes):

```bash
SCW_SECRET_KEY=<scw profile key> ./set-gateway-lb-failover.sh
```

The script (idempotent, derives all IDs live from the Service's `scw-loadbalancer-id`
annotation) annotates the Service for HTTP backends (the CCM creates TCP by default and the API
rejects failover_host on TCP), then merges the failover host into a full backend PUT (the API
validates the request body, so forward_protocol must be present). Not in Terraform: the backend
is CCM-owned (pool = node IPs, ID derived from the Service UID) and a second owner would fight
the controller and reference dead IDs after recreation.
