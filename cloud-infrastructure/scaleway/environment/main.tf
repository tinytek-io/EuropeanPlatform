terraform {
  required_version = ">= 1.10"

  required_providers {
    scaleway = {
      source  = "scaleway/scaleway"
      version = "~> 2.86"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.0"
    }
  }

  # Remote state lives in the bucket each environment's bootstrap created, using Scaleway's S3
  # conditional-write locking. The bucket and endpoint are per environment and come from
  # environments/<environment>.backend.hcl (partial configuration), so the working copy can never
  # silently plan against another environment's state:
  #   terraform init -reconfigure -backend-config=environments/<environment>.backend.hcl
  # (the deploy command runs exactly that before it reads the state). Credentials come from the
  # deploy pipeline API key:
  #   export AWS_ACCESS_KEY_ID=$(pipeline access key)
  #   export AWS_SECRET_ACCESS_KEY=$(pipeline secret key)
  backend "s3" {
    key          = "platformplatform/environment.tfstate"
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

provider "scaleway" {
  region = var.region
  zone   = local.zone
}

variable "prefix" {
  description = "Unique prefix shared with the bootstrap module, e.g. `ep`"
  type        = string
}

variable "environment" {
  description = "Environment this stack serves, e.g. `stage`"
  type        = string
}

variable "region" {
  description = "Scaleway region, e.g. `fr-par`"
  type        = string
  default     = "fr-par"
}

# The per-environment project created by the bootstrap module (e.g. `ep-stage`).
variable "project_id" {
  description = "Scaleway Project ID from the bootstrap module's environment_project_id output"
  type        = string
}

# The bootstrap's outputs (the operators, the registry, the cluster's pull identity) come from its
# state, which the bootstrap command keeps in this environment's state bucket next to this module's.
data "terraform_remote_state" "bootstrap" {
  backend = "s3"
  config = {
    bucket = "${var.prefix}-${var.environment}-${var.region}-terraform-state"
    key    = "platformplatform/bootstrap.tfstate"
    region = "us-east-1"
    endpoints = {
      s3 = "https://s3.${var.region}.scw.cloud"
    }
    skip_credentials_validation = true
    skip_region_validation      = true
    skip_requesting_account_id  = true
    skip_metadata_api_check     = true
  }
}

locals {
  # A bucket policy is the complete access spec for its bucket (IAM permissions alone no longer
  # grant anything), so whoever runs Terraform must be listed or it locks itself out: the
  # environment's operator application (deploys) and the administrator who runs the bootstrap.
  terraform_operator_principals = [
    "application_id:${data.terraform_remote_state.bootstrap.outputs.operator_application_id}",
    "user_id:${data.terraform_remote_state.bootstrap.outputs.administrator_user_id}"
  ]
}

variable "kubernetes_version" {
  description = "Kapsule Kubernetes version; 1.37 ships Cilium 1.20 (Gateway API ExternalAuth available)"
  type        = string
  default     = "1.37.0"
}

variable "environment_type" {
  description = "What the environment is for: review, test, staging or production. It selects the environment's profile in ../environment-profiles.yaml (cluster, database, backups, load balancer, NAT gateway and blob versioning here; workload replicas and memory in the AppHost), and it is fixed once the environment exists."
  type        = string
  validation {
    condition     = contains(["review", "test", "staging", "production"], var.environment_type)
    error_message = "environment_type must be one of review, test, staging or production."
  }
}

# The sizing the environment's type gets, from the platform's profile matrix. A tier file may override a single value
# through the variables below (null keeps the profile's); everything else follows the type.
locals {
  environment_profile = yamldecode(file("${path.module}/../environment-profiles.yaml")).infrastructure[var.environment_type]

  node_type                 = coalesce(var.node_type, local.environment_profile.cluster.nodeType)
  node_count                = coalesce(var.node_count, local.environment_profile.cluster.nodeCount)
  database_node_type        = coalesce(var.database_node_type, local.environment_profile.database.nodeType)
  database_ha               = var.database_ha != null ? var.database_ha : local.environment_profile.database.highAvailability
  backup_schedule_frequency = coalesce(var.backup_schedule_frequency, local.environment_profile.database.backupFrequencyHours)
  backup_schedule_retention = coalesce(var.backup_schedule_retention, local.environment_profile.database.backupRetentionDays)
  load_balancer_type        = coalesce(var.load_balancer_type, local.environment_profile.network.loadBalancerType)
  nat_gateway_type          = coalesce(var.nat_gateway_type, local.environment_profile.network.natGatewayType)
  blob_versioning           = var.blob_versioning != null ? var.blob_versioning : local.environment_profile.storage.blobVersioning
}

variable "node_type" {
  description = "Overrides the profile's pool node type for this environment. Changing it replaces the pool, whose name carries the node type."
  type        = string
  default     = null
}

variable "node_count" {
  description = "Overrides the profile's pool size for this environment."
  type        = number
  default     = null
}

variable "monthly_budget_eur" {
  description = "What the environment may cost per month in euros, excluding VAT and usage-billed charges. Terraform does not use it: the developer CLI's cost-estimate prices the plan against it, and the deploy asks for confirmation before applying a plan that would exceed it."
  type        = number
  validation {
    condition     = var.monthly_budget_eur > 0
    error_message = "monthly_budget_eur must be a positive number of euros."
  }
}

variable "database_node_type" {
  description = "Overrides the profile's managed PostgreSQL node type for this environment."
  type        = string
  default     = null
}

variable "database_ha" {
  description = "Overrides whether the profile runs managed PostgreSQL as a high-availability cluster, which survives a host failure and roughly doubles the database cost."
  type        = bool
  default     = null
}

variable "backup_schedule_frequency" {
  description = "Overrides the profile's automated backup frequency, in hours."
  type        = number
  default     = null
}

variable "backup_schedule_retention" {
  description = "Overrides the profile's automated backup retention, in days."
  type        = number
  default     = null
}

variable "load_balancer_type" {
  description = "Overrides the profile's type for the public Load Balancer in front of the cluster gateway."
  type        = string
  default     = null
}

variable "nat_gateway_type" {
  description = "Overrides the profile's type for the Public Gateway that carries the cluster's outbound traffic."
  type        = string
  default     = null
}

variable "blob_versioning" {
  description = "Overrides whether the profile versions the buckets holding user uploads (avatars and logos). Once enabled, Object Storage can only suspend versioning, never remove it."
  type        = bool
  default     = null
}

variable "database_admin_user" {
  description = "Name of the auto-created RDB admin user"
  type        = string
  default     = "platformplatform"
}

locals {
  # Zonal resources (NAT gateway, LB, node pool) all live in the region's first zone.
  zone = "${var.region}-1"

  # Most resources take tags as a string list; object buckets take a string map.
  tags = ["environment:${var.environment}", "managed-by:terraform"]
  bucket_tags = {
    environment = var.environment
    managed-by  = "terraform"
  }
}

# All workloads and the database share one private network. The database has ONLY a private
# endpoint (scaleway_rdb_instance.main declares no load_balancer block, and the provider adds the
# public endpoint only when no private network is set), so it is reachable from the cluster and
# nowhere else; operator access needs a path into the private network (the default-deny network
# policies in cluster-ops admit database egress only from the application workloads).
resource "scaleway_vpc_private_network" "vpc" {
  name       = "${var.prefix}-${var.environment}"
  project_id = var.project_id
  tags       = local.tags
}

# NAT egress for the node pool: the nodes have no public IPs (see scaleway_k8s_pool.default), and
# the Kapsule API refuses that configuration unless a public gateway on the private network
# advertises the default route. The gateway masquerades outbound traffic (image pulls, telemetry,
# third-party APIs); nothing can reach the nodes inbound through it.
resource "scaleway_vpc_public_gateway_ip" "nat" {
  zone       = local.zone
  project_id = var.project_id
  tags       = local.tags
}

resource "scaleway_vpc_public_gateway" "nat" {
  zone       = local.zone
  project_id = var.project_id
  name       = "${var.prefix}-${var.environment}-nat"
  type       = local.nat_gateway_type
  ip_id      = scaleway_vpc_public_gateway_ip.nat.id
  tags       = local.tags
}

resource "scaleway_vpc_gateway_network" "nat" {
  zone               = local.zone
  gateway_id         = scaleway_vpc_public_gateway.nat.id
  private_network_id = scaleway_vpc_private_network.vpc.id
  enable_masquerade  = true
  ipam_config {
    push_default_route = true
  }
}

# The public Load Balancer in front of the Cilium Envoy gateway. Terraform owns the LB shell
# and its flexible IP (which survives LB recreation); the Kapsule cloud-controller-manager
# creates the frontends and backends on it through the Service annotations
# (scw-loadbalancer-id + scw-loadbalancer-externally-managed) — see cluster-ops. The name MUST
# NOT start with the cluster id: the CCM refuses to manage such LBs in externally-managed mode.
resource "scaleway_lb_ip" "gateway" {
  zone       = local.zone
  project_id = var.project_id
  tags       = local.tags
}

resource "scaleway_lb" "gateway" {
  zone       = local.zone
  project_id = var.project_id
  name       = "${var.prefix}-${var.environment}-gateway"
  type       = local.load_balancer_type
  ip_ids     = [scaleway_lb_ip.gateway.id]
  tags       = local.tags
}

# Managed PostgreSQL: one instance, one database per self-contained system. The databases are
# isolated from each other (no shared schema), which keeps the system boundaries hard and makes
# per-system pg_dump restores trivial. The connection strings reach the workloads through
# Secret Manager (see local.base_workload_secrets), never through Terraform outputs.
resource "scaleway_rdb_instance" "main" {
  name          = "${var.prefix}-${var.environment}-database"
  project_id    = var.project_id
  engine        = "PostgreSQL-17"
  node_type     = local.database_node_type
  is_ha_cluster = local.database_ha
  user_name     = var.database_admin_user
  password      = random_password.database.result
  tags          = local.tags

  # Durability: automated backups are pinned explicitly instead of relying on Scaleway's invisible
  # defaults (autobackup on, once a day, 7-day retention — verified against the product docs), so
  # the durability contract travels with the environment. Restore path: restore a backup to a NEW
  # instance (console/API), verify it, then cut over by pointing this resource at the clone —
  # never restore in place under a running workload.
  backup_schedule_frequency = local.backup_schedule_frequency
  backup_schedule_retention = local.backup_schedule_retention

  private_network {
    pn_id       = scaleway_vpc_private_network.vpc.id
    enable_ipam = true
  }
}

resource "random_password" "database" {
  # Scaleway requires digit + uppercase + lowercase + special character.
  length  = 32
  special = true
}

resource "scaleway_rdb_database" "account" {
  instance_id = scaleway_rdb_instance.main.id
  name        = "account"
}

resource "scaleway_rdb_database" "main" {
  instance_id = scaleway_rdb_instance.main.id
  name        = "main"
}

# Databases created through the API do not grant the instance's admin user any privilege on
# them, so migrations fail with "permission denied for database" at connect time. Each database
# needs an explicit privilege grant for the application user.
resource "scaleway_rdb_privilege" "account" {
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.account.name
  user_name     = var.database_admin_user
  permission    = "all"
}

resource "scaleway_rdb_privilege" "main" {
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.main.name
  user_name     = var.database_admin_user
  permission    = "all"
}

# Least-privilege database roles: the running application connects as a readwrite user that can
# never change database structure, while a dedicated migrator user owns the schema and is used
# only by the pre-upgrade migration Job (MIGRATE_ONLY). The instance's admin user stays for
# Terraform and operator access only — its credential no longer reaches the application.
resource "random_password" "app_user" {
  length  = 32
  special = true
}

resource "random_password" "migrator_user" {
  length  = 32
  special = true
}

resource "scaleway_rdb_user" "app" {
  instance_id = scaleway_rdb_instance.main.id
  name        = "${var.database_admin_user}_app"
  password    = random_password.app_user.result
  is_admin    = false
}

resource "scaleway_rdb_user" "migrator" {
  instance_id = scaleway_rdb_instance.main.id
  name        = "${var.database_admin_user}_migrator"
  password    = random_password.migrator_user.result
  is_admin    = false
}

# The preset grants the app user access to the database; the table-level grants are the migrator's
# (it owns the tables and grants the app user on them, including default privileges for tables
# later migrations create). Scaleway then reads the privilege back as "custom", so the permission
# is ignored after creation instead of resetting the preset on every apply.
resource "scaleway_rdb_privilege" "app_account" {
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.account.name
  user_name     = scaleway_rdb_user.app.name
  permission    = "readwrite"

  lifecycle {
    ignore_changes = [permission]
  }
}

resource "scaleway_rdb_privilege" "app_main" {
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.main.name
  user_name     = scaleway_rdb_user.app.name
  permission    = "readwrite"

  lifecycle {
    ignore_changes = [permission]
  }
}

resource "scaleway_rdb_privilege" "migrator_account" {
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.account.name
  user_name     = scaleway_rdb_user.migrator.name
  permission    = "all"
}

resource "scaleway_rdb_privilege" "migrator_main" {
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.main.name
  user_name     = scaleway_rdb_user.migrator.name
  permission    = "all"
}

# Object storage for avatars and logos, mirroring the public containers they replace: the
# gateway proxies unsigned GETs to these path-style bucket URLs. Names carry the
# prefix-environment prefix because Scaleway's bucket namespace is SHARED ACROSS ALL CUSTOMERS
# per region (found live: creating the literal "avatars" bucket failed with BucketAlreadyExists)
# — the applications address buckets by logical name and get this prefix injected through the
# blob-storage connection string's BucketPrefix entry. TF pre-creates them so the storage
# client's lazy bucket-creation path never runs in deployed environments.
resource "scaleway_object_bucket" "avatars" {
  name       = "${var.prefix}-${var.environment}-avatars"
  region     = var.region
  project_id = var.project_id
  tags       = local.bucket_tags
  versioning {
    enabled = local.blob_versioning
  }
  cors_rule {
    allowed_methods = ["GET"]
    allowed_origins = ["*"]
    max_age_seconds = 3600
  }
}

resource "scaleway_object_bucket" "logos" {
  name       = "${var.prefix}-${var.environment}-logos"
  region     = var.region
  project_id = var.project_id
  tags       = local.bucket_tags
  versioning {
    enabled = local.blob_versioning
  }
  cors_rule {
    allowed_methods = ["GET"]
    allowed_origins = ["*"]
    max_age_seconds = 3600
  }
}

# Public serving works through a BUCKET POLICY, not the S3 ACL: the ACL grants AllUsers READ (it
# really is set on the buckets), but Scaleway does not serve anonymous requests off ACLs — found
# live: anonymous GET returned 403 with the ACL in place. Scaleway policies reference resources as
# plain bucket-relative strings, NOT ARNs (ARN-style resources fail with MalformedPolicy — also
# found live). The storage client's lazy public-bucket policy (AWS ARN format) only ever runs
# against MinIO in local development, where that format is accepted.
resource "scaleway_object_bucket_policy" "avatars" {
  bucket = scaleway_object_bucket.avatars.name
  policy = jsonencode({
    Version = "2023-04-17"
    Statement = [
      # A bucket policy REPLACES IAM evaluation for its bucket (found live: with a policy
      # attached, even the storage application's ObjectStorageFullAccess stopped granting
      # access — logo uploads 500'd with BucketAlreadyOwnedByYou because HeadBucket 403'd).
      # Both statements are needed: anonymous public reads, and the account system's storage
      # application keeping its upload/delete access.
      {
        Effect    = "Allow"
        Principal = "*"
        Action    = "s3:GetObject"
        Resource = [
          scaleway_object_bucket.avatars.name,
          "${scaleway_object_bucket.avatars.name}/*"
        ]
      },
      {
        Effect = "Allow"
        Principal = {
          SCW = "application_id:${scaleway_iam_application.storage["account"].id}"
        }
        Action = [
          "s3:ListBucket",
          "s3:GetObject",
          "s3:PutObject",
          "s3:DeleteObject"
        ]
        Resource = [
          scaleway_object_bucket.avatars.name,
          "${scaleway_object_bucket.avatars.name}/*"
        ]
      },
      {
        # Terraform's own identities (see terraform_operator_principals): without them Terraform
        # is locked out (found live: refresh started failing with AccessDenied on GetBucketCors).
        Effect = "Allow"
        Principal = {
          SCW = local.terraform_operator_principals
        }
        Action = "s3:*"
        Resource = [
          scaleway_object_bucket.avatars.name,
          "${scaleway_object_bucket.avatars.name}/*"
        ]
      }
    ]
  })
}

resource "scaleway_object_bucket_policy" "logos" {
  bucket = scaleway_object_bucket.logos.name
  policy = jsonencode({
    Version = "2023-04-17"
    Statement = [
      {
        Effect    = "Allow"
        Principal = "*"
        Action    = "s3:GetObject"
        Resource = [
          scaleway_object_bucket.logos.name,
          "${scaleway_object_bucket.logos.name}/*"
        ]
      },
      {
        Effect = "Allow"
        Principal = {
          SCW = "application_id:${scaleway_iam_application.storage["account"].id}"
        }
        Action = [
          "s3:ListBucket",
          "s3:GetObject",
          "s3:PutObject",
          "s3:DeleteObject"
        ]
        Resource = [
          scaleway_object_bucket.logos.name,
          "${scaleway_object_bucket.logos.name}/*"
        ]
      },
      {
        # Terraform's own identities (see terraform_operator_principals): without them Terraform
        # is locked out (found live: refresh started failing with AccessDenied on GetBucketCors).
        Effect = "Allow"
        Principal = {
          SCW = local.terraform_operator_principals
        }
        Action = "s3:*"
        Resource = [
          scaleway_object_bucket.logos.name,
          "${scaleway_object_bucket.logos.name}/*"
        ]
      }
    ]
  })
}

# Private bucket persisting the shared Data Protection keyring (antiforgery and token state span
# account-api and main-api, so the ring must survive restarts and scale-out). Same prefix rule as
# the avatar/logo buckets: the storage client resolves the logical name "data-protection" through
# the connection string's BucketPrefix, and the bucket is pre-created PRIVATE so the client's lazy
# creation (which would apply a public-read policy) never fires for keyring material.
resource "scaleway_object_bucket" "data_protection" {
  name       = "${var.prefix}-${var.environment}-data-protection"
  region     = var.region
  project_id = var.project_id
  tags       = local.bucket_tags

  lifecycle {
    # Create the replacement before destroying the old bucket: a rename requires copying the
    # keyring blobs to the replacement bucket first, and a gap between buckets would have every
    # API mint and validate against an empty ring. The provider has no force_destroy (2.84), so a
    # rename with content requires hand-emptying the old bucket; the keyring itself is recreatable
    # by the app (the key-ring owner generates a fresh ring on an empty bucket) at the cost of one
    # token invalidation — never data loss.
    create_before_destroy = true
  }
}

# The keyring bucket admits every system's storage identity (each API reads and extends the one
# shared ring) and Terraform; nothing is public. Without a policy, any identity with Object Storage
# access in the project could read or replace the keyring.
resource "scaleway_object_bucket_policy" "data_protection" {
  bucket = scaleway_object_bucket.data_protection.name
  policy = jsonencode({
    Version = "2023-04-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          SCW = [for system in local.storage_systems : "application_id:${scaleway_iam_application.storage[system].id}"]
        }
        Action = [
          "s3:ListBucket",
          "s3:GetObject",
          "s3:PutObject",
          "s3:DeleteObject"
        ]
        Resource = [
          scaleway_object_bucket.data_protection.name,
          "${scaleway_object_bucket.data_protection.name}/*"
        ]
      },
      {
        Effect = "Allow"
        Principal = {
          SCW = local.terraform_operator_principals
        }
        Action = "s3:*"
        Resource = [
          scaleway_object_bucket.data_protection.name,
          "${scaleway_object_bucket.data_protection.name}/*"
        ]
      }
    ]
  })
}

# Static maintenance page for a cluster-down fallback, versioned with the infrastructure. Nothing
# serves it yet: the Load Balancer's failover host cannot reach edge visitors (see
# ../cluster-ops/edge-services.md), so the fallback has to come from the edge.
resource "scaleway_object_bucket" "maintenance" {
  name       = "${var.prefix}-${var.environment}-maintenance"
  region     = var.region
  project_id = var.project_id
  tags       = local.bucket_tags
}

resource "scaleway_object_bucket_website_configuration" "maintenance" {
  bucket = scaleway_object_bucket.maintenance.id

  index_document {
    suffix = "index.html"
  }
}

resource "scaleway_object" "maintenance_index" {
  bucket     = scaleway_object_bucket.maintenance.name
  region     = var.region
  key        = "index.html"
  file       = "${path.module}/maintenance/index.html"
  visibility = "public-read"

  depends_on = [scaleway_object_bucket_website_configuration.maintenance]
}

# The maintenance page is public to read and only Terraform writes it; no workload's storage
# identity is listed, so no workload can replace the page.
resource "scaleway_object_bucket_policy" "maintenance" {
  bucket = scaleway_object_bucket.maintenance.name
  policy = jsonencode({
    Version = "2023-04-17"
    Statement = [
      {
        Effect    = "Allow"
        Principal = "*"
        Action    = "s3:GetObject"
        Resource = [
          scaleway_object_bucket.maintenance.name,
          "${scaleway_object_bucket.maintenance.name}/*"
        ]
      },
      {
        Effect = "Allow"
        Principal = {
          SCW = local.terraform_operator_principals
        }
        Action = "s3:*"
        Resource = [
          scaleway_object_bucket.maintenance.name,
          "${scaleway_object_bucket.maintenance.name}/*"
        ]
      }
    ]
  })
}

# Observability: Cockpit receives the workloads' OTLP telemetry (metrics, logs, traces) and the
# Edge Services WAF logs for this project. The Cockpit itself is implicit (every project has
# one), so this module owns its three data sources and the push token explicitly. The OTLP
# endpoints are derived from each data source's id; the token authenticates the pushes
# (X-TOKEN header). write_traces defaults to FALSE on the token, so all three write scopes are
# pinned explicitly.
data "scaleway_cockpit_config" "main" {
  region = var.region
}

resource "scaleway_cockpit_source" "telemetry" {
  for_each = toset(["metrics", "logs", "traces"])

  project_id = var.project_id
  region     = var.region
  name       = "${var.prefix}-${var.environment}-${each.key}"
  type       = each.key
  # The plan's default retention for custom data sources of this type — no over-retention cost.
  retention_days = {
    metrics = data.scaleway_cockpit_config.main.custom_metrics_retention[0].default_days
    logs    = data.scaleway_cockpit_config.main.custom_logs_retention[0].default_days
    traces  = data.scaleway_cockpit_config.main.custom_traces_retention[0].default_days
  }[each.key]

  lifecycle {
    # New or updated data sources only appear in Grafana after a sync; the sync action runs
    # automatically after every create/update of any of the three sources.
    action_trigger {
      events  = [after_create, after_update]
      actions = [action.scaleway_cockpit_grafana_sync_data_sources.sync]
    }
  }
}

# Scaleway creates a project's Grafana on its first access, and the sync is refused until then
# ("Grafana does not exist, cannot synchronize data sources"). Reading it here is that access,
# and the sync takes its project from the read so it always runs after it.
data "scaleway_cockpit_grafana" "main" {
  project_id = var.project_id
}

action "scaleway_cockpit_grafana_sync_data_sources" "sync" {
  config {
    project_id = data.scaleway_cockpit_grafana.main.project_id
  }
}

resource "scaleway_cockpit_token" "workload_telemetry" {
  name       = "${var.prefix}-${var.environment}-workload-telemetry"
  project_id = var.project_id
  region     = var.region

  scopes {
    write_metrics = true
    write_logs    = true
    write_traces  = true
  }
}

# Query-only token for operators and tooling (log review, dashboards, scripted checks): the
# workloads' token is write-only by design, so read access lives on a separate identity that can
# be handed out without granting any push permission.
resource "scaleway_cockpit_token" "operator_query" {
  name       = "${var.prefix}-${var.environment}-operator-query"
  project_id = var.project_id
  region     = var.region

  scopes {
    query_metrics = true
    query_logs    = true
    query_traces  = true
  }
}

# Kapsule: Cilium CNI for the Gateway API ingress. Control plane is free; the pool nodes are the
# only cluster cost.
resource "scaleway_k8s_cluster" "kapsule" {
  name                        = "${var.prefix}-${var.environment}"
  project_id                  = var.project_id
  region                      = var.region
  version                     = var.kubernetes_version
  cni                         = "cilium"
  private_network_id          = scaleway_vpc_private_network.vpc.id
  delete_additional_resources = true
  tags                        = local.tags
}

resource "scaleway_k8s_pool" "default" {
  cluster_id = scaleway_k8s_cluster.kapsule.id
  # The node type cannot change in place, so a new type replaces the pool. The name carries the
  # type and the replacement is created first: the new nodes are ready before Kapsule drains and
  # deletes the old pool, and the workloads reschedule onto them without an outage.
  name      = "default-${lower(local.node_type)}"
  node_type = local.node_type
  size      = local.node_count
  # No public IPs on the nodes: the edge WAF (and the gateway load balancer, which sits on the
  # private network) is the only public entry — a node's public IP would bypass it entirely.
  # Nodes keep outbound access for image pulls and telemetry through the private network.
  public_ip_disabled  = true
  autoscaling         = false
  zone                = local.zone
  wait_for_pool_ready = true
  tags                = local.tags
  # The Kapsule API only accepts public_ip_disabled when the NAT gateway is attached and
  # advertises the default route; the dependency guarantees the apply ordering.
  depends_on = [scaleway_vpc_gateway_network.nat]

  # Kapsule upgrades add a surge node alongside the old one, so a single-node pool has somewhere to
  # reschedule during an upgrade. The API requires max_unavailable >= 1, so the old node may drain
  # while the surge node is still joining: a single-node environment can blip during upgrades.
  upgrade_policy {
    max_unavailable = 1
    max_surge       = 1
  }

  lifecycle {
    create_before_destroy = true
  }
}

# -------------------------------------------------------------------------------------------------
# Secret Manager: one path per workload, one IAM identity per workload.
#
# Terraform owns the secret values (a new `terraform apply` writes a new version, which the
# workloads pick up within their 5-minute configuration refresh — that is the rotation path;
# Secret Manager has no built-in auto-rotation). Each workload's IAM policy is path-conditioned
# so a service identity can only list and read its own path.
# -------------------------------------------------------------------------------------------------


variable "workload_key_expiry" {
  description = "Expiry of the per-workload Secret Manager API keys (RFC3339). Change to rotate."
  type        = string
  default     = "2027-10-05T00:00:00Z"
}

# Tier configuration: every environment-specific value lives here or in a sibling tfvars file
# (environments/<environment>.tfvars, selected by the deploy command). One module serves
# preview/staging/production; a new environment is a new tier file plus a bootstrap apply, never
# a module change. Sizing and durability defaults reproduce staging; the public identity
# (domain_name, edge_pipeline_name_prefix) has NO default, so a tier file that forgets it fails at
# plan time instead of silently planning staging's hostnames into another environment.

variable "domain_name" {
  description = "This environment's zone on the public domain — the single source for every public hostname and the Transactional Email sending domain. The edge hostnames derive as app.<domain_name> and back-office.<domain_name>; staging sends mail from its sub-zone (isolated reputation), production registers the apex domain."
  type        = string
}

locals {
  # Everything public derives from domain_name: one edge pipeline per hostname, and the TEM
  # sending domain is the zone itself.
  edge_hostnames  = { app = "app.${var.domain_name}", back-office = "back-office.${var.domain_name}" }
  tem_domain_name = var.domain_name
}

variable "edge_pipeline_name_prefix" {
  description = "Edge pipeline names are \"<prefix>-<hostname key>\". Staging keeps its historical \"staging-\" prefix; other environments use their tier name."
  type        = string
}

# The HMAC token-signing key is shared by the token-issuing APIs and the token-validating gateway,
# so it is fanned out into every path that needs it.
resource "random_bytes" "authentication_token_signing_key" {
  length = 64
}

# The AES key material protecting the shared Data Protection keyring persisted in Object Storage:
# the SPA-serving APIs (account-api, main-api) encrypt keyring elements with it before they are
# written to the data-protection bucket, so bucket access alone yields no usable key material.
resource "random_bytes" "data_protection_encryption_key" {
  length = 64
}

# One storage identity per self-contained system, shared by that system's API and workers — the
# counterpart of the per-system managed identities and storage accounts of the previous cloud.
# Its key is embedded in the system's blob-storage connection string, and it is never the deploy
# pipeline key, which can also write the Terraform state bucket. The IAM policy is project-wide
# because Object Storage permission sets cannot name a bucket; what confines each identity to its
# own buckets is that every bucket carries a bucket policy listing exactly who may use it, and a
# bucket policy replaces IAM evaluation for its bucket: avatars and logos admit the account system,
# data-protection (the keyring every API shares) admits every system, maintenance admits none.
locals {
  storage_systems = toset(["account", "main"])
}

resource "scaleway_iam_application" "storage" {
  for_each = local.storage_systems

  name        = "${var.prefix}-${var.environment}-${each.key}-storage"
  description = "Object storage access for the ${each.key} system's workloads"
}

resource "scaleway_iam_policy" "storage" {
  for_each = local.storage_systems

  name           = "${var.prefix}-${var.environment}-${each.key}-storage"
  application_id = scaleway_iam_application.storage[each.key].id
  rule {
    permission_set_names = ["ObjectStorageFullAccess"]
    project_ids          = [var.project_id]
  }
}

resource "scaleway_iam_api_key" "storage" {
  for_each = local.storage_systems

  application_id     = scaleway_iam_application.storage[each.key].id
  description        = "API key for the ${each.key} system's object storage (embedded in its blob-storage connection string)"
  expires_at         = var.workload_key_expiry
  default_project_id = var.project_id
}

# The single storage identity every workload used to share becomes the account system's, so the
# account workloads keep their key through the change.
moved {
  from = scaleway_iam_application.storage
  to   = scaleway_iam_application.storage["account"]
}

moved {
  from = scaleway_iam_policy.storage
  to   = scaleway_iam_policy.storage["account"]
}

moved {
  from = scaleway_iam_api_key.storage
  to   = scaleway_iam_api_key.storage["account"]
}

# Internal TLS: one Terraform-managed CA signs each serving workload's in-cluster certificate.
# The workloads serve HTTPS only — the pod network is not trusted to carry plaintext — and Envoy
# validates them through BackendTLSPolicy against the CA. The leaf key material reaches the
# workloads through Secret Manager; the CA certificate (public material) travels to the deploy
# pipeline as an output, which the chart turns into Envoy's validation ConfigMap and the
# app-gateway reads as configuration. Certificates do not renew themselves: rotate with
# terraform apply -replace=tls_private_key.internal_ca (the new CA key re-signs everything; the
# Secret Manager versions update within the workloads' 5-minute refresh), then redeploy so the
# pods restart onto the new material.
locals {
  # The serving workloads and their in-cluster Service DNS names (the certificates' SANs).
  internal_tls_workloads = {
    account-api = "account-api-service"
    main-api    = "main-api-service"
    app-gateway = "app-gateway-service"
  }
}

resource "tls_private_key" "internal_ca" {
  algorithm   = "ECDSA"
  ecdsa_curve = "P256"
}

resource "tls_self_signed_cert" "internal_ca" {
  private_key_pem       = tls_private_key.internal_ca.private_key_pem
  is_ca_certificate     = true
  set_subject_key_id    = true
  validity_period_hours = 87600 # 10 years
  allowed_uses          = ["cert_signing"]

  subject {
    common_name = "${var.prefix}-${var.environment} internal CA"
  }
}

resource "tls_private_key" "workload" {
  for_each = local.internal_tls_workloads

  algorithm   = "ECDSA"
  ecdsa_curve = "P256"
}

resource "tls_cert_request" "workload" {
  for_each = local.internal_tls_workloads

  private_key_pem = tls_private_key.workload[each.key].private_key_pem

  subject {
    common_name = each.value
  }

  dns_names = [
    each.value,
    "${each.value}.default",
    "${each.value}.default.svc",
    "${each.value}.default.svc.cluster.local",
  ]
}

resource "tls_locally_signed_cert" "workload" {
  for_each = local.internal_tls_workloads

  cert_request_pem      = tls_cert_request.workload[each.key].cert_request_pem
  ca_private_key_pem    = tls_private_key.internal_ca.private_key_pem
  ca_cert_pem           = tls_self_signed_cert.internal_ca.cert_pem
  validity_period_hours = 8760 # 1 year
  allowed_uses          = ["server_auth"]
}


locals {
  # The application connects as the readwrite user (it can never change database structure);
  # the migrator connection string reaches only the workers' Secret Manager paths, where the
  # pre-upgrade migration Job (MIGRATE_ONLY) resolves it.
  database_host                 = "${scaleway_rdb_instance.main.private_network[0].ip};Port=${scaleway_rdb_instance.main.private_network[0].port}"
  app_database_credentials      = "Username=${scaleway_rdb_user.app.name};Password=${random_password.app_user.result};SSL Mode=Require"
  migrator_database_credentials = "Username=${scaleway_rdb_user.migrator.name};Password=${random_password.migrator_user.result};SSL Mode=Require"

  # Every workload shares the instance's max_connections (100 on db-dev-s, a few of them reserved for superusers), and
  # Npgsql's default pool lets each one open 100, so a burst in any workload could exhaust the database for all of
  # them (found live: "too many clients"). The caps keep the worst case under the limit: two APIs at 8 and two
  # workers at 4 is 24, doubled while a rollout runs old and new pods side by side is 48, and the migration Jobs add 2
  # each. A larger database tier raises max_connections, and these can grow with it.
  api_database_pool                  = ";Maximum Pool Size=8"
  worker_database_pool               = ";Maximum Pool Size=4"
  migrator_database_pool             = ";Maximum Pool Size=2"
  account_database_connection_string = "Host=${local.database_host};Database=account;${local.app_database_credentials}"
  main_database_connection_string    = "Host=${local.database_host};Database=main;${local.app_database_credentials}"
  account_migrator_connection_string = "Host=${local.database_host};Database=account;${local.migrator_database_credentials}"
  main_migrator_connection_string    = "Host=${local.database_host};Database=main;${local.migrator_database_credentials}"
  # One blob-storage connection string per system, each carrying that system's storage key.
  blob_storage_connection_strings = {
    for system in local.storage_systems : system => "ServiceUrl=https://s3.${var.region}.scw.cloud;AccessKey=${scaleway_iam_api_key.storage[system].access_key};SecretKey=${scaleway_iam_api_key.storage[system].secret_key};ForcePathStyle=true;Region=${var.region};BucketPrefix=${var.prefix}-${var.environment}-"
  }
  signing_key                    = random_bytes.authentication_token_signing_key.base64
  data_protection_encryption_key = random_bytes.data_protection_encryption_key.base64

  # Path-style addressing (s3.<region>.scw.cloud/<bucket>) keeps every Object Storage call on the
  # one hostname the workloads' egress policies admit (cluster-ops/network-policies.yaml);
  # virtual-hosted URLs (<bucket>.s3.<region>.scw.cloud) are dropped there and hang at connect.

  # Secret NAME = .NET configuration key (double underscore flattens to a section separator).
  base_workload_secrets = {
    account-api = {
      ConnectionStrings__account-database = "${local.account_database_connection_string}${local.api_database_pool}"
      ConnectionStrings__blob-storage     = local.blob_storage_connection_strings["account"]
      # The back-office blob proxy resolves a SECOND keyed storage client by this connection name
      # (AddNamedBlobStorages("account-storage")); without it the proxy 500s on first use.
      ConnectionStrings__account-storage = local.blob_storage_connection_strings["account"]
      Authentication__TokenSigning__Key  = local.signing_key
      data-protection-encryption-key     = local.data_protection_encryption_key
      InternalTls__CertificatePem        = tls_locally_signed_cert.workload["account-api"].cert_pem
      InternalTls__KeyPem                = tls_private_key.workload["account-api"].private_key_pem
    }
    account-workers = {
      ConnectionStrings__account-database = "${local.account_database_connection_string}${local.worker_database_pool}"
      ConnectionStrings__blob-storage     = local.blob_storage_connection_strings["account"]
      Authentication__TokenSigning__Key   = local.signing_key
    }
    main-api = {
      ConnectionStrings__main-database  = "${local.main_database_connection_string}${local.api_database_pool}"
      ConnectionStrings__blob-storage   = local.blob_storage_connection_strings["main"]
      Authentication__TokenSigning__Key = local.signing_key
      data-protection-encryption-key    = local.data_protection_encryption_key
      InternalTls__CertificatePem       = tls_locally_signed_cert.workload["main-api"].cert_pem
      InternalTls__KeyPem               = tls_private_key.workload["main-api"].private_key_pem
    }
    main-workers = {
      ConnectionStrings__main-database = "${local.main_database_connection_string}${local.worker_database_pool}"
      ConnectionStrings__blob-storage  = local.blob_storage_connection_strings["main"]
    }
    # The dedicated migrator workloads (pre-upgrade Jobs): the SAME logical connection name as
    # the application, but their own Secret Manager path resolves it as the structure-owner
    # migrator role. The runtime workloads above resolve the same name as the readwrite app user.
    # The migrator owns every table it creates, so it also grants the app user access to them
    # (Database__ApplicationUser): the readwrite privilege covers only tables that existed when
    # it was set, which on a new environment is none.
    account-migrator = {
      ConnectionStrings__account-database = "${local.account_migrator_connection_string}${local.migrator_database_pool}"
      Database__ApplicationUser           = scaleway_rdb_user.app.name
    }
    main-migrator = {
      ConnectionStrings__main-database = "${local.main_migrator_connection_string}${local.migrator_database_pool}"
      Database__ApplicationUser        = scaleway_rdb_user.app.name
    }
    app-gateway = {
      Authentication__TokenSigning__Key = local.signing_key
      # The CA is public material, but the gateway reads it through Secret Manager like its other
      # configuration: a multi-line PEM must not travel as a chart value (it breaks values.yaml).
      InternalTls__CaPem          = tls_self_signed_cert.internal_ca.cert_pem
      InternalTls__CertificatePem = tls_locally_signed_cert.workload["app-gateway"].cert_pem
      InternalTls__KeyPem         = tls_private_key.workload["app-gateway"].private_key_pem
    }
  }

  # Every workload also pushes OTLP telemetry to the project Cockpit; the token comes from the
  # Cockpit resource and travels the same Secret Manager path as the workload's other secrets.
  workload_secrets = {
    for workload, secrets in local.base_workload_secrets :
    workload => merge(concat(
      [secrets],
      [{ Cockpit__Token = scaleway_cockpit_token.workload_telemetry.secret_key }]
    )...)
  }

  secret_entries = merge([
    for workload, secrets in local.workload_secrets : {
      for name, value in secrets : "${workload}/${name}" => {
        workload = workload
        name     = name
        value    = value
      }
    }
  ]...)
}

resource "scaleway_secret" "workload" {
  for_each   = local.secret_entries
  name       = each.value.name
  path       = "/${each.value.workload}"
  project_id = var.project_id
  tags       = local.tags
}

resource "scaleway_secret_version" "workload" {
  for_each  = local.secret_entries
  secret_id = scaleway_secret.workload[each.key].id
  data      = each.value.value
}

# One IAM application + policy + API key per workload; the policy only allows secrets under the
# workload's own path. There is no Kubernetes workload-identity federation on Scaleway, so the
# per-workload API key is the service identity and is delivered as a deploy-time secret.
resource "scaleway_iam_application" "workload" {
  for_each    = local.workload_secrets
  name        = "${var.prefix}-${var.environment}-${each.key}"
  description = "Service identity for the ${each.key} workload"
}

resource "scaleway_iam_policy" "workload" {
  for_each       = local.workload_secrets
  name           = "${var.prefix}-${var.environment}-${each.key}-secrets"
  application_id = scaleway_iam_application.workload[each.key].id
  rule {
    # ReadOnly only covers listing/metadata — reading the values requires the separate
    # SecretManagerSecretAccess set (found live: workloads listed all secrets and 403'd on every value).
    permission_set_names = ["SecretManagerReadOnly", "SecretManagerSecretAccess"]
    project_ids          = [var.project_id]
    condition            = "resource.name.startsWith(\"/${each.key}\") || !has(resource.id)"
  }

  # The account workloads (user sign-in and notifications) send transactional email through the
  # TEM HTTP API with this same key (the SMTP credentials are console-only). EmailFullAccess is
  # send-only: no domain admin.
  dynamic "rule" {
    for_each = startswith(each.key, "account-") ? [1] : []
    content {
      permission_set_names = ["TransactionalEmailEmailFullAccess"]
      project_ids          = [var.project_id]
    }
  }
}

resource "scaleway_iam_api_key" "workload" {
  for_each           = local.workload_secrets
  application_id     = scaleway_iam_application.workload[each.key].id
  description        = "Secret Manager key for the ${each.key} workload"
  expires_at         = var.workload_key_expiry
  default_project_id = var.project_id
}

# Transactional Email: the environment-specific sending domain — staging mail stays isolated
# from production's domain reputation, and the apex domain stays free for the production project
# to register. The offer subscription itself has no Terraform resource (read-only data source),
# so subscribing to the essential plan stays an imperative one-time step. The resource's ADDRESS
# keeps its historical "staging" label for every environment: renaming it would force a domain
# replacement (re-verification, DKIM re-check) with no benefit.
resource "scaleway_tem_domain" "staging" {
  name       = local.tem_domain_name
  accept_tos = true
  project_id = var.project_id
  region     = var.region
}

# Edge Services: the professional plan (wildcard support, 10 pipelines) plus one pipeline per
# public hostname in front of the TF-owned gateway LB. Origin connections use TLS (is_ssl) to a
# dedicated LB frontend per hostname: the edge only keeps ONE active backend stage per frontend,
# so both pipelines cannot share the app frontend (found live: the back-office stage sat
# inactive and its managed certificate never issued). The LB is a plain TCP passthrough — TLS
# is terminated by Envoy in the cluster — so a TCP frontend needs no certificate of its own;
# the edge's origin SNI passes through to Envoy, which presents the cert-manager certificate.
# The parked "auto" DNS stages reserve each pipeline's .svc.edge.scw.cloud endpoint — the
# user's CNAME targets and smoke-test hosts.
resource "scaleway_edge_services_plan" "main" {
  name       = "professional"
  project_id = var.project_id
}

locals {
  # The CCM creates the gateway LB's own frontends and backends; their ids arrive as variables
  # (captured after each environment's first deploy) because they belong to the cluster, not to
  # this module's resources.
  edge_pipelines = { for key, fqdn in local.edge_hostnames : key => { fqdn = fqdn } }
}

# Dedicated origin frontends for every pipeline but app (which keeps the CCM-managed 443
# frontend): the edge keeps one active backend stage per frontend. TCP passthrough to the same
# Envoy backend: Envoy terminates TLS and routes by the SNI the edge presents (the backend stage's
# domain_name).
locals {
  dedicated_origin_ports = { back-office = 8443 }
}

resource "scaleway_lb_frontend" "edge_origin" {
  for_each     = local.gateway_ready ? local.dedicated_origin_ports : {}
  lb_id        = scaleway_lb.gateway.id
  name         = "${var.prefix}-${var.environment}-${each.key}-https"
  inbound_port = each.value
  backend_id   = local.gateway_envoy_backend_id

  # The edge-only ACLs below are separate resources (one shape for these and the CCM's frontend).
  external_acls = true
}

moved {
  from = scaleway_lb_frontend.back_office
  to   = scaleway_lb_frontend.edge_origin["back-office"]
}

# Origin lockdown: every frontend an edge pipeline targets accepts connections from Scaleway Edge
# Services only, so nobody can reach Envoy on the LB's public IP and skip the WAF. Envoy routes by
# SNI, not by frontend, so every edge frontend needs it: a single open frontend would serve every
# hostname. The backend is TCP passthrough, so the source IP is the only thing an ACL can match.
#
# The Gateway's listeners (443 for the app pipeline, and the plain-HTTP 80 that serves only direct
# hits on the LB IP) are frontends the CCM creates, so their ids are read from the LB rather than
# captured by hand: a frontend the CCM recreates gets a new id, the plan then replaces its ACL, and
# the deploy applies that (after confirmation) in the same run. The ports are fixed, not discovered,
# so the plan stays computable while the LB itself has pending changes; any other frontend that
# appears on the LB fails the deploy's security check (gateway-frontends-edge-only).
#
# The CCM only replaces the ACLs it generates itself (named "<frontend-id>-lb-source-range-*",
# from the Service's loadBalancerSourceRanges), so these Terraform-owned ACLs survive its
# reconciliation. But setting loadBalancerSourceRanges on the gateway Service makes the CCM replace
# ALL ACLs on that frontend, these included. Never set it.
#
# The LB is looked up by name, not through scaleway_lb.gateway.id: on an environment's first plan
# the LB does not exist yet, and an unknown id would leave every frontend (and every for_each over
# them) unknown. By name, the first plan sees no LB and so no frontends.
data "scaleway_lbs" "gateway" {
  name       = "${var.prefix}-${var.environment}-gateway"
  zone       = local.zone
  project_id = var.project_id
}

data "scaleway_lb_frontends" "gateway" {
  for_each = toset([for lb in data.scaleway_lbs.gateway.lbs : lb.id if lb.name == "${var.prefix}-${var.environment}-gateway"])
  lb_id    = each.value
  zone     = local.zone # the API call's zone comes from this argument, not from the zoned lb_id
}

locals {
  gateway_listener_ports = { https = 443, http = 80 }
  gateway_frontends      = flatten([for lb_frontends in data.scaleway_lb_frontends.gateway : lb_frontends.frontends])

  edge_only_frontend_ids = merge(
    { for key, frontend in scaleway_lb_frontend.edge_origin : key => frontend.id },
    {
      for listener, port in local.gateway_listener_ports : "gateway-${listener}" => one([
        for frontend in local.gateway_frontends : frontend.id if frontend.inbound_port == port
      ]) if anytrue([for frontend in local.gateway_frontends : frontend.inbound_port == port])
    }
  )

  # The CCM's 443 frontend carries the app pipeline, and the Envoy TCP backend behind it also
  # serves the Terraform-owned edge frontends. Both are read from the LB instead of captured by
  # hand, so they exist only after the environment's first rollout created the gateway Service.
  # Until then the edge pipelines exist without stages, and the deploy's pass after the rollout
  # adds them. The data source returns the backend id bare, while frontends take it zone-qualified.
  gateway_app_frontend     = one([for frontend in local.gateway_frontends : frontend if frontend.inbound_port == 443])
  gateway_ready            = local.gateway_app_frontend != null
  gateway_envoy_backend_id = local.gateway_ready ? "${local.zone}/${local.gateway_app_frontend.backend_id}" : null
  edge_staged_pipelines    = local.gateway_ready ? local.edge_pipelines : {}
}

resource "scaleway_lb_acl" "edge_only" {
  for_each    = local.edge_only_frontend_ids
  frontend_id = each.value
  name        = "${var.prefix}-${var.environment}-${each.key}-edge-only"
  description = "Only Scaleway Edge Services may connect; everything else is refused before it reaches Envoy"
  index       = 0

  action {
    type = "deny"
  }

  match {
    ips_edge_services = true
    invert            = true
  }
}

resource "scaleway_edge_services_pipeline" "gateway" {
  for_each   = local.edge_pipelines
  name       = "${var.edge_pipeline_name_prefix}-${each.key}"
  project_id = var.project_id
}

resource "scaleway_edge_services_backend_stage" "gateway" {
  for_each    = local.edge_staged_pipelines
  pipeline_id = scaleway_edge_services_pipeline.gateway[each.key].id

  lb_backend_config {
    lb_config {
      id          = scaleway_lb.gateway.id
      zone        = local.zone
      frontend_id = contains(keys(scaleway_lb_frontend.edge_origin), each.key) ? scaleway_lb_frontend.edge_origin[each.key].id : local.gateway_app_frontend.id
      # With is_ssl, domain_name is both the origin SNI and the Host. Left empty, the visitor's
      # Host passes through but the edge sends no SNI, which Envoy's listeners need.
      domain_name   = local.edge_pipelines[each.key].fqdn
      is_ssl        = true
      has_websocket = true
    }
  }
}

# The WAF runs in blocking mode ("enable": requests matching the managed OWASP CRS ruleset are
# rejected at the edge before reaching the cluster) at paranoia level 1. The /api/track false
# positive that blocked this move is gone — the SPA's telemetry client sends a minimal JSON
# payload since the Application Insights SDK removal. Known false positives can be excepted with
# exclusion_rules by CRS rule ID.
resource "scaleway_edge_services_waf_stage" "gateway" {
  for_each         = local.edge_staged_pipelines
  pipeline_id      = scaleway_edge_services_pipeline.gateway[each.key].id
  mode             = "enable"
  paranoia_level   = 1
  backend_stage_id = scaleway_edge_services_backend_stage.gateway[each.key].id
}

resource "scaleway_edge_services_cache_stage" "gateway" {
  for_each     = local.edge_staged_pipelines
  pipeline_id  = scaleway_edge_services_pipeline.gateway[each.key].id
  fallback_ttl = 3600
  waf_stage_id = scaleway_edge_services_waf_stage.gateway[each.key].id
}

resource "scaleway_edge_services_tls_stage" "gateway" {
  for_each            = local.edge_staged_pipelines
  pipeline_id         = scaleway_edge_services_pipeline.gateway[each.key].id
  managed_certificate = true
  cache_stage_id      = scaleway_edge_services_cache_stage.gateway[each.key].id

  # A failed managed certificate is never retried, so the deploy requests a new one by replacing
  # this stage. The DNS stage still links the old stage, and the API refuses to delete a linked
  # stage (found live: a bare HTTP 500), so the replacement is created first, the DNS stage moves
  # to it in place, and only then is the old stage deleted.
  lifecycle {
    create_before_destroy = true
  }
}

resource "scaleway_edge_services_dns_stage" "gateway_custom" {
  for_each     = local.edge_staged_pipelines
  pipeline_id  = scaleway_edge_services_pipeline.gateway[each.key].id
  fqdns        = [local.edge_pipelines[each.key].fqdn]
  tls_stage_id = scaleway_edge_services_tls_stage.gateway[each.key].id
}

# The pipeline HEAD: the entry point linking the DNS stage chain to the backend. Without it the
# pipeline errors with pipeline_missing_head_stage and every stage sits inactive (found live:
# recreating a DNS stage silently dropped the head, and nothing re-attached it). The provider's
# head_stage Read is a no-op, so edge-side head drops are NOT detected as drift — this resource
# guarantees the re-attach whenever Terraform itself replaces the DNS stage. replace_triggered_by
# forces destroy+create (remove old head, then attach the new stage) rather than an in-place swap
# against a stage that no longer exists.
resource "scaleway_edge_services_head_stage" "gateway" {
  for_each      = local.edge_staged_pipelines
  pipeline_id   = scaleway_edge_services_pipeline.gateway[each.key].id
  head_stage_id = scaleway_edge_services_dns_stage.gateway_custom[each.key].id

  lifecycle {
    replace_triggered_by = [scaleway_edge_services_dns_stage.gateway_custom[each.key].id]
  }
}

resource "scaleway_edge_services_dns_stage" "gateway_auto" {
  for_each     = local.edge_staged_pipelines
  pipeline_id  = scaleway_edge_services_pipeline.gateway[each.key].id
  tls_stage_id = scaleway_edge_services_tls_stage.gateway[each.key].id
}


