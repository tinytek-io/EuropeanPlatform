using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Pins how inline enums are named and shared. Scaleway inlines the same logical enum on an entity and on its
///     create/update requests; the generator must collapse those onto one type (so a response value assigns to a
///     request), yet keep genuinely-different value sets apart.
/// </summary>
public sealed class EnumResolutionTests
{
    private const string PrivacyEnum = """{ "type": "string", "enum": ["public", "private"] }""";

    [Fact]
    public void AnInlineEnum_BecomesAGeneratedTypeNamedForTheEntity()
    {
        var api = Parse(PrivacyEnum, PrivacyEnum);

        // CreateWidgetRequestPrivacy normalises to WidgetPrivacy (Create + Request stripped).
        FieldType(api, "CreateWidgetRequest", "privacy").Should().Be("WidgetPrivacy?");
        api.Enums.Should().ContainSingle(parsedEnum => parsedEnum.Name == "WidgetPrivacy");
    }

    [Fact]
    public void TheSameEnumOnRequestAndResponse_CollapsesToOneSharedType()
    {
        var api = Parse(PrivacyEnum, PrivacyEnum);

        FieldType(api, "CreateWidgetRequest", "privacy").Should().Be("WidgetPrivacy?");
        FieldType(api, "Widget", "privacy").Should().Be("WidgetPrivacy?");
        api.Enums.Count(parsedEnum => parsedEnum.Name.EndsWith("Privacy", StringComparison.Ordinal)).Should().Be(1, "one concept, one type");
    }

    [Fact]
    public void NestedComponentsSharingALastSegment_KeepTheirOwnValues()
    {
        // Transactional Email declares Domain.Status and Email.Status. Named by their last segment alone they were one
        // "Status", and the domain's status was generated with the email statuses (sending, sent, ...).
        var api = SchemaSource.ParseClientApi("transactional-email", File.ReadAllText(SchemaFiles.SchemaPath("transactional-email")))!;

        api.Enums.Single(parsedEnum => parsedEnum.Name == "DomainStatus").Values.Should().Contain("checked").And.NotContain("sending");
        api.Enums.Single(parsedEnum => parsedEnum.Name == "EmailStatus").Values.Should().Contain("sending").And.NotContain("checked");
    }

    [Theory]
    [InlineData("""{ "type": "string", "enum": [] }""")]
    [InlineData("""{ "type": "string", "enum": [1, 2] }""")]
    public void AnEmptyOrNonStringEnum_FallsBackToString(string enumSchema)
    {
        var api = Parse(enumSchema, null);

        FieldType(api, "CreateWidgetRequest", "privacy").Should().Be("string?");
        api.Enums.Should().BeEmpty();
    }

    private static string FieldType(ParsedApi api, string recordName, string fieldName)
    {
        return api.Records.Single(record => record.Name == recordName).Fields.Single(field => field.Name == fieldName).CSharpType;
    }

    private static ParsedApi Parse(string requestPrivacy, string? widgetPrivacy)
    {
        var widgetPrivacyProperty = widgetPrivacy is null ? "" : $", \"privacy\": {widgetPrivacy}";
        var json = $$"""
                     {
                       "components": { "schemas": {
                         "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" }{{widgetPrivacyProperty}} } }
                       } },
                       "paths": {
                         "/widget/v1/widgets": {
                           "post": {
                             "operationId": "CreateWidget",
                             "requestBody": { "content": { "application/json": { "schema": {
                               "type": "object", "properties": { "name": { "type": "string" }, "privacy": {{requestPrivacy}} }
                             } } } },
                             "responses": { "200": { "content": { "application/json": { "schema": {
                               "$ref": "#/components/schemas/scaleway.widget.v1.Widget"
                             } } } } }
                           }
                         }
                       }
                     }
                     """;
        return OpenApiClientParser.Parse("widget", json, ("Widget", "Widgets"));
    }
}
