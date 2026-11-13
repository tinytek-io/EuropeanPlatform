using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Orchestrates the drift comparison across every schema that has a generated client, working-tree vs committed.
///     The committed content is supplied through a seam, so these run without git or disk. Schemas are given as JSON
///     (valid YAML), which the source parser accepts.
/// </summary>
public sealed class SchemaDriftCheckTests
{
    [Fact]
    public void Compare_WhenWorkingTreeMatchesCommitted_ReportsNoDrift()
    {
        var current = new Dictionary<string, string> { ["vpc"] = Vpc() };

        var report = SchemaDriftCheck.Compare(current, _ => Vpc());

        report.Changes.Should().BeEmpty();
        report.HasBreaking.Should().BeFalse();
        report.Render().Should().Be("No schema drift: the generated surface is unchanged.");
    }

    [Fact]
    public void Compare_WhenAFieldWasAddedSinceCommit_ReportsAdditive()
    {
        var current = new Dictionary<string, string> { ["vpc"] = Vpc("vlan_id") };

        var report = SchemaDriftCheck.Compare(current, _ => Vpc());

        report.HasBreaking.Should().BeFalse();
        report.Changes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Kind = SchemaDriftKind.Additive, Service = "vpc", Description = "field CreatePrivateNetworkRequest.vlan_id added" });
    }

    [Fact]
    public void Compare_WhenAFieldWasRemovedSinceCommit_ReportsBreaking()
    {
        var current = new Dictionary<string, string> { ["vpc"] = Vpc() };

        // Committed had an extra field the refreshed schema no longer declares.
        var report = SchemaDriftCheck.Compare(current, _ => Vpc("vlan_id"));

        report.HasBreaking.Should().BeTrue();
        report.Changes.Should().Contain(change => change.Description == "field CreatePrivateNetworkRequest.vlan_id removed");
    }

    [Fact]
    public void Compare_WhenASchemaIsNewSinceCommit_ReportsAdditive()
    {
        var current = new Dictionary<string, string> { ["vpc"] = Vpc() };

        // A null committed reading means the schema is not in HEAD — added since the last commit.
        var report = SchemaDriftCheck.Compare(current, _ => null);

        report.HasBreaking.Should().BeFalse();
        report.Changes.Should().ContainSingle().Which.Description.Should().Be("new schema 'vpc'");
    }

    [Fact]
    public void Compare_SkipsSchemasThatHaveNoGeneratedClient()
    {
        // No client is generated for this service, so a change to it can't affect the SDK — it must not be reported.
        var current = new Dictionary<string, string> { ["cockpit"] = "{}" };

        var report = SchemaDriftCheck.Compare(current, _ => "{}");

        report.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Render_ListsEachChangeAndCountsByKind()
    {
        var current = new Dictionary<string, string> { ["vpc"] = Vpc("vlan_id") };

        var rendered = SchemaDriftCheck.Compare(current, _ => Vpc()).Render();

        rendered.Should().Contain("ADDITIVE  vpc: field CreatePrivateNetworkRequest.vlan_id added");
        rendered.Should().Contain("1 change(s): 1 additive.");
    }

    private static string Vpc(string extraRequestField = "")
    {
        var extra = extraRequestField.Length > 0 ? $$""", "{{extraRequestField}}": { "type": "string" }""" : "";
        return $$"""
                 {
                   "components": {
                     "schemas": {
                       "scaleway.vpc.v2.PrivateNetwork": {
                         "type": "object",
                         "properties": { "id": { "type": "string" }, "name": { "type": "string" } }
                       }
                     }
                   },
                   "paths": {
                     "/vpc/v2/regions/{region}/private-networks": {
                       "post": {
                         "operationId": "CreatePrivateNetwork",
                         "summary": "Create a private network",
                         "requestBody": {
                           "content": { "application/json": { "schema": {
                             "type": "object",
                             "properties": { "name": { "type": "string" }, "project_id": { "type": "string" }{{extra}} }
                           } } }
                         },
                         "responses": { "200": { "content": { "application/json": { "schema": {
                           "$ref": "#/components/schemas/scaleway.vpc.v2.PrivateNetwork"
                         } } } } }
                       }
                     }
                   }
                 }
                 """;
    }
}
