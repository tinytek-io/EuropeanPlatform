# The staff identity provider: authentik (MIT core) at auth.<domain_name>, signing staff in to the
# back-office over OIDC with an emailed one-time code or a passkey — never a password. Its
# configuration (groups, flows, the back-office client) and the staff list are blueprints in
# cloud-infrastructure/authentik; this file provisions what authentik needs from the platform:
# a database, generated secrets, and an SMTP identity for the sign-in codes. The edge pipeline and
# origin frontend come from local.edge_hostnames. Everything here exists only with
# staff_identity_enabled.
#
# authentik reads its settings from environment variables, not Secret Manager, so the secrets
# reach it the way the workload Secret Manager keys reach the workloads: a sensitive output the
# deploy command passes as deploy-time parameters into the chart.

locals {
  staff_identity_count = var.staff_identity_enabled ? 1 : 0
}

# Its own database and user on the shared instance: authentik owns its schema (it migrates on
# start), so the user holds every privilege on that database and none on the others.
resource "scaleway_rdb_database" "authentik" {
  count       = local.staff_identity_count
  instance_id = scaleway_rdb_instance.main.id
  name        = "authentik"
}

resource "random_password" "authentik_database_user" {
  count   = local.staff_identity_count
  length  = 32
  special = true
}

resource "scaleway_rdb_user" "authentik" {
  count       = local.staff_identity_count
  instance_id = scaleway_rdb_instance.main.id
  name        = "${var.database_admin_user}_authentik"
  password    = random_password.authentik_database_user[0].result
  is_admin    = false
}

resource "scaleway_rdb_privilege" "authentik" {
  count         = local.staff_identity_count
  instance_id   = scaleway_rdb_instance.main.id
  database_name = scaleway_rdb_database.authentik[0].name
  user_name     = scaleway_rdb_user.authentik[0].name
  permission    = "all"
}

# authentik's own signing/encryption secret, and the back-office client secret (the same value
# reaches the back-office through its Secret Manager path, local.staff_identity_workload_secrets).
resource "random_password" "authentik_secret_key" {
  count   = local.staff_identity_count
  length  = 64
  special = false
}

resource "random_password" "back_office_oidc_client_secret" {
  count   = local.staff_identity_count
  length  = 48
  special = false
}

# Sign-in codes go out through Transactional Email over SMTP (authentik speaks SMTP only): the
# username is the project id, the password the secret key of an IAM identity that may only send.
resource "scaleway_iam_application" "staff_identity_mail" {
  count       = local.staff_identity_count
  name        = "${var.prefix}-${var.environment}-staff-identity-mail"
  description = "Sends the staff sign-in codes through Transactional Email (SMTP)"
}

resource "scaleway_iam_policy" "staff_identity_mail" {
  count          = local.staff_identity_count
  name           = "${var.prefix}-${var.environment}-staff-identity-mail"
  application_id = scaleway_iam_application.staff_identity_mail[0].id
  rule {
    permission_set_names = ["TransactionalEmailEmailFullAccess"]
    project_ids          = [var.project_id]
  }
}

resource "scaleway_iam_api_key" "staff_identity_mail" {
  count              = local.staff_identity_count
  application_id     = scaleway_iam_application.staff_identity_mail[0].id
  description        = "SMTP credential for the staff sign-in codes"
  expires_at         = var.workload_key_expiry
  default_project_id = var.project_id
}

output "staff_identity" {
  description = "Deploy-time settings for the authentik staff identity provider (null when staff_identity_enabled is off). The deploy command passes them as chart parameters."
  sensitive   = true
  value = var.staff_identity_enabled ? {
    url                       = "https://${local.edge_hostnames["auth"]}"
    secret_key                = random_password.authentik_secret_key[0].result
    database_host             = scaleway_rdb_instance.main.private_network[0].ip
    database_port             = tostring(scaleway_rdb_instance.main.private_network[0].port)
    database_name             = scaleway_rdb_database.authentik[0].name
    database_user             = scaleway_rdb_user.authentik[0].name
    database_password         = random_password.authentik_database_user[0].result
    smtp_host                 = "smtp.tem.scaleway.com"
    smtp_port                 = "587"
    smtp_username             = var.project_id
    smtp_password             = scaleway_iam_api_key.staff_identity_mail[0].secret_key
    smtp_from                 = "staff-sign-in@${local.tem_domain_name}"
    back_office_client_secret = random_password.back_office_oidc_client_secret[0].result
  } : null
}
