# Bootstrap: the two resources everything else depends on

This directory is the **bootstrap tier** of the Scaleway migration. It solves a chicken-and-egg
problem: the main Terraform module stores its state in Object Storage and `aspire publish`/`deploy`
push images to the Container Registry, so both must exist *before* any module can run against them.

The bootstrap itself therefore runs with **local state** (a single `terraform.tfstate` file next to
this module). That is deliberate and documented here — it is the only tier that does. Keep the
bootstrap file safe or re-run the bootstrap; all it does is create the state bucket, the registry
namespace, and the deploy pipeline credentials.

## What it creates

- `Scaleway Project` for the environment (e.g. `pp-stage`), the counterpart of the previous
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
durability, the CCM-captured LB ids) with module defaults reproducing staging exactly.

| Knob | preview (future) | staging (`stage.tfvars`) | production (future) |
|---|---|---|---|
| Bootstrap project | `pp-preview` + own state bucket | `pp-stage` | `pp-prod` + own state bucket |
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

Bootstrap runs on **local state**; each environment's bootstrap state is a separate file
(`terraform apply -state=terraform.<environment>.tfstate`), because the project resource is keyed
by the environment name — applying a second environment into one state file would REPLACE the
first environment's project. The environment module's remote state bucket is per-environment
(created by that environment's bootstrap); switching the working copy between environments is
`terraform init -reconfigure` with the target bucket.

Known provider gap (terraform-provider-scaleway ~> 2.86): `scaleway_k8s_cluster` has no
apiserver access-restriction attributes, so the Kubernetes API server stays publicly reachable
with no allow-list. Revisit on the next provider upgrade; until then it is documented exposure,
mitigated by kubeconfig being the only credential.


## Usage

Credentials come from the environment (`SCW_ACCESS_KEY`, `SCW_SECRET_KEY`,
`SCW_DEFAULT_ORGANIZATION_ID`, `SCW_DEFAULT_PROJECT_ID`) or the equivalent variables below.

The environment module also takes operator- and cluster-specific inputs that are never committed:
`TF_VAR_operator_user_id` (the IAM user id of whoever runs Terraform) and
`TF_VAR_gateway_lb_frontend_id` / `TF_VAR_gateway_lb_backend_id` (captured after the first deploy;
see the cluster-ops README).

```bash
cd cloud-infrastructure/scaleway/bootstrap
terraform init
terraform apply -var prefix=<your-unique-prefix> -var environment=stage
terraform output   # registry endpoint + pipeline API key (secret is sensitive)
```

## Handover to the main module

The main module (staging/prod topology) stores its state in the bucket created here, using S3
conditional-write locking (Terraform >= 1.10). Fill in the bucket name and use the pipeline API key
as the S3 credentials:

```hcl
terraform {
  backend "s3" {
    bucket       = "<prefix>-<environment>-<region>-terraform-state"
    key          = "platformplatform/terraform.tfstate"
    region       = "fr-par"
    endpoint     = "https://s3.fr-par.scw.cloud"
    use_lockfile = true
  }
}
```

```bash
export AWS_ACCESS_KEY_ID=<pipeline access key>
export AWS_SECRET_ACCESS_KEY=<pipeline secret key>
```

The `environment` and `managed-by=terraform` tags follow the naming conventions described in
[`cloud-infrastructure/README.md`](../README.md), with the region (e.g. `fr-par`) as the
location part of resource names.
