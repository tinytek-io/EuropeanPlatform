# Changelog

All notable changes to the `Scaleway.Sdk` package are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Pre-1.0 versions
may include breaking changes in minor releases; each is called out under `Changed` with a
`BREAKING:` prefix.

## [Unreleased]

### Added

- **Secret Manager paths on `ScalewaySecretClient`** — `GetOrCreateAsync` and `SetAsync` take an optional
  `path` (default `/`, the root): the secret is created under it, and an existing secret found on a different
  path is moved there via `UpdateSecret` without touching its value. Paths are the unit Scaleway's resource-level
  IAM conditions scope Secret Manager access to, which is what makes this worth expressing at this layer.
  Comparison is normalization-aware (`/runtime/` equals `/runtime`), so a converged redeploy issues no write.
  Existing positional callers that passed a `CancellationToken` in the sixth position must name it.

- **Serverless Jobs** — `ScalewayProjectClient.Jobs` (`Scaleway.Sdk.Jobs.V1Alpha2`), the first service added since
  the package was extracted. `JobDefinition` carries the full five CRUD operations; `JobRun` carries `List` and
  `Get`, which is the whole of what Scaleway declares — a run is produced by starting a definition, not created.
  `StopJobRunAsync` is there too.

  **`StartJobDefinition` is not**, and that is the one hole worth knowing about: it is a POST carrying a body, and
  the action emitter supports only body-less GETs and empty-body POSTs, so declaring it fails generation rather
  than emitting something that compiles and sends nothing. The exclusion and its reason sit at the generator's
  `jobs` action entry. Job secrets and job triggers are `v1alpha2` collections that are also not generated yet.

  Generated from **`v1alpha2`**, not the `v1alpha1` the schema had been pinned to since before the package
  existed. Scaleway keeps superseded documents serving `200`, so the stale pin failed nothing and prompted nobody.
  Reviewed for the class of change the Serverless Containers `v1beta1`→`v1` migration produced (a `memory_limit`
  whose unit moved from MiB to bytes, silently): **there is none here** — `cpu_limit` is still mvCPU,
  `memory_limit` and `local_storage_capacity` are still MiB, and no field was renamed. What did change is
  additive or a vocabulary change: `startup_command`, `args`, `retry_policy` and `attempts` are new, `command` is
  deprecated in favour of `startup_command`, and `JobRun`'s state vocabulary dropped `scheduled`, `canceled` and
  `internal_error` while gaining `initialized`, `validated`, `interrupting`, `interrupted` and `retrying`.
  No consumer existed, so nothing had to move.

- NuGet packaging metadata — `PackageId`, `Description`, `Authors`, MIT license expression, repository URL, and a
  `README.md` packed into the package, so the listing is a landing page rather than a bare assembly. The XML
  documentation ships alongside it. Publishing is still out of scope; the package builds correctly as one.

- A package [README](./README.md): quick start, the hand-written clients, the emitter's conventions, and the
  coverage table. **The coverage table is rendered from `Generated/manifest.json` by the generator**, never typed,
  and a test fails if the checked-in README stops matching it — per service the API version, namespace, entry
  point and declared actions, and per resource group its validation tier and every emitted operation. A tier says
  what has been verified, not what is expected to work, and the two tiers currently claimed are documented
  alongside the two that nothing claims yet.

- Every request the package sends now carries a `User-Agent`: `Scaleway.Sdk-dotnet/{version}`, the shape
  Scaleway's own libraries use. .NET sends none by default, so this traffic used to arrive indistinguishable from
  an unnamed script — which is what a provider deciding where to apply a rate limit has to fall back on.

  It applies to `ScalewayApiClient` (both constructors, so a caller-supplied `HttpClient` from
  `IHttpClientFactory` is identified too), `ScalewayPricingCatalogClient` and the Object Storage client. The
  product names the package and nothing else: a library's traffic must not advertise the application it is
  embedded in, or the repository it was built from.

  A consumer that wants to be named alongside it passes its own token —
  `new ScalewayApiClient(credentials, new ProductInfoHeaderValue("MyApp", "2.3"))` sends
  `Scaleway.Sdk-dotnet/0.1.0-preview MyApp/2.3`. `ScalewayUserAgent.ApplyTo(httpClient, productToken)` does the
  same for a client built by hand, and `ScalewayUserAgent.ProductToken(name, assembly)` builds the token from an
  assembly's own version rather than a constant that drifts. Applying it twice does not repeat a product.

- The package now ships XML documentation, and every public type and member carries a doc comment (#36 W7).
  Consumers get IntelliSense across the whole surface, including the generated clients: a record property carries
  Scaleway's own description of the field (94% of them have one) or, failing that, the wire name; a record type
  names the schema component or the operation it is the body of; an extensible enum member names the wire value it
  sends, which is not always derivable from the C# name (`unknown_status` → `UnknownStatus`).

  Enforced by the compiler rather than by review: `GenerateDocumentationFile` is on and CS1591 is an error, so a
  new public member without a doc comment fails the build.

- Initial extraction of the Scaleway client out of `Aspire.Hosting.Scaleway` into a standalone package with
  **no Aspire dependency** (#36). Referencing it no longer drags the Aspire AppHost framework in, which is what
  lets `SharedKernel` retire its hand-written runtime HTTP clients and lets a deploy bootstrap consume the same
  typed surface as the hosting package.

  The package contains the generated typed clients (`Scaleway.Sdk.<Service>.<Version>`), the transport
  (`IScalewayApiClient` / `ScalewayApiClient`) with its request scoping, snake_case serialization and structured
  error parsing, the scoped entry points `ScalewayClient` / `ScalewayProjectClient`, the value types
  (`OrganizationId`, `ProjectId`, `ScalewayRegion`, `ScalewayZone`, `ScalewayCredentialConfig`), the
  S3 client (`ScalewayS3Client`, hand-written because Object Storage has no OpenAPI schema), and
  `ScalewaySecretClient`.

- `ScalewayPricingCatalogClient` — the Product Catalog fetch, paging and 24h disk cache, split out of the
  hosting package's `ScalewayPricingClient`. Cost figures are a product concern rather than a provisioning one
  (#73), so the catalog belongs here; turning a catalog price into a per-resource estimate stays with the
  caller.

- `ScalewayRegionExtensions.DefaultZone(this ScalewayRegion)` — the first availability zone of a region, for
  callers handed a region that must name a zone. `ScalewayCredentialConfig.DefaultZone` falls back to `fr-par-1`
  regardless of region, which is wrong for anyone deploying outside `fr-par`.

- `ScalewayEndpoints` — the Scaleway service hostnames that are not the management API (Object Storage,
  Container Registry, Cockpit OTLP, the predicted Serverless Container domain). Written down once so the URL
  scheme drifts visibly rather than one call site at a time.

- `TemApi.CreateEmailAsync` and the `Email` resource group, so workloads send transactional email through the
  generated client instead of a hand-written POST.

- `ScalewayProjectClient.Kubernetes` (`Scaleway.Sdk.K8s.V1.K8sApi`) — Kapsule cluster CRUD plus
  `GetClusterKubeConfigAsync`, generated from the newly checked-in `kubernetes.yml` schema. Kapsule is part of
  this package's coverage of the Scaleway API; no deploy path in this repository consumes it today.

  Node pools are reachable only through `CreateClusterRequest.Pools`; no pool operations are generated yet.
  Scaleway nests the pool collection under the cluster (`/clusters/{cluster_id}/pools`) but addresses individual
  pools at the top level (`/pools/{pool_id}`). The emitter handles that split, so adopting pool scaling is now a
  matter of adding the resource group rather than a generator change.

- The checked-in Scaleway OpenAPI schemas now live here (`Schemas/`), alongside the SDK they generate.

- VPC S3 integration: `PrivateNetwork.HasS3Integration`, `VPC.S3IntegrationEnabled`, and an
  `S3IntegrationEnabled` filter on both `ListPrivateNetworksRequest` and `ListVpcsRequest`. The checked-in
  `vpc.yml` already declared these; the generated client was stale because the schema was refreshed without
  regenerating VPC, so this change is a regeneration from the unchanged schema rather than a schema refresh.
  Purely additive — no existing member changes. The two entity properties are named differently because
  Scaleway names them differently (`has_s3_integration` on Private Network, `s3_integration_enabled` on VPC).
  The schema's separate `s3-integration` collection remains ungenerated, as do VPC's other uncovered
  collections (routes, subnets, vpc-connectors, ingress-rules).

### Fixed

- `BREAKING:` a schema field typed `number` now generates `double?` (and `double[]?`) instead of `long?`. JSON
  Schema's `number` is a real, not a whole number, so any fractional value threw at the deserializer instead of
  binding — found live, where Kapsule returns `scale_down_utilization_threshold: 0.5` and `CreateCluster` failed
  after the cluster had already been created. Two generated fields change type; `integer` is unaffected.

### Changed

- The package declares a version for the first time: **0.1.0-preview**, where it previously took .NET's implicit
  `1.0.0`. The assembly version moves with it, and the version is now consumer-visible in the `User-Agent`. The
  remaining NuGet metadata (`PackageId`, `Description`, license, README) is still to come.

- `BREAKING:` `IScalewayApiClient` gains a query-carrying
  `GetAsync(apiPath, region, queryParams, cancellationToken)` overload, and generated action operations now emit
  the query parameters their schema declares as a typed `{Operation}Request` record — the same shape a List's
  query gets, so a scoped client fills `project_id` there too. A schema-`required` set is emitted without a
  default, so an operation that names its target through the query string cannot be called without naming it.
  Consumers implementing the transport interface add the overload; consumers calling the existing three-argument
  `GetAsync` are unaffected.

  One generated signature changes: `K8sApi.GetClusterKubeConfigAsync` gains an optional
  `GetClusterKubeConfigRequest? query` between the cluster id and the region, carrying the schema's `redacted`
  parameter, which the emitter previously dropped. Callers passing the region positionally now pass it by name.

- `BREAKING:` the generated client namespace moved from `Aspire.Hosting.Scaleway.Api.<Service>.<Version>` to
  `Scaleway.Sdk.<Service>.<Version>`. Consumers holding `using Scw… = Aspire.Hosting.Scaleway.Api.…` aliases
  update the right-hand side; no member names changed.

- `BREAKING:` `ToQueryParameters()` returns `List<KeyValuePair<string, string>>` instead of
  `Dictionary<string, string>`, and `IScalewayApiClient.ListResourcesAsync` takes the same. Array-valued query
  parameters are now **repeated** (`tags=a&tags=b`) rather than comma-joined, which is what OpenAPI's
  `style=form, explode=true` default specifies and what Scaleway expects. A comma-joined value was read as one
  literal tag, so any tag-filtered list silently matched nothing.

- `BREAKING:` `ScalewayApiClient.Dispose()` and `ScalewayPricingCatalogClient.Dispose()` no longer dispose an
  `HttpClient` the caller supplied — only one the type constructed itself. Disposing a pooled client from
  `IHttpClientFactory` tore down a handler other callers still held, which defeats the reason the `HttpClient`
  constructor was made public in the first place. Callers that relied on the old behaviour dispose their own
  client.

- `ScalewayApiClient(HttpClient)`, `ScalewaySecretClient` and the exception surface (`ScalewayApiException`,
  `ScalewayTransientException`, `ScalewayResourceAlreadyExistsException`, `ScalewayError`,
  `ScalewayErrorDetail`) are public. A standalone package's error types are consumer API, and the `HttpClient`
  constructor lets a long-running workload supply a pooled, resilience-configured client from
  `IHttpClientFactory` rather than letting the SDK own a socket for the process lifetime.
