namespace Scaleway.Sdk.Generator;

/// <summary>
///     Runs <see cref="SchemaDriftComparer" /> across every schema that has a generated client, comparing the
///     working-tree schemas (just refreshed) against their committed versions. The <c>readCommittedYaml</c> seam
///     supplies the committed content (git in production, a fixture in tests), so the orchestration is testable
///     without git or disk.
/// </summary>
public static class SchemaDriftCheck
{
    public static SchemaDriftReport Compare(IReadOnlyDictionary<string, string> currentYamlBySlug, Func<string, string?> readCommittedYaml)
    {
        var changes = new List<SchemaDriftChange>();

        foreach (var (slug, currentYaml) in currentYamlBySlug.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var current = SchemaSource.ParseClientApi(slug, currentYaml);
            if (current is null)
            {
                continue; // no generated client for this service — nothing the SDK depends on
            }

            var committedYaml = readCommittedYaml(slug);
            if (committedYaml is null)
            {
                // Not in the committed tree: a schema added since the last commit. Purely additive.
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, current.ServiceName, $"new schema '{slug}'"));
                continue;
            }

            var previous = SchemaSource.ParseClientApi(slug, committedYaml);
            if (previous is not null)
            {
                changes.AddRange(SchemaDriftComparer.Compare(previous, current).Changes);
            }
        }

        var ordered = changes
            .OrderBy(change => (int)change.Kind)
            .ThenBy(change => change.Service, StringComparer.Ordinal)
            .ThenBy(change => change.Description, StringComparer.Ordinal)
            .ToList();
        return new SchemaDriftReport(ordered);
    }

    /// <summary>
    ///     Fields the current schemas declare but the generator drops — a gap between the API and the SDK, surfaced
    ///     so it can't hide (e.g. the <c>tcp</c> health-check probe). Informational, not drift: these are a standing
    ///     property of the current schemas, not a change since the last commit.
    /// </summary>
    public static IReadOnlyList<ParsedDroppedField> DroppedFields(IReadOnlyDictionary<string, string> currentYamlBySlug)
    {
        var dropped = new List<ParsedDroppedField>();

        foreach (var (slug, yaml) in currentYamlBySlug.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var api = SchemaSource.ParseClientApi(slug, yaml);
            if (api is not null)
            {
                dropped.AddRange(api.DroppedFields);
            }
        }

        return dropped;
    }
}
