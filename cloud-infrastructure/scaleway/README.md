# Bootstrap: the two resources everything else depends on

This directory is the **bootstrap tier** of the Scaleway migration. It solves a chicken-and-egg
problem: the main Terraform module stores its state in Object Storage and `aspire publish`/`deploy`
push images to the Container Registry, so both must exist *before* any module can run against them.

The bootstrap itself therefore runs with **local state** (one `terraform.<environment>.tfstate` file
per environment next to this module). That is deliberate and documented here — it is the only tier
that does. Keep the
bootstrap file safe or re-run the bootstrap; all it does is create the state bucket, the registry
namespace, and the deploy pipeline credentials.

## What it creates

- `Scaleway Project` for the environment (e.g. `ep-stage`), the counterpart of the previous
  per-environment resource groups: every resource of an environment lives in its own project so
  cost and blast radius can be tracked per environment
- `Object Storage bucket` for Terraform remote state (versioned, `prevent_destroy`)
- `Container Registry namespace` for the application images
- `IAM application + API key` scoped to the project with `ContainerRegistryFullAccess` and
  `ObjectStorageFullAccess` — the credentials the CI pipeline uses to push images *and* read/write
  remote state. (Least-privilege refinement tracked as a follow-up.)

## DNS and email

After the first deploy, point the staging hostnames at the cluster and verify the sending
domain: see the [DNS guide](docs/dns-guide.md).

## Environment tiers (preview / staging / production)

One bootstrap module and one environment module serve every tier. An environment is a
**tier file** (`environment/environments/<terraform-environment>.tfvars`) plus a bootstrap
apply — never a module change. The deploy command refuses to plan an environment without its
tier file, and every environment-specific value lives in that file (the domain, sizing,
durability, the CCM-captured LB ids). Sizing and durability defaults reproduce staging; the
public identity (`domain_name`, `edge_pipeline_name_prefix`) has no default, so a tier file that
omits it fails at plan time.

| Knob | preview (future) | staging (`stage.tfvars`) | production (future) |
|---|---|---|---|
| Bootstrap project | `ep-preview` + own state bucket | `ep-stage` | `ep-prod` + own state bucket |
| Domain (`domain_name`) | `preview.europeanplatform.net` | `staging.europeanplatform.net` | `europeanplatform.net` (apex) |
| Node pool | 1 x DEV1-S | 2 x PLAY2-NANO | 2+ x PLAY2-NANO or larger |
| Database tier / HA | db-dev-s, no HA | db-dev-s, no HA | db-s or larger, HA (`database_ha = true`) |
| Backups | 24 h / 7 d | 24 h / 7 d (pinned) | 24 h / longer retention |
| Edge | own plan, own pipelines | professional plan, 2 pipelines | own plan, apex hostnames |
| Registry | private; cluster pulls via the read-only registry-pull identity | same | same |

The domain is the single public-facing knob: the edge hostnames derive as `app.<domain>` /
`back-office.<domain>`, Transactional Email sends from the zone itself, and the deploy command
passes all three to the AppHost (`Hostnames__App`, `Hostnames__BackOffice`, `Email__Sender`)
so application configuration never duplicates them.

Each environment's bootstrap keeps its own state in that environment's state bucket (see
[State](#state)), because the project resource is keyed by the environment name — one shared
state would REPLACE one environment's project with another's. Both modules' state is selected
per environment through `environment/environments/<environment>.backend.hcl`, and the bootstrap
and deploy commands run `terraform init -reconfigure` with it before they read any state.

Known provider gap (terraform-provider-scaleway ~> 2.86): `scaleway_k8s_cluster` has no
apiserver access-restriction attributes, so the Kubernetes API server stays publicly reachable
with no allow-list. Revisit on the next provider upgrade; until then it is documented exposure,
mitigated by kubeconfig being the only credential.


## Usage

Two developer CLI commands drive both modules; neither needs exported ids, a local state file, or
a hand-run `terraform`:

```bash
# Once per environment, and again whenever the bootstrap changes (idempotent, reviewed plan).
# Runs with an administrator's personal key (the active scw profile).
dotnet run --project developer-cli -- bootstrap -e staging

# Every deploy. Runs with the environment's operator key, from the scw profile the bootstrap wrote.
dotnet run --project developer-cli -- deploy -e staging
```

Both resolve the environment from its name: the prefix comes from the committed tier file
(`environment/environments/<environment>.tfvars`), the project is found by name
(`<prefix>-<environment>`), and the organization from the project. In CI, export the operator's
key as `SCW_ACCESS_KEY` and `SCW_SECRET_KEY` instead of using a profile.

### Identities

| Identity | Created by | Used for | Reach |
|---|---|---|---|
| Administrator (a person's own key) | — | `bootstrap` | the organization |
| `<prefix>-<environment>-operator` | bootstrap | `deploy`: the environment module's plan/apply, the Aspire deploy | everything in the environment's project, plus IAM application and policy management |
| `<prefix>-<environment>-deploy-pipeline` | bootstrap | registry pushes and state access for a pipeline that does not run Terraform | registry and Object Storage in the project |
| `<prefix>-<environment>-registry-pull` | bootstrap | the cluster's image pulls | registry read in the project |

Scaleway's IAM permissions exist at organization scope only, and the environment module creates
the workloads' IAM applications, policies and keys, so the operator also manages IAM applications
and policies. That stops a mistake from reaching another environment, not a stolen key: a holder
could write a policy for another project. Hard isolation (production) is a separate Scaleway
Organization.

### State

The environment module's state and the bootstrap's own state both live in the environment's state
bucket (`platformplatform/environment.tfstate` and `platformplatform/bootstrap.tfstate`). The
bucket's policy, which is its complete access spec, admits the administrator, the operator and the
deploy pipeline only — workload storage keys cannot read the state. The bootstrap command applies
a new environment's first run with local state (the bucket does not exist yet) and moves it into
the bucket right after; a bootstrap state left from before is moved the same way on the next run.
The environment module reads the bootstrap's outputs (operators, registry, pull key) through
`terraform_remote_state`.

Object Storage acts in the API key's preferred project, so both commands pass the state backend
the key as `<access-key>@<project-id>`, which targets the environment's project whatever the key's
preferred project is.

The `environment` and `managed-by=terraform` tags follow the naming conventions described in
[`cloud-infrastructure/README.md`](../README.md), with the region (e.g. `fr-par`) as the
location part of resource names.
