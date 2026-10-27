# Staging tier — the exemplar environment configuration.
#
# One module serves every tier; the deploy command selects this file by the Terraform environment
# name (stage) for its convergence plan, and manual applies pass the same -var-file. The values
# here MUST stay identical to the module defaults while staging is the only live environment —
# they are spelled out anyway so the tier contract is readable in one place and a future
# production tier file has a complete example to diff against.
#
# prefix, environment, and project_id stay deploy-command -var inputs (they come from the
# bootstrap module's outputs) and are intentionally not repeated here.

node_count       = 2 # Two nodes so a Kapsule node reboot or drain never drops the whole serving path
node_type        = "PLAY2-NANO"

database_node_type = "db-dev-s" # No SLA — production moves to db-s or larger
database_ha        = false     # Staging tolerates a database host failure; production flips this

backup_schedule_frequency = 24 # Automated backups: once a day...
backup_schedule_retention = 7  # ...kept seven days (Scaleway's default cadence, pinned explicitly)

# The environment's zone on the public domain — the single domain knob: the edge hostnames
# derive as app.<domain> / back-office.<domain>, and Transactional Email sends from the zone.
domain_name = "staging.europeanplatform.net"

edge_pipeline_name_prefix = "staging"

# gateway_lb_frontend_id, gateway_lb_backend_id, and operator_user_id are cluster- and
# operator-specific: they come from TF_VAR_* environment variables and are never committed
# (see the cluster-ops README for capturing the LB ids).
