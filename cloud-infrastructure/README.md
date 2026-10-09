# Cloud Infrastructure

This folder contains the Terraform modules that provision the platform on [Scaleway](https://www.scaleway.com), used by Aspire's publish/deploy pipeline for the application topology.

## Structure

- `scaleway/bootstrap`: the one-time bootstrap tier — creates the per-environment Scaleway Project (e.g. `pp-stage`), the Object Storage bucket that holds every later module's Terraform state, the Container Registry namespace, and the deploy pipeline's IAM application, policy, and API key. Runs with local state by design; see its [README](scaleway/README.md).
- `scaleway/environment`: the environment stack — VPC, managed PostgreSQL (one database per self-contained system), public-read Object Storage buckets for avatars/logos, and the Kapsule cluster (Cilium CNI for the Gateway API ingress). State lives in the bootstrap bucket with S3 conditional-write locking.

## Usage

```bash
cd cloud-infrastructure/scaleway/bootstrap
terraform init && terraform apply -var prefix=pp -var environment=stage -var project_id=<default project id>

cd ../environment
export AWS_ACCESS_KEY_ID=<bootstrap output: deploy_pipeline_access_key>
export AWS_SECRET_ACCESS_KEY=<bootstrap output: deploy_pipeline_secret_key>
terraform init && terraform apply -var prefix=pp -var environment=stage \
  -var project_id=<bootstrap output: environment_project_id>
```

> **Always set `SCW_DEFAULT_PROJECT_ID` when running plan/apply against the environment module.**
> Scaleway's S3 API acts through the API key's preferred project: with an operator key whose
> preferred project is the organization default, refreshes of `scaleway_object` resources fail
> with `GetObjectTagging` 403 until the variable is set.

Connection strings and bucket URLs for the deploy-time secrets come from the environment module's outputs (`terraform output`); the Kapsule kubeconfig is the sensitive `kubeconfig` output.

## Naming conventions

Resources are named `{prefix}-{environment}-{name}`, e.g. `pp-stage-database` and `pp-stage-avatars`, following the same convention as the previous cloud (where the resource group carried the environment, that role now belongs to the per-environment Scaleway Project). All resources are tagged with `environment:{stage|prod}` and `managed-by:terraform`.
