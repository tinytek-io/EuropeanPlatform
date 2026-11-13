# Remote state backend for the staging environment (partial configuration for the backend block in
# main.tf): the bucket the staging bootstrap created, in the region it lives in.
bucket = "ep-stage-fr-par-terraform-state"
endpoints = {
  s3 = "https://s3.fr-par.scw.cloud"
}
