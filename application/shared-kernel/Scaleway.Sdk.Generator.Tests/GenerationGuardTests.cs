using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     The structural guards that fail generation rather than emit a method that addresses the wrong thing, and the
///     item-path and action-query shapes the emitter does support. Each guard exists because the emitter shipped the
///     defect it now rejects, and was avoided by a prose comment and human memory until it became checkable here.
/// </summary>
public sealed class GenerationGuardTests
{
    private const string CollectionPath = "/widget/v1/regions/{region}/clusters/{cluster_id}/widgets";
    private const string FlatItemPath = "/widget/v1/regions/{region}/widgets/{widget_id}";
    private const string GlobalCollectionPath = "/widget/v1/clusters/{cluster_id}/widgets";

    [Fact]
    public void ItemPath_MayDifferFromTheCollectionPath_WhenEveryItemOperationAgrees()
    {
        // Kapsule's Pool shape: listed under its parent, addressed flat. Supported, because the item path is read
        // from the item operations rather than derived from the collection path.
        var api = Parse(CollectionPath, ("GetWidget", FlatItemPath), ("UpdateWidget", FlatItemPath), ("DeleteWidget", FlatItemPath));

        var group = api.ResourceGroups.Single();
        group.BasePath.Should().Be("widget/v1/regions/{region}/clusters/{cluster_id}/widgets");
        group.ItemPath.Should().Be("widget/v1/regions/{region}/widgets", "the trailing id parameter is the transport's to append");
    }

    [Fact]
    public void EmittedClient_AddressesTheItemPath_RatherThanTheCollectionPath()
    {
        // The parsed model is only half the claim; this pins the emitted call, which is what actually 404s.
        var api = Parse(CollectionPath, ("GetWidget", FlatItemPath));

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("""ListResourcesAsync($"widget/v1/regions/{region}/clusters/{clusterId}/widgets", region""");
        client.Should().Contain("""GetResourceAsync($"widget/v1/regions/{region}/widgets", region, widgetId""");
        client.Should().Contain("public async Task<IReadOnlyList<Widget>> ListWidgetsAsync(string clusterId, ");
        client.Should().Contain("public async Task<Widget?> GetWidgetAsync(string widgetId, ", "the item path carries no cluster id, so the method must not ask for one");
    }

    [Fact]
    public void ItemPathGuard_FailsGeneration_WhenTheIdSegmentIsALiteral()
    {
        // A literal trailing segment is a different disguise for the same defect: the transport substitutes the
        // caller's id there, so the emitted method never addresses the path the schema declares.
        var act = () => Parse(CollectionPath, ("GetWidget", "/widget/v1/regions/{region}/widgets/latest"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*widget.Widget*GetWidget*/widget/v1/regions/{region}/widgets/latest*does not end in a single '{id}' path parameter*");
    }

    [Fact]
    public void ItemPathGuard_FailsGeneration_WhenItemOperationsDisagreeOnTheirPath()
    {
        // One resource group emits one item path. Two operations addressing different prefixes cannot both be
        // honoured, and picking either silently would mislabel the other.
        var act = () => Parse(CollectionPath, ("GetWidget", FlatItemPath), ("DeleteWidget", "/widget/v1/regions/{region}/gadgets/{widget_id}"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*addresses items at two different paths*GetWidget*DeleteWidget*gadgets*");
    }

    [Fact]
    public void VersionGuard_FailsGeneration_WhenTheItemPathSitsAtADifferentApiVersion()
    {
        // A group straddling two versions would otherwise be labelled with whichever version the collection used.
        var act = () => Parse(CollectionPath, ("GetWidget", "/widget/v2/regions/{region}/widgets/{widget_id}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly one API version*v1, v2*");
    }

    [Theory]
    [InlineData("/widget/v1/widgets", "/widget/v1/widgets/{widget_id}")]
    [InlineData(CollectionPath, "/widget/v1/regions/{region}/clusters/{cluster_id}/widgets/{widget_id}")]
    public void ItemPath_MatchingTheCollectionPathPlusAnId_IsTheOrdinaryCase(string collectionPath, string itemPath)
    {
        var api = Parse(collectionPath, ("GetWidget", itemPath));

        api.ResourceGroups.Single().ItemPath.Should().Be(collectionPath.TrimStart('/'));
    }

    [Fact]
    public void ActionWithRequiredQueryParameters_TakesThemAsARecordWithNoDefault()
    {
        // AccessSecretVersionByPath's shape: the query parameters are what name the target, so a method callable
        // without them would compile and fetch something else. A required record has no default, so it cannot be.
        var api = ParseWithAction(("secret_name", true), ("secret_path", true), ("project_id", true));

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        api.QueryRecords.Single(record => record.Name == "PeekWidgetRequest").Fields
            .Select(field => field.Name).Should().Equal("secret_name", "secret_path", "project_id");
        client.Should().Contain("public async Task<Widget?> PeekWidgetAsync(PeekWidgetRequest query, string? region = null, CancellationToken cancellationToken = default)");
        client.Should().Contain("""client.GetAsync($"widget/v1/regions/{region}/peek", region, query.ToQueryParameters(), cancellationToken)""");
    }

    [Fact]
    public void ActionQueryRecord_IsFilledFromTheClientScope_LikeAListQuery()
    {
        // The record goes through the same scope-interface path a List's does, which is the reason to emit one at
        // all: a bare `string projectId` parameter would need the action emitter to re-implement scoping.
        var api = ParseWithAction(("project_id", true));

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("query = scope?.ApplyProject(query) ?? query;");
    }

    [Fact]
    public void ActionWithOnlyOptionalQueryParameters_TakesADefaultedRecord()
    {
        // GetClusterKubeConfig's shape: the path names the target and the query only selects an option, so the
        // record defaults to null and an existing caller keeps its call site.
        var api = ParseWithAction(("redacted", false));

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("public async Task<Widget?> PeekWidgetAsync(PeekWidgetRequest? query = null, string? region = null, CancellationToken cancellationToken = default)");
        client.Should().Contain("""client.GetAsync($"widget/v1/regions/{region}/peek", region, query?.ToQueryParameters(), cancellationToken)""");
    }

    [Fact]
    public void ActionWithNoQueryParameters_KeepsTheQuerylessCall()
    {
        // Unlike a List, an action addresses one fixed thing, so there is nothing an ad-hoc query parameter would
        // mean — and the operations that declare none emit exactly what they did before query support existed.
        var api = ParseWithAction();

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("public async Task<Widget?> PeekWidgetAsync(string? region = null, CancellationToken cancellationToken = default)");
        client.Should().Contain("""client.GetAsync($"widget/v1/regions/{region}/peek", region, cancellationToken)""");
    }

    [Fact]
    public void ActionWhoseResponseIsAListWrapper_ReturnsTheWrapper_NotItsElement()
    {
        // An action is sent with the plain GET, which returns the body as Scaleway sends it. ListOfferSubscriptions is
        // the live case: typed as the element, "{ offer_subscriptions: [...] }" deserialized into an empty record.
        const string json = """
                            {
                              "components": { "schemas": {
                                "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                                "scaleway.widget.v1.ListWidgetsResponse": {
                                  "type": "object",
                                  "properties": { "widgets": { "type": "array", "items": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } }, "total_count": { "type": "integer" } }
                                }
                              } },
                              "paths": {
                                "/widget/v1/regions/{region}/widgets": { "get": {
                                  "operationId": "ListWidgets",
                                  "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse" } } } } }
                                } },
                                "/widget/v1/regions/{region}/widget-subscriptions": { "get": {
                                  "operationId": "ListWidgetSubscriptions",
                                  "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse" } } } } }
                                } }
                              }
                            }
                            """;

        var api = OpenApiClientParser.Parse("widget", json, [("Widget", "Widgets")], ["ListWidgetSubscriptions"]);
        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("public async Task<ListWidgetsResponse?> ListWidgetSubscriptionsAsync(");
        client.Should().Contain("public async Task<IReadOnlyList<Widget>> ListWidgetsAsync(", "the CRUD List keeps unwrapping to the element");
        api.Records.Should().Contain(record => record.Name == "ListWidgetsResponse");
    }

    [Fact]
    public void ActionQueryGuard_FailsGeneration_WhenAPostActionDeclaresQueryParameters()
    {
        // The empty-body POST trigger goes through CreateResourceAsync, which sends a body and no query string.
        // Emitting one anyway would drop the parameters — the defect query support exists to remove.
        var act = () => ParseWithAction("post", ("dry_run", false));

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*PeekWidget*POST declaring query parameter(s)*CreateResourceAsync sends a body, not a query string*");
    }

    [Fact]
    public void AccessSecretVersionByPath_GeneratesCorrectly_AgainstTheCheckedInSchema()
    {
        // The operation defect (a) kept out, proved against the real schema rather than a shape resembling it.
        // It stays out of SchemaSource.ClientActionOperations until the rest of Secret Manager joins (W21); this
        // is what says the exclusion is now about scope rather than about the emitter.
        var json = SchemaSource.YamlToJson(File.ReadAllText(SchemaFiles.SchemaPath("secret-manager")));

        var api = OpenApiClientParser.Parse("secret", json, [("Secret", "Secrets")], ["AccessSecretVersionByPath"]);
        var client = ClientEmitter.EmitClient(api, "Scaleway.Sdk.Secret.V1Beta1", "SecretApi");

        api.QueryRecords.Single(record => record.Name == "AccessSecretVersionByPathRequest").Fields
            .Select(field => field.Name).Should().BeEquivalentTo("secret_path", "secret_name", "project_id");

        // The whole method body rather than a line at a time: the scope fill is a line ListSecretsAsync emits too,
        // so a per-line assertion passes even when the action drops it.
        client.Should().Contain(
            """
                public async Task<AccessSecretVersionResponse?> AccessSecretVersionByPathAsync(string revision, AccessSecretVersionByPathRequest query, string? region = null, CancellationToken cancellationToken = default)
                {
                    region ??= scope?.Region ?? throw new InvalidOperationException("This operation is region-scoped: pass a region or use a client created with a default region.");
                    query = scope?.ApplyProject(query) ?? query;
                    var response = await client.GetAsync($"secret-manager/v1beta1/regions/{region}/secrets-by-path/versions/{revision}/access", region, query.ToQueryParameters(), cancellationToken);
                    return response?.Deserialize(Json.AccessSecretVersionResponse);
                }
            """
        );
    }

    [Fact]
    public void RegionlessPath_BindsItsParentId_RatherThanEmittingTheTokenVerbatim()
    {
        // IAM's shape, and the reason this is not hypothetical: it is the only region-less service, so its
        // sub-resource collections ("groups/{group_id}/members") are the one place a parent id appears on a path
        // with no region or zone to make the literal interpolated. Nothing in the allow-list is this shape yet —
        // W23 adds the first — so the emitted text is what says the emitter can address it.
        var api = Parse(GlobalCollectionPath, ("GetWidget", $"{GlobalCollectionPath}/{{widget_id}}"));

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("""ListResourcesAsync($"widget/v1/clusters/{clusterId}/widgets", string.Empty""");
        client.Should().Contain("""GetResourceAsync($"widget/v1/clusters/{clusterId}/widgets", string.Empty""");
        client.Should().Contain("public static string WidgetCollectionPath(string clusterId)");
        client.Should().Contain("public async Task<Widget?> GetWidgetAsync(string clusterId, string widgetId, ");
    }

    [Fact]
    public void PathWithNoPlaceholder_StaysAPlainLiteral()
    {
        // The other half of the same decision: interpolating a path with nothing to bind would churn every
        // region-less literal IAM already emits, for no gain.
        var api = Parse("/widget/v1/widgets", ("GetWidget", "/widget/v1/widgets/{widget_id}"));

        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("""ListResourcesAsync("widget/v1/widgets", string.Empty""");
    }

    [Fact]
    public void PathLiteralGuard_FailsGeneration_WhenAPlaceholderMatchesNoEmittedParameter()
    {
        // A token outside the snake_case shape ParentPathParameters reads is bound by nothing, so interpolating
        // it would not compile and emitting it plainly would send the brace as part of the URL. Neither is a
        // thing to ship, and the failure names the path rather than surfacing as a build error in generated code.
        var api = Parse("/widget/v1/clusters/{clusterId}/widgets", ("GetWidget", "/widget/v1/clusters/{clusterId}/widgets/{widget_id}"));

        var act = () => ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*widget/v1/clusters/{clusterId}/widgets*'{clusterId}'*no emitted parameter binds*sent verbatim*");
    }

    private static ParsedApi ParseWithAction(params (string Name, bool Required)[] queryParameters)
    {
        return ParseWithAction("get", queryParameters);
    }

    private static ParsedApi ParseWithAction(string verb, params (string Name, bool Required)[] queryParameters)
    {
        var parameters = queryParameters.Select(parameter =>
            {
                var required = parameter.Required ? "\"required\": true, " : "";
                return $$"""{ "in": "query", "name": "{{parameter.Name}}", {{required}}"schema": { "type": "string" } }""";
            }
        );

        var json = $$"""
                     {
                       "components": { "schemas": {
                         "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                         "scaleway.widget.v1.ListWidgetsResponse": {
                           "type": "object",
                           "properties": { "widgets": { "type": "array", "items": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } }, "total_count": { "type": "integer" } }
                         }
                       } },
                       "paths": {
                         "/widget/v1/regions/{region}/widgets": { "get": {
                           "operationId": "ListWidgets",
                           "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse" } } } } }
                         } },
                         "/widget/v1/regions/{region}/peek": { "{{verb}}": {
                           "operationId": "PeekWidget",
                           "parameters": [ {{string.Join(", ", parameters)}} ],
                           "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                         } }
                       }
                     }
                     """;
        return OpenApiClientParser.Parse("widget", json, [("Widget", "Widgets")], ["PeekWidget"]);
    }

    private static ParsedApi Parse(string collectionPath, params (string OperationId, string Path)[] itemOperations)
    {
        var pathEntries = new List<string>
        {
            $$"""
              "{{collectionPath}}": { "get": {
                "operationId": "ListWidgets",
                "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse" } } } } }
              } }
              """
        };

        foreach (var operationsAtPath in itemOperations.GroupBy(operation => operation.Path))
        {
            var verbs = operationsAtPath.Select(operation =>
                $$"""
                  "{{VerbFor(operation.OperationId)}}": {
                    "operationId": "{{operation.OperationId}}",
                    "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                  }
                  """
            );
            pathEntries.Add($$""""
                              "{{operationsAtPath.Key}}": { {{string.Join(", ", verbs)}} }
                              """"
            );
        }

        var json = $$"""
                     {
                       "components": { "schemas": {
                         "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                         "scaleway.widget.v1.ListWidgetsResponse": {
                           "type": "object",
                           "properties": { "widgets": { "type": "array", "items": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } }, "total_count": { "type": "integer" } }
                         }
                       } },
                       "paths": { {{string.Join(", ", pathEntries)}} }
                     }
                     """;
        return OpenApiClientParser.Parse("widget", json, ("Widget", "Widgets"));
    }

    // The synthetic schema declares one verb per item operationId, matching how Scaleway names them.
    private static string VerbFor(string operationId)
    {
        return operationId switch
        {
            "GetWidget" => "get",
            "UpdateWidget" => "patch",
            "DeleteWidget" => "delete",
            _ => throw new ArgumentOutOfRangeException(nameof(operationId), operationId, "Unsupported item operation.")
        };
    }
}
