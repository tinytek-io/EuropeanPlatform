output "terraform_state_bucket" {
  description = "Object Storage bucket that backs every later module's Terraform state"
  value       = scaleway_object_bucket.terraform_state.name
}

output "registry_endpoint" {
  description = "Registry endpoint to push images to, e.g. rg.fr-par.scw.cloud/<namespace>"
  value       = scaleway_registry_namespace.registry.endpoint
}

output "registry_namespace_id" {
  description = "Registry namespace ID — the deploy command prunes stale deploy tags through it"
  value       = scaleway_registry_namespace.registry.id
}

output "deploy_pipeline_access_key" {
  description = "Access key of the deploy pipeline API key (CI username)"
  value       = scaleway_iam_api_key.deploy_pipeline.access_key
}

output "deploy_pipeline_secret_key" {
  description = "Secret key of the deploy pipeline API key (CI secret; shown once)"
  value       = scaleway_iam_api_key.deploy_pipeline.secret_key
  sensitive   = true
}

output "environment_project_id" {
  description = "The per-environment Scaleway Project ID (feed this to the environment module's project_id)"
  value       = scaleway_account_project.environment.id
}

output "registry_pull_access_key" {
  description = "Access key of the cluster's read-only registry-pull identity (dockerconfigjson username-adjacent; the secret key authenticates)"
  value       = scaleway_iam_api_key.registry_pull.access_key
}

output "registry_pull_secret_key" {
  description = "Secret key of the cluster's read-only registry-pull identity — the deploy command writes it into the cluster's dockerconfigjson pull Secret (sensitive)"
  value       = scaleway_iam_api_key.registry_pull.secret_key
  sensitive   = true
}
