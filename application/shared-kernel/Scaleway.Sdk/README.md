# Scaleway.Sdk

A typed .NET client for the Scaleway API, generated from Scaleway's own published OpenAPI schemas.

**It has no Aspire dependency.** That is the point of the package: a workload running in a container, a deploy
bootstrap, and the `Aspire.Hosting.Scaleway` hosting package all consume the same typed surface without the
hosting framework coming along for the ride. An architecture test asserts the assembly references no Aspire
assembly, so the property fails loudly rather than eroding.

Version history: [CHANGELOG.md](./CHANGELOG.md). Currently `0.1.0-preview` and never released — minor releases
may break API compatibility (`BREAKING:` prefix in the `Changed` section).

## Quick Start

Credentials come from the standard `SCW_*` environment variables, validated on the way in rather than yielding
nulls that fail several calls later:

```csharp
using Scaleway.Sdk;

using var scaleway = new ScalewayClient(ScalewayCredentialConfig.FromEnvironment());

// IAM is organization-scoped, so it hangs off the account client.
var applications = await scaleway.Iam.ListApplicationsAsync();

// Everything else lives inside a project. Narrowing returns a client whose operations
// fill project_id and the default region themselves.
var project = scaleway.WithProject(ProjectId.Parse("11111111-2222-3333-4444-555555555555"));

var instances = await project.Databases.ListInstancesAsync(new ListInstancesRequest { Name = "postgres" });
foreach (var instance in instances)
{
    Console.WriteLine($"{instance.Name} ({instance.Engine}) is {instance.Status}");
}
```

Every field Scaleway declares as optional is nullable here, because that is what the schema says. The generated
models do not narrow it away.

The project requirement is carried by the type, not checked at runtime: `ScalewayProjectClient` is reachable
only through `ScalewayClient.WithProject`, so a project-scoped call cannot be written without a project.

Region-scoped operations take an optional `region` argument and fall back to the client's `DefaultRegion`:

```csharp
var amsterdam = await project.Databases.ListInstancesAsync(region: "nl-ams");
```

Listing pages automatically. `ScalewayApiClient` walks up to 100 pages of 100 items and throws past that rather
than returning a silently truncated list.

### Beyond the generated clients

Two hand-written clients wrap paths where the generated surface is awkward to use directly:

| Client | Why it is hand-written |
|---|---|
| `ScalewaySecretClient` | Read/write helpers over Secret Manager: find-by-name, read latest version, create-or-add-version. |
| `ScalewayPricingCatalogClient` | The public product catalog, with paging and a 24-hour on-disk cache. |

Object Storage is also in the package — it is S3-compatible and publishes no OpenAPI schema, so it is
hand-written with its own SigV4 signing — but it is **internal**, reachable only by the hosting package and the
tests. Making it public is a deliberate decision nobody has needed yet, not an oversight; until then it is not
part of this package's supported surface.

Errors surface as three exception types and only three: `ScalewayApiException` (carrying the HTTP status and
Scaleway's own error code), `ScalewayTransientException`, and `ScalewayResourceAlreadyExistsException`.

## Coverage

Everything below is rendered from [`Generated/manifest.json`](./Generated/manifest.json) by the generator. It is
not maintained by hand, and a test fails if it stops matching the manifest — a coverage claim that can drift
upward on its own is the failure this package went out of its way to make impossible.

<!-- coverage:start — generated from Generated/manifest.json; do not edit by hand -->

**10 services, 19 resource groups, 92 operations** — 86 on resource groups plus 6 declared actions.

| Service | API version | Namespace | Entry point | Actions |
|---|---|---|---|---|
| `iam` | `v1alpha1` | `Scaleway.Sdk.Iam.V1Alpha1` | `ScalewayClient.Iam` | — |
| `k8s` | `v1` | `Scaleway.Sdk.K8s.V1` | `ScalewayProjectClient.Kubernetes` | `GetClusterKubeConfig` |
| `rdb` | `v1` | `Scaleway.Sdk.Rdb.V1` | `ScalewayProjectClient.Databases` | `GetInstanceCertificate` |
| `redis` | `v1` | `Scaleway.Sdk.Redis.V1` | `ScalewayProjectClient.Redis` | — |
| `registry` | `v1` | `Scaleway.Sdk.Registry.V1` | `ScalewayProjectClient.Registry` | — |
| `secret` | `v1beta1` | `Scaleway.Sdk.Secret.V1Beta1` | `ScalewayProjectClient.Secrets` | `AccessSecretVersion` |
| `container` | `v1` | `Scaleway.Sdk.ServerlessContainer.V1` | `ScalewayProjectClient.Containers` | — |
| `jobs` | `v1alpha2` | `Scaleway.Sdk.Jobs.V1Alpha2` | `ScalewayProjectClient.Jobs` | `StopJobRun` |
| `tem` | `v1alpha1` | `Scaleway.Sdk.Tem.V1Alpha1` | `ScalewayProjectClient.Tem` | `GetDomainLastStatus`, `CheckDomain` |
| `vpc` | `v2` | `Scaleway.Sdk.Vpc.V2` | `ScalewayProjectClient.Vpc` | — |

| Service | Resource group | Tier | Operations |
|---|---|---|---|
| `iam` | `Application` | RoundTripValidated | `ListApplications`, `CreateApplication`, `GetApplication`, `UpdateApplication`, `DeleteApplication` |
| `iam` | `Policy` | RoundTripValidated | `ListPolicies`, `CreatePolicy`, `GetPolicy`, `UpdatePolicy`, `DeletePolicy` |
| `iam` | `APIKey` | RoundTripValidated | `ListAPIKeys`, `CreateAPIKey`, `GetAPIKey`, `UpdateAPIKey`, `DeleteAPIKey` |
| `k8s` | `Cluster` | StructurallyConformant | `ListClusters`, `CreateCluster`, `GetCluster`, `UpdateCluster`, `DeleteCluster` |
| `k8s` | `Version` | StructurallyConformant | `ListVersions`, `GetVersion` |
| `rdb` | `Instance` | RoundTripValidated | `ListInstances`, `CreateInstance`, `GetInstance`, `UpdateInstance`, `DeleteInstance` |
| `redis` | `Cluster` | RoundTripValidated | `ListClusters`, `CreateCluster`, `GetCluster`, `UpdateCluster`, `DeleteCluster` |
| `registry` | `Namespace` | RoundTripValidated | `ListNamespaces`, `CreateNamespace`, `GetNamespace`, `UpdateNamespace`, `DeleteNamespace` |
| `secret` | `Secret` | RoundTripValidated | `ListSecrets`, `CreateSecret`, `GetSecret`, `UpdateSecret`, `DeleteSecret` |
| `secret` | `SecretVersion` | RoundTripValidated | `ListSecretVersions`, `CreateSecretVersion`, `GetSecretVersion`, `UpdateSecretVersion`, `DeleteSecretVersion` |
| `container` | `Container` | RoundTripValidated | `ListContainers`, `CreateContainer`, `GetContainer`, `UpdateContainer`, `DeleteContainer` |
| `container` | `Namespace` | RoundTripValidated | `ListNamespaces`, `CreateNamespace`, `GetNamespace`, `UpdateNamespace`, `DeleteNamespace` |
| `container` | `Domain` | StructurallyConformant | `ListDomains`, `CreateDomain`, `GetDomain`, `UpdateDomain`, `DeleteDomain` |
| `jobs` | `JobDefinition` | StructurallyConformant | `ListJobDefinitions`, `CreateJobDefinition`, `GetJobDefinition`, `UpdateJobDefinition`, `DeleteJobDefinition` |
| `jobs` | `JobRun` | StructurallyConformant | `ListJobRuns`, `GetJobRun` |
| `tem` | `Domain` | StructurallyConformant | `ListDomains`, `CreateDomain`, `GetDomain`, `UpdateDomain` |
| `tem` | `Email` | StructurallyConformant | `ListEmails`, `CreateEmail`, `GetEmail` |
| `vpc` | `PrivateNetwork` | RoundTripValidated | `ListPrivateNetworks`, `CreatePrivateNetwork`, `GetPrivateNetwork`, `UpdatePrivateNetwork`, `DeletePrivateNetwork` |
| `vpc` | `VPC` | RoundTripValidated | `ListVPCs`, `CreateVPC`, `GetVPC`, `UpdateVPC`, `DeleteVPC` |

<!-- coverage:end -->

### What a tier asserts

A tier says what has been *verified*, never what is expected to work. Both claims below are deliberately
conservative: a group is left at the lower tier unless something already enforces the higher one.

| Tier | Asserts | Earned by |
|---|---|---|
| `StructurallyConformant` | The emitted methods address paths the schema declares and carry every parameter it declares. Says nothing about the API answering as typed. | Automatically — generation fails otherwise. |
| `RoundTripValidated` | Create/update/delete behave as the SDK claims. | The deploy simulation exercising the group, asserted by a test. |

Two further tiers exist in the model and nothing claims them yet. `LiveReadOnly` — a List call shown to work
against real Scaleway — waits on generated live probes; `Excluded` never appears here, because an excluded group
is not generated at all. A group excluded on purpose records the reason at its `SchemaSource.ClientResourceGroups`
entry; today that is Kapsule's `Pool` and `AccessSecretVersionByPath`, both by scope rather than by capability.

The services above are the ones with a generated client. `Schemas/` also holds schemas awaiting one — a service
there with no row above has no client yet, which is the honest reading of "not covered".

## Building the SDK on its own

Copy this folder anywhere on disk and build it:

```bash
dotnet build Scaleway.Sdk.csproj -warnaserror
```

Nothing else has to be present — no sibling projects, no `Directory.Build.props` above it, no Aspire tooling
installed. The project references no other project and no NuGet package, and declares the implicit usings it
needs rather than inheriting them from the repository it happens to live in.

`ScalewaySdkStandaloneBuildTests` is what keeps that true: it copies the project to a temporary directory outside
the repository and runs exactly that command against the copy, on every `pp test`. Warnings are errors because
the drift it guards against is a warning — a `ProjectReference` that resolves only inside this repository builds
fine outside it and reports `MSB9008` while the exit code stays 0.

**Be precise about what that proves and what the sibling assertion proves.**
`GeneratedSdkArchitectureTests.ScalewaySdk_DoesNotDependOnAspire` reflects over the assembly the ordinary
solution build produced and asserts it names no Aspire assembly. That is strong evidence, but it is a different
claim: it builds nothing, so it cannot say the project compiles where Aspire is absent. Neither test says the SDK
*works* anywhere — both are compile-time properties, and calls are what the tiers above are about.

## Generated code

**Everything under `Generated/` is emitted. Do not hand-edit it — the next generator run overwrites it, and a
test fails if the checked-in output stops matching a fresh parse of the schemas.**

```bash
dotnet run --project application/shared-kernel/Aspire.Hosting.Scaleway.Generator
```

That reads the checked-in schemas in [`Schemas/`](./Schemas) and writes the typed clients here, the Aspire
resource wrappers into `Aspire.Hosting.Scaleway/Generated`, `Generated/manifest.json`, and this README's coverage
region. Generation is byte-reproducible: a run that changes nothing produces no diff.

Refresh the schemas themselves with `pp scaleway-schemas`, which downloads each pinned document, reports what
Scaleway changed, and gates on breaking drift. Each pin carries a written rationale for its version, and the
command separately reports when Scaleway has published a newer version than a pin names.

Adding a service is an allow-list entry in `SchemaSource.ClientResourceGroups` plus a property on
`ScalewayClient` or `ScalewayProjectClient`; generation fails if the second half is missing, so a client nobody
can reach cannot ship.

### Conventions the emitter follows

- **Namespaces keep the API version** — `Scaleway.Sdk.Tem.V1Alpha1`, never a version-less alias. Half of
  Scaleway's products are alpha or beta upstream and the package does not present them as GA.
- **No closed C# enums.** A schema `enum` becomes an extensible `IScalewayEnumValue<T>` struct that round-trips a
  value it has not met. Scaleway adds values; their own schemas ship `unknown_*` members for exactly that reason.
- **Generated models are `{ get; init; }`** — built by object initializer or by the deserializer, never mutated
  afterwards.
- **Type-name collisions across services are resolved by namespace only.** `ClusterStatus` exists in both K8s and
  Redis, and neither is prefixed.
- **Array query parameters repeat rather than join** (`tags=a&tags=b`), which is why the query records return
  `List<KeyValuePair<string, string>>` rather than a dictionary. Comma-joining makes Scaleway match nothing.

## License

MIT. See [LICENSE](../../../LICENSE).
