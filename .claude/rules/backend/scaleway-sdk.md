---
paths: application/shared-kernel/Scaleway.Sdk*/**,developer-cli/Commands/ScalewaySdk/**,**/*Scaleway*.cs
description: How code talks to the Scaleway API through the generated Scaleway.Sdk clients, and how to extend the generator instead of hand-writing API calls
---

# Scaleway SDK

All Scaleway API calls go through `Scaleway.Sdk`, whose typed clients are generated from the OpenAPI schemas checked in under `application/shared-kernel/Scaleway.Sdk/Schemas/`. The generator is `Scaleway.Sdk.Generator`, run through the developer CLI.

## Implementation

1. Never hand-write a Scaleway API path or build your own `HttpClient` for the Scaleway API. Use the generated clients through the entry points:
   - `ScalewayClient` (organization-scoped): `Iam`, `Projects`
   - `ScalewayProjectClient` (from `ScalewayClient.WithProject`): `Containers`, `Databases`, `EdgeServices`, `Jobs`, `Kubernetes`, `LoadBalancers`, `Redis`, `Registry`, `Secrets`, `Tem`, `Vpc`
   - Code that holds no organization (for example a worker given only a project) constructs the generated `*Api` directly on a `ScalewayApiClient` and names the project on the request
2. When an operation is missing, extend the generator rather than falling back to a raw path:
   - Add the resource group to `SchemaSource.ClientResourceGroups`, or the exact `operationId` to `SchemaSource.ClientActionOperations` for a body-less GET or empty-body POST action
   - A new service also needs `SchemaSource.ServiceForSlug`, `SchemaSource.EntryPointFor`, and the property on `ScalewayClient` or `ScalewayProjectClient`
   - Regenerate with `dotnet run --project developer-cli -- scaleway-sdk generate`
3. Review the `Generated/manifest.json` diff after every regeneration. The CRUD matcher silently skips an operation whose `operationId` does not fit the singular/plural pattern (Load Balancer's `PUT` updates are an example), and the manifest diff is the only place that shows it
4. Add or refresh a schema with a pin in `SchemaPins.All`:
   - `Slug` is Scaleway's documentation path (`load-balancer/zoned`); the file on disk is its flattened `FileName` (`load-balancer-zoned.yml`)
   - Every pin carries a rationale for its version; a pin and its file are added in the same commit, or `SchemaPinTests` fails
   - `dotnet run --project developer-cli -- scaleway-sdk schemas` downloads the pinned schemas and gates on breaking drift. www.scaleway.com's bot protection currently rejects the .NET client, so download new schema files with curl or a browser until that is resolved
5. Never edit `Generated/` or the README's coverage region by hand. Both are generator output, and `scaleway-sdk generate` must reproduce the committed files with no diff
6. Claim a validation tier above `StructurallyConformant` (in `SchemaSource.ValidationTierFor`) only in the same change that adds the test which verifies it
7. Record consumer-visible SDK changes in `Scaleway.Sdk/CHANGELOG.md`; renamed or retyped generated types are `BREAKING:`
8. Every public member of `Scaleway.Sdk` carries a doc comment (CS1591 is an error there). State a fact the declaration does not already state

Known exceptions to rule 1, each to be removed rather than copied:

- Transactional Email offer subscriptions in `ScalewayEnvironment.EnsureEmailOfferAsync`: a `PATCH` on the collection itself, which the CRUD matcher does not model; it uses the SDK transport
- `ScalewayPricingCatalogClient`'s product-catalog path

## Examples

### Example 1 - Calling Scaleway

```csharp
// ✅ DO: Use the generated client through an entry point
using var scaleway = new ScalewayClient(credentials);
var registry = scaleway.WithProject(projectId).Registry;
var images = await registry.ListImagesAsync(new ScwRegistry.ListImagesRequest { NamespaceId = namespaceId }, region);

// ❌ DON'T: Hand-write the path and the HTTP call
using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);
var response = await httpClient.GetAsync($"https://api.scaleway.com/registry/v1/regions/{region}/images?namespace_id={namespaceId}");
```

### Example 2 - Adding a Resource Group

```csharp
// ✅ DO: Declare the group, regenerate, and review the manifest diff
"registry" => [("Namespace", "Namespaces"), ("Image", "Images"), ("Tag", "Tags")],

// ❌ DON'T: Work around a missing generated operation with the raw transport
var tags = await transport.ListResourcesAsync($"registry/v1/regions/{region}/images/{imageId}/tags", region);
```
