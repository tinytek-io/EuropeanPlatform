using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Holds the pin table to the schemas it claims to describe. A pin is the only record of which upstream
///     document a checked-in schema came from, and Scaleway keeps superseded documents serving 200 forever, so
///     nothing else in the tree can contradict a wrong one.
/// </summary>
public sealed class SchemaPinTests
{
    [Fact]
    public void EveryPin_CarriesARationale()
    {
        // The type makes omitting the rationale a compile error; this is the empty string it cannot catch. Without
        // it, "pinned deliberately below current" and "nobody has looked since 2024" render identically.
        SchemaPins.PinsWithoutRationale().Should().BeEmpty();
    }

    [Fact]
    public void PinnedSlugs_AreExactlyTheCheckedInSchemas()
    {
        var pinned = SchemaPins.All.Select(pin => pin.FileName).ToHashSet(StringComparer.Ordinal);
        var onDisk = SchemaFiles.AllSchemas().Keys.ToHashSet(StringComparer.Ordinal);

        // Both directions matter: a schema with no pin is a file nobody can refresh or check for staleness, and a
        // pin with no schema downloads a document the generator never reads.
        pinned.Should().BeEquivalentTo(onDisk);
    }

    [Fact]
    public void EveryPin_NamesTheApiVersionInsideTheSchemaOnDisk()
    {
        var schemas = SchemaFiles.AllSchemas();

        foreach (var pin in SchemaPins.All)
        {
            // Read off the document's own paths rather than through SchemaSource.ParseClientApi: that returns null
            // for a service with no generated client, which silently skipped serverless-jobs, serverless-functions
            // and serverless-databases — the three pins most in need of checking, since the first of them is the
            // superseded one. Every checked-in document declares exactly one version segment.
            var declared = VersionSegmentsIn(schemas[pin.FileName]);

            // Catches a pin edited without a re-download: the table says v1alpha2 and the bytes are still v1alpha1.
            declared.Should().Equal([pin.Version], $"the pin for '{pin.Slug}' must name the version its checked-in document declares");
        }
    }

    [Fact]
    public void EveryPin_HasAParsableVersion()
    {
        foreach (var pin in SchemaPins.All)
        {
            var parsed = SchemaApiVersion.Parse(pin.Version);

            parsed.Text.Should().Be(pin.Version);
        }
    }

    private static string[] VersionSegmentsIn(string yaml)
    {
        using var document = JsonDocument.Parse(SchemaSource.YamlToJson(yaml));

        return document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(path => path.Name.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segments => segments.Length > 1)
            .Select(segments => segments[1])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
