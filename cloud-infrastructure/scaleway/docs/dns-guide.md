# DNS and email setup for staging (europeanplatform.net)

This guide covers the records you need to create **once**, after the first `aspire deploy`. The
records are created in the DNS console of wherever the `europeanplatform.net` zone is hosted,
unless you delegate the staging sub-zone to Scaleway DNS (see the last section). The same record
shapes apply on any other DNS provider (Hetzner, Simply.com, Cloudflare, ...).

## 0. Let the tooling check it for you

The `dns-check` command derives every record below from the Terraform state (never mutating it),
resolves each one against live DNS, and prints a table with ok/missing/mismatch per record — it
also runs automatically at the end of every `pp deploy`. Fix whatever it reports here in the DNS
console.

## 1. Web traffic: point the staging hostnames at the Edge Services pipelines

Public traffic never touches the cluster's Load Balancer directly: each public hostname is a
Scaleway Edge Services pipeline (WAF + edge TLS + caching) in front of the gateway LB. The DNS
records are therefore **CNAMEs to the pipeline endpoints**, not A records to the LB IP.

Find the pipeline endpoints for your environment (they are stable — the pipelines are managed by
Terraform and only change if the pipeline resources are replaced):

```bash
cd cloud-infrastructure/scaleway/environment
terraform state list | grep edge_services_pipeline          # pipeline ids
scw edge-services pipeline list -o json                    # id + endpoint per pipeline
```

Then create one CNAME per public hostname (example values for the staging environment):

| Type  | Name                   | Value                                                          |
|-------|------------------------|----------------------------------------------------------------|
| CNAME | `app.staging`          | `<app-pipeline-id>.svc.edge.scw.cloud.`                        |
| CNAME | `back-office.staging`  | `<back-office-pipeline-id>.svc.edge.scw.cloud.`                |

The edge issues and serves the TLS certificates for these hostnames automatically (managed
Let's Encrypt per pipeline). The cluster's cert-manager separately issues the certificate that
Envoy serves behind the edge — its HTTP-01 challenges also travel through these records, so the
edge must be serving before the in-cluster certificate can renew (it takes a few minutes after
the CNAMEs resolve).

### Where the A records went

An earlier revision of this guide pointed the hostnames at the gateway LB's public IP. Do not
do that anymore: the LB is intentionally kept behind the Edge Services pipelines, and the
cert-manager issuance path described above depends on the hostnames resolving through the edge.

## 2. Email: verify the Transactional Email sending domain

Transactional email is sent through Scaleway TEM with the environment-specific sending domain
`staging.europeanplatform.net` (isolated from the apex domain's reputation; the apex stays free for the
production environment). After `terraform apply` registers the domain
(`scaleway_tem_domain`), get the expected records:

```bash
scw tem domain list -o json                          # find the staging domain id
scw tem domain get domain-id=<id> -o json            # dkim_config contains the DKIM public key
```

Create three records in the DNS console for the `staging` zone:

| Type | Name                     | Value                                            |
|------|--------------------------|--------------------------------------------------|
| TXT  | `staging` (zone apex)    | `v=spf1 include:_spf.tem.scaleway.com ~all`     |
| TXT  | `tem._domainkey.staging` | the `dkim_config` value from the command above  |
| TXT  | `_dmarc.staging`         | `v=DMARC1; p=none; rua=mailto:ops@europeanplatform.net` |

Then ask Scaleway to re-check the records:

```bash
scw tem domain check domain-id=<id>
```

The domain shows all records verified once DNS propagates. Subscription to the `essential`
plan (300 emails/month included) stays a one-time console action — the plan has no Terraform
resource.

## 3. Verify

```bash
# Full read-only health check of the edge pipelines and both public endpoints:
SCW_SECRET_KEY=<key> dotnet run --project developer-cli -- edge-health

# Record-by-record:
dig +short app.staging.europeanplatform.net         # should chain to the pipeline endpoint IPs
dig +short back-office.staging.europeanplatform.net
curl -sI https://app.staging.europeanplatform.net/         # expect 200
curl -sI https://back-office.staging.europeanplatform.net/ # expect 401 (auth gate) while MockEasyAuth is active
```

## 4. Optional: delegate the staging sub-zone to Scaleway DNS

Instead of maintaining records on an external provider, you can delegate
`staging.europeanplatform.net` to Scaleway DNS (`scw-dns` manages the zone, TEM/Edge record
changes become Scaleway-side). That is a trade-off between provider consolidation and keeping
all DNS for the apex in one console — until the delegation, the records live with the zone's
DNS provider.
