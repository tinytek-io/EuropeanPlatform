using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Pins the emitted C# text for representative shapes. Regeneration is already byte-reproducible; these say
///     <em>what</em> it reproduces, so an accidental change to the emitted enum/record/method shape is caught at the
///     generator rather than only downstream.
/// </summary>
public sealed class EmitterOutputTests
{
    // A request whose body declares two fields sharing one x-one-of group ("scope").
    private const string OneOfSchema = """
                                       {
                                         "components": { "schemas": { "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } } } },
                                         "paths": {
                                           "/widget/v1/widgets": {
                                             "post": {
                                               "operationId": "CreateWidget",
                                               "requestBody": { "content": { "application/json": { "schema": {
                                                 "type": "object",
                                                 "properties": {
                                                   "by_name": { "type": "string", "nullable": true, "x-one-of": "scope" },
                                                   "by_id": { "type": "string", "nullable": true, "x-one-of": "scope" }
                                                 }
                                               } } } },
                                               "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                             }
                                           }
                                         }
                                       }
                                       """;

    private const string Schema = """
                                  {
                                    "components": {
                                      "schemas": {
                                        "scaleway.widget.v1.Widget": {
                                          "type": "object",
                                          "properties": { "id": { "type": "string" }, "name": { "type": "string" } }
                                        },
                                        "scaleway.widget.v1.ListWidgetsResponse": {
                                          "type": "object",
                                          "properties": { "widgets": { "type": "array", "items": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } }, "total_count": { "type": "integer" } }
                                        }
                                      }
                                    },
                                    "paths": {
                                      "/widget/v1/regions/{region}/widgets": {
                                        "post": {
                                          "operationId": "CreateWidget",
                                          "summary": "Create a widget",
                                          "requestBody": { "content": { "application/json": { "schema": {
                                            "type": "object",
                                            "properties": {
                                              "name": { "type": "string" },
                                              "project_id": { "type": "string" },
                                              "privacy": { "type": "string", "enum": ["public", "private"] }
                                            }
                                          } } } },
                                          "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                        },
                                        "get": {
                                          "operationId": "ListWidgets",
                                          "parameters": [
                                            { "in": "query", "name": "order_by", "schema": { "type": "string", "enum": ["created_at_asc", "created_at_desc"] } },
                                            { "in": "query", "name": "page", "schema": { "type": "integer" } }
                                          ],
                                          "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse" } } } } }
                                        }
                                      },
                                      "/widget/v1/regions/{region}/widgets/{widget_id}": {
                                        "get": {
                                          "operationId": "GetWidget",
                                          "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                        }
                                      }
                                    }
                                  }
                                  """;

    // Descriptions as Scaleway writes them: on the component, on most properties, absent on some, and wrapped
    // across lines on the long ones.
    private const string DocumentedSchema = """
                                            {
                                              "components": {
                                                "schemas": {
                                                  "scaleway.widget.v1.Widget": {
                                                    "type": "object",
                                                    "description": "A thing the API manages.",
                                                    "properties": {
                                                      "id": { "type": "string", "description": "UUID of the widget." },
                                                      "name": { "type": "string", "description": "Name of the widget, unique in a region." },
                                                      "undocumented": { "type": "boolean" },
                                                      "note": { "type": "string", "description": "First sentence.\n\nSecond sentence, wrapped by Scaleway.\n" }
                                                    }
                                                  }
                                                }
                                              },
                                              "paths": {
                                                "/widget/v1/regions/{region}/widgets": {
                                                  "post": {
                                                    "operationId": "CreateWidget",
                                                    "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "name": { "type": "string" } } } } } },
                                                    "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                                  }
                                                }
                                              }
                                            }
                                            """;

    // One nested object, inlined twice with the same shape and different wording — the entity's copy and the
    // create request's, which is how Scaleway denormalizes them.
    private const string SharedNestedObjectSchema = """
                                                    {
                                                      "components": {
                                                        "schemas": {
                                                          "scaleway.widget.v1.Widget": {
                                                            "type": "object",
                                                            "properties": {
                                                              "settings": { "type": "object", "properties": { "size": { "type": "integer", "description": "Size of the widget." } } }
                                                            }
                                                          }
                                                        }
                                                      },
                                                      "paths": {
                                                        "/widget/v1/regions/{region}/widgets": {
                                                          "post": {
                                                            "operationId": "CreateWidget",
                                                            "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": {
                                                              "settings": { "type": "object", "properties": { "size": { "type": "integer", "description": "Size to give the widget." } } }
                                                            } } } } },
                                                            "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                                          }
                                                        }
                                                      }
                                                    }
                                                    """;

    // A collection nested under its parent: "widgets/{widget_id}/parts".
    private const string SubResourceSchema = """
                                             {
                                               "components": { "schemas": { "scaleway.widget.v1.Part": { "type": "object", "properties": { "id": { "type": "string" } } } } },
                                               "paths": {
                                                 "/widget/v1/regions/{region}/widgets/{widget_id}/parts": {
                                                   "post": {
                                                     "operationId": "CreatePart",
                                                     "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "data": { "type": "string" } } } } } },
                                                     "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Part" } } } } }
                                                   }
                                                 }
                                               }
                                             }
                                             """;

    // A component nothing references, and an inline object that normalizes onto its name — two different types
    // that the naming scheme collapses onto one name.
    private const string CollidingInlineObjectSchema = """
                                                       {
                                                         "components": {
                                                           "schemas": {
                                                             "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                                                             "scaleway.widget.v1.WidgetSettings": { "type": "object", "properties": { "colour": { "type": "string" } } }
                                                           }
                                                         },
                                                         "paths": {
                                                           "/widget/v1/regions/{region}/widgets": {
                                                             "post": {
                                                               "operationId": "CreateWidget",
                                                               "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": {
                                                                 "settings": { "type": "object", "properties": { "size": { "type": "integer" } } }
                                                               } } } } },
                                                               "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                                             }
                                                           }
                                                         }
                                                       }
                                                       """;

    private static readonly ParsedApi Api = OpenApiClientParser.Parse("widget", Schema, ("Widget", "Widgets"));
    private static readonly string Models = ClientEmitter.EmitModels(Api, "Test.Ns");
    private static readonly string Client = ClientEmitter.EmitClient(Api, "Test.Ns", "WidgetApi");

    [Fact]
    public void Enum_IsEmittedAsAnExtensibleRecordStructWithKnownValues()
    {
        Models.Should().Contain("public readonly record struct WidgetPrivacy(string Value) : IScalewayEnumValue<WidgetPrivacy>");
        Models.Should().Contain("public static readonly WidgetPrivacy Public = new(\"public\");");
        Models.Should().Contain("public static WidgetPrivacy FromValue(string value) => new(value);");
        Models.Should().Contain("public static implicit operator WidgetPrivacy(string value) => new(value);");
        Models.Should().Contain("public override string ToString() => Value;");
    }

    [Fact]
    public void ARequestDeclaringProjectId_ImplementsTheScopeInterfaceAndInjector()
    {
        Models.Should().Contain("public sealed record CreateWidgetRequest : IProjectScopedRequest<CreateWidgetRequest>");
        Models.Should().Contain("public CreateWidgetRequest WithProjectId(string projectId) => this with { ProjectId = projectId };");
    }

    [Fact]
    public void AListQueryRecord_EmitsTypedPropertiesAndToQueryParameters()
    {
        Models.Should().Contain("public sealed record ListWidgetsRequest");
        Models.Should().Contain("public ListWidgetsOrderBy? OrderBy { get; init; }", "the order_by enum is typed, not a magic string");
        // A list of pairs, not a dictionary: OpenAPI query arrays are repeated (tags=a&tags=b), which a
        // dictionary cannot express.
        Models.Should().Contain("public List<KeyValuePair<string, string>> ToQueryParameters()");
        Models.Should().Contain("if (OrderBy is not null) parameters.Add(new KeyValuePair<string, string>(\"order_by\", OrderBy.Value.Value));");
    }

    [Fact]
    public void ACreateMethod_IsNamedForItsOperationIdAndFillsScope()
    {
        Client.Should().Contain("public sealed class WidgetApi(IScalewayApiClient client, ScalewayRequestScope? scope = null)");
        Client.Should().Contain("public async Task<Widget> CreateWidgetAsync(CreateWidgetRequest request, string? region = null, CancellationToken cancellationToken = default)");
        Client.Should().Contain("region ??= scope?.Region ?? throw new InvalidOperationException(", "the scope's default region fills in when the call passes none");
        Client.Should().Contain("request = scope?.ApplyProject(request) ?? request;");
    }

    [Fact]
    public void EachMethod_CarriesAnXmlDocTracingItToTheScalewayOperation()
    {
        Client.Should().Contain("///     Scaleway operation <c>CreateWidget</c> — <c>POST /widget/v1/regions/{region}/widgets</c> (V1).");
    }

    [Fact]
    public void ASubResourceGroup_BindsItsParentIdAsALeadingParameter()
    {
        // "widgets/{widget_id}/parts" — the parent token becomes a method parameter and the interpolated path
        // binds it, so a sub-resource operation is unreachable without its parent's id.
        var api = OpenApiClientParser.Parse("widget", SubResourceSchema, ("Part", "Parts"));
        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("public async Task<Part> CreatePartAsync(string widgetId, CreatePartRequest request, string? region = null, CancellationToken cancellationToken = default)");
        client.Should().Contain("$\"widget/v1/regions/{region}/widgets/{widgetId}/parts\"");
    }

    [Fact]
    public void ADeclaredAction_EmitsABodylessGetWithPathTokensAsParameters()
    {
        // "…/{revision}/access" doesn't fit the CRUD shape; a declared action emits a GET whose path tokens are
        // parameters in path order, returning the action's own response record (null on 404).
        const string actionSchema = """
                                    {
                                      "components": { "schemas": {
                                        "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                                        "scaleway.widget.v1.PeekWidgetResponse": { "type": "object", "properties": { "data": { "type": "string" } } }
                                      } },
                                      "paths": {
                                        "/widget/v1/regions/{region}/widgets": {
                                          "post": {
                                            "operationId": "CreateWidget",
                                            "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "name": { "type": "string" } } } } } },
                                            "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                          }
                                        },
                                        "/widget/v1/regions/{region}/widgets/{widget_id}/revisions/{revision}/peek": {
                                          "get": {
                                            "operationId": "PeekWidget",
                                            "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.PeekWidgetResponse" } } } } }
                                          }
                                        }
                                      }
                                    }
                                    """;

        var api = OpenApiClientParser.Parse("widget", actionSchema, [("Widget", "Widgets")], ["PeekWidget"]);
        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("public async Task<PeekWidgetResponse?> PeekWidgetAsync(string widgetId, string revision, string? region = null, CancellationToken cancellationToken = default)");
        client.Should().Contain("client.GetAsync($\"widget/v1/regions/{region}/widgets/{widgetId}/revisions/{revision}/peek\", region, cancellationToken)");
    }

    [Fact]
    public void AListMethod_TakesTheTypedQueryRecord()
    {
        Client.Should().Contain("public async Task<IReadOnlyList<Widget>> ListWidgetsAsync(ListWidgetsRequest? query = null, string? region = null, CancellationToken cancellationToken = default)");
    }

    [Fact]
    public void AFractionalNumber_MapsToDoubleRatherThanLong()
    {
        // JSON Schema separates "integer" (whole) from "number" (real). Mapping both to long deserialized fine
        // until Scaleway returned a fractional one — Kapsule's scale_down_utilization_threshold is 0.5 — and then
        // threw at the deserializer, mid-deploy, after the cluster had already been created.
        const string numericSchema = """
                                     {
                                       "components": { "schemas": {
                                         "scaleway.widget.v1.Widget": { "type": "object", "properties": {
                                           "threshold": { "type": "number", "format": "float" },
                                           "count": { "type": "integer", "format": "int32" },
                                           "ratios": { "type": "array", "items": { "type": "number" } }
                                         } }
                                       } },
                                       "paths": {
                                         "/widget/v1/regions/{region}/widgets": {
                                           "post": {
                                             "operationId": "CreateWidget",
                                             "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "name": { "type": "string" } } } } } },
                                             "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } } }
                                           }
                                         }
                                       }
                                     }
                                     """;

        var api = OpenApiClientParser.Parse("widget", numericSchema, ("Widget", "Widgets"));
        var models = ClientEmitter.EmitModels(api, "Test.Ns");

        models.Should().Contain("public double? Threshold { get; init; }");
        models.Should().Contain("public long? Count { get; init; }");
        models.Should().Contain("public double[]? Ratios { get; init; }");
    }

    [Fact]
    public void AListMethodWithoutQueryParameters_FallsBackToThePairListTheTransportTakes()
    {
        // The fallback shape is a list of pairs, not a dictionary: a dictionary cannot repeat a key, which is
        // what OpenAPI's style=form/explode=true default requires, and it does not convert to the transport's
        // parameter type — so emitting one produced a client that did not compile.
        const string noQuerySchema = """
                                     {
                                       "components": { "schemas": {
                                         "scaleway.widget.v1.Widget": { "type": "object", "properties": { "id": { "type": "string" } } },
                                         "scaleway.widget.v1.ListWidgetsResponse": { "type": "object", "properties": { "widgets": { "type": "array", "items": { "$ref": "#/components/schemas/scaleway.widget.v1.Widget" } } } }
                                       } },
                                       "paths": {
                                         "/widget/v1/regions/{region}/widgets": {
                                           "get": {
                                             "operationId": "ListWidgets",
                                             "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/scaleway.widget.v1.ListWidgetsResponse" } } } } }
                                           }
                                         }
                                       }
                                     }
                                     """;

        var api = OpenApiClientParser.Parse("widget", noQuerySchema, ("Widget", "Widgets"));
        var client = ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi");

        client.Should().Contain("ListWidgetsAsync(IReadOnlyList<KeyValuePair<string, string>>? query = null");
        client.Should().NotContain("Dictionary<string, string>? query");
    }

    [Fact]
    public void AMutuallyExclusiveGroup_EmitsAValidateRejectingMoreThanOneMember()
    {
        var api = OpenApiClientParser.Parse("widget", OneOfSchema, ("Widget", "Widgets"));
        var models = ClientEmitter.EmitModels(api, "Test.Ns");

        models.Should().Contain("public void Validate()");
        models.Should().Contain("if ((ByName is null ? 0 : 1) + (ById is null ? 0 : 1) > 1)");
        models.Should().Contain("'scope' is a mutually-exclusive group — set only one of ByName, ById");
    }

    /// <summary>
    ///     Scaleway documents its properties heavily and the generator used to drop every word of it at parse time,
    ///     so the SDK's largest surface — the record types — shipped with no documentation at all while the schema
    ///     next to it had the text.
    /// </summary>
    [Fact]
    public void ARecordPropertyAndItsType_CarryTheSchemasOwnDescription()
    {
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", DocumentedSchema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("/// <summary>A thing the API manages.</summary>\npublic sealed record Widget");
        models.Should().Contain("    /// <summary>UUID of the widget.</summary>\n    public string? Id { get; init; }");
        models.Should().Contain("    /// <summary>Name of the widget, unique in a region.</summary>\n    public string? Name { get; init; }");
    }

    /// <summary>
    ///     The documentation standard's honesty rule. Emitting <c>&lt;summary&gt;Name.&lt;/summary&gt;</c>
    ///     on a property called <c>Name</c> would satisfy a documentation gate and a doc generator while telling a
    ///     reader strictly less than the silence does — silence is at least visibly absent.
    /// </summary>
    [Fact]
    public void APropertyTheSchemaDoesNotDescribe_GetsNoSummaryRatherThanOneDerivedFromItsName()
    {
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", DocumentedSchema, ("Widget", "Widgets")), "Test.Ns");

        LinePreceding(models, "    public bool? Undocumented { get; init; }").Should().NotContain("<summary>");
    }

    [Fact]
    public void ADescriptionSpanningSeveralSchemaLines_CollapsesToOneSummaryLine()
    {
        // A YAML block scalar keeps its blank lines and its trailing newline, and XmlEscape turns each into a
        // space — so escaping alone yields "successful.  Possible" and a space before the closing tag. Not
        // hypothetical: it is what Scaleway's liveness_probe and startup_probe descriptions do today.
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", DocumentedSchema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("/// <summary>First sentence. Second sentence, wrapped by Scaleway.</summary>");
        models.Should().NotContain("Scaleway. </summary>", "a block scalar's trailing newline must not survive as a trailing space");
    }

    /// <summary>
    ///     Documentation is not shape. Scaleway inlines the same nested object on an entity and on its create/update
    ///     requests and words the two descriptions differently; comparing whole fields to decide identity would stop
    ///     collapsing them and emit a near-duplicate record per context — which is the thing
    ///     <c>NormalizeSharedName</c> exists to prevent, undone by a documentation change.
    /// </summary>
    [Fact]
    public void TwoInlineCopiesOfOneNestedObject_StillCollapse_WhenOnlyTheirDescriptionsDiffer()
    {
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", SharedNestedObjectSchema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("public sealed record WidgetSettings");
        models.Should().NotContain("public sealed record CreateWidgetRequestSettings", "the request's copy of the nested object is the same type as the response's");
        models.Should().Contain("public WidgetSettings? Settings { get; init; }");
    }

    /// <summary>
    ///     R2's <c>&lt;param&gt;</c> requirement. Every parameter of a generated method is
    ///     documented, and each tag states a fact the declaration does not carry — never the parameter's own name
    ///     restated. Completeness itself is the compiler's job once XML documentation is on: CS1573 fires for a
    ///     parameter left out while its siblings are documented.
    /// </summary>
    [Fact]
    public void EveryParameterOfAGeneratedMethod_CarriesAParamTag()
    {
        Client.Should().Contain("/// <param name=\"request\">The request body, serialized with the snake_case field names Scaleway declares.</param>");
        Client.Should().Contain("/// <param name=\"region\">The region to address. Falls back to the client scope's default when null; a client created without one must pass it.</param>");
        Client.Should().Contain("/// <param name=\"cancellationToken\">Passed to the underlying HTTP request.</param>");
        Client.Should().Contain("/// <param name=\"widgetId\">Scaleway ID of the Widget to address. The transport appends it to <c>widget/v1/regions/{region}/widgets</c>.</param>");
    }

    [Fact]
    public void AParentIdParameter_NamesThePathSegmentItBinds()
    {
        // "clusterId" does not say which segment it fills, and the emitted signature does not either — the path
        // literal is inside the method body. This is the fact worth stating.
        var api = OpenApiClientParser.Parse("widget", SubResourceSchema, ("Part", "Parts"));

        ClientEmitter.EmitClient(api, "Test.Ns", "WidgetApi").Should().Contain("/// <param name=\"widgetId\">Binds the <c>{widget_id}</c> segment of the path.</param>");
    }

    /// <summary>
    ///     The static path builder takes its region rather than falling back to a client scope — it has none — so
    ///     documenting it with the scope-fallback sentence the instance methods use would be false.
    /// </summary>
    [Fact]
    public void ACollectionPathBuilder_DocumentsItsRegionAsSupplied_NotAsScopeDefaulted()
    {
        Client.Should().Contain("/// <param name=\"region\">The region to build the path for.</param>");
    }

    /// <summary>
    ///     Every generated member the schema says nothing about still carries an upstream fact:
    ///     the component a reader follows to Scaleway's documentation, the call a synthesized record belongs to, or
    ///     the wire spelling. None of them restates the C# name.
    /// </summary>
    [Fact]
    public void ARecordTheSchemaDoesNotDescribe_NamesItsUpstreamOrigin()
    {
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", Schema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("/// <remarks>Scaleway schema component <c>scaleway.widget.v1.Widget</c>.</remarks>");
        models.Should().Contain("/// <remarks>Body of <c>POST /widget/v1/regions/{region}/widgets</c> (Scaleway operation <c>CreateWidget</c>).</remarks>");
        models.Should().Contain("/// <remarks>Query parameters of <c>GET /widget/v1/regions/{region}/widgets</c> (Scaleway operation <c>ListWidgets</c>).</remarks>");
    }

    [Fact]
    public void ARecordTheSchemaDoesDescribe_KeepsItsDescriptionAndNotItsOrigin()
    {
        // The origin is a fallback, not an annotation. Where Scaleway supplies words they say more than the
        // component name does, and emitting both would bury the useful half.
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", DocumentedSchema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("/// <summary>A thing the API manages.</summary>\npublic sealed record Widget");
        models.Should().NotContain("Scaleway schema component <c>scaleway.widget.v1.Widget</c>");
    }

    [Fact]
    public void APropertyTheSchemaDoesNotDescribe_NamesItsWireField()
    {
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", DocumentedSchema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("    /// <remarks>Scaleway field <c>undocumented</c>.</remarks>\n    public bool? Undocumented { get; init; }");
        models.Should().Contain("    /// <summary>UUID of the widget.</summary>\n    public string? Id { get; init; }", "a described property keeps its description instead");
    }

    /// <summary>
    ///     The precision that makes the component remark worth trusting. A nested inline object can normalize onto a
    ///     name a component also uses, and it is then a different type resolved from a different schema — claiming
    ///     the component for it would point a reader at documentation for something else.
    /// </summary>
    [Fact]
    public void AnInlineObject_DoesNotClaimAComponentItMerelySharesANameWith()
    {
        var models = ClientEmitter.EmitModels(OpenApiClientParser.Parse("widget", CollidingInlineObjectSchema, ("Widget", "Widgets")), "Test.Ns");

        models.Should().Contain("public sealed record WidgetSettings", "the inline object still resolves under the normalized name");
        models.Should().NotContain("Scaleway schema component <c>scaleway.widget.v1.WidgetSettings</c>", "this record's fields came from the inline object, not from that component");
    }

    [Fact]
    public void AnExtensibleEnum_DocumentsItsWireValuesAndItsToStringBeingTheWireValue()
    {
        Models.Should().Contain("/// <param name=\"Value\">The string as it travels on the wire, which is what equality and serialization use.</param>");
        Models.Should().Contain("    /// <remarks>Scaleway value <c>created_at_asc</c>.</remarks>\n    public static readonly ListWidgetsOrderBy CreatedAtAsc = new(\"created_at_asc\");");
        Models.Should().Contain("/// <summary>The wire value — not the C# member name, which differs wherever Scaleway's spelling does not fit .NET casing.</summary>");
    }

    [Fact]
    public void TheGeneratedApiClassAndItsGeneratedHelpers_AreDocumented()
    {
        Client.Should().Contain("///     Typed client for Scaleway's <c>widget</c> API (V1). One method per operation, each named");
        Client.Should().Contain("/// <param name=\"client\">Transport carrying the base address, the auth token, snake_case serialization and pagination.</param>");
        Models.Should().Contain("/// <summary>Returns a copy carrying <c>project_id</c>. A scoped client calls this before sending, so a caller never repeats the id.</summary>");
        Models.Should().Contain("    ///     Flattens the properties that are set into the pairs the transport sends; an unset property is omitted.");
    }

    /// <summary>The line above a member, which is where its doc comment would be. Trailing attributes are not in play here.</summary>
    private static string LinePreceding(string source, string line)
    {
        var lines = source.Split('\n');
        var index = Array.IndexOf(lines, line);
        index.Should().BeGreaterThan(0, $"the emitted output must contain '{line.Trim()}' for this assertion to mean anything");

        return lines[index - 1];
    }
}
