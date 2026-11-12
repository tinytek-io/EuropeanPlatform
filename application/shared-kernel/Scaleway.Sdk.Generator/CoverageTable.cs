using System.Text;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     Renders the SDK README's coverage claim from <c>Generated/manifest.json</c>. A hand-typed coverage claim
///     ("38 Scaleway services are supported", once, against six generated resource types) drifts upward on its
///     own. A hand-written table is the same defect waiting to happen, so the
///     README carries a generated region instead and nothing about coverage is written by hand.
///     <para>
///         The chain is deliberately four links, each pinned by a different test: the schemas are the source,
///         <c>GenerationManifestTests</c> pins the checked-in manifest against a fresh parse of them, these render
///         the README region from that manifest, and <c>ScalewayEntryPointReachabilityTests</c> pins the manifest's
///         entry points against the SDK's actual properties. No single test would close it.
///     </para>
/// </summary>
public static class CoverageTable
{
    /// <summary>Opens the generated region. The text after it, up to <see cref="EndMarker" />, is overwritten on every run.</summary>
    public const string StartMarker = "<!-- coverage:start — generated from Generated/manifest.json; do not edit by hand -->";

    /// <summary>Closes the generated region.</summary>
    public const string EndMarker = "<!-- coverage:end -->";

    /// <summary>
    ///     Renders the region's body: the headline totals, one row per service, and one row per resource group with
    ///     the validation tier it declares. Every number is counted from the manifest rather than stated, so the
    ///     README cannot claim coverage that generation did not produce.
    /// </summary>
    /// <param name="manifest">The rendered manifest, read back from the file the generator just wrote.</param>
    public static string Render(ManifestDocument manifest)
    {
        var groups = manifest.Services.SelectMany(service => service.ResourceGroups).ToList();
        var resourceOperations = groups.Sum(group => group.Operations.Count);
        var actions = manifest.Services.Sum(service => service.Actions.Count);

        var builder = new StringBuilder();
        builder.AppendLine($"**{manifest.Services.Count} services, {groups.Count} resource groups, {resourceOperations + actions} operations** — {resourceOperations} on resource groups plus {actions} declared actions.");
        builder.AppendLine();
        builder.AppendLine("| Service | API version | Namespace | Entry point | Actions |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var service in manifest.Services)
        {
            builder.AppendLine($"| `{service.Service}` | `{WireVersion(service)}` | `{service.Namespace}` | `{service.EntryPoint}` | {JoinAsCode(service.Actions)} |");
        }

        builder.AppendLine();
        builder.AppendLine("| Service | Resource group | Tier | Operations |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var service in manifest.Services)
        {
            foreach (var group in service.ResourceGroups)
            {
                builder.AppendLine($"| `{service.Service}` | `{group.Name}` | {group.Tier} | {JoinAsCode(group.Operations)} |");
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    ///     Replaces the marked region of a README with freshly rendered content, keeping everything outside it. The
    ///     prose around the region is hand-written and stays that way; only the facts are generated.
    /// </summary>
    /// <param name="readme">The README's current contents.</param>
    /// <param name="body">The region body, as produced by <see cref="Render" />.</param>
    public static string ReplaceRegion(string readme, string body)
    {
        var start = readme.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = readme.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            // Silently appending the region would put the coverage claim somewhere nobody chose. A README that
            // wants generated coverage says where it goes.
            throw new InvalidOperationException($"The README does not contain the '{StartMarker}' / '{EndMarker}' pair the coverage table is written into.");
        }

        return readme[..start] + StartMarker + "\n\n" + body + "\n\n" + readme[end..];
    }

    // Scaleway's own version segment, read out of a path the generator emitted rather than derived from the C#
    // namespace casing: it is what a reader needs to find the matching page in Scaleway's API reference. Every group
    // of one service agrees on it — OpenApiClientParser.ResolveApiVersion fails generation otherwise — and a service
    // with no resource groups has no client and never reaches the manifest.
    private static string WireVersion(ManifestService service)
    {
        return service.ResourceGroups[0].CollectionPath.Split('/')[1];
    }

    private static string JoinAsCode(IReadOnlyCollection<string> values)
    {
        return values.Count == 0 ? "—" : string.Join(", ", values.Select(value => $"`{value}`"));
    }
}
