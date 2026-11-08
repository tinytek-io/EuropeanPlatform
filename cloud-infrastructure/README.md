# Cloud Infrastructure

This folder contains the Terraform modules that provision the platform on [Scaleway](https://www.scaleway.com), used by Aspire's publish/deploy pipeline for the application topology.

## Structure

- `scaleway/bootstrap`: the one-time bootstrap tier — creates the per-environment Scaleway Project (e.g. `ep-stage`), the Object Storage bucket that holds every later module's Terraform state, the Container Registry namespace, and the operator, deploy pipeline and registry-pull identities. Its own state lives in the bucket it creates; see its [README](scaleway/README.md).
- `scaleway/environment`: the environment stack — VPC, managed PostgreSQL (one database per self-contained system), public-read Object Storage buckets for avatars/logos, and the Kapsule cluster (Cilium CNI for the Gateway API ingress). State lives in the bootstrap bucket with S3 conditional-write locking.

## Usage

```bash
dotnet run --project developer-cli -- bootstrap -e staging   # administrator key; idempotent
dotnet run --project developer-cli -- deploy -e staging      # the operator's scw profile the bootstrap wrote
```

See [scaleway/README.md](scaleway/README.md#usage) for the identities, the state layout, and CI.

The deploy command reads its inputs from the environment module's outputs (`terraform output`): the sensitive `kubeconfig`, the per-workload Secret Manager keys, the Cockpit OTLP endpoints, the internal CA, and the edge hostnames. Database and storage connection strings never leave Terraform as outputs — they reach the workloads through Secret Manager.

## Naming conventions

Resources are named `{prefix}-{environment}-{name}`, e.g. `ep-stage-database` and `ep-stage-avatars`, following the same convention as the previous cloud (where the resource group carried the environment, that role now belongs to the per-environment Scaleway Project). All resources are tagged with `environment:{stage|prod}` and `managed-by:terraform`.
