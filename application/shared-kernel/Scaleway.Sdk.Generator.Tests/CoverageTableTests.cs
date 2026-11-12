using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     The README coverage table is the one claim in this repository that has already gone wrong once — "38
///     Scaleway services are supported" against six generated resource types. These pin the rendering
///     side and the checked-in README against the checked-in manifest; <c>GenerationManifestTests</c> pins the
///     manifest against the schemas.
/// </summary>
public sealed class CoverageTableTests
{
    [Fact]
    public void Render_CountsTheManifestRatherThanRestatingIt()
    {
        // The headline is the sentence a reader takes away and the one nobody would check by hand.
        var rendered = CoverageTable.Render(TwoServices());

        rendered.Should().Contain("**2 services, 3 resource groups, 5 operations** — 4 on resource groups plus 1 declared actions.");
    }

    [Fact]
    public void Render_NamesScalewaysOwnApiVersion_NotTheNamespaceCasing()
    {
        // A reader cross-references Scaleway's API reference, which spells it `v1alpha1`. The manifest's apiVersion
        // field carries the C# spelling (`V1Alpha1`), so the version is read out of an emitted path instead.
        var rendered = CoverageTable.Render(TwoServices());

        rendered.Should().Contain("| `v1alpha1` |");
        rendered.Should().NotContain("| `V1Alpha1` |");
    }

    [Fact]
    public void Render_CarriesTheDeclaredTierOfEveryGroup()
    {
        var rendered = CoverageTable.Render(TwoServices());

        rendered.Should().Contain("| `tem` | `Domain` | StructurallyConformant |");
        rendered.Should().Contain("| `vpc` | `VPC` | RoundTripValidated |");
    }

    [Fact]
    public void CheckedInReadme_WhenRenderedFromTheCheckedInManifest_ShouldBeUnchanged()
    {
        // Arrange
        var manifest = GenerationManifest.Parse(File.ReadAllText(Path.Combine(SchemaFiles.GeneratedDirectory(), GenerationManifest.FileName)));
        var readme = File.ReadAllText(Path.Combine(SchemaFiles.GeneratedDirectory(), "..", "README.md"));

        // Act
        var regenerated = CoverageTable.ReplaceRegion(readme, CoverageTable.Render(manifest));

        // Assert
        readme.Should().Be(regenerated, "the coverage region is generated output — run scaleway-sdk generate rather than editing it");
    }

    [Fact]
    public void ReplaceRegion_RewritesOnlyTheMarkedRegion()
    {
        var readme = $"# Title\n\nProse before.\n\n{CoverageTable.StartMarker}\nstale\n{CoverageTable.EndMarker}\n\nProse after.\n";

        var updated = CoverageTable.ReplaceRegion(readme, "fresh");

        updated.Should().Contain("Prose before.").And.Contain("Prose after.");
        updated.Should().Contain("fresh").And.NotContain("stale");
    }

    [Fact]
    public void ReplaceRegion_WhenTheMarkersAreMissing_Throws()
    {
        // Appending the region instead would drop a coverage table wherever the file happened to end, which is not
        // a location anybody chose — and the generator would report success either way.
        var act = () => CoverageTable.ReplaceRegion("# Title\n\nNo markers here.\n", "fresh");

        act.Should().Throw<InvalidOperationException>().WithMessage("*coverage:start*coverage:end*");
    }

    /// <summary>
    ///     The rendering reads the manifest back off disk, so the reader's naming policy is load-bearing: a reader
    ///     that disagreed with the writer would deserialize every property to its default and render a table
    ///     claiming zero coverage — a wrong claim that looks exactly like a correctly generated one.
    /// </summary>
    [Fact]
    public void Parse_ReadsBackWhatRenderWrote()
    {
        var manifest = GenerationManifest.Parse(GenerationManifest.Render(SchemaFiles.AllSchemas()));

        manifest.Services.Should().NotBeEmpty();
        manifest.Services.Should().OnlyContain(service => service.ResourceGroups.Count > 0 && service.EntryPoint.Length > 0);
        CoverageTable.Render(manifest).Should().NotContain("**0 services");
    }

    private static ManifestDocument TwoServices()
    {
        return new ManifestDocument([
                new ManifestService("tem", "transactional-email", "Scaleway.Sdk.Tem.V1Alpha1", "V1Alpha1", "ScalewayProjectClient.Tem", [
                        new ManifestResourceGroup("Domain", nameof(ValidationTier.StructurallyConformant), "transactional-email/v1alpha1/regions/{region}/domains", "transactional-email/v1alpha1/regions/{region}/domains", ["ListDomains", "GetDomain"])
                    ], ["CheckDomain"]
                ),
                new ManifestService("vpc", "vpc", "Scaleway.Sdk.Vpc.V2", "V2", "ScalewayProjectClient.Vpc", [
                        new ManifestResourceGroup("VPC", nameof(ValidationTier.RoundTripValidated), "vpc/v2/regions/{region}/vpcs", "vpc/v2/regions/{region}/vpcs", ["ListVPCs"]),
                        new ManifestResourceGroup("PrivateNetwork", nameof(ValidationTier.RoundTripValidated), "vpc/v2/regions/{region}/private-networks", "vpc/v2/regions/{region}/private-networks", ["ListPrivateNetworks"])
                    ], []
                )
            ]
        );
    }
}
