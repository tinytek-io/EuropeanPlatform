using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     An object with no schema shape is a marker (Scaleway's <c>ipam_config</c>, its Go SDK's empty struct), so
///     the generator now emits it as an empty record settable to <c>{}</c> rather than dropping it. What genuinely
///     can't be typed — an array of untyped items, a field with no type at all — is still recorded as dropped, so
///     those gaps stay visible.
/// </summary>
public sealed class DroppedFieldTests
{
    [Fact]
    public void AnUntypedObject_BecomesAnEmptyMarkerRecord_NotDropped()
    {
        var api = SchemaSource.ParseClientApi("vpc", Vpc("\"ipam_config\": { \"type\": \"object\", \"nullable\": true }"))!;

        var request = api.Records.Single(record => record.Name == "CreatePrivateNetworkRequest");
        request.Fields.Should().Contain(field => field.Name == "ipam_config" && field.CSharpType == "PrivateNetworkIpamConfig?");
        api.Records.Should().Contain(record => record.Name == "PrivateNetworkIpamConfig", "the marker is a real (empty) record");
        api.DroppedFields.Should().BeEmpty("a marker object is representable, not a gap");
    }

    [Fact]
    public void AnArrayOfUntypedObjects_IsStillDropped()
    {
        var api = SchemaSource.ParseClientApi("vpc", Vpc("\"blobs\": { \"type\": \"array\", \"items\": { \"type\": \"object\" } }"))!;

        api.DroppedFields.Should().ContainSingle().Which.Reason.Should().Be("array with an unsupported element type");
    }

    [Fact]
    public void AnUntypedArray_TakesTheElementTypeOfTheSameNamedTypedArray()
    {
        // Scaleway's update bodies declare a nullable array without items while the resource still types it.
        var schema = Vpc("\"tags\": { \"type\": [\"array\", \"null\"] }", "\"tags\": { \"type\": \"array\", \"items\": { \"type\": \"string\" } }");

        var api = SchemaSource.ParseClientApi("vpc", schema)!;

        var request = api.Records.Single(record => record.Name == "CreatePrivateNetworkRequest");
        request.Fields.Should().Contain(field => field.Name == "tags" && field.CSharpType == "string[]?");
        api.DroppedFields.Should().BeEmpty();
    }

    [Fact]
    public void AnUntypedArray_WhoseNameIsTypedTwoWays_IsStillDropped()
    {
        var schema = Vpc(
            "\"tags\": { \"type\": [\"array\", \"null\"] }",
            "\"tags\": { \"type\": \"array\", \"items\": { \"type\": \"string\" } }",
            "\"tags\": { \"type\": \"array\", \"items\": { \"type\": \"integer\" } }"
        );

        var api = SchemaSource.ParseClientApi("vpc", schema)!;

        api.DroppedFields.Should().ContainSingle().Which.FieldName.Should().Be("tags");
    }

    [Fact]
    public void AFieldWithNoType_IsStillDropped()
    {
        var api = SchemaSource.ParseClientApi("vpc", Vpc("\"mystery\": { \"description\": \"no type here\" }"))!;

        var dropped = api.DroppedFields.Should().ContainSingle().Which;
        dropped.FieldName.Should().Be("mystery");
        dropped.Reason.Should().Be("no type declared");
    }

    [Fact]
    public void ParseClientApi_HasNoDroppedFields_WhenEveryFieldIsTypeable()
    {
        var api = SchemaSource.ParseClientApi("vpc", Vpc())!;

        api.DroppedFields.Should().BeEmpty();
    }

    [Fact]
    public void DroppedFields_AcrossSchemas_AreCollectedTogether()
    {
        var current = new Dictionary<string, string> { ["vpc"] = Vpc("\"mystery\": { \"description\": \"no type\" }") };

        var dropped = SchemaDriftCheck.DroppedFields(current);

        dropped.Should().ContainSingle().Which.FieldName.Should().Be("mystery");
    }

    private static string Vpc(string extraProperty = "", string privateNetworkProperty = "", string routeProperty = "")
    {
        var extra = extraProperty.Length > 0 ? $", {extraProperty}" : "";
        var privateNetworkExtra = privateNetworkProperty.Length > 0 ? $", {privateNetworkProperty}" : "";
        var routeExtra = routeProperty.Length > 0 ? $", {routeProperty}" : "";
        return $$"""
                 {
                   "components": {
                     "schemas": {
                       "scaleway.vpc.v2.PrivateNetwork": {
                         "type": "object",
                         "properties": { "id": { "type": "string" }{{privateNetworkExtra}} }
                       },
                       "scaleway.vpc.v2.Route": {
                         "type": "object",
                         "properties": { "id": { "type": "string" }{{routeExtra}} }
                       }
                     }
                   },
                   "paths": {
                     "/vpc/v2/regions/{region}/private-networks": {
                       "post": {
                         "operationId": "CreatePrivateNetwork",
                         "requestBody": {
                           "content": { "application/json": { "schema": {
                             "type": "object",
                             "properties": { "name": { "type": "string" }{{extra}} }
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
