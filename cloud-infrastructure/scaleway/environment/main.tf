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
  }

  # Remote state lives in the bucket the bootstrap module created, using Scaleway's S3
  # conditional-write locking. Credentials come from the deploy pipeline API key:
  #   export AWS_ACCESS_KEY_ID=$(pipeline access key)
  #   export AWS_SECRET_ACCESS_KEY=$(pipeline secret key)
  backend "s3" {
    bucket       = "pp-stage-fr-par-terraform-state"
    key          = "platformplatform/environment.tfstate"
    region       = "us-east-1"
    use_lockfile = true
    endpoints = {
      s3 = "https://s3.fr-par.scw.cloud"
    }
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
  zone   = "${var.region}-1"
}

variable "prefix" {
  description = "Unique prefix shared with the bootstrap module, e.g. `pp`"
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

# The per-environment project created by the bootstrap module (e.g. `pp-stage`).
variable "project_id" {
  description = "Scaleway Project ID from the bootstrap module's environment_project_id output"
  type        = string
}

variable "operator_user_id" {
  description = "IAM user id of the Terraform operator. A bucket policy is the complete access spec for its bucket, so the operator must be listed or Terraform locks itself out. Supply it as TF_VAR_operator_user_id; it is never committed."
  type        = string
}

variable "gateway_lb_frontend_id" {
  description = "Id of the CCM-created LB 443 frontend the app pipeline's edge backend stage targets. Cluster-specific: capture it after the environment's first deploy (see cluster-ops README) and supply it as TF_VAR_gateway_lb_frontend_id; it is never committed."
  type        = string
}

variable "gateway_lb_backend_id" {
  description = "Zone-qualified id of the CCM-created Envoy TCP backend the edge backend stages target. Same capture flow as the frontend id; supply it as TF_VAR_gateway_lb_backend_id."
  type        = string
}

variable "kubernetes_version" {
  description = "Kapsule Kubernetes version; 1.37 ships Cilium 1.20 (Gateway API ExternalAuth available)"
  type        = string
  default     = "1.37.0"
}

variable "node_type" {
  description = "Pool node type. PLAY2-NANO (2 vCPU / 4 GB, ~0.028 EUR/h) fits the staging workloads; DEV1-S (~0.009 EUR/h) is the review-environment tier."
  type        = string
  default     = "PLAY2-NANO"
}

variable "node_count" {
  description = "Pool size. One node is enough for staging; use at least two in production so workloads stay up while Kapsule reboots a node for upgrades."
  type        = number
  default     = 1
}

variable "database_node_type" {
  description = "Managed PostgreSQL node type. db-dev-s (2 vCPU / 2 GB) is the development tier: fine for staging and a starting point for production, with no SLA. Production should move to db-s or larger."
  type        = string
  default     = "db-dev-s"
}

variable "database_admin_user" {
  description = "Name of the auto-created RDB admin user"
  type        = string
  default     = "platformplatform"
}

locals {
  # Most resources take tags as a string list; object buckets take a string map.
  tags = ["environment:${var.environment}", "managed-by:terraform"]
  bucket_tags = {
    environment = var.environment
    managed-by  = "terraform"
  }
}

# All workloads and the database share one private network; the database is reachable from the
# cluster, the public endpoint stays for operator access.
resource "scaleway_vpc_private_network" "vpc" {
  name       = "${var.prefix}-${var.environment}"
  project_id = var.project_id
  tags       = local.tags
}

# The public Load Balancer in front of the Cilium Envoy gateway. Terraform owns the LB shell
# and its flexible IP (which survives LB recreation); the Kapsule cloud-controller-manager
# creates the frontends and backends on it through the Service annotations
# (scw-loadbalancer-id + scw-loadbalancer-externally-managed) — see cluster-ops. The name MUST
# NOT start with the cluster id: the CCM refuses to manage such LBs in externally-managed mode.
resource "scaleway_lb_ip" "gateway" {
  zone       = "fr-par-1"
  project_id = var.project_id
  tags       = local.tags
}

resource "scaleway_lb" "gateway" {
  zone       = "fr-par-1"
  project_id = var.project_id
  name       = "${var.prefix}-${var.environment}-gateway"
  type       = "lb-s"
  ip_ids     = [scaleway_lb_ip.gateway.id]
  tags       = local.tags
}

# Managed PostgreSQL: one instance, one database per self-contained system. The databases are
# isolated from each other (no shared schema), which keeps the system boundaries hard and makes
# per-system pg_dump restores trivial. Connection string outputs feed the deploy-time secrets.
resource "scaleway_rdb_instance" "main" {
  name          = "${var.prefix}-${var.environment}-database"
  project_id    = var.project_id
  engine        = "PostgreSQL-17"
  node_type     = var.database_node_type
  is_ha_cluster = false
  user_name     = var.database_admin_user
  password      = random_password.database.result
  tags          = local.tags

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
      # Both statements are needed: anonymous public reads, and the workload's storage
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
          SCW = "application_id:${scaleway_iam_application.storage.id}"
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
        # The Terraform operator's IAM user (the owner). With a policy attached, the policy is
        # the COMPLETE access spec for the bucket — IAM permissions alone no longer grant
        # anything — so the operator must be listed or Terraform itself is locked out
        # (found live: refresh started failing with AccessDenied on GetBucketCors).
        Effect = "Allow"
        Principal = {
          SCW = "user_id:${var.operator_user_id}"
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
          SCW = "application_id:${scaleway_iam_application.storage.id}"
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
        # The Terraform operator's IAM user (the owner). With a policy attached, the policy is
        # the COMPLETE access spec for the bucket — IAM permissions alone no longer grant
        # anything — so the operator must be listed or Terraform itself is locked out
        # (found live: refresh started failing with AccessDenied on GetBucketCors).
        Effect = "Allow"
        Principal = {
          SCW = "user_id:${var.operator_user_id}"
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
    # Create the replacement before destroying the old bucket: the keyring blobs are copied
    # across during a rename (see the decision notes) and a gap between buckets would have every
    # API mint and validate against an empty ring. The provider has no force_destroy (2.84), so a
    # rename with content requires hand-emptying the old bucket; the keyring itself is recreatable
    # by the app (the key-ring owner generates a fresh ring on an empty bucket) at the cost of one
    # token invalidation — never data loss.
    create_before_destroy = true
  }
}

# Static maintenance page served as the Load Balancer's failover target: when all of the LB's
# backends are down (cluster unreachable), the LB redirects to this bucket website instead of a
# bare 503. The page content is managed here so it stays versioned with the infrastructure.
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

# The public-read bucket policies are applied by the application's S3 client when it creates the
# buckets on first use: Scaleway's S3 API always acts through the API key's preferred project, so
# policy writes must come from a key whose preferred project is this environment's project rather
# than from whoever runs Terraform.

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

action "scaleway_cockpit_grafana_sync_data_sources" "sync" {
  config {
    project_id = var.project_id
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
  cluster_id          = scaleway_k8s_cluster.kapsule.id
  name                = "default"
  node_type           = var.node_type
  size                = var.node_count
  autoscaling         = false
  zone                = "${var.region}-1"
  wait_for_pool_ready = true
  tags                = local.tags
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

# A dedicated identity for the S3 access embedded in the blob-storage connection strings — NOT the
# deploy pipeline key, which is also allowed to write the Terraform state bucket.
resource "scaleway_iam_application" "storage" {
  name        = "${var.prefix}-${var.environment}-storage"
  description = "Object storage access for workloads reading/writing avatars and logos"
}

resource "scaleway_iam_policy" "storage" {
  name           = "${var.prefix}-${var.environment}-storage"
  application_id = scaleway_iam_application.storage.id
  rule {
    permission_set_names = ["ObjectStorageFullAccess"]
    project_ids          = [var.project_id]
  }
}

resource "scaleway_iam_api_key" "storage" {
  application_id     = scaleway_iam_application.storage.id
  description        = "API key for workload object storage (embedded in the blob-storage connection string)"
  expires_at         = var.workload_key_expiry
  default_project_id = var.project_id
}

locals {
  account_database_connection_string = "Host=${scaleway_rdb_instance.main.private_network[0].ip};Port=${scaleway_rdb_instance.main.private_network[0].port};Database=account;Username=${var.database_admin_user};Password=${random_password.database.result};SSL Mode=Require"
  main_database_connection_string    = "Host=${scaleway_rdb_instance.main.private_network[0].ip};Port=${scaleway_rdb_instance.main.private_network[0].port};Database=main;Username=${var.database_admin_user};Password=${random_password.database.result};SSL Mode=Require"
  blob_storage_connection_string     = "ServiceUrl=https://s3.${var.region}.scw.cloud;AccessKey=${scaleway_iam_api_key.storage.access_key};SecretKey=${scaleway_iam_api_key.storage.secret_key};ForcePathStyle=false;Region=${var.region};BucketPrefix=${var.prefix}-${var.environment}-"
  signing_key                        = random_bytes.authentication_token_signing_key.base64
  data_protection_encryption_key     = random_bytes.data_protection_encryption_key.base64

  # Secret NAME = .NET configuration key (double underscore flattens to a section separator).
  base_workload_secrets = {
    account-api = {
      ConnectionStrings__account-database = local.account_database_connection_string
      ConnectionStrings__blob-storage     = local.blob_storage_connection_string
      # The back-office blob proxy resolves a SECOND keyed storage client by this connection name
      # (AddNamedBlobStorages("account-storage")); without it the proxy 500s on first use.
      ConnectionStrings__account-storage = local.blob_storage_connection_string
      Authentication__TokenSigning__Key  = local.signing_key
      data-protection-encryption-key     = local.data_protection_encryption_key
    }
    account-workers = {
      ConnectionStrings__account-database = local.account_database_connection_string
      ConnectionStrings__blob-storage     = local.blob_storage_connection_string
      Authentication__TokenSigning__Key   = local.signing_key
    }
    main-api = {
      ConnectionStrings__main-database  = local.main_database_connection_string
      ConnectionStrings__blob-storage   = local.blob_storage_connection_string
      Authentication__TokenSigning__Key = local.signing_key
      data-protection-encryption-key    = local.data_protection_encryption_key
    }
    main-workers = {
      ConnectionStrings__main-database = local.main_database_connection_string
      ConnectionStrings__blob-storage  = local.blob_storage_connection_string
    }
    app-gateway = {
      Authentication__TokenSigning__Key = local.signing_key
    }
  }

  # Every workload also pushes OTLP telemetry to the project Cockpit; the token comes from the
  # Cockpit resource and travels the same Secret Manager path as the workload's other secrets.
  workload_secrets = {
    for workload, secrets in local.base_workload_secrets :
    workload => merge(secrets, { Cockpit__Token = scaleway_cockpit_token.workload_telemetry.secret_key })
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

  # The account workloads send transactional email through the TEM HTTP API with this same
  # key (the SMTP credentials are console-only). EmailFullAccess is send-only: no domain admin.
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
# so subscribing to the essential plan stays an imperative one-time step.
resource "scaleway_tem_domain" "staging" {
  name       = "staging.tinytek.dev"
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
  # because they belong to the cluster, not to this module's resources.
  edge_app_frontend_id  = var.gateway_lb_frontend_id # LB 443 frontend (CCM-managed)
  edge_envoy_backend_id = var.gateway_lb_backend_id  # Envoy TCP backend (CCM-managed, zone-localized id)
  edge_pipelines = {
    app = {
      fqdn = "app.staging.tinytek.dev"
    }
    back-office = {
      fqdn = "back-office.staging.tinytek.dev"
    }
  }
}

# Dedicated origin frontend for the back-office pipeline (app keeps the CCM-managed 443
# frontend). TCP passthrough to the same Envoy backend: Envoy terminates TLS and routes by
# the SNI the edge presents (the backend stage's domain_name).
resource "scaleway_lb_frontend" "back_office" {
  lb_id        = scaleway_lb.gateway.id
  name         = "${var.prefix}-${var.environment}-back-office-https"
  inbound_port = 8443
  backend_id   = local.edge_envoy_backend_id
}

resource "scaleway_edge_services_pipeline" "gateway" {
  for_each   = local.edge_pipelines
  name       = "staging-${each.key}"
  project_id = var.project_id
}

resource "scaleway_edge_services_backend_stage" "gateway" {
  for_each    = local.edge_pipelines
  pipeline_id = scaleway_edge_services_pipeline.gateway[each.key].id

  lb_backend_config {
    lb_config {
      id          = scaleway_lb.gateway.id
      zone        = "fr-par-1"
      frontend_id = each.key == "back-office" ? scaleway_lb_frontend.back_office.id : local.edge_app_frontend_id
      # With is_ssl the edge requires an explicit origin SNI/Host; pass-through backends
      # (empty domain_name) leave the pipeline unconfigurable.
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
  for_each         = local.edge_pipelines
  pipeline_id      = scaleway_edge_services_pipeline.gateway[each.key].id
  mode             = "enable"
  paranoia_level   = 1
  backend_stage_id = scaleway_edge_services_backend_stage.gateway[each.key].id
}

resource "scaleway_edge_services_cache_stage" "gateway" {
  for_each     = local.edge_pipelines
  pipeline_id  = scaleway_edge_services_pipeline.gateway[each.key].id
  fallback_ttl = 3600
  waf_stage_id = scaleway_edge_services_waf_stage.gateway[each.key].id
}

resource "scaleway_edge_services_tls_stage" "gateway" {
  for_each            = local.edge_pipelines
  pipeline_id         = scaleway_edge_services_pipeline.gateway[each.key].id
  managed_certificate = true
  cache_stage_id      = scaleway_edge_services_cache_stage.gateway[each.key].id
}

resource "scaleway_edge_services_dns_stage" "gateway_custom" {
  for_each     = local.edge_pipelines
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
  for_each      = local.edge_pipelines
  pipeline_id   = scaleway_edge_services_pipeline.gateway[each.key].id
  head_stage_id = scaleway_edge_services_dns_stage.gateway_custom[each.key].id

  lifecycle {
    replace_triggered_by = [scaleway_edge_services_dns_stage.gateway_custom[each.key].id]
  }
}

resource "scaleway_edge_services_dns_stage" "gateway_auto" {
  for_each     = local.edge_pipelines
  pipeline_id  = scaleway_edge_services_pipeline.gateway[each.key].id
  tls_stage_id = scaleway_edge_services_tls_stage.gateway[each.key].id
}


