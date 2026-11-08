terraform {
  required_version = ">= 1.10"

  required_providers {
    scaleway = {
      source  = "scaleway/scaleway"
      version = "~> 2.86"
    }
  }

  # The bootstrap keeps its own state in the state bucket it creates, next to the environment
  # module's, so any machine (or CI) can run it again safely: the bootstrap command applies a new
  # environment's first run with local state and migrates it into the bucket right after. The
  # bucket comes from the command as partial configuration (-backend-config), like the
  # environment module's environments/<environment>.backend.hcl.
  backend "s3" {
    key          = "platformplatform/bootstrap.tfstate"
    region       = "us-east-1"
    use_lockfile = true
    # The backend validates against AWS by default (STS, region names, account IDs); none of that
    # exists for a third-party S3-compatible endpoint like Scaleway Object Storage.
    skip_credentials_validation = true
    skip_region_validation      = true
    skip_requesting_account_id  = true
    skip_metadata_api_check     = true
  }
}

# Credentials default to the SCW_* environment variables; the variables below only override them.
provider "scaleway" {
  region          = var.region
  organization_id = var.organization_id
  project_id      = var.project_id
}

# One Scaleway Project per environment (the counterpart of the per-environment resource groups
# of the previous cloud): every staging resource lives in the "pp-stage" project, production in
# its own, so cost and blast radius can be tracked and isolated per environment.
resource "scaleway_account_project" "environment" {
  name        = "${var.prefix}-${var.environment}"
  description = "PlatformPlatform ${var.environment} environment"
}

# Terraform remote state for every later module. Versioned so a botched apply is recoverable,
# and protected from destruction because rebuilding it orphans all downstream state.
resource "scaleway_object_bucket" "terraform_state" {
  name       = "${var.prefix}-${var.environment}-${var.region}-terraform-state"
  region     = var.region
  project_id = scaleway_account_project.environment.id
  tags       = local.tags
  versioning {
    enabled = true
  }

  lifecycle {
    prevent_destroy = true
  }
}

# Without a bucket policy, every identity with ObjectStorage access in the project could read the
# state — including the workloads' storage identity, whose key ships in every pod — and the state
# holds the kubeconfig, the database admin password, the internal CA key and every API key. A
# bucket policy is the COMPLETE access spec for its bucket (IAM permissions alone no longer grant
# anything), so only Terraform's identities are listed: the administrator who runs the bootstrap,
# the environment's operator (deploys), and the deploy pipeline.
resource "scaleway_object_bucket_policy" "terraform_state" {
  bucket     = scaleway_object_bucket.terraform_state.name
  region     = var.region
  project_id = scaleway_account_project.environment.id
  policy = jsonencode({
    Version = "2023-04-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          SCW = [
            "user_id:${var.administrator_user_id}",
            "application_id:${scaleway_iam_application.operator.id}",
            "application_id:${scaleway_iam_application.deploy_pipeline.id}"
          ]
        }
        Action = "s3:*"
        Resource = [
          scaleway_object_bucket.terraform_state.name,
          "${scaleway_object_bucket.terraform_state.name}/*"
        ]
      }
    ]
  })
}

# One registry namespace for all application images; `aspire deploy` pushes here. (The registry
# namespace resource does not support tags.) Private: nobody pulls anonymously — the cluster pulls
# through the read-only registry-pull identity below (a dockerconfigjson Secret the deploy command
# applies; Kapsule nodes have no native registry authentication). Flip is sequenced: apply the pull
# identity first, deploy once with the pull Secret in place, only then let this visibility change
# reach the live namespace.
resource "scaleway_registry_namespace" "registry" {
  name       = "${var.prefix}-${var.environment}-registry"
  region     = var.region
  project_id = scaleway_account_project.environment.id
  is_public  = false
}

# The cluster's pull identity: read-only registry access for image pulls. The deploy pipeline
# keeps its separate FullAccess identity for pushes — this one is deliberately scoped down and is
# the credential embedded in the cluster's dockerconfigjson pull Secret.
resource "scaleway_iam_application" "registry_pull" {
  name        = "${var.prefix}-${var.environment}-registry-pull"
  description = "Read-only registry pulls for the ${var.environment} cluster nodes"
}

resource "scaleway_iam_policy" "registry_pull" {
  name           = "${var.prefix}-${var.environment}-registry-pull"
  description    = "Registry pulls for the cluster nodes (read-only)"
  application_id = scaleway_iam_application.registry_pull.id

  rule {
    permission_set_names = ["ContainerRegistryReadOnly"]
    project_ids          = [scaleway_account_project.environment.id]
  }
}

# See the deploy pipeline key for the expiry-pinning rationale; rotate by changing the date.
resource "scaleway_iam_api_key" "registry_pull" {
  application_id     = scaleway_iam_application.registry_pull.id
  description        = "API key for the ${var.environment} cluster's image pulls (registry pull Secret)"
  expires_at         = var.registry_pull_key_expiry
  default_project_id = scaleway_account_project.environment.id
}

# The deploy pipeline's identity: pushes images and reads/writes remote state.
resource "scaleway_iam_application" "deploy_pipeline" {
  name        = "${var.prefix}-${var.environment}-deploy-pipeline"
  description = "Builds and publishes PlatformPlatform to the ${var.environment} environment"
}

# The policy is scoped to the per-environment project this module creates (never organization-wide),
# so the pipeline key can reach only that environment's registry and state bucket.
resource "scaleway_iam_policy" "deploy_pipeline" {
  name           = "${var.prefix}-${var.environment}-deploy-pipeline"
  description    = "Registry push and state bucket access for the deploy pipeline"
  application_id = scaleway_iam_application.deploy_pipeline.id

  rule {
    permission_set_names = [
      "ContainerRegistryFullAccess",
      "ObjectStorageFullAccess",
    ]
    project_ids = [scaleway_account_project.environment.id]
  }
}

# The Scaleway provider defaults API keys to a one-year expiry but records it in state without the
# config asking for it, which forces a replacement on every apply. Pin the expiry explicitly so the
# plan is stable; rotate by changing the date.
resource "scaleway_iam_api_key" "deploy_pipeline" {
  application_id = scaleway_iam_application.deploy_pipeline.id
  description    = "API key for the ${var.environment} deploy pipeline (CI secret)"
  expires_at     = var.pipeline_key_expiry
  # Scaleway's S3 API always acts through the key's preferred project, so the pipeline key must
  # point at the per-environment project its bucket and state live in.
  default_project_id = scaleway_account_project.environment.id
}

# The environment's operator: the identity deploys run as (the environment module's plan/apply and
# the Aspire deploy), so a day-to-day key reaches this environment's project only, never another
# environment's resources or state. Scaleway's IAM permissions exist at organization scope only,
# and the environment module creates the workloads' IAM applications, policies and keys, so the
# operator also manages IAM applications and policies. That makes it a guard against mistakes, not
# against a stolen key: a holder could write a policy for another project. Hard isolation for
# production is a separate Scaleway Organization.
resource "scaleway_iam_application" "operator" {
  name        = "${var.prefix}-${var.environment}-operator"
  description = "Runs Terraform and deploys for the ${var.environment} environment"
}

resource "scaleway_iam_policy" "operator" {
  name           = "${var.prefix}-${var.environment}-operator"
  description    = "Everything in the ${var.environment} project, plus the IAM the environment module manages"
  application_id = scaleway_iam_application.operator.id

  rule {
    permission_set_names = ["AllProductsFullAccess"]
    project_ids          = [scaleway_account_project.environment.id]
  }

  rule {
    # The workloads' identities (IAMApplicationManager includes their API keys), and finding the
    # environment's project by name.
    permission_set_names = ["IAMApplicationManager", "IAMPolicyManager", "ProjectReadOnly"]
    organization_id      = scaleway_account_project.environment.organization_id
  }
}

# See the deploy pipeline key for the expiry-pinning rationale; rotate by changing the date.
resource "scaleway_iam_api_key" "operator" {
  application_id = scaleway_iam_application.operator.id
  description    = "API key for the ${var.environment} operator (the operator's scw profile)"
  expires_at     = var.operator_key_expiry
  # Object Storage acts in the key's preferred project, where the state bucket lives.
  default_project_id = scaleway_account_project.environment.id
}

locals {
  tags = {
    environment = var.environment
    managed-by  = "terraform"
  }
}
