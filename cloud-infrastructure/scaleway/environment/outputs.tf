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

output "account_database_connection_string" {
  description = "Connection string for the account system database (sensitive)"
  value       = "Host=${scaleway_rdb_instance.main.private_network[0].ip};Port=${scaleway_rdb_instance.main.private_network[0].port};Database=account;Username=${var.database_admin_user};Password=${random_password.database.result};SSL Mode=Require"
  sensitive   = true
}

output "main_database_connection_string" {
  description = "Connection string for the main system database (sensitive)"
  value       = "Host=${scaleway_rdb_instance.main.private_network[0].ip};Port=${scaleway_rdb_instance.main.private_network[0].port};Database=main;Username=${var.database_admin_user};Password=${random_password.database.result};SSL Mode=Require"
  sensitive   = true
}

output "avatars_bucket_url" {
  description = "Path-style public URL of the avatars bucket (also the gateway's ACCOUNT_STORAGE_URL base)"
  value       = "https://s3.${var.region}.scw.cloud/${scaleway_object_bucket.avatars.name}"
}

output "logos_bucket_url" {
  description = "Path-style public URL of the logos bucket (also the gateway's MAIN_STORAGE_URL base)"
  value       = "https://s3.${var.region}.scw.cloud/${scaleway_object_bucket.logos.name}"
}

output "cluster_id" {
  description = "Kapsule cluster ID"
  value       = scaleway_k8s_cluster.kapsule.id
}

output "cluster_api_endpoint" {
  description = "Kapsule API server URL"
  value       = scaleway_k8s_cluster.kapsule.apiserver_url
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

output "gateway_lb_ip" {
  description = "Flexible IP of the gateway Load Balancer (stable across LB recreation)"
  value       = scaleway_lb_ip.gateway.ip_address
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
