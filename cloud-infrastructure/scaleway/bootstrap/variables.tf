variable "prefix" {
  description = "Unique prefix for resource names, e.g. `pp` or `ppdemo` (2-6 characters)"
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{2,6}$", var.prefix))
    error_message = "The prefix must be 2-6 lowercase alphanumeric characters."
  }
}

variable "environment" {
  description = "Environment the bootstrap serves, e.g. `stage` or `prod` (max 5 characters)"
  type        = string
  default     = "stage"

  validation {
    condition     = can(regex("^[a-z0-9]{1,5}$", var.environment))
    error_message = "The environment must be 1-5 lowercase alphanumeric characters."
  }
}

variable "region" {
  description = "Scaleway region, e.g. `fr-par` or `nl-ams`"
  type        = string
  default     = "fr-par"
}

variable "pipeline_key_expiry" {
  description = "Expiry of the deploy pipeline API key (RFC3339). Change to rotate the key."
  type        = string
  default     = "2027-10-05T00:00:00Z"

  validation {
    condition     = can(regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", var.pipeline_key_expiry))
    error_message = "The expiry must be an RFC3339 UTC timestamp, e.g. 2027-10-05T00:00:00Z."
  }
}

variable "registry_pull_key_expiry" {
  description = "Expiry of the cluster registry-pull API key (RFC3339). Change to rotate."
  type        = string
  default     = "2027-10-05T00:00:00Z"

  validation {
    condition     = can(regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", var.registry_pull_key_expiry))
    error_message = "The expiry must be an RFC3339 UTC timestamp, e.g. 2027-10-05T00:00:00Z."
  }
}

variable "organization_id" {
  description = "Scaleway Organization ID; defaults to SCW_DEFAULT_ORGANIZATION_ID when unset"
  type        = string
  default     = null
}

variable "project_id" {
  description = "Scaleway Project ID the provider uses as its default; defaults to SCW_DEFAULT_PROJECT_ID when unset. Every resource here sets its project explicitly (the per-environment project this module creates, which also scopes the deploy pipeline and registry-pull IAM policies), so this only matters for the provider's own default."
  type        = string
  default     = null

  validation {
    condition     = var.project_id == null || can(regex("^[0-9a-f-]{36}$", var.project_id))
    error_message = "The project ID must be a Scaleway UUID."
  }
}

variable "operator_user_id" {
  description = "IAM user id of the Terraform operator. The state bucket's policy is the complete access spec for the bucket, so the operator must be listed or Terraform locks itself out of its own state. Supply it as TF_VAR_operator_user_id (the same value the environment module takes); it is never committed."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-f-]{36}$", var.operator_user_id))
    error_message = "The operator user ID must be a Scaleway UUID."
  }
}
