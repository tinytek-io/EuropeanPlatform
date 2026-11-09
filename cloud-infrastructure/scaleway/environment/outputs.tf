output "region" {
  description = "Scaleway region of every regional resource; the deploy passes it to the AppHost, which substitutes it for the <region> placeholder in network-policies.yaml and set-gateway-lb-failover.sh derives the LB API region from it"
  value       = var.region
}

output "database_endpoint_ip" {
  description = "Managed PostgreSQL endpoint IP (private network)"
  value       = scaleway_rdb_instance.main.private_network[0].ip
}

output "database_endpoint_port" {
  description = "Managed PostgreSQL endpoint port"
  value       = scaleway_rdb_instance.main.private_network[0].port
}

output "database_admin_user" {
  description = "Managed PostgreSQL admin user"
  value       = var.database_admin_user
}

output "database_admin_password" {
  description = "Managed PostgreSQL admin password (sensitive)"
  value       = random_password.database.result
  sensitive   = true
}

output "object_storage_endpoint" {
  description = "Object Storage endpoint the app-gateway proxies avatars and logos from (the deploy passes it as Storage:Endpoint)"
  value       = "https://s3.${var.region}.scw.cloud"
}

output "bucket_prefix" {
  description = "Prefix of every environment bucket name; the applications keep logical bucket names (avatars, logos, data-protection) and the deploy passes this as Storage:BucketPrefix"
  value       = "${var.prefix}-${var.environment}-"
}

output "kubeconfig" {
  description = "Cluster kubeconfig (sensitive)"
  value       = scaleway_k8s_cluster.kapsule.kubeconfig[0].config_file
  sensitive   = true
}

output "workload_secret_manager_keys" {
  description = "Per-workload Secret Manager API keys (sensitive) — supply these as the deploy-time SCW_SECRET_KEY inputs"
  value       = { for workload, key in scaleway_iam_api_key.workload : workload => { access_key = key.access_key, secret_key = key.secret_key } }
  sensitive   = true
}

output "gateway_lb_ref" {
  description = "Zone-qualified LB id for the gateway Service's scw-loadbalancer-id annotation (cluster-ops adoption step)"
  value       = scaleway_lb.gateway.id
}

output "maintenance_website_endpoint" {
  description = "Website endpoint of the maintenance bucket — the LB failover host that set-gateway-lb-failover.sh wires onto the gateway's HTTP backend"
  value       = scaleway_object_bucket_website_configuration.maintenance.website_endpoint
}

output "cockpit_operator_query_token" {
  description = "Query-only Cockpit token for operators and tooling (sensitive)"
  value       = scaleway_cockpit_token.operator_query.secret_key
  sensitive   = true
}

output "cockpit_otlp_endpoints" {
  description = "Per-signal Cockpit OTLP endpoints (Metrics, Logs, Traces) for the workloads' telemetry exporters"
  value = {
    # Data source ids are region-qualified (fr-par/<uuid>); the OTLP push URL uses the bare uuid
    # plus the signal-specific path.
    Metrics = "https://${split("/", scaleway_cockpit_source.telemetry["metrics"].id)[1]}.metrics.cockpit.${var.region}.scw.cloud/otlp/v1/metrics"
    Logs    = "https://${split("/", scaleway_cockpit_source.telemetry["logs"].id)[1]}.logs.cockpit.${var.region}.scw.cloud/otlp/v1/logs"
    Traces  = "https://${split("/", scaleway_cockpit_source.telemetry["traces"].id)[1]}.traces.cockpit.${var.region}.scw.cloud/otlp/v1/traces"
  }
}

output "internal_ca_pem" {
  description = "The public internal CA certificate (PEM) that signs the workloads' in-cluster TLS certificates; the deploy passes it to the chart (Envoy validation) and the app-gateway"
  value       = tls_self_signed_cert.internal_ca.cert_pem
}

output "domain_name" {
  description = "The environment's zone on the public domain — the single source for the edge hostnames and the Transactional Email sending domain"
  value       = var.domain_name
}

output "edge_hostnames" {
  description = "Public hostname per edge pipeline (app and back-office), derived from domain_name; the deploy passes them to the AppHost as Hostnames__App / Hostnames__BackOffice"
  value       = local.edge_hostnames
}

# Bootstrap-owned values the deploy needs, passed through so the deploy reads only this module's
# state: the registry it pushes to and prunes, and the cluster's read-only pull identity.
output "registry_endpoint" {
  description = "Registry endpoint images are pushed to, e.g. rg.fr-par.scw.cloud/<namespace> (from the bootstrap)"
  value       = data.terraform_remote_state.bootstrap.outputs.registry_endpoint
}

output "registry_namespace_id" {
  description = "Registry namespace id the deploy prunes stale deploy tags through (from the bootstrap)"
  value       = data.terraform_remote_state.bootstrap.outputs.registry_namespace_id
}

output "registry_pull_secret_key" {
  description = "Secret key of the cluster's read-only registry-pull identity, which the deploy writes into the dockerconfigjson pull Secret (from the bootstrap)"
  value       = data.terraform_remote_state.bootstrap.outputs.registry_pull_secret_key
  sensitive   = true
}

output "staff_identity_client_secrets" {
  description = "The staff OIDC provider's client secret per clientId in the staff identity file — configure each tool with its id, its secret, and the issuer https://<back-office hostname> (discovery at /.well-known/openid-configuration)"
  value       = { for client_id, secret in random_password.staff_identity_client : client_id => secret.result }
  sensitive   = true
}
