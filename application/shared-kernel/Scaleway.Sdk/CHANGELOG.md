# Changelog

All notable changes to the `Scaleway.Sdk` package are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Pre-1.0 versions
may include breaking changes in minor releases; each is called out under `Changed` with a
`BREAKING:` prefix.

## [Unreleased]

### Added

- Initial version, **0.1.0-preview**: a typed .NET client for the Scaleway API that depends on nothing but the .NET
  base class library.

- Generated clients for Account projects, Edge Services, IAM, Kubernetes Kapsule, Load Balancer, Managed Databases,
  the public Product Catalog, Redis, Container Registry, Secret Manager, Serverless Containers, Serverless Jobs,
  Transactional Email and VPC, each
  in `Scaleway.Sdk.<Service>.<Version>`. The [README](./README.md)'s coverage table lists per service the API version,
  entry point, declared actions and every emitted operation; the generator renders it from `Generated/manifest.json`,
  and a test fails if the checked-in table stops matching.

- The transport (`IScalewayApiClient` / `ScalewayApiClient`) with request scoping, snake_case serialization and
  structured error parsing, `ScalewayApiClient.ConfigureHttpClient` for a caller that holds only a secret key (including
  inside an `IHttpClientFactory` registration), and the scoped entry points `ScalewayClient` (organization) and `ScalewayProjectClient`
  (project), whose operations fill `organization_id`, `project_id` and the default region themselves.

- Hand-written clients where Scaleway publishes no usable schema or the operation is a composite: `ScalewayS3Client`
  (Object Storage), `ScalewaySecretClient` (get-or-create and set, optionally under a path) and
  `ScalewayPricingCatalogClient` (the generated Product Catalog listing per region, zone or globally, with a 24-hour
  disk cache).

- Value types (`OrganizationId`, `ProjectId`, `ScalewayRegion`, `ScalewayZone`, `ScalewayCredentialConfig`),
  `ScalewayRegionExtensions.DefaultZone`, and `ScalewayEndpoints` for the service hostnames that are not the
  management API.

- Source-generated JSON serialization throughout, so the package is trim and Native AOT safe.

- A `User-Agent` of `Scaleway.Sdk-dotnet/{version}` on every request, to which a consumer can add its own product
  token through the constructors or `ScalewayUserAgent`.

- XML documentation on every public type and member, enforced by the compiler (CS1591 is an error), and NuGet
  packaging metadata with this README packed into the package.

- The pinned Scaleway OpenAPI schemas the clients are generated from, in `Schemas/`.
