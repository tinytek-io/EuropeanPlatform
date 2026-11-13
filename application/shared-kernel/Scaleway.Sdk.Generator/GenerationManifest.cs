using System.Text.Json;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     What generation actually produced, written next to the code it describes. The emitter skips a CRUD operation
///     it cannot match rather than failing, so a resource whose <c>Get</c> Scaleway renames
///     upstream simply stops being generated — silently. At 17 resource groups a human notices; at 60 nobody does.
///     The manifest turns that silence into a diff: the operation disappears from a checked-in file that a test
///     re-derives from the schemas on every run.
///     <para>
///         It is also the single machine-readable answer to "what does this package cover, and how well is each
///         part of it checked" — the source for the README coverage table, so a coverage claim cannot drift upward
///         on its own the way "38 services are supported" did.
///     </para>
/// </summary>
public static class GenerationManifest
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions RenderOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    ///     Reads a rendered manifest back. Everything that consumes the manifest — the README coverage table, the
    ///     reachability tests — goes through here rather than spelling the naming policy out again, since a second
    ///     reader that disagreed would deserialize every property to null and report the file as empty coverage.
    /// </summary>
    /// <param name="json">The contents of <see cref="FileName" />.</param>
    public static ManifestDocument Parse(string json)
    {
        return JsonSerializer.Deserialize<ManifestDocument>(json, ReadOptions)
               ?? throw new InvalidOperationException($"'{FileName}' does not contain a generation manifest.");
    }

    /// <summary>Renders the manifest for a set of schemas, keyed by slug — the production path.</summary>
    public static string Render(IReadOnlyDictionary<string, string> yamlBySlug)
    {
        var apis = new List<(string Slug, ParsedApi Api)>();
        foreach (var (slug, yaml) in yamlBySlug.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            // A schema with no generated client contributes nothing; it is in Schemas/ awaiting an allow-list entry.
            if (SchemaSource.ParseClientApi(slug, yaml) is { } api)
            {
                apis.Add((slug, api));
            }
        }

        return Render(apis);
    }

    /// <summary>
    ///     Renders the manifest for already-parsed APIs. Ordering is fixed at every level — services by slug, groups
    ///     and operations in allow-list and parse order — so a re-render is byte-stable and any difference in the
    ///     file is a difference in what was generated.
    /// </summary>
    public static string Render(IEnumerable<(string Slug, ParsedApi Api)> apis)
    {
        var services = apis
            .OrderBy(entry => entry.Slug, StringComparer.Ordinal)
            .Select(entry => Describe(entry.Slug, entry.Api))
            .ToList();

        return JsonSerializer.Serialize(new ManifestDocument(services), RenderOptions) + "\n";
    }

    private static ManifestService Describe(string slug, ParsedApi api)
    {
        var (client, property) = SchemaSource.EntryPointFor(api.ServiceName);

        var groups = api.ResourceGroups
            .Select(group => new ManifestResourceGroup(
                    group.Name,
                    SchemaSource.ValidationTierFor(api.ServiceName, group.Name).ToString(),
                    group.BasePath,
                    group.ItemPath,
                    group.Operations.Select(operation => operation.OperationId).ToList()
                )
            )
            .ToList();

        return new ManifestService(
            api.ServiceName,
            slug,
            $"Scaleway.Sdk.{ServiceCatalog.GetServicePrefix(api.ServiceName)}.{api.ApiVersion}",
            api.ApiVersion,
            $"{client}.{property}",
            groups,
            api.Actions.Select(action => action.OperationId).ToList()
        );
    }
}

/// <summary>
///     How thoroughly a generated resource group has actually been checked. Declared per group rather than
///     inferred, and rendered into the manifest, so the claim is reviewable instead of implied by the fact that code
///     exists. A tier says what has been verified — never what is expected to work.
/// </summary>
public enum ValidationTier
{
    /// <summary>Not generated. Present for completeness; an excluded group never reaches the manifest.</summary>
    Excluded = 0,

    /// <summary>
    ///     The emitted methods address paths the schema declares and carry every parameter it declares. Earned
    ///     automatically — generation fails otherwise — and asserts nothing about the API answering as typed.
    /// </summary>
    StructurallyConformant = 1,

    /// <summary>
    ///     A List call has been shown to work against real Scaleway: path, auth and response deserialization. Says
    ///     nothing about create/update/delete bodies, side effects, or a field null on every item in the probe.
    /// </summary>
    LiveReadOnly = 2,

    /// <summary>Create/update/delete behave as the SDK claims, because the deploy path or a live test exercises them.</summary>
    RoundTripValidated = 3
}

public sealed record ManifestDocument(List<ManifestService> Services);

public sealed record ManifestService(
    string Service,
    string Slug,
    string Namespace,
    string ApiVersion,
    string EntryPoint,
    List<ManifestResourceGroup> ResourceGroups,
    List<string> Actions
);

/// <param name="ItemPath">
///     The prefix Get/Update/Delete address, which the transport completes with <c>/{id}</c>. Rendered even when it
///     equals <paramref name="CollectionPath" />, so the day Scaleway splits them apart it shows as a change rather
///     than as a field appearing.
/// </param>
public sealed record ManifestResourceGroup(
    string Name,
    string Tier,
    string CollectionPath,
    string ItemPath,
    List<string> Operations
);
