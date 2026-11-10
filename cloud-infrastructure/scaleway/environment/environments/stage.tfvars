# Staging tier — the exemplar environment configuration.
#
# One module serves every tier; the deploy command selects this file by the Terraform environment
# name (stage) for its convergence plan, and manual applies pass the same -var-file. The sizing
# and durability values match the module defaults while staging is the only live environment —
# they are spelled out anyway so the tier contract is readable in one place and a future
# production tier file has a complete example to diff against. domain_name and
# edge_pipeline_name_prefix have no module default: every tier file must set them.
#
# environment and project_id stay -var inputs: the deploy and bootstrap commands pass the
# environment name and the project they resolve by name (<prefix>-<environment>).

# The infrastructure prefix every resource name starts with (<prefix>-<environment>-...). The
# bootstrap and deploy commands read it from here. Bucket and registry names are global across
# all Scaleway customers, so each fork picks its own (EuropeanPlatform: ep), and a live
# environment never changes it, because every resource would be renamed.
prefix = "ep"

# The fixed monthly cost the environment may reach (euros, excluding VAT; storage, traffic and emails are billed
# by use on top). The deploy prices every infrastructure change against it and asks before exceeding it.
monthly_budget_eur = 100

node_count = 1 # Staging is for development: a node replacement or upgrade may briefly interrupt it
node_type  = "PLAY2-NANO"

database_node_type = "db-dev-s" # No SLA — production moves to db-s or larger
database_ha        = false      # Staging tolerates a database host failure; production flips this

backup_schedule_frequency = 24 # Automated backups: once a day...
backup_schedule_retention = 7  # ...kept seven days (Scaleway's default cadence, pinned explicitly)

# The environment's zone on the public domain — the single domain knob: the edge hostnames
# derive as app.<domain> / back-office.<domain>, and Transactional Email sends from the zone.
# To deploy under another domain (a test domain, say), set domain_name in a git-ignored
# stage.local.tfvars beside this file; the deploy layers it over this one.
domain_name = "staging.europeanplatform.net"

edge_pipeline_name_prefix = "staging"
