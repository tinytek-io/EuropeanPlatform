# Staging tier — the exemplar environment configuration.
#
# One module serves every tier; the deploy command selects this file by the Terraform environment
# name (stage) for its convergence plan, and manual applies pass the same -var-file. Sizing is
# not set here: the environment type selects it from the platform's profile matrix.
# environment_type, domain_name and edge_pipeline_name_prefix have no module default: every
# tier file must set them.
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

# What the environment is for. It selects the sizing (cluster, database, backups, network, blob versioning, workload
# replicas and memory) from ../../environment-profiles.yaml, and it is fixed: the deploy refuses to change an existing
# environment's type. A single value can still be overridden here (node_type, node_count, database_node_type,
# database_ha, backup_schedule_frequency, backup_schedule_retention, load_balancer_type, nat_gateway_type,
# blob_versioning) when this environment must differ from its type.
environment_type = "staging"

# The environment's zone on the public domain — the single domain knob: the edge hostnames
# derive as app.<domain> / back-office.<domain>, and Transactional Email sends from the zone.
# To deploy under another domain (a test domain, say), set domain_name in a git-ignored
# stage.local.tfvars beside this file; the deploy layers it over this one.
domain_name = "staging.europeanplatform.net"

edge_pipeline_name_prefix = "staging"
