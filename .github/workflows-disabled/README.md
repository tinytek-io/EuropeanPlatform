# Disabled workflows

These GitHub Actions workflows are kept in their original format but disabled: GitHub only
executes files under `.github/workflows/`. They ran the code-style checks (format/lint/build)
and pull-request conventions against GitHub-hosted runners. The equivalent checks run locally
through the developer CLI (`build`, `format`, `lint`, `test`), which is the source of truth.

Re-enable by moving the files back to `.github/workflows/` — or port them to the CI system that
replaces GitHub Actions, updating the runner labels and any GitHub-specific context.
