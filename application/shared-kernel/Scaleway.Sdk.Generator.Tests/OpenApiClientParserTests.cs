using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Pins how the parser resolves each schema shape to a C# type — the rules the whole generated SDK rests on,
///     previously exercised only indirectly through generation. Each test states one rule against a minimal schema.
/// </summary>
public sealed class OpenApiClientParserTests
{
    [Theory]
    [InlineData("""{ "type": "string" }""", "string?")]
    [InlineData("""{ "type": "string", "format": "date-time" }""", "DateTimeOffset?")]
    [InlineData("""{ "type": "integer" }""", "long?")]
    // "number" is a real, not a whole number — see AFractionalNumber_MapsToDoubleRatherThanLong.
    [InlineData("""{ "type": "number" }""", "double?")]
    [InlineData("""{ "type": "boolean" }""", "bool?")]
    [InlineData("""{ "type": "array", "items": { "type": "string" } }""", "string[]?")]
    [InlineData("""{ "type": "array", "items": { "type": "number" } }""", "double[]?")]
    public void Resolves_ScalarAndArrayShapes(string schema, string expectedType)
    {
        var api = Parse($"\"value\": {schema}");

        FieldType(api, "CreateWidgetRequest", "value").Should().Be(expectedType);
    }

    [Fact]
    public void Resolves_RefToAnObjectComponent_AsANamedRecord()
    {
        var api = Parse(
            "\"thing\": { \"$ref\": \"#/components/schemas/scaleway.widget.v1.Thing\" }",
            "\"scaleway.widget.v1.Thing\": { \"type\": \"object\", \"properties\": { \"label\": { \"type\": \"string\" } } }"
        );

        FieldType(api, "CreateWidgetRequest", "thing").Should().Be("Thing?");
        api.Records.Select(record => record.Name).Should().Contain("Thing", "the referenced component becomes a shared record");
    }

    [Fact]
    public void Resolves_ArrayOfRef_AsAList()
    {
        var api = Parse(
            "\"things\": { \"type\": \"array\", \"items\": { \"$ref\": \"#/components/schemas/scaleway.widget.v1.Thing\" } }",
            "\"scaleway.widget.v1.Thing\": { \"type\": \"object\", \"properties\": { \"label\": { \"type\": \"string\" } } }"
        );

        FieldType(api, "CreateWidgetRequest", "things").Should().Be("List<Thing>?");
    }

    [Fact]
    public void Resolves_InlineNestedObject_AsARecordNamedForItsProperty()
    {
        // The request wrapper is stripped from the nested record's name (CreateWidgetRequestConfig →
        // WidgetConfig) so the same inline object on the entity and its create/update requests is one type.
        var api = Parse("\"config\": { \"type\": \"object\", \"properties\": { \"tries\": { \"type\": \"integer\" } } }");

        FieldType(api, "CreateWidgetRequest", "config").Should().Be("WidgetConfig?");
        api.Records.Should().Contain(record => record.Name == "WidgetConfig");
    }

    [Fact]
    public void Deduplicates_IdenticalInlineObjects_AcrossEntityAndRequest()
    {
        // Scaleway denormalizes the same object onto the entity and its requests; both must resolve to one
        // shared record so a response's value is assignable to a request's.
        var api = Parse(
            "\"config\": { \"type\": \"object\", \"properties\": { \"tries\": { \"type\": \"integer\" } } }",
            widgetProperties: "\"config\": { \"type\": \"object\", \"properties\": { \"tries\": { \"type\": \"integer\" } } }"
        );

        FieldType(api, "CreateWidgetRequest", "config").Should().Be("WidgetConfig?");
        FieldType(api, "Widget", "config").Should().Be("WidgetConfig?");
        api.Records.Count(record => record.Name.EndsWith("Config", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void KeepsAnInlineObject_UnderItsFullContextName_WhenTheShapeDiffers()
    {
        // Same collapsed name but different fields means genuinely distinct types; the later one keeps its
        // unabbreviated context name instead of silently merging.
        var json = """
                   {
                     "components": { "schemas": { "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } } } },
                     "paths": {
                       "/widget/v1/widgets": {
                         "post": {
                           "operationId": "CreateWidget",
                           "requestBody": { "content": { "application/json": { "schema": {
                             "type": "object", "properties": { "config": { "type": "object", "properties": { "tries": { "type": "integer" } } } }
                           } } } },
                           "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                         }
                       },
                       "/widget/v1/widgets/{widget_id}": {
                         "patch": {
                           "operationId": "UpdateWidget",
                           "requestBody": { "content": { "application/json": { "schema": {
                             "type": "object", "properties": { "config": { "type": "object", "properties": { "retries": { "type": "integer" } } } }
                           } } } },
                           "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                         }
                       }
                     }
                   }
                   """;

        var api = OpenApiClientParser.Parse("widget", json, ("Widget", "Widgets"));

        FieldType(api, "CreateWidgetRequest", "config").Should().Be("WidgetConfig?", "the first shape claims the collapsed name");
        FieldType(api, "UpdateWidgetRequest", "config").Should().Be("UpdateWidgetRequestConfig?");
    }

    [Fact]
    public void FailsLoudly_WhenAnEntityNestedObject_CollidesWithADifferentlyShapedClaimant()
    {
        // The request resolves first and its collapsed name equals the entity's own context name; with a
        // different shape there is no longer name to fall back to. Silently returning either type would
        // mislabel one of them, so generation must fail instead.
        var act = () => Parse(
            "\"config\": { \"type\": \"object\", \"properties\": { \"retries\": { \"type\": \"integer\" } } }",
            widgetProperties: "\"config\": { \"type\": \"object\", \"properties\": { \"tries\": { \"type\": \"integer\" } } }"
        );

        act.Should().Throw<InvalidOperationException>().WithMessage("*WidgetConfig*different shape*");
    }

    [Fact]
    public void Resolves_AdditionalPropertiesObject_AsAStringMap()
    {
        var api = Parse("\"tags\": { \"type\": \"object\", \"additionalProperties\": { \"type\": \"string\" } }");

        FieldType(api, "CreateWidgetRequest", "tags").Should().Be("Dictionary<string, string>?");
    }

    [Fact]
    public void Resolves_PlaceholderKeyObject_AsAStringMap()
    {
        // Scaleway also models a map as an object whose only property is a placeholder key like <envKey>.
        var api = Parse("\"env\": { \"type\": \"object\", \"properties\": { \"<envKey>\": { \"type\": \"string\" } } }");

        FieldType(api, "CreateWidgetRequest", "env").Should().Be("Dictionary<string, string>?");
    }

    [Fact]
    public void Resolves_ACyclicReference_WithoutInfiniteRecursion()
    {
        // The Widget response DTO references itself via `parent`. Resolution must terminate.
        var api = Parse(
            "\"name\": { \"type\": \"string\" }",
            widgetProperties: "\"id\": { \"type\": \"string\" }, \"parent\": { \"$ref\": \"#/components/schemas/scaleway.widget.v1.Widget\" }"
        );

        FieldType(api, "Widget", "parent").Should().Be("Widget?");
    }

    [Fact]
    public void UnwrapsAListResponse_ToItsElementType()
    {
        // A List response is `{ widgets: [Widget], total_count }`; the generated method deals in the element DTO.
        var api = ParseWithList();

        api.ResourceGroups.SelectMany(group => group.Operations)
            .Single(operation => operation.OperationId == "ListWidgets")
            .ResponseTypeName.Should().Be("Widget");
    }

    private static string FieldType(ParsedApi api, string recordName, string fieldName)
    {
        return api.Records.Concat(api.QueryRecords)
            .Single(record => record.Name == recordName)
            .Fields.Single(field => field.Name == fieldName)
            .CSharpType;
    }

    private static ParsedApi Parse(string requestProperties, string extraSchemas = "", string widgetProperties = "\"id\": { \"type\": \"string\" }")
    {
        var schemas = $"\"scaleway.widget.v1.Widget\": {{ \"type\": \"object\", \"properties\": {{ {widgetProperties} }} }}";
        if (extraSchemas.Length > 0)
        {
            schemas += ", " + extraSchemas;
        }

        var json = $$"""
                     {
                       "components": { "schemas": { {{schemas}} } },
                       "paths": {
                         "/widget/v1/widgets": {
                           "post": {
                             "operationId": "CreateWidget",
                             "requestBody": { "content": { "application/json": { "schema": {
                               "type": "object", "properties": { {{requestProperties}} }
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

    private static ParsedApi ParseWithList()
    {
        var json = """
                   {
                     "components": { "schemas": {
                       "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                       "scaleway.widget.v1.ListWidgetsResponse": {
                         "type": "object",
                         "properties": { "widgets": { "type": "array", "items": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } }, "total_count": { "type": "integer" } }
                       }
                     } },
                     "paths": {
                       "/widget/v1/widgets": {
                         "get": {
                           "operationId": "ListWidgets",
                           "responses": { "200": { "content": { "application/json": { "schema": {
                             "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse"
                           } } } } }
                         }
                       }
                     }
                   }
                   """;
        return OpenApiClientParser.Parse("widget", json, ("Widget", "Widgets"));
    }
}
