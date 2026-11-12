namespace Scaleway.Sdk;

/// <summary>
///     Source-generated serialization for the SDK's own files (the pricing disk cache, the diagnostics log), so the
///     package uses no reflection-based JSON and stays trim and Native AOT safe. Each generated service client has
///     its own context beside it.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScalewayPricingCatalogClient.DiskCache))]
[JsonSerializable(typeof(ScalewayDiagnosticEvent))]
internal sealed partial class ScalewaySdkJsonContext : JsonSerializerContext;
