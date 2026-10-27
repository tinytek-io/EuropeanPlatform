terraform {
  required_version = ">= 1.10"

  required_providers {
    scaleway = {
      source  = "scaleway/scaleway"
      version = "~> 2.86"
    }
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

# The policy is project-scoped; the provider cannot resolve the CLI profile's default project into
# a rule scope, so an apply without project_id set fails here with an explicit error rather than
# granting a broader organization-wide scope.
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

locals {
  tags = {
    environment = var.environment
    managed-by  = "terraform"
  }
}
