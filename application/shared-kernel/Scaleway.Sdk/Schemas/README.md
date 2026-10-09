# Scaleway API schemas

The published Scaleway OpenAPI schemas (one YAML per product), downloaded verbatim from Scaleway and
**checked into git on purpose**: when Scaleway changes an API, re-downloading produces a reviewable diff
against exactly what they publish instead of a silent surprise.

They're stored as **YAML** — the exact bytes Scaleway serves — so the diff is faithful to upstream. The
generator reads them with `YamlDotNet` (a tooling-only dependency of `Aspire.Hosting.Scaleway.Generator`,
never of the shipped package) and converts each to JSON in memory before parsing. Consumers:

- **A generated Scaleway SDK** — the schemas drive codegen; the generated client is plain
  `System.Net.Http` C#, so the shipped `Aspire.Hosting.Scaleway` package stays dependency-free.
- **The mock server's request validation** (planned) — validating our POST/PATCH bodies against a product's
  schema catches contract drift locally (removed/renamed fields, wrong types, invalid enum values, missing
  required fields). Note it does *not* catch unit mistakes: `memory_limit` is an unbounded `uint32` and the
  unit ("in MB") lives only in the description — the strongly-typed `DataSize`/`Cpu` units guard that instead.

The schema URL pattern is `https://www.scaleway.com/en/developers/api/<product-slug>/<version>/schema.yml`.
The slug is the product's doc slug (e.g. containers → `serverless-containers`), not the REST API path.

To refresh: `pp scaleway-schemas` (downloads the YAML and then reports how the generated surface drifted
against the committed schemas), regenerate with the generator, then review the diff. The drift report is the
point of the refresh — it exits non-zero on a breaking change (removed operation or field, changed field
type, API version bump), because those move the generated surface under existing callers. `--check-only`
re-runs the report without downloading again.
