---
name: adopt-platformplatform-pr
description: Adopt the changes from an upstream PlatformPlatform (PP) pull request into EuropeanPlatform (EP), faithfully and with adaptation. Reads the PP PR summary and commits from the upstream repo with gh, applies it recipe-first (converting Azure-native pieces to the Scaleway stack, honoring EP's separate back-office SCS and EU brand), verifies with the developer CLI, and records the result in the adoption log. Invoke as `/adopt-platformplatform-pr <PP-PR-number-or-url>` to adopt a specific change, or `/adopt-platformplatform-pr next` to walk the backlog — proposing one candidate at a time and asking whether to adopt or skip it before any work starts.
allowed-tools: *
---

# Adopt a PlatformPlatform Pull Request

Adopt one upstream PlatformPlatform (PP) pull request into EuropeanPlatform (EP). The argument is a PP **PR number** (e.g. `913`), its URL, or the literal `next`. Faithfulness to PP plus adaptation to EP's divergences is the whole job: EP runs on Scaleway (not Azure), keeps the back-office as its own self-contained system, and carries its own EU brand. A clean cherry-pick is the rare exception; **recipe-based adoption is the norm.**

## How EP diverges from upstream — the adaptation checklist

When a PR touches any of these areas, convert it to EP's way instead of copying upstream's:

| Area | Upstream (PP) | EuropeanPlatform (EP) |
|---|---|---|
| Infrastructure | Azure Bicep + GitHub Actions | Terraform (`cloud-infrastructure/scaleway`) + the Aspire publish/deploy pipeline via the developer CLI `deploy` command |
| App hosting | Azure Container Apps | Scaleway Kapsule; wiring lives in `application/AppHost/Program.cs` publish mode |
| Blob storage | `Azure.Storage.Blobs` | `S3BlobStorageClient` (Scaleway Object Storage; SeaweedFS in local dev) |
| Email | Azure Communication Services | SMTP (Scaleway Transactional Email) |
| Telemetry | Application Insights | OpenTelemetry → Scaleway Cockpit; frontend uses its own tracker |
| Back-office | Hosted inside the account SCS | Its own SCS: BFF over `/internal-api/back-office/*` with the shared delegation contract (see `application/README.md` § Back-office) |
| Staff auth | Entra Easy Auth | OIDC provider with the mock identity selector as the dev/staging fallback (`BackOffice:Oidc`) |
| CI | GitHub Actions workflows | Workflows disabled (`.github/workflows-disabled/`); checks run through the developer CLI (`build`, `format`, `lint`, `test`) |
| Brand | PlatformPlatform, orange logos | EuropeanPlatform, EU-flag blue logos, EU-hosted tagline (`application/platform-settings.jsonc`) |

Also: commit messages are one descriptive imperative line; the `PlatformPlatform PR <N>` subject convention is reserved for adoptions and is exempt from the single-line rule only in the disabled upstream workflow regex.

## Reference setup (verify, don't assume)

- **Upstream is `platformplatform/PlatformPlatform`.** PR metadata, bodies, diffs, and current file contents are read from that repo with `gh`, so this skill needs no local clone. Never point it at a personal fork: forks carry no PRs of their own, so PR numbers resolve upstream only.
- **No local checkout required.** Use `gh pr view N --repo platformplatform/PlatformPlatform --json ...` and `gh api repos/platformplatform/PlatformPlatform/contents/<path>?ref=main`. If a local PP clone exists, `PLATFORMPLATFORM_DIR` may point at it, but never assume it exists.
- **The adoption log**: `.claude/platformplatform-adoption-log.md` — the single checked-in ledger. It carries the status vocabulary (`adopted` · `partial` · `skipped` · `not-applicable` · `planned` · `unknown` · `pre-tracking`), every upstream change EP tracks, and the decisions in detail. It drives `next` and prerequisite detection.
- Read EP's rules before editing — `AGENTS.md` plus the path-loaded rules in `.claude/rules/`. Read every rule whose paths match a file you are about to touch.
- **Verification** runs through the developer CLI, sequentially, never in parallel: `build` first (green), then `format`, then `build`, then `lint`, then `test` — each with the CLI (`dotnet run --project developer-cli -- <command>`). e2e specs run when the change is observable in the browser and a dev stack is up.

**Definition of done:** the change is adopted faithfully and adapted to EP; build/format/lint/test are green; runtime behaviour is verified where observable (deploy to staging via the CLI `deploy` command when the change is deployed-surface); the adoption log is updated; the work is committed with a one-line imperative message (never commit, amend, or push without explicit user instruction).

## STEP 1: Resolve the PP PR (or `next`)

1. If the argument is a number or `.../pull/N` URL, take that as PP PR `N`.
2. If the argument is `next`, work the queue **one candidate at a time, asking before each**:
   - Read the adoption log: the queue is every row not in a terminal state (`planned`, `partial`, `unknown`), sorted by **merge date** — never by PR number; the two disagree, and ordering by number silently reverses real dependencies.
   - Never propose anything marked `skipped` — those are settled decisions, not backlog.
   - An `unknown` row is **triage work, not adoption work**: establish what actually happened first (check EP's git history for a `PlatformPlatform PR <N>` subject, then verify the change's distinctive markers directly in EP), write the resolved status back to the ledger, and report before proposing anything.
   - Present each candidate with enough context to decide without opening the PR — title, merge date, what it touches, why it is next, and what the log already records about adapting it — then offer **Adopt / Skip / Not now / Stop**. For Skip, ask for the reason and record the user's own words; never invent one.
   - If the queue is empty, offer to re-survey upstream (`gh pr list --repo platformplatform/PlatformPlatform --state merged --json number,title,mergedAt`) for PRs merged since the ledger was last extended, and append candidates in merge order. Adding rows is not adopting them; each still gets proposed and confirmed.
3. Anything already `adopted` is done — say so and stop, or advance to the next candidate when invoked with `next`.

## STEP 2: Read the summary and analyse every commit

1. Pull the PR metadata with `gh pr view N --repo platformplatform/PlatformPlatform --json title,body,commits,files,baseRefName,mergedAt,url`. For large PRs, get the complete file list with `gh api repos/platformplatform/PlatformPlatform/pulls/N/files --paginate --jq '.[].filename'` and **account for every file** — classify each as *ported* or *not-applicable, with a reason*. "Take only what applies" means skip the genuinely irrelevant, never silently under-port.
2. Read the summary and any embedded migration recipe; note the stated preconditions.
3. Read the diff (save large output to the scratchpad, don't flood context) and extract each commit's **recipe**: what changed, why, and what it becomes in EP.
4. **Adopt PP's current `main`, in its corrected state — not the merge-time diff.** Reproduce files as `main` has them now, so EP gets corrected code instead of inheriting a bug upstream has already fixed. When `main` has superseded the PR's design, say so before porting: present adopting the successor instead, or the original as an interim step with the successor queued behind it.
5. **A feature is often split across several PRs — adopt the whole set as one unit.** Search for follow-ups (`gh pr list --search "<feature keywords>"`, PRs merged shortly after touching the same files, "part 2"/"follow-up" in the body). Record them together in the log.

## STEP 3: Preconditions and prerequisites

Check the log for a dependency chain: a row whose prerequisite is still unadopted is not ready regardless of its date. If the change depends on something EP deliberately diverged on (the table above), decide adaptation openly: port the intent, not the Azure implementation.

## STEP 4: Apply recipe-first

1. Convert Azure-native pieces to the Scaleway stack per the table above; when in doubt about infra, prefer `AppHost/Program.cs` publish-mode wiring over new Terraform resources, and never hand-create cloud resources — infrastructure changes go through Terraform, deployment through the Aspire pipeline.
2. Honor EP's back-office boundary: user-facing account changes port to `account`; anything staff-facing ports to `back-office` through the internal API + delegation contract.
3. Keep EP's brand: product names in new UI strings come from `Settings.Current.Branding.ProductName` (never hardcode a product name); upstream literals that say "PlatformPlatform" become the EP brand or an explicit upstream attribution, never a mixed claim.

## STEP 5: Verify and record

1. Run the developer-CLI chain sequentially (build → format → build → lint → test); all green or fix before proceeding. e2e where observable.
2. Update the adoption log row: status, every PR number in the set, what was adapted and why, evidence for `unknown` resolutions.
3. Commit with a one-line imperative message — only on explicit user instruction.
