using System.Text;
using System.Text.RegularExpressions;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     Emits a typed HTTP client for a <see cref="ParsedApi" />: request/response records plus a
///     per-service operations class that layers over the hand-written <c>ScalewayApiClient</c> transport (base URL,
///     <c>X-Auth-Token</c>, snake_case JSON) — so the shipped package gains typed calls with no new dependency.
///     Enum-typed fields become extensible enums (see <c>IScalewayEnumValue{TSelf}</c>), so values Scaleway adds
///     later round-trip instead of throwing.
/// </summary>
public static class ClientEmitter
{
    private const string RequestParameterDescription = "The request body, serialized with the snake_case field names Scaleway declares.";

    public static void Emit(ParsedApi api, string outputDirectory)
    {
        var servicePrefix = ServiceCatalog.GetServicePrefix(api.ServiceName);

        // The version is part of the namespace and the directory so two versions of a service can coexist —
        // Scaleway versions each service independently and moves them off alpha/beta on their own schedule.
        var namespaceName = $"Scaleway.Sdk.{servicePrefix}.{api.ApiVersion}";
        var serviceOutputDirectory = Path.Combine(outputDirectory, "Client", servicePrefix, api.ApiVersion);
        Directory.CreateDirectory(serviceOutputDirectory);

        File.WriteAllText(Path.Combine(serviceOutputDirectory, $"{servicePrefix}Models.cs"), EmitModels(api, namespaceName));
        File.WriteAllText(Path.Combine(serviceOutputDirectory, $"{servicePrefix}Api.cs"), EmitClient(api, namespaceName, $"{servicePrefix}Api"));
    }

    /// <summary>
    ///     Emits an enum as an extensible (string-backed) readonly record struct implementing
    ///     <c>IScalewayEnumValue</c>: known values are discoverable statics, an implicit conversion from string keeps
    ///     literals working, and any value Scaleway adds later round-trips instead of failing deserialization.
    /// </summary>
    private static void AppendEnum(StringBuilder body, ParsedClientEnum parsedEnum)
    {
        body.AppendLine();
        body.AppendLine("/// <summary>");
        body.AppendLine("///     An extensible enum. A value Scaleway adds after this was generated still deserializes and round-trips");
        body.AppendLine("///     rather than throwing, so an unrecognised value is one you have not seen — not an error.");
        body.AppendLine("/// </summary>");
        body.AppendLine("/// <param name=\"Value\">The string as it travels on the wire, which is what equality and serialization use.</param>");
        body.AppendLine($"[JsonConverter(typeof(ScalewayEnumValueConverter<{parsedEnum.Name}>))]");
        body.AppendLine($"public readonly record struct {parsedEnum.Name}(string Value) : IScalewayEnumValue<{parsedEnum.Name}>");
        body.AppendLine("{");
        foreach (var value in parsedEnum.Values)
        {
            // The wire value, carried where a consumer of the package can read it. It is in the initializer beside
            // it, but a NuGet consumer reads XML docs and IntelliSense rather than this source — and the C# name
            // does not always yield it back (unknown_status → UnknownStatus, and Scaleway spells the same concept
            // https_connections_only on one request and https_connection_only on another).
            body.AppendLine($"    /// <remarks>Scaleway value <c>{value}</c>.</remarks>");
            body.AppendLine($"    public static readonly {parsedEnum.Name} {CSharpNaming.ToPascalCase(value)} = new(\"{value}\");");
        }

        body.AppendLine();
        body.AppendLine("    /// <summary>Wraps a wire value this SDK declares no member for, so a value newer than the SDK stays usable.</summary>");
        body.AppendLine("    /// <param name=\"value\">The wire value, as Scaleway spells it.</param>");
        body.AppendLine($"    public static {parsedEnum.Name} FromValue(string value) => new(value);");
        body.AppendLine();
        body.AppendLine("    /// <summary>Lets a wire value stand in for a member, so a value newer than this SDK can still be sent.</summary>");
        body.AppendLine("    /// <param name=\"value\">The wire value, as Scaleway spells it.</param>");
        body.AppendLine($"    public static implicit operator {parsedEnum.Name}(string value) => new(value);");
        body.AppendLine();
        body.AppendLine("    /// <summary>The wire value — not the C# member name, which differs wherever Scaleway's spelling does not fit .NET casing.</summary>");
        body.AppendLine("    public override string ToString() => Value;");
        body.AppendLine("}");
    }

    private static string PropertyNameFor(ParsedRecord record, ParsedRecordField field)
    {
        var propertyName = CSharpNaming.ToPascalCase(field.Name);

        // A property cannot share its enclosing record's name (CS0542).
        return propertyName == record.Name ? $"{propertyName}Value" : propertyName;
    }

    /// <summary>
    ///     The records that need a <c>Validate()</c>: those declaring an <c>x-one-of</c> group with more than one
    ///     emitted member, plus — transitively — any record reaching one through a field, so validating a request
    ///     validates the whole tree hanging off it.
    /// </summary>
    private static HashSet<string> ComputeValidatedRecords(List<ParsedRecord> records)
    {
        var validated = records.Where(HasEnforceableGroup).Select(record => record.Name).ToHashSet(StringComparer.Ordinal);

        // A record whose nested type validates must validate too, so the requirement propagates up the graph.
        // Iterate to a fixpoint: each pass can only add names, and the record set is finite, so this terminates.
        bool addedThisPass;
        do
        {
            addedThisPass = false;
            foreach (var record in records.Where(record => !validated.Contains(record.Name)))
            {
                if (record.Fields.Any(field => ElementTypeName(field.CSharpType) is { } element && validated.Contains(element)))
                {
                    validated.Add(record.Name);
                    addedThisPass = true;
                }
            }
        } while (addedThisPass);

        return validated;
    }

    /// <summary>
    ///     A group is only enforceable when at least two of its members survive into the emitted record. Scaleway
    ///     marks alternatives that this generator drops (an untyped <c>object</c> resolves to no field at all), and
    ///     a one-member group can never conflict.
    /// </summary>
    private static bool HasEnforceableGroup(ParsedRecord record)
    {
        return EnforceableGroups(record).Count > 0;
    }

    private static List<IGrouping<string, ParsedRecordField>> EnforceableGroups(ParsedRecord record)
    {
        return record.Fields
            .Where(field => field.OneOfGroup is not null)
            .GroupBy(field => field.OneOfGroup!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Strips nullability and any <c>List&lt;&gt;</c> wrapper to the underlying record name, if it is one.</summary>
    private static string? ElementTypeName(string csharpType)
    {
        var type = csharpType.TrimEnd('?');
        if (type.StartsWith("List<", StringComparison.Ordinal))
        {
            type = type[5..].TrimEnd('>').TrimEnd('?');
        }

        // Generated records are PascalCase names; primitives and collections are not record references.
        return type.Length > 0 && char.IsUpper(type[0]) && !type.Contains('<') ? type : null;
    }

    /// <summary>
    ///     Emits the mutual-exclusion check for Scaleway's <c>x-one-of</c> groups. This is "at most one", not
    ///     "exactly one", by decision (2026-07-23): groups genuinely differ — a trigger's <c>config</c> requires
    ///     exactly one alternative, while <c>scaling_rule</c>'s members are all nullable and omitting the whole
    ///     group is legal (server default) — and the bare <c>x-one-of</c> tag carries no requiredness bit to
    ///     tell them apart. Only the two-set case is unambiguously the 400 the API would return; the
    ///     missing-required-member case is left to the API, whose structured error names the field.
    /// </summary>
    private static void AppendValidate(StringBuilder body, ParsedRecord record, HashSet<string> validatedRecords)
    {
        body.AppendLine();
        body.AppendLine("    /// <summary>Throws when a mutually-exclusive (<c>x-one-of</c>) group has more than one member set.</summary>");
        body.AppendLine("    public void Validate()");
        body.AppendLine("    {");

        var isFirstBlock = true;
        foreach (var group in EnforceableGroups(record))
        {
            if (!isFirstBlock)
            {
                body.AppendLine();
            }

            isFirstBlock = false;
            var names = group.Select(field => PropertyNameFor(record, field)).ToList();
            var setCount = string.Join(" + ", names.Select(name => $"({name} is null ? 0 : 1)"));
            body.AppendLine($"        if ({setCount} > 1)");
            body.AppendLine("        {");
            body.AppendLine($"            throw new InvalidOperationException(\"{record.Name}: '{group.Key}' is a mutually-exclusive group — set only one of {string.Join(", ", names)}.\");");
            body.AppendLine("        }");
        }

        var nestedFields = record.Fields
            .Where(field => ElementTypeName(field.CSharpType) is { } element && validatedRecords.Contains(element))
            .ToList();

        if (nestedFields.Count > 0 && !isFirstBlock)
        {
            body.AppendLine();
        }

        foreach (var field in nestedFields)
        {
            var propertyName = PropertyNameFor(record, field);
            if (field.CSharpType.StartsWith("List<", StringComparison.Ordinal))
            {
                body.AppendLine($"        foreach (var item in {propertyName} ?? [])");
                body.AppendLine("        {");
                body.AppendLine("            item.Validate();");
                body.AppendLine("        }");
            }
            else
            {
                body.AppendLine($"        {propertyName}?.Validate();");
            }
        }

        body.AppendLine("    }");
    }

    /// <summary>
    ///     Marks a *request* record that declares an identity field, so a scoped client can fill it. Limited to
    ///     requests because only they are ever sent: a response DTO carrying <c>project_id</c> is data we read,
    ///     not something to inject into.
    /// </summary>
    private static List<string> ScopeInterfacesFor(ParsedRecord record)
    {
        var interfaces = new List<string>();
        if (!record.Name.EndsWith("Request", StringComparison.Ordinal))
        {
            return interfaces;
        }

        if (record.Fields.Any(field => field.Name == "project_id"))
        {
            interfaces.Add($"IProjectScopedRequest<{record.Name}>");
        }

        if (record.Fields.Any(field => field.Name == "organization_id"))
        {
            interfaces.Add($"IOrganizationScopedRequest<{record.Name}>");
        }

        return interfaces;
    }

    private static void AppendScopeInjectors(StringBuilder body, ParsedRecord record, List<string> scopes)
    {
        foreach (var scope in scopes)
        {
            var isProject = scope.StartsWith("IProjectScoped", StringComparison.Ordinal);
            var property = isProject ? "ProjectId" : "OrganizationId";
            var parameter = isProject ? "projectId" : "organizationId";
            var field = isProject ? "project_id" : "organization_id";

            body.AppendLine();
            body.AppendLine($"    /// <summary>Returns a copy carrying <c>{field}</c>. A scoped client calls this before sending, so a caller never repeats the id.</summary>");
            body.AppendLine($"    /// <param name=\"{parameter}\">The value to stamp on the copy. An explicit value already on this record is not overwritten by the client.</param>");
            body.AppendLine($"    public {record.Name} With{property}(string {parameter}) => this with {{ {property} = {parameter} }};");
        }
    }

    internal static string EmitModels(ParsedApi api, string namespaceName)
    {
        var body = new StringBuilder();
        var needsSerializationUsing = api.Enums.Count > 0;

        foreach (var parsedEnum in api.Enums.OrderBy(parsedEnum => parsedEnum.Name, StringComparer.Ordinal))
        {
            AppendEnum(body, parsedEnum);
        }

        var validatedRecords = ComputeValidatedRecords(api.Records);
        var originsByRecord = RecordOriginsFromOperations(api);

        foreach (var record in api.Records.OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            var scopes = ScopeInterfacesFor(record);
            body.AppendLine();
            AppendSummary(body, record.Description, "");
            AppendRecordOrigin(body, record, originsByRecord);
            body.AppendLine($"public sealed record {record.Name}{(scopes.Count > 0 ? " : " + string.Join(", ", scopes) : "")}");
            body.AppendLine("{");
            foreach (var field in record.Fields)
            {
                var propertyName = PropertyNameFor(record, field);
                AppendFieldDoc(body, field);
                if (propertyName != CSharpNaming.ToPascalCase(field.Name))
                {
                    // A property cannot share its enclosing record's name (CS0542); rename it and pin the wire name.
                    needsSerializationUsing = true;
                    body.AppendLine($"    [JsonPropertyName(\"{field.Name}\")]");
                }

                body.AppendLine($"    public {field.CSharpType} {propertyName} {{ get; init; }}");
            }

            if (validatedRecords.Contains(record.Name))
            {
                AppendValidate(body, record, validatedRecords);
            }

            AppendScopeInjectors(body, record, scopes);

            body.AppendLine("}");
        }

        foreach (var queryRecord in api.QueryRecords.OrderBy(queryRecord => queryRecord.Name, StringComparer.Ordinal))
        {
            AppendQueryRecord(body, queryRecord, originsByRecord);
        }

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        if (api.QueryRecords.Count > 0)
        {
            builder.AppendLine("using System.Globalization;");
        }

        if (needsSerializationUsing)
        {
            builder.AppendLine("using System.Text.Json.Serialization;");
        }

        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName};");
        builder.Append(body);
        return builder.ToString();
    }

    /// <summary>
    ///     Emits a typed query record for a List operation: one property per query parameter (documented, enums
    ///     typed, deprecated fields marked), the scope injectors it qualifies for, and a <c>ToQueryParameters</c>
    ///     that flattens the set values to the wire dictionary the transport sends. An unset property is omitted,
    ///     so leaving <c>Page</c>/<c>PageSize</c> null lets the transport follow pagination to the end.
    /// </summary>
    private static void AppendQueryRecord(StringBuilder body, ParsedRecord queryRecord, IReadOnlyDictionary<string, string> originsByRecord)
    {
        var scopes = ScopeInterfacesFor(queryRecord);
        body.AppendLine();
        AppendSummary(body, queryRecord.Description, "");
        AppendRecordOrigin(body, queryRecord, originsByRecord);
        body.AppendLine($"public sealed record {queryRecord.Name}{(scopes.Count > 0 ? " : " + string.Join(", ", scopes) : "")}");
        body.AppendLine("{");
        foreach (var field in queryRecord.Fields)
        {
            var propertyName = CSharpNaming.ToPascalCase(field.Name);
            AppendFieldDoc(body, field);

            if (field.Deprecated)
            {
                body.AppendLine($"    [Obsolete(\"{(field.Description ?? "Deprecated.").Replace("\"", "'")}\")]");
            }

            body.AppendLine($"    public {field.CSharpType} {propertyName} {{ get; init; }}");
        }

        AppendScopeInjectors(body, queryRecord, scopes);

        body.AppendLine();

        // Serialising a record that carries a Scaleway-deprecated field means reading that field, which would
        // warn against the [Obsolete] this same emitter put there. The attribute is for consumers who *set* the
        // filter; suppressing it around the serializer keeps that signal without the generator shouting at
        // itself. Scoped to this method, and only emitted for records that actually have such a field.
        var hasDeprecatedField = queryRecord.Fields.Any(field => field.Deprecated);
        if (hasDeprecatedField)
        {
            body.AppendLine("#pragma warning disable CS0618 // Reading a field Scaleway deprecated is what serialization is for.");
        }

        // A list of pairs rather than a dictionary: OpenAPI query arrays default to style=form/explode=true, so
        // an array parameter is repeated (tags=a&tags=b) rather than comma-joined. A dictionary cannot express
        // that, and Scaleway reads a comma-joined value as one literal tag.
        body.AppendLine("    /// <summary>");
        body.AppendLine("    ///     Flattens the properties that are set into the pairs the transport sends; an unset property is omitted.");
        body.AppendLine("    ///     A list rather than a dictionary because an array parameter is repeated per element rather than joined —");
        body.AppendLine("    ///     Scaleway reads a comma-joined value as one literal and matches nothing.");
        body.AppendLine("    /// </summary>");
        body.AppendLine("    public List<KeyValuePair<string, string>> ToQueryParameters()");
        body.AppendLine("    {");
        body.AppendLine("        var parameters = new List<KeyValuePair<string, string>>();");
        foreach (var field in queryRecord.Fields)
        {
            var propertyName = CSharpNaming.ToPascalCase(field.Name);

            if (IsRepeatedQueryField(field.CSharpType))
            {
                body.AppendLine($"        if ({propertyName} is not null) parameters.AddRange({RepeatedQueryValuesExpression(propertyName, field.CSharpType)}.Select(value => new KeyValuePair<string, string>(\"{field.Name}\", value)));");
                continue;
            }

            body.AppendLine($"        if ({propertyName} is not null) parameters.Add(new KeyValuePair<string, string>(\"{field.Name}\", {QueryValueExpression(propertyName, field.CSharpType)}));");
        }

        body.AppendLine("        return parameters;");
        body.AppendLine("    }");

        if (hasDeprecatedField)
        {
            body.AppendLine("#pragma warning restore CS0618");
        }

        body.AppendLine("}");
    }

    // Array-valued query parameters are emitted once per element rather than joined — see ToQueryParameters.
    // Every array shape counts, not just string[]: a numeric array routed through the scalar path would emit
    // code that does not compile, which is how the long[]/double[] cases were missed until a schema used one.
    private static bool IsRepeatedQueryField(string csharpType)
    {
        return csharpType.EndsWith("[]?", StringComparison.Ordinal) || csharpType.StartsWith("List<", StringComparison.Ordinal);
    }

    // The wire strings for a repeated parameter's elements.
    private static string RepeatedQueryValuesExpression(string propertyName, string csharpType)
    {
        return csharpType switch
        {
            "string[]?" => propertyName,
            "long[]?" or "double[]?" => $"{propertyName}.Select(value => value.ToString(CultureInfo.InvariantCulture))",
            _ => $"{propertyName}.Select(value => value.Value)" // a list of extensible enums
        };
    }

    // How a set query property renders to its wire string. Scaleway wants lowercase bools and invariant numbers;
    // the extensible enum's .Value is already the wire token.
    private static string QueryValueExpression(string propertyName, string csharpType)
    {
        return csharpType switch
        {
            "string?" => propertyName,
            "bool?" => $"{propertyName}.Value ? \"true\" : \"false\"",
            "long?" or "double?" => $"{propertyName}.Value.ToString(CultureInfo.InvariantCulture)",
            "DateTimeOffset?" => $"{propertyName}.Value.ToString(\"O\", CultureInfo.InvariantCulture)",
            _ => $"{propertyName}.Value.Value" // an extensible enum: nullable struct → the enum → its wire string
        };
    }

    /// <summary>
    ///     Emits a schema description as a one-line <c>&lt;summary&gt;</c>, and emits nothing at all when the schema
    ///     carries none. The silence is deliberate: a fallback derived from the member's own name reads as
    ///     documentation to a doc generator and to a compiler gate while telling a reader strictly less than its
    ///     absence would, and an absent summary is at least visibly absent.
    /// </summary>
    private static void AppendSummary(StringBuilder body, string? description, string indent)
    {
        if (description is not { Length: > 0 })
        {
            return;
        }

        // Scaleway wraps long descriptions across lines (folded and block scalars both), which XmlEscape turns into
        // runs of spaces; collapsing them keeps the summary one readable line rather than one line with gaps in it.
        var summary = string.Join(' ', XmlEscape(description).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (summary.Length > 0)
        {
            body.AppendLine($"{indent}/// <summary>{summary}</summary>");
        }
    }

    /// <summary>
    ///     A field's own documentation, falling back to the wire name when the schema describes it in no other way.
    ///     The fallback is deliberately the <em>only</em> place the wire name is emitted: where Scaleway supplies a
    ///     description, that description says more than the JSON key does, and repeating the key alongside it would
    ///     be noise. The key is not derivable from the property in every case — a property colliding with its
    ///     record's name is renamed and pinned with <c>[JsonPropertyName]</c>, which no consumer sees in IntelliSense.
    /// </summary>
    private static void AppendFieldDoc(StringBuilder body, ParsedRecordField field)
    {
        if (field.Description is { Length: > 0 })
        {
            AppendSummary(body, field.Description, "    ");
            return;
        }

        body.AppendLine($"    /// <remarks>Scaleway field <c>{XmlEscape(field.Name)}</c>.</remarks>");
    }

    /// <summary>
    ///     Where a record came from, emitted only when the schema described it in no other way: the component a
    ///     reader follows to Scaleway's own documentation, or — for a record synthesized from an operation rather
    ///     than declared as a component — the request it is the body or query of.
    ///     <para>
    ///         A record matching neither gets nothing. That is the honest outcome and it does not stay silent:
    ///         CS1591 names it once the SDK generates XML documentation, which is a better place to discover the
    ///         gap than a generator guard duplicating the compiler.
    ///     </para>
    /// </summary>
    private static void AppendRecordOrigin(StringBuilder body, ParsedRecord record, IReadOnlyDictionary<string, string> originsByRecord)
    {
        if (record.Description is { Length: > 0 })
        {
            return;
        }

        if (record.SchemaComponent is { Length: > 0 } component)
        {
            body.AppendLine($"/// <remarks>Scaleway schema component <c>{XmlEscape(component)}</c>.</remarks>");
            return;
        }

        if (originsByRecord.TryGetValue(record.Name, out var origin))
        {
            body.AppendLine($"/// <remarks>{origin}</remarks>");
        }
    }

    // A request or query record is synthesized from an operation rather than declared as a component, so the fact
    // worth stating is the call it belongs to — which the type name does not carry and the record itself cannot know.
    private static Dictionary<string, string> RecordOriginsFromOperations(ParsedApi api)
    {
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in api.ResourceGroups)
        {
            foreach (var operation in group.Operations)
            {
                var path = operation.Kind is ApiOperationKind.List or ApiOperationKind.Create ? $"/{group.BasePath}" : $"/{group.ItemPath}/{{id}}";
                var call = $"<c>{HttpVerbFor(operation.Kind)} {XmlEscape(path)}</c> (Scaleway operation <c>{operation.OperationId}</c>)";

                if (operation.RequestRecordName is { } requestRecord)
                {
                    origins[requestRecord] = $"Body of {call}.";
                }

                if (operation.QueryRecordName is { } queryRecord)
                {
                    origins[queryRecord] = $"Query parameters of {call}.";
                }
            }
        }

        foreach (var action in api.Actions.Where(action => action.QueryRecordName is not null))
        {
            origins[action.QueryRecordName!] = $"Query parameters of <c>{(action.IsPost ? "POST" : "GET")} /{XmlEscape(action.PathTemplate)}</c> (Scaleway operation <c>{action.OperationId}</c>).";
        }

        return origins;
    }

    private static string HttpVerbFor(ApiOperationKind kind)
    {
        return kind switch
        {
            ApiOperationKind.List or ApiOperationKind.Get => "GET",
            ApiOperationKind.Create => "POST",
            ApiOperationKind.Update => "PATCH",
            ApiOperationKind.Delete => "DELETE",
            _ => "GET"
        };
    }

    private static string XmlEscape(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", " ").Replace("\r", " ");
    }

    internal static string EmitClient(ParsedApi api, string namespaceName, string className)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.Text.Json;");
        builder.AppendLine("using System.Text.Json.Serialization;");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();
        // The optional scope fills project_id / organization_id into requests that declare them, so the caller
        // never repeats them. Null (the raw client) sends requests exactly as given.
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"///     Typed client for Scaleway's <c>{api.ServiceName}</c> API ({api.ApiVersion}). One method per operation, each named");
        builder.AppendLine("///     after Scaleway's <c>operationId</c> so it matches their documentation, CLI and other SDKs verbatim.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <param name=\"client\">Transport carrying the base address, the auth token, snake_case serialization and pagination.</param>");
        builder.AppendLine("/// <param name=\"scope\">");
        builder.AppendLine("///     Fills <c>project_id</c>, <c>organization_id</c> and the default region into requests that declare them.");
        builder.AppendLine("///     Null — a raw client — sends requests exactly as given and requires a region on every region-scoped call.");
        builder.AppendLine("/// </param>");
        builder.AppendLine($"public sealed class {className}(IScalewayApiClient client, ScalewayRequestScope? scope = null)");
        builder.AppendLine("{");
        var jsonContext = JsonContextName(className);
        builder.AppendLine($"    private static {jsonContext} Json => {jsonContext}.Default;");

        var validatedRecords = ComputeValidatedRecords(api.Records);
        var scopeKinds = ScopeKindsByRecord(api);

        foreach (var group in api.ResourceGroups)
        {
            var identifierParameter = CSharpNaming.IdentifierParameter(group.Name);

            // List/Create address the collection, Get/Update/Delete the item prefix. The two paths are usually
            // identical, but each carries its own scope and parent-id parameters, so each is resolved separately:
            // Kapsule's ListPools needs the cluster id its collection path names, while GetPool does not.
            var collection = ResolvePathContext(group.BasePath);
            var item = ResolvePathContext(group.ItemPath);

            builder.AppendLine();
            builder.Append(EmitCollectionPath(group, collection.Scope, collection.ParentParameters));

            foreach (var operation in group.Operations)
            {
                var context = operation.Kind is ApiOperationKind.List or ApiOperationKind.Create ? collection : item;
                builder.AppendLine();
                builder.Append(EmitMethodDoc(operation, context.Path, api.ApiVersion, identifierParameter, group.Name, context.Scope.Parameter, context.ParentParameters));
                builder.Append(EmitMethod(operation, identifierParameter, context.Scope, validatedRecords, scopeKinds, context.ParameterList));
            }
        }

        foreach (var action in api.Actions)
        {
            builder.AppendLine();
            builder.Append(EmitActionMethod(action, api.ApiVersion, scopeKinds));
        }

        builder.AppendLine("}");
        builder.Append(EmitJsonContext(api, jsonContext));
        return builder.ToString();
    }

    /// <summary>
    ///     The source-generated serializer context a client class serializes through: <c>SecretApi</c> →
    ///     <c>SecretJsonContext</c>.
    /// </summary>
    internal static string JsonContextName(string className)
    {
        return $"{(className.EndsWith("Api", StringComparison.Ordinal) ? className[..^3] : className)}JsonContext";
    }

    /// <summary>
    ///     Emits the service's <c>JsonSerializerContext</c>: one entry per record, with the snake_case naming and the
    ///     null omission the wire format needs. Serialization goes through this generated metadata rather than
    ///     reflection, so a trimmed or Native AOT consumer keeps every request and response it calls, and none pays
    ///     reflection's startup cost. Internal: the generated client is the surface, the metadata is how it works.
    /// </summary>
    private static string EmitJsonContext(ParsedApi api, string jsonContext)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]");
        foreach (var record in api.Records.OrderBy(record => record.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"[JsonSerializable(typeof({record.Name}))]");
        }

        builder.AppendLine($"internal sealed partial class {jsonContext} : JsonSerializerContext;");
        return builder.ToString();
    }

    /// <summary>
    ///     Emits a body-less GET action (<c>…/versions/{revision}/access</c>): every non-region path token becomes
    ///     a parameter in path order, the query parameters the schema declares become a typed record between those
    ///     and the region, region trails as the usual optional scope-defaulted argument, and the response
    ///     deserializes to the action's own response record (null on 404). A query record the schema marks required
    ///     is emitted without a default — an action that names its target through the query string cannot be called
    ///     without naming it.
    /// </summary>
    private static string EmitActionMethod(ParsedActionOperation action, string apiVersion, Dictionary<string, (bool Project, bool Organization)> scopeKinds)
    {
        // Same resolution the CRUD methods use, so an action's path is held to the same interpolation guard.
        var context = ResolvePathContext(action.PathTemplate);
        var scope = context.Scope;
        var pathLiteral = scope.PathLiteral;
        var parameterList = context.ParameterList;
        var trailingParameter = scope.Parameter is null ? "" : $"string? {scope.Parameter} = null, ";
        var scopeResolution = scope.Parameter is null
            ? ""
            : $"        {scope.Parameter} ??= scope?.{CSharpNaming.ToPascalCase(scope.Parameter)} ?? throw new InvalidOperationException(\"This operation is {scope.Parameter}-scoped: pass a {scope.Parameter} or use a client created with a default {scope.Parameter}.\");\n";
        var method = CSharpNaming.NormalizeOperationId(action.OperationId);

        var builder = new StringBuilder();
        if (action.Summary is { Length: > 0 } summary)
        {
            builder.AppendLine($"    /// <summary>{XmlEscape(summary)}</summary>");
        }

        var actionDocs = ParentParameterDocs(context.ParentParameters);
        if (action.QueryRecordName is not null)
        {
            actionDocs.Add(("query", action.QueryRequired
                    ? "Names the target of the action through the query string. Required by the schema, so it carries no default."
                    : "Query parameters the schema declares for this action. An unset property is omitted."
                )
            );
        }

        AppendScopeAndCancellationDocs(actionDocs, scope.Parameter);
        AppendParameterDocs(builder, actionDocs);

        var verb = action.IsPost ? "POST" : "GET";
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine($"    ///     Scaleway operation <c>{action.OperationId}</c> — <c>{verb} /{action.PathTemplate}</c> ({apiVersion}).");
        if (action.Deprecated)
        {
            builder.AppendLine("    ///     Deprecated by Scaleway.");
        }

        builder.AppendLine("    /// </remarks>");
        if (action.Deprecated)
        {
            builder.AppendLine($"    [Obsolete(\"Scaleway has deprecated the {action.OperationId} operation.\")]");
        }

        if (action.IsPost)
        {
            // A trigger-style action: POST with an empty body, response always present.
            builder.AppendLine($"    public async Task<{action.ResponseTypeName}> {method}Async({parameterList}{trailingParameter}CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.Append(scopeResolution);
            builder.AppendLine($"        var response = await client.CreateResourceAsync({pathLiteral}, {scope.TransportArgument}, ScalewayJsonBody.EmptyObject, cancellationToken);");
            builder.AppendLine($"        return response.Deserialize(Json.{action.ResponseTypeName})!;");
            builder.AppendLine("    }");
            return builder.ToString();
        }

        // An action declaring no query parameters keeps the query-less transport call: unlike List, it addresses one
        // fixed thing, so there is nothing an ad-hoc query parameter would mean.
        var queryParameter = action.QueryRecordName is { } queryRecord
            ? action.QueryRequired ? $"{queryRecord} query, " : $"{queryRecord}? query = null, "
            : "";
        var queryArgument = action.QueryRecordName is null ? "" : action.QueryRequired ? ", query.ToQueryParameters()" : ", query?.ToQueryParameters()";

        builder.AppendLine($"    public async Task<{action.ResponseTypeName}?> {method}Async({parameterList}{queryParameter}{trailingParameter}CancellationToken cancellationToken = default)");
        builder.AppendLine("    {");
        builder.Append(scopeResolution);
        builder.Append(EmitActionScopeFill(action, scopeKinds));
        builder.AppendLine($"        var response = await client.GetAsync({pathLiteral}, {scope.TransportArgument}{queryArgument}, cancellationToken);");
        builder.AppendLine($"        return response?.Deserialize(Json.{action.ResponseTypeName});");
        builder.AppendLine("    }");
        return builder.ToString();
    }

    /// <summary>
    ///     Fills the client's project/organization into an action's query record, so <c>project_id</c> comes from the
    ///     scope here exactly as it does on a List. A required record is always present; an optional one is
    ///     materialised first, so a caller that passed none is still scoped.
    /// </summary>
    private static string EmitActionScopeFill(ParsedActionOperation action, Dictionary<string, (bool Project, bool Organization)> scopeKinds)
    {
        if (action.QueryRecordName is not { } queryRecord || !scopeKinds.ContainsKey(queryRecord))
        {
            return "";
        }

        if (action.QueryRequired)
        {
            return EmitScopeFill("query", queryRecord, scopeKinds, "        ");
        }

        var builder = new StringBuilder();
        builder.AppendLine("        if (scope is not null)");
        builder.AppendLine("        {");
        builder.AppendLine($"            query ??= new {queryRecord}();");
        builder.Append(EmitScopeFill("query", queryRecord, scopeKinds, "            "));
        builder.AppendLine("        }");
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>
    ///     Emits the resource group's collection path as a static builder. Callers that need the path itself —
    ///     the deployment's resource-type-agnostic list/poll/delete helpers, which work over any Scaleway
    ///     resource that carries id/name/tags — get it from the schema instead of hand-writing it, so an API
    ///     version bump reaches them through the compiler like every other generated member.
    /// </summary>
    private static string EmitCollectionPath(ParsedResourceGroup group, (string? Parameter, string TransportArgument, string PathLiteral) scope, List<(string Token, string Parameter)> parentParameters)
    {
        var parameters = parentParameters.Select(parent => parent.Parameter).ToList();
        if (scope.Parameter is not null)
        {
            parameters.Add(scope.Parameter);
        }

        var docs = ParentParameterDocs(parentParameters);
        if (scope.Parameter is not null)
        {
            // No scope to fall back on here: this is a static builder, so the caller supplies the region itself.
            docs.Add((scope.Parameter, $"The {scope.Parameter} to build the path for."));
        }

        var builder = new StringBuilder();
        builder.AppendLine($"    /// <summary>Collection path for <c>{group.Name}</c> — <c>{XmlEscape(group.BasePath)}</c>.</summary>");
        AppendParameterDocs(builder, docs);
        builder.AppendLine($"    public static string {NormalizeAcronyms(group.Name)}CollectionPath({string.Join(", ", parameters.Select(parameter => $"string {parameter}"))})");
        builder.AppendLine("    {");
        builder.AppendLine($"        return {scope.PathLiteral};");
        builder.AppendLine("    }");
        return builder.ToString();
    }

    /// <summary>
    ///     Folds the SCREAMING acronyms Scaleway's schema uses in record names (<c>VPC</c>, <c>APIKey</c>) down to
    ///     .NET casing (<c>Vpc</c>, <c>ApiKey</c>), matching how the operation methods are already named. A run of
    ///     capitals ending the name is one word; otherwise its last capital starts the next word.
    /// </summary>
    private static string NormalizeAcronyms(string name)
    {
        return Regex.Replace(name, "[A-Z]{2,}", match =>
            {
                var run = match.Value;
                return match.Index + run.Length == name.Length
                    ? run[0] + run[1..].ToLowerInvariant()
                    : run[0] + run[1..^1].ToLowerInvariant() + run[^1];
            }
        );
    }

    // Everything an emitted method needs to address one path: its region/zone scope and the parent-resource id
    // tokens it takes as leading parameters. A sub-resource's path carries its parent's id
    // ("secrets/{secret_id}/versions"); each such token becomes a method parameter that the interpolated path
    // literal binds by its camelCase name.
    private static PathContext ResolvePathContext(string path)
    {
        var scope = ResolveScope(path);
        var parentParameters = ParentPathParameters(path);
        foreach (var (token, parameter) in parentParameters)
        {
            scope = scope with { PathLiteral = scope.PathLiteral.Replace($"{{{token}}}", $"{{{parameter}}}") };
        }

        // Every placeholder left in the literal must be one the emitted method binds: region or zone from the
        // scope, a parent id from ParentPathParameters. A token in any other shape has no parameter to bind to,
        // so interpolating it would not compile and emitting it plainly would send the brace verbatim as part of
        // the URL. Fail here naming the path, rather than either.
        var boundParameters = parentParameters.Select(parent => parent.Parameter).ToHashSet(StringComparer.Ordinal);
        if (scope.Parameter is not null)
        {
            boundParameters.Add(scope.Parameter);
        }

        var unboundTokens = Regex.Matches(scope.PathLiteral, "\\{([^}]*)\\}")
            .Select(match => match.Groups[1].Value)
            .Where(token => !boundParameters.Contains(token))
            .ToList();

        if (unboundTokens.Count > 0)
        {
            throw new InvalidOperationException(
                $"Path '{path}' carries the placeholder(s) {string.Join(", ", unboundTokens.Select(token => $"'{{{token}}}'"))}, " +
                "which no emitted parameter binds, so they would be sent verbatim. A placeholder must be region, zone, " +
                "or a parent id the path names in snake_case."
            );
        }

        var parameterList = string.Concat(parentParameters.Select(parent => $"string {parent.Parameter}, "));
        return new PathContext(path, scope, parentParameters, parameterList);
    }

    // Scaleway resources are region-scoped, zone-scoped, or organization-global (IAM). The scope parameter (if any)
    // matches the path token so the interpolated base path binds it; the transport's region argument is unused for
    // zonal/global resources but still required, so it gets the zone parameter or an empty string respectively.
    // Region/zone is a trailing optional argument falling back to the client scope's default — an explicit
    // argument always wins, and a raw client (no scope) must pass one.
    // A path is interpolated whenever it carries any placeholder, not only a region or zone one: a global service
    // still names its parent ids that way (IAM's "groups/{group_id}/members"), and those bind exactly as region
    // does. A path with no placeholder at all stays a plain literal.
    private static (string? Parameter, string TransportArgument, string PathLiteral) ResolveScope(string basePath)
    {
        if (basePath.Contains("{region}", StringComparison.Ordinal))
        {
            return ("region", "region", $"$\"{basePath}\"");
        }

        if (basePath.Contains("{zone}", StringComparison.Ordinal))
        {
            return ("zone", "zone", $"$\"{basePath}\"");
        }

        return (null, "string.Empty", basePath.Contains('{') ? $"$\"{basePath}\"" : $"\"{basePath}\"");
    }

    /// <summary>
    ///     The parent-resource tokens a sub-resource path carries besides region/zone — e.g.
    ///     <c>secrets/{secret_id}/versions</c> yields <c>("secret_id", "secretId")</c>. Each becomes a leading
    ///     method parameter interpolated into the path.
    /// </summary>
    private static List<(string Token, string Parameter)> ParentPathParameters(string basePath)
    {
        return Regex.Matches(basePath, "\\{([a-z_]+)\\}")
            .Select(match => match.Groups[1].Value)
            .Where(token => token is not ("region" or "zone"))
            .Select(token =>
                {
                    var pascal = CSharpNaming.ToPascalCase(token);
                    return (token, char.ToLowerInvariant(pascal[0]) + pascal[1..]);
                }
            )
            .ToList();
    }

    // Documents each generated method with the Scaleway operation it maps to: operationId, HTTP verb + URL, and
    // version — so the call is traceable to Scaleway's own docs/CLI without leaving the code. A deprecated
    // operation additionally gets [Obsolete], which surfaces as a build warning at the call site.
    private static string EmitMethodDoc(
        ParsedOperation operation,
        string basePath,
        string apiVersion,
        string identifierParameter,
        string resourceName,
        string? scopeParameter,
        List<(string Token, string Parameter)> parentParameters
    )
    {
        var verb = HttpVerbFor(operation.Kind);

        // Get/Update/Delete address a single resource; the transport appends "/{id}" to the item prefix they were
        // given, which is not always the collection path.
        var url = operation.Kind is ApiOperationKind.Get or ApiOperationKind.Update or ApiOperationKind.Delete ? $"/{basePath}/{{id}}" : $"/{basePath}";

        var builder = new StringBuilder();
        if (operation.Summary is { Length: > 0 } summary)
        {
            builder.AppendLine($"    /// <summary>{XmlEscape(summary)}</summary>");
        }

        AppendParameterDocs(builder, MethodParameterDocs(operation, identifierParameter, resourceName, basePath, scopeParameter, parentParameters));

        builder.AppendLine("    /// <remarks>");
        builder.AppendLine($"    ///     Scaleway operation <c>{operation.OperationId}</c> — <c>{verb} {url}</c> ({apiVersion}).");
        if (operation.Deprecated)
        {
            builder.AppendLine("    ///     Deprecated by Scaleway.");
        }

        builder.AppendLine("    /// </remarks>");
        if (operation.Deprecated)
        {
            builder.AppendLine($"    [Obsolete(\"Scaleway has deprecated the {operation.OperationId} operation.\")]");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Emits a <c>&lt;param&gt;</c> for every parameter of a generated method, in signature order. Each states
    ///     something the declaration does not — which path segment an id binds, that a region falls back to the
    ///     client's scope, that an unset query property is omitted — never a restatement of the parameter's name.
    ///     <para>
    ///         Completeness stops being a convention once XML documentation is generated: CS1573 fires for a
    ///         parameter left undocumented while its siblings are documented, and CS1572 for a tag naming a
    ///         parameter that does not exist. The docs cannot drift from the signature without the build saying so.
    ///     </para>
    /// </summary>
    private static void AppendParameterDocs(StringBuilder builder, IEnumerable<(string Name, string Description)> parameters)
    {
        foreach (var (name, description) in parameters)
        {
            builder.AppendLine($"    /// <param name=\"{name}\">{description}</param>");
        }
    }

    private static List<(string Name, string Description)> MethodParameterDocs(
        ParsedOperation operation,
        string identifierParameter,
        string resourceName,
        string basePath,
        string? scopeParameter,
        List<(string Token, string Parameter)> parentParameters
    )
    {
        var docs = ParentParameterDocs(parentParameters);

        switch (operation.Kind)
        {
            case ApiOperationKind.List:
                docs.Add(("query", operation.QueryRecordName is null
                        ? "Query parameters to send as given. A repeated key is repeated on the wire rather than joined, which is what Scaleway reads as several values."
                        : "Filters and paging. An unset property is omitted, and passing none lets the transport page to the end of the collection."
                    )
                );
                break;
            case ApiOperationKind.Create:
                docs.Add(("request", RequestParameterDescription));
                break;
            case ApiOperationKind.Get:
            case ApiOperationKind.Delete:
                docs.Add((identifierParameter, IdentifierParameterDescription(resourceName, basePath)));
                break;
            case ApiOperationKind.Update:
                docs.Add((identifierParameter, IdentifierParameterDescription(resourceName, basePath)));
                docs.Add(("request", RequestParameterDescription));
                break;
        }

        AppendScopeAndCancellationDocs(docs, scopeParameter);
        return docs;
    }

    private static void AppendScopeAndCancellationDocs(List<(string Name, string Description)> docs, string? scopeParameter)
    {
        if (scopeParameter is not null)
        {
            docs.Add((scopeParameter, $"The {scopeParameter} to address. Falls back to the client scope's default when null; a client created without one must pass it."));
        }

        docs.Add(("cancellationToken", "Passed to the underlying HTTP request."));
    }

    // A parent-id parameter exists because the path names its collection under another resource. Naming the segment
    // it fills is the fact the parameter name does not carry — "clusterId" does not say it binds "{cluster_id}".
    private static List<(string Name, string Description)> ParentParameterDocs(List<(string Token, string Parameter)> parentParameters)
    {
        return parentParameters.Select(parent => (parent.Parameter, $"Binds the <c>{{{parent.Token}}}</c> segment of the path.")).ToList();
    }

    private static string IdentifierParameterDescription(string resourceName, string basePath)
    {
        return $"Scaleway ID of the {resourceName} to address. The transport appends it to <c>{XmlEscape(basePath)}</c>.";
    }

    // Which scope fields each request/query record declares, so a method can fill them before sending.
    private static Dictionary<string, (bool Project, bool Organization)> ScopeKindsByRecord(ParsedApi api)
    {
        var kinds = new Dictionary<string, (bool, bool)>(StringComparer.Ordinal);
        foreach (var record in api.Records.Concat(api.QueryRecords))
        {
            var scopes = ScopeInterfacesFor(record);
            var project = scopes.Any(s => s.StartsWith("IProjectScoped", StringComparison.Ordinal));
            var organization = scopes.Any(s => s.StartsWith("IOrganizationScoped", StringComparison.Ordinal));
            if (project || organization)
            {
                kinds[record.Name] = (project, organization);
            }
        }

        return kinds;
    }

    // Applies the client's scope to a request variable. Order matters for the ProjectIdentifier pair: fill project
    // first so the organization fill sees it and no-ops, keeping the mutually-exclusive fields from both being set.
    private static string EmitScopeFill(string variable, string recordName, Dictionary<string, (bool Project, bool Organization)> scopeKinds, string indent)
    {
        if (!scopeKinds.TryGetValue(recordName, out var kind))
        {
            return "";
        }

        var builder = new StringBuilder();
        if (kind.Project)
        {
            builder.AppendLine($"{indent}{variable} = scope?.ApplyProject({variable}) ?? {variable};");
        }

        if (kind.Organization)
        {
            builder.AppendLine($"{indent}{variable} = scope?.ApplyOrganization({variable}) ?? {variable};");
        }

        return builder.ToString();
    }

    private static string EmitMethod(ParsedOperation operation, string identifierParameter, (string? Parameter, string TransportArgument, string PathLiteral) scope, HashSet<string> validatedRecords, Dictionary<string, (bool Project, bool Organization)> scopeKinds, string parentParameters)
    {
        // The caller resolved the path for this operation's kind — the collection for List/Create, the item prefix
        // for Get/Update/Delete, which the transport completes with "/{id}".
        var path = scope.PathLiteral;
        var transportScope = scope.TransportArgument;
        var response = operation.ResponseTypeName;

        // Region/zone trails the operation's own parameters as an optional argument: an explicit value wins,
        // otherwise the client scope's default fills it, and a raw client (no scope) must pass it.
        var trailingParameter = scope.Parameter is null ? "" : $"string? {scope.Parameter} = null, ";
        var scopeResolution = scope.Parameter is null
            ? ""
            : $"        {scope.Parameter} ??= scope?.{CSharpNaming.ToPascalCase(scope.Parameter)} ?? throw new InvalidOperationException(\"This operation is {scope.Parameter}-scoped: pass a {scope.Parameter} or use a client created with a default {scope.Parameter}.\");\n";

        // The method is named after Scaleway's operationId (normalised to .NET casing), so it matches their docs,
        // CLI, and other SDKs verbatim.
        var method = CSharpNaming.NormalizeOperationId(operation.OperationId);
        var builder = new StringBuilder();

        // Fail locally on a mutually-exclusive violation rather than sending a request Scaleway would reject.
        var validation = operation.RequestRecordName is { } requestRecord && validatedRecords.Contains(requestRecord)
            ? "        request.Validate();\n"
            : "";

        switch (operation.Kind)
        {
            case ApiOperationKind.List:
                // An operation with no query parameters of its own still accepts ad-hoc ones, in the same shape
                // the transport takes: a list of pairs, not a dictionary, so a caller can repeat a key the way
                // OpenAPI's style=form/explode=true default requires (see ToQueryParameters).
                var queryParameter = operation.QueryRecordName is { } queryRecord
                    ? $"{queryRecord}? query = null"
                    : "IReadOnlyList<KeyValuePair<string, string>>? query = null";
                var queryArgument = operation.QueryRecordName is not null ? "query?.ToQueryParameters()" : "query";
                builder.AppendLine($"    public async Task<IReadOnlyList<{response}>> {method}Async({parentParameters}{queryParameter}, {trailingParameter}CancellationToken cancellationToken = default)");
                builder.AppendLine("    {");
                builder.Append(scopeResolution);
                // Scope the list to the client's project/organization. Materialise an empty query first so a caller
                // that passed none still gets scoped; a null scope (the raw client) leaves query untouched.
                if (operation.QueryRecordName is { } scopedQuery && scopeKinds.ContainsKey(scopedQuery))
                {
                    builder.AppendLine("        if (scope is not null)");
                    builder.AppendLine("        {");
                    builder.AppendLine($"            query ??= new {scopedQuery}();");
                    builder.Append(EmitScopeFill("query", scopedQuery, scopeKinds, "            "));
                    builder.AppendLine("        }");
                    builder.AppendLine();
                }

                builder.AppendLine($"        var items = await client.ListResourcesAsync({path}, {transportScope}, {queryArgument}, cancellationToken);");
                builder.AppendLine($"        return items.Select(item => item.Deserialize(Json.{response})!).ToList();");
                builder.AppendLine("    }");
                break;
            case ApiOperationKind.Create:
                builder.AppendLine($"    public async Task<{response}> {method}Async({parentParameters}{operation.RequestRecordName} request, {trailingParameter}CancellationToken cancellationToken = default)");
                builder.AppendLine("    {");
                builder.Append(scopeResolution);
                builder.Append(EmitScopeFill("request", operation.RequestRecordName!, scopeKinds, "        "));
                builder.Append(validation);
                builder.AppendLine($"        var response = await client.CreateResourceAsync({path}, {transportScope}, JsonSerializer.SerializeToElement(request, Json.{operation.RequestRecordName}), cancellationToken);");
                builder.AppendLine($"        return response.Deserialize(Json.{response})!;");
                builder.AppendLine("    }");
                break;
            case ApiOperationKind.Get:
                builder.AppendLine($"    public async Task<{response}?> {method}Async({parentParameters}string {identifierParameter}, {trailingParameter}CancellationToken cancellationToken = default)");
                builder.AppendLine("    {");
                builder.Append(scopeResolution);
                builder.AppendLine($"        var response = await client.GetResourceAsync({path}, {transportScope}, {identifierParameter}, cancellationToken);");
                builder.AppendLine($"        return response?.Deserialize(Json.{response});");
                builder.AppendLine("    }");
                break;
            case ApiOperationKind.Update:
                builder.AppendLine($"    public async Task<{response}> {method}Async({parentParameters}string {identifierParameter}, {operation.RequestRecordName} request, {trailingParameter}CancellationToken cancellationToken = default)");
                builder.AppendLine("    {");
                builder.Append(scopeResolution);
                builder.Append(EmitScopeFill("request", operation.RequestRecordName!, scopeKinds, "        "));
                builder.Append(validation);
                builder.AppendLine($"        var response = await client.UpdateResourceAsync({path}, {transportScope}, {identifierParameter}, JsonSerializer.SerializeToElement(request, Json.{operation.RequestRecordName}), cancellationToken);");
                builder.AppendLine($"        return response.Deserialize(Json.{response})!;");
                builder.AppendLine("    }");
                break;
            case ApiOperationKind.Delete:
                builder.AppendLine($"    public Task {method}Async({parentParameters}string {identifierParameter}, {trailingParameter}CancellationToken cancellationToken = default)");
                builder.AppendLine("    {");
                builder.Append(scopeResolution);
                builder.AppendLine($"        return client.DeleteResourceAsync({path}, {transportScope}, {identifierParameter}, cancellationToken);");
                builder.AppendLine("    }");
                break;
        }

        return builder.ToString();
    }

    private sealed record PathContext(
        string Path,
        (string? Parameter, string TransportArgument, string PathLiteral) Scope,
        List<(string Token, string Parameter)> ParentParameters,
        string ParameterList
    );
}
