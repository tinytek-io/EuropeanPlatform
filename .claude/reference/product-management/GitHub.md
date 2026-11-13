# GitHub

## Configuration

```
DEFAULT_PROJECT=null   # GitHub Projects (v2) number of the board, under the tinytek-io organization
DEFAULT_REPO=tinytek-io/EuropeanPlatform   # owner/repo issues are created in (also GH_REPO in .claude/settings.json)
```

## Never Mention Users — Escape Every `@`

In any content written to GitHub — issue and pull request titles, bodies and **comments** — a bare `@` followed by a name notifies a real person. Wrap every `@`-containing token in backticks so GitHub renders it as inline code instead of a mention: `` `@repo/ui` ``, `` `@lingui/core` ``, `` `@me` ``. This includes npm package scopes and test tags that start with `@`. CLI flag *values* such as `gh issue edit <n> --add-assignee "@me"` are fine; they are not written content.

## Terminology Mapping

| Generic Term | GitHub |
|---|---|
| `[Feature]` | Parent issue, tracked on the Projects v2 board |
| `[Task]` | Sub-issue of the parent issue |
| `[Subtask]` | Checklist bullet (`- [ ]`) in the task issue body |

## Status Mapping

Status is the board's **Status** single-select field, never a label and not open/closed alone.

**For [Feature]:**
| Generic Status | GitHub |
|---|---|
| `[Planned]` | Todo |
| `[Active]` | In progress |
| `[Resolved]` | Done |

**For [Task]:**
| Generic Status | GitHub |
|---|---|
| `[Planned]` | Todo |
| `[Active]` | In progress |
| `[Review]` | In review |
| `[Completed]` | Done |

The Status field has four options: `Todo`, `In progress`, `In review`, `Done` (lowercase `p` and `r`). A [task] moves to `In review` when its pull request opens and stays there until the pull request merges.

A [task] reaches `[Completed]` when **its pull request merges**, not by a manual status change. A pull request whose body links it with `Closes #<taskId>` closes the [task] on merge, and the board's `Item closed` workflow sets its Status to `Done` in the same moment. So once a pull request covers a [task], don't also set it to Done or close it by hand. Setting Status to Done directly still applies to the rare [task] that finishes without a pull request.

A [task] staying `In review` until the merge is the normal path, not broken automation. `Closes #<taskId>` registers on the pull request when it opens (`closingIssuesReferences` confirms it), but GitHub's closing keywords fire on merge into the default branch. That is core repository behaviour, so no Projects setting changes it.

**Assign on start.** When you move a [task] to `[Active]` (In progress), also **assign it to the current developer** and **set the Iteration field to the current iteration** (skip this while the board has no iterations configured):

```bash
gh issue edit <taskNumber> --add-assignee "@me"
```

Status, assignee and iteration are three separate writes: Status and Iteration through the `updateProjectV2ItemFieldValue` mutation (see Operations), the assignee through `gh issue edit`. The **current iteration** is the one whose date range contains today. If you stop working on a [task] without finishing it, move it back to Todo and unassign yourself (`--remove-assignee "@me"`); leave the iteration as it is, since it records which sprint the work happened in.

## Creating a [Task] or [Feature]

Every new [task] or [feature] issue gets, at creation time:

1. **A System.** Set the board's **System** field to every self-contained system the work touches: `Account`, `Main`, `Back-Office`, or `Platform` for cross-cutting work (shared-kernel, shared-webapp, developer-cli, Scaleway.Sdk, Aspire.Hosting.EuropeanPlatform, cloud-infrastructure, CI, AI rules). It is a multi-select field; pick more than one when a [task] spans systems.
2. **A parent [feature], when a relevant one exists.** Attach the [task] as a **sub-issue** of the most relevant open [feature] so it inherits context and rolls up on the board. Look for an `[Active]` or `[Planned]` [feature] covering the same area before creating a top-level [task]; only leave it parentless when none fits.
3. **A place on the board.** Add every new issue to the configured project and set its Status: `Todo` for planned work, or `In progress` if you start it immediately (then also assign yourself and set the current iteration, see **Assign on start**).
4. **A self-contained body.** The repository is public. Copy the requirements a [task] needs into its body (rules, states, acceptance criteria) rather than linking anything in `.workspace/` or outside the repository, and never include credentials, personal data or names of unrelated projects.

## Completion

After the pull request merges, **verify the [task] actually closed**. A [task] left `In progress` or `In review` after its work shipped is an invisible mistake, and the check is one command:

```bash
gh issue view <taskId> --json state,stateReason
```

If it did not close (the pull request merged into a branch other than the default one, or the link was never registered), close the issue or set Status `Done` then. A merged [task] must never be left `In progress` or `In review`.

## Authentication

All operations use the `gh` CLI, which authenticates through GitHub's browser-based OAuth flow, so no Personal Access Token is created, pasted or stored. Authenticate once per machine with `gh auth login` (GitHub.com → HTTPS → Login with a web browser). Board operations need the `project` scope on top of `repo`:

```bash
gh auth refresh --scopes project
```

`gh` keeps the token in the OS keychain. Check the active account and scopes with `gh auth status`.

## ID Mapping

| Generic ID | GitHub |
|---|---|
| `featureId` | Parent issue number (e.g. `#42`) |
| `taskId` | Sub-issue number (e.g. `#57`) |

## Rules

- All operations use the `gh` CLI, **not** the GitHub MCP server. The MCP server needs a long-lived Personal Access Token, which we don't store. Do not enable it.
- If a `gh` command fails: Stop immediately and report the error. Don't work around failures.
- Don't skip status updates. When you **start** a [task]: move it to In progress, assign yourself and set the current iteration. When you open its pull request: put `Closes #<taskId>` in the body and move the [task] to In review. **After the pull request merges**: verify the [task] is closed and Done.
- **Pull request linking is not automatic from the branch name.** Link a [task] by putting `Closes #<taskId>` in the pull request body. Starting the branch with the issue number (`57-short-description`) lets the create-pull-request workflow find the [task].
- featureId is a parent issue number (e.g. `#42`); taskId is a sub-issue number (e.g. `#57`).
- Issue bodies are GitHub-Flavored Markdown; no HTML wrapping is needed.
- To create the [feature]/[task] hierarchy: create the parent issue, create each [task] issue in the order the PRD lists them, attach each as a sub-issue of the parent in that order (the sub-issue order is the implementation sequence), then add every issue to the board and set its Status and System.
- To query the [tasks] of a [feature]: list the sub-issues of the parent issue. Filter board queries to issues, so a pull request on the board isn't counted as a [task].

## Operations

Issue CRUD uses the `gh issue` commands. Sub-issues and the board's fields have no high-level command, so they go through `gh api` and `gh api graphql`: GitHub keeps an issue's status on a separate board item, not on the issue.

| Operation | `gh` command |
|---|---|
| Create a [feature] or [task] issue | `gh issue create` |
| Edit an issue | `gh issue edit` |
| Assign / unassign the current developer | `gh issue edit <number> --add-assignee "@me"` / `--remove-assignee "@me"` |
| List / search issues | `gh issue list` / `gh search issues` |
| Attach / list **sub-issues** | `gh api` REST `.../issues/<number>/sub_issues` |
| **Add** an issue to the board | `gh api graphql` → `addProjectV2ItemById` |
| Read board items / an issue's current Status | `gh api graphql` query on `projectV2` |
| Set **Status**, **System** or **Iteration** | `gh api graphql` → `updateProjectV2ItemFieldValue` |

Examples:

```bash
# Create the parent (feature) and a task issue (then add both to the board and set Status and System)
gh issue create --title "<title>" --body-file <file>

# Attach a task as a sub-issue of the parent. sub_issue_id is the task's REST id (the numeric "id", NOT the #number)
taskId=$(gh api repos/tinytek-io/EuropeanPlatform/issues/<taskNumber> --jq .id)
gh api repos/tinytek-io/EuropeanPlatform/issues/<parentNumber>/sub_issues -F sub_issue_id="$taskId"

# List a parent's sub-issues, in order
gh api repos/tinytek-io/EuropeanPlatform/issues/<parentNumber>/sub_issues --jq '.[] | "\(.number) \(.state) \(.title)"'

# Resolve the project node id and the Status and System fields with their option ids
gh api graphql -f query='query{ organization(login:"tinytek-io"){ projectV2(number:<DEFAULT_PROJECT>){ id
  status: field(name:"Status"){ ... on ProjectV2SingleSelectField { id options { id name } } }
  system: field(name:"System"){ ... on ProjectV2MultiSelectField { id multiSelectOptions { id name } } } } } }'

# Read an issue's node id and its board items (the board may auto-add new issues; add only when missing)
gh api graphql -f query='query{ repository(owner:"tinytek-io", name:"EuropeanPlatform"){ issue(number:<number>){ id
  projectItems(first:10){ nodes { id project { number } } } } } }'

# Add an issue to the board (returns the board item id)
gh api graphql -f query='mutation{ addProjectV2ItemById(input:{ projectId:"<projectId>", contentId:"<issueNodeId>" }){ item { id } } }'

# Set the item's Status
gh api graphql -f query='mutation{ updateProjectV2ItemFieldValue(input:{ projectId:"<projectId>", itemId:"<itemId>",
  fieldId:"<statusFieldId>", value:{ singleSelectOptionId:"<optionId>" } }){ projectV2Item { id } } }'

# Set the System field to Account and Platform (multi-select)
gh api graphql -f query='mutation{ updateProjectV2ItemFieldValue(input:{ projectId:"<projectId>", itemId:"<itemId>",
  fieldId:"<systemFieldId>", value:{ multiSelectOptionIds:["<accountOptionId>","<platformOptionId>"] } }){ projectV2Item { id } } }'

# Resolve the iterations, pick the one whose [startDate, startDate + duration) contains today, and set it
gh api graphql -f query='query{ organization(login:"tinytek-io"){ projectV2(number:<DEFAULT_PROJECT>){
  field(name:"Iteration"){ ... on ProjectV2IterationField { id configuration { iterations { id title startDate duration } } } } } } }'
gh api graphql -f query='mutation{ updateProjectV2ItemFieldValue(input:{ projectId:"<projectId>", itemId:"<itemId>",
  fieldId:"<iterationFieldId>", value:{ iterationId:"<currentIterationId>" } }){ projectV2Item { id } } }'
```

## Initialization

Run `gh auth status` to confirm an authenticated session with the `project` scope. If `gh` is not authenticated, or the scope is missing for board work: Stop the workflow and tell the user to run `gh auth login` or `gh auth refresh --scopes project`. If `DEFAULT_PROJECT` is unset, ask the user which board to use before creating items.
