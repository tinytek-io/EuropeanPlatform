# Disabled workflows

These GitHub Actions workflows are kept up to date but disabled: GitHub only executes files under
`.github/workflows/`. The same checks run locally through the developer CLI (`build`, `format`, `lint`, `test`),
which is the source of truth.

| Workflow | Runs on | What it does |
|---|---|---|
| `code-style.yml` | Pull requests and `main` touching `application/` or `developer-cli/` | Backend and frontend build, lint, format check, and the backend tests (scoped to one self-contained system when only that one changed); SonarCloud when configured |
| `developer-cli.yml` | Changes to `developer-cli/` or `Scaleway.Sdk` | Builds, tests, lints and format-checks the developer CLI |
| `pull-request-conventions.yml` | Pull requests | Title format, no merge commits, single-line commit messages |
| `environment-checks.yml` | Daily and on demand | The read-only `security-check`, `cost-estimate` and `edge-health` against staging, catching what changes between deploys. Needs a read-only Scaleway key in the `staging` environment's secrets (see the workflow) |

Re-enable a workflow by moving it to `.github/workflows/`.
