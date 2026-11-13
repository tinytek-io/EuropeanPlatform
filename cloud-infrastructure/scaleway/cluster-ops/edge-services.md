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

Pipeline ids are stable (TF-managed); read them from `terraform state`, or run `edge-health`, which
lists each pipeline with its stage chain. The parked "auto" DNS stages reserve each pipeline's
`*.svc.edge.scw.cloud` endpoint and double as smoke-test hosts.

### Head stages — the one reference outside the API

The pipeline HEAD (entry point) is managed by `scaleway_edge_services_head_stage` with
`replace_triggered_by` the custom DNS stage, so any DNS-stage replacement re-attaches the head
automatically inside one apply. Caveat: the provider's head-stage Read is a no-op, so an
edge-side head drop NOT caused by Terraform is invisible to `terraform plan` — the
`edge-health` command (developer-cli) is the check that catches it:

```bash
dotnet run --project developer-cli -- edge-health -e staging
```

## Backend stages — live-derived API quirks (all found the hard way)

- The edge keeps only ONE active backend stage per LB frontend — hence the dedicated 8443
  frontend for the back-office pipeline (TF-owned; the LB is plain TCP passthrough, Envoy
  terminates TLS, so the extra frontend needs no certificate).
- A backend stage's `frontend_id` CANNOT be updated in place (API 400) — taint and recreate the
  stage instead.
- With `is_ssl`, `domain_name` becomes BOTH the origin SNI and the Host header Envoy routes by.
  Left empty, the stage is still accepted: the visitor's Host header passes through, but the
  edge sends NO SNI (measured with an echo origin, 2026-11-08). Envoy's listeners match by SNI,
  so the pipelines set `domain_name` to their own hostname.
- The edge does not verify the origin's certificate (the echo origin's self-signed certificate
  was accepted). The edge-to-LB hop is encrypted but not authenticated, so Envoy's Let's Encrypt
  certificate adds no security today; it stays so the origin keeps working if Scaleway starts
  verifying.
- Stage evaluation is PATCH-triggered: freshly created stages can sit `inactive` until an
  update re-evaluates them.

## Why one pipeline per hostname, not one wildcard pipeline

A single pipeline with a wildcard DNS stage (`*.staging...`) would remove the per-hostname CNAME,
pipeline, and LB frontend. It is parked until per-PR preview environments need hostnames without
setup, because it trades away more security than it buys:

- A wildcard certificate cannot use the edge's managed Let's Encrypt (wildcards are DNS-01 only).
  cert-manager would issue it with a DNS-write API key inside the cluster, and a sync job would
  copy the private key into Secret Manager. Today Scaleway issues and holds every edge key, and
  no automated credential can write DNS.
- One key would cover every hostname of the environment, and one cache would serve all of them
  (its key must then include the Host).
- With `domain_name` empty the visitor chooses the Host, and the edge sends no SNI, so Envoy
  would need a listener without hostnames that routes on Host alone.
- Every name under the wildcard resolves, and a wildcard left after a pipeline is removed points
  at nothing we own.

If preview environments come, give them their own environment and project with the wildcard,
so the DNS-write key can only touch throwaway environments.

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
  so no id is captured for the ACLs. If the CCM recreates one, the next plan replaces its ACL; the
  deploy applies pending changes before the rollout and again after it, so a frontend the rollout
  recreated is closed in the same run (after confirmation, or `--auto-approve`).
  (The app pipeline's backend stage still targets the captured `gateway_lb_frontend_id`, §5b of the
  cluster-ops README.)
- A frontend on any other port is not covered; the security check below reports it.

## Security check (developer CLI)

`dotnet run --project developer-cli -- security-check -e staging` verifies this with the
environment's operator key, read-only, and prints one line per check, naming the offending resource
on a failure. The deploy runs it as its last step, after the pass that re-applies the edge-only
ACLs, and fails on a finding. Rules: `developer-cli/Commands/SecurityCheck/SecurityCheckEvaluator.cs`.

| Check | Holds when |
|---|---|
| `gateway-frontends-edge-only` | Every frontend on the gateway LB has, as its first ACL, a deny unless the source is Edge Services (no extra IP list). |
| `only-gateway-load-balancer-is-public` | No other Load Balancer in the project's region has a public IP. |
| `edge-waf-enforced` | Every pipeline has a WAF stage in `enable` mode with no exclusion rules, and no TLS or cache stage targets the backend directly. |

An unreadable inventory (API outage, missing permission) fails the check too, never passes it. The
checks read the configuration through the API, so they cannot prove the LB refuses internet
traffic; that needs the external probe below, run from outside Scaleway. Changes made in the
console between deploys are caught only by running the command; a scheduled CI run will cover that
once CI is restored.

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
- The edge does not verify the origin certificate (see the backend-stage notes above), so
  nothing proves to the edge that it reached our LB rather than something on the path to it.
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

Nothing serves it yet. The Load Balancer's `failover_host` cannot be the fallback: it only applies
to an HTTP backend, and the edge pipelines reach the cluster through the TCP passthrough 443/8443
frontends, whose Envoy TCP backend cannot carry one (Envoy terminates TLS). The plain-HTTP :80
frontend could carry it, but the origin lockdown closes :80 to everything but Edge Services, which
never uses it. So during a cluster outage edge visitors see the edge's own origin error. A
maintenance page they actually see needs the edge itself to serve it when the origin is down.
