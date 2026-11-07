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

## Origin lockdown: only Edge Services may reach the gateway Load Balancer

Edge Services connects to the gateway Load Balancer's public flexible IP. Without a restriction,
anyone who learns that IP could connect straight to Envoy (`curl --resolve app.<domain>:443:<lb-ip>
...`) and skip the WAF, edge TLS, and caching. Envoy routes by SNI, not by frontend, so one open
frontend would serve every hostname.

`scaleway_lb_acl.edge_only` (`../environment/main.tf`) closes that: every frontend on the LB (the
CCM's 443 frontend for app and its plain-HTTP 80 frontend, the Terraform-owned 8443/8444 frontends
for back-office and auth) carries an ACL at index 0 that denies every source except Scaleway Edge Services
(`ips_edge_services`, inverted). The backend is TCP passthrough, so source IP is all an ACL can
match. LB health checks run on the backend side and are not affected.

Rules that keep it closed:

- **Never set `loadBalancerSourceRanges` on the gateway Service.** The CCM only replaces the ACLs
  it generates itself (named `<frontend-id>-lb-source-range-*`), which is why the Terraform ACL on
  its 443 frontend survives. With source ranges set, the CCM replaces ALL ACLs on the frontend.
- The CCM's frontends are found by port (443, 80) through the `scaleway_lb_frontends` data source,
  so no id is captured for the ACLs. If the CCM recreates one, the next plan replaces its ACL and
  the deploy's convergence check stops until that is applied; until then the new frontend is open.
  (The app pipeline's backend stage still targets the captured `gateway_lb_frontend_id`, §5b of the
  cluster-ops README.)
- A frontend on any other port is not covered; the security check below reports it.

## Continuous security check (back-office worker)

The back-office worker (`SecurityCheckWorker`) re-verifies this every 15 minutes with a read-only
key (`LoadBalancersReadOnly` + `EdgeServicesReadOnly`) and logs one line per check: information on
pass, error on fail, naming the offending resource. Rules: `SecurityCheckEvaluator`.

| Check | Holds when |
|---|---|
| `gateway-frontends-edge-only` | Every frontend on the gateway LB has, as its first ACL, a deny unless the source is Edge Services (no extra IP list). |
| `only-gateway-load-balancer-is-public` | No other Load Balancer in the project's region has a public IP. |
| `edge-waf-enforced` | Every pipeline has a WAF stage in `enable` mode with no exclusion rules, and no TLS or cache stage targets the backend directly. |

An unreadable inventory (API outage, missing permission) is logged as an error too, never as a
pass. Alerting on these errors comes with the operations work. The checks run inside Scaleway, so
they cannot prove the LB refuses internet traffic; that needs the external probe in the
verification step above, run from outside Scaleway.

Verify after apply (from outside Scaleway):

```bash
LB_IP=<the gateway LB flexible IP>
curl -sk --max-time 5 --resolve "app.<domain>:443:$LB_IP" https://app.<domain>/ -o /dev/null -w '%{http_code}\n' || echo "refused (correct)"
curl -s -o /dev/null -w '%{http_code}\n' https://app.<domain>/      # via the edge: still 200
```

Residual risks (open with Scaleway):

- The match is "any Edge Services IP", shared by every Scaleway customer. Whether another
  organization's pipeline can target our LB (and so pass the ACL with its own, weaker WAF) is
  unverified. Edge Services offers no origin authentication (no client certificate, no custom
  origin header) to close that.
- The end state is a private LB reached by Edge over a VPC endpoint. The SDK already carries the
  VPC endpoint API, but the docs and the Terraform provider do not support private LB backends yet.

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
cluster outage — not this page. And since the origin lockdown also closes :80 to everything but
Edge Services (which never uses it), the page is unreachable today; a maintenance page for edge
visitors needs the edge itself to serve it.

The script (idempotent, derives the region and failover host from the environment module's
outputs and all IDs live from the Service's `scw-loadbalancer-id` annotation) annotates the Service for HTTP backends (the CCM creates TCP by default and the API
rejects failover_host on TCP), then merges the failover host into a full backend PUT (the API
validates the request body, so forward_protocol must be present). Not in Terraform: the backend
is CCM-owned (pool = node IPs, ID derived from the Service UID) and a second owner would fight
the controller and reference dead IDs after recreation.
