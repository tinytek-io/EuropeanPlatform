using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Runs the whole pipeline a schema refresh would: parse two versions of a schema through the real
///     <see cref="OpenApiClientParser" /> and classify the difference. Also the first coverage of the parser
///     itself (previously untested) — the harness the rest of the generator's tests build on.
/// </summary>
public sealed class SchemaDriftEndToEndTests
{
    // A minimal Scaleway-shaped OpenAPI document: one CreateWidget operation with a request body and a response DTO.
    private static string Schema(string extraRequestField = "", string widgetVersion = "v1")
    {
        var extra = extraRequestField.Length > 0 ? $$""", "{{extraRequestField}}": { "type": "string" }""" : "";
        return $$"""
                 {
                   "components": {
                     "schemas": {
                       "scaleway.widget.{{widgetVersion}}.Widget": {
                         "type": "object",
                         "properties": { "id": { "type": "string" }, "name": { "type": "string" } }
                       }
                     }
                   },
                   "paths": {
                     "/widget/{{widgetVersion}}/widgets": {
                       "post": {
                         "operationId": "CreateWidget",
                         "summary": "Create a widget",
                         "requestBody": {
                           "content": { "application/json": { "schema": {
                             "type": "object",
                             "properties": { "name": { "type": "string" }, "size": { "type": "integer" }{{extra}} }
                           } } }
                         },
                         "responses": { "200": { "content": { "application/json": { "schema": {
                           "$ref": "#/components/schemas/scaleway.widget.{{widgetVersion}}.Widget"
                         } } } } }
                       }
                     }
                   }
                 }
                 """;
    }

    private static ParsedApi Parse(string json)
    {
        return OpenApiClientParser.Parse("widget", json, ("Widget", "Widgets"));
    }

    [Fact]
    public void Parse_ProducesTheExpectedOperationRecordsAndVersion()
    {
        var api = Parse(Schema());

        api.ApiVersion.Should().Be("V1", "the version is read from the URL path segment");
        api.ResourceGroups.SelectMany(group => group.Operations).Select(operation => operation.OperationId).Should().Contain("CreateWidget");
        api.Records.Select(record => record.Name).Should().Contain("CreateWidgetRequest").And.Contain("Widget");
        api.Records.Single(record => record.Name == "CreateWidgetRequest").Fields.Select(field => field.Name).Should().BeEquivalentTo("name", "size");
    }

    [Fact]
    public void Compare_DetectsAnAddedRequestFieldAcrossTwoParsedVersions_AsAdditive()
    {
        var previous = Parse(Schema());
        var current = Parse(Schema("color"));

        var report = SchemaDriftComparer.Compare(previous, current);

        report.HasBreaking.Should().BeFalse();
        report.Changes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Kind = SchemaDriftKind.Additive, Description = "field CreateWidgetRequest.color added" });
    }

    [Fact]
    public void Compare_DetectsAVersionBump_AsBreaking()
    {
        var previous = Parse(Schema(widgetVersion: "v1"));
        var current = Parse(Schema(widgetVersion: "v2"));

        var report = SchemaDriftComparer.Compare(previous, current);

        report.HasBreaking.Should().BeTrue();
        report.Changes.Should().Contain(change => change.Description == "API version changed V1 → V2");
    }
}
