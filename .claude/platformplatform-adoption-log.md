# PlatformPlatform Adoption Log

The single record of how EuropeanPlatform (EP) tracks upstream PlatformPlatform (PP, `platformplatform/PlatformPlatform`). It answers two questions in one place: **what did EP do about a given upstream change**, and **what is still worth adopting**. Maintained by the [adopt-platformplatform-pr](/.claude/skills/adopt-platformplatform-pr/SKILL.md) skill.

## How EP relates to upstream

EP forked from PP at upstream commit `269de1c18` ("Consolidate module federation on a single router with federated route contribution", PR #913). Everything at or before that commit is inherited — it is the shared baseline, not adoption backlog. From the fork point, EP immediately re-platformed from Azure to Scaleway; the Scaleway migration era below is EP-owned work, recorded as narrative rather than per-PR rows (status `pre-tracking`).

Everything PP merges **after** `269de1c18` is adoption backlog. Work it chronologically by **merge date** with the `adopt-platformplatform-pr` skill (`next`).

## EP's standing divergences (adapt, never overwrite)

| Area | Upstream (PP) | EuropeanPlatform (EP) |
|---|---|---|
| Infrastructure | Azure Bicep + GitHub Actions | Terraform (`cloud-infrastructure/scaleway`) + the Aspire publish/deploy pipeline |
| App hosting | Azure Container Apps | Scaleway Kapsule (Cilium Gateway, edge WAF/TLS/caching) |
| Blob storage | Azure Blob Storage | Scaleway Object Storage via the S3 client (SeaweedFS in local dev) |
| Email | Azure Communication Services | Scaleway Transactional Email over SMTP |
| Telemetry | Application Insights | OpenTelemetry → Scaleway Cockpit |
| Back-office | Hosted inside the account SCS | Its own SCS — BFF over `/internal-api/back-office/*` with the shared delegation contract |
| Staff auth | Entra Easy Auth | OIDC provider (`BackOffice:Oidc`), mock identity selector as the dev/staging fallback |
| CI | GitHub Actions | Workflows disabled (`.github/workflows-disabled/`); developer CLI is the gate |
| Brand | PlatformPlatform, orange | EuropeanPlatform, EU-flag blue, EU-hosted tagline |

## Status vocabulary

`adopted` · `partial` · `skipped` · `not-applicable` · `planned` · `unknown` · `pre-tracking`

- `adopted` — the whole set landed, adapted, verified.
- `partial` — some of the set landed; the row names the outstanding part and stays a candidate.
- `skipped` — a settled decision with the user's own words as the reason; never re-proposed.
- `not-applicable` — upstream change with no EP counterpart (e.g. ACA-specific infra).
- `planned` — decided to adopt, not yet done.
- `unknown` — triage needed: establish what actually happened before proposing anything.
- `pre-tracking` — inherited or EP-owned history; context, never a candidate.

## Era: the Scaleway migration (EP-owned, pre-tracking)

The migration is EP's baseline divergence, in rough order: bootstrap + environment Terraform (VPC, Kapsule, RDB on the private network, Object Storage, Secret Manager with per-workload IAM identities, Edge Services, Transactional Email); the Aspire publish/deploy pipeline replacing Bicep + GitHub Actions; application clients re-platformed (S3 blob storage, SMTP email, OpenTelemetry → Cockpit including the frontend tracker replacing Application Insights); back-office extracted into its own self-contained system (BFF over the internal API with the platform-signed delegation contract, staff OIDC with the Authelia dev harness and the mock selector fallback); local development on SeaweedFS; the EU-flag blue brand and the EU-hosted, GDPR-native tagline; legal templates rewritten for the EU-only, Scaleway reality.

## Adoption backlog

| PP PR | Title | Merged | Status | Notes |
|---|---|---|---|---|
| — | — | — | — | No rows yet: the fork point is #913 (`269de1c18`). Run `/adopt-platformplatform-pr next` to survey upstream for PRs merged after the fork point and append candidates here in merge order — each is proposed and confirmed before any adoption work starts. |
