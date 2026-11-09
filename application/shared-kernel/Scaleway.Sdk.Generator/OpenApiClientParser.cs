using System.Text;
using System.Text.Json;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     Parses a Scaleway OpenAPI schema into a <see cref="ParsedApi" /> for the typed-client emitters:
///     the CRUD operations for the requested resource groups plus every request/response record they touch,
///     resolved recursively out of <c>components.schemas</c> (following <c>$ref</c> and inline nested objects).
///     It walks GET/POST/PATCH/DELETE and reads response schemas.
/// </summary>
public static class OpenApiClientParser
{
    public static ParsedApi Parse(string serviceName, string json, params (string Singular, string Plural)[] resourceGroups)
    {
        return Parse(serviceName, json, resourceGroups, []);
    }

    public static ParsedApi Parse(string serviceName, string json, (string Singular, string Plural)[] resourceGroups, string[] actionOperations)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var resolver = new RecordResolver(schemas);

        var queryRecords = new List<ParsedRecord>();
        var groups = new List<ParsedResourceGroup>();
        foreach (var (singular, plural) in resourceGroups)
        {
            var operations = new List<ParsedOperation>();
            var itemPaths = new List<(string OperationId, string Path)>();
            string? basePath = null;

            foreach (var path in root.GetProperty("paths").EnumerateObject())
            {
                foreach (var operation in path.Value.EnumerateObject())
                {
                    var method = operation.Name.ToUpperInvariant();
                    if (method is not ("GET" or "POST" or "PATCH" or "DELETE"))
                    {
                        continue;
                    }

                    var operationId = operation.Value.TryGetProperty("operationId", out var idElement) ? idElement.GetString() : null;
                    if (operationId is null || !TryMatchOperation(operationId, singular, plural, out var kind))
                    {
                        continue;
                    }

                    var requestRecordName = ReadRequestRecord(operation.Value, operationId, resolver);
                    var responseTypeName = ReadResponseType(operation.Value, resolver);

                    // A Delete answering 204 (no body) has no response type and needs none — the emitted method
                    // returns Task. Every other verb deserializes its response, so an unresolvable type skips it.
                    if (responseTypeName is null && kind != ApiOperationKind.Delete)
                    {
                        continue;
                    }

                    // Only List carries a query record: the other verbs address a single resource by id and take
                    // no filter/sort/paging parameters worth a typed shape.
                    var queryRecordName = kind == ApiOperationKind.List
                        ? ReadQueryRecord(operation.Value, operationId, resolver, queryRecords)
                        : null;

                    var summary = operation.Value.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
                    operations.Add(new ParsedOperation(operationId, kind, requestRecordName, responseTypeName ?? "", queryRecordName, summary, ReadBool(operation.Value, "deprecated")));

                    // List/Create address the collection; Get/Update/Delete address a single item. The two are
                    // usually the same path, but not always, so each is read from the schema rather than derived
                    // from the other.
                    if (kind is ApiOperationKind.List or ApiOperationKind.Create)
                    {
                        basePath = path.Name.TrimStart('/');
                    }
                    else
                    {
                        itemPaths.Add((operationId, path.Name.TrimStart('/')));
                    }
                }
            }

            if (basePath is not null && operations.Count > 0)
            {
                var itemPath = ResolveItemPath(serviceName, singular, basePath, itemPaths);
                groups.Add(new ParsedResourceGroup(singular, plural, basePath, itemPath, operations));
            }
        }

        var actions = ParseActions(root, actionOperations, resolver, queryRecords);
        return new ParsedApi(serviceName, ResolveApiVersion(serviceName, groups), groups, resolver.Records, resolver.Enums, queryRecords, resolver.DroppedFields, actions);
    }

    /// <summary>
    ///     The prefix Get/Update/Delete build on, taken from those operations' own paths with the trailing id
    ///     parameter removed — the transport puts the caller's id back. It need not equal the collection path
    ///     (Kapsule lists pools under the cluster and fetches one at <c>/pools/{pool_id}</c>), but it must be a shape
    ///     the transport can address, so two cases still fail generation rather than emit a method that compiles and
    ///     404s: an item path not ending in a single <c>{id}</c> parameter, and item operations that disagree on
    ///     their prefix. A group with no item operations falls back to the collection path.
    /// </summary>
    private static string ResolveItemPath(string serviceName, string singular, string basePath, List<(string OperationId, string Path)> itemPaths)
    {
        (string OperationId, string Prefix)? resolved = null;
        foreach (var (operationId, itemPath) in itemPaths)
        {
            if (!TryTrimIdSegment(itemPath, out var prefix))
            {
                throw new InvalidOperationException(
                    $"Resource group '{serviceName}.{singular}' declares '{operationId}' at '/{itemPath}', which does not " +
                    "end in a single '{id}' path parameter. The transport addresses an item as its prefix plus the " +
                    "caller's id, so the generated method would call a route that does not exist. Leave the resource " +
                    "group out with the reason recorded at its SchemaSource.ClientResourceGroups entry."
                );
            }

            if (resolved is { } first && first.Prefix != prefix)
            {
                throw new InvalidOperationException(
                    $"Resource group '{serviceName}.{singular}' addresses items at two different paths: '{first.OperationId}' " +
                    $"at '/{first.Prefix}/{{id}}' and '{operationId}' at '/{prefix}/{{id}}'. One resource group emits one " +
                    "item path, so generation cannot honour both. Split the group or leave it out with the reason " +
                    "recorded at its SchemaSource.ClientResourceGroups entry."
                );
            }

            resolved = (operationId, prefix);
        }

        return resolved?.Prefix ?? basePath;
    }

    // The id segment must be a template token: a literal segment would be overwritten by the caller's id at
    // request time, which is the same defect in a different disguise.
    private static bool TryTrimIdSegment(string itemPath, out string prefix)
    {
        prefix = "";
        var lastSlash = itemPath.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return false;
        }

        var idSegment = itemPath[(lastSlash + 1)..];
        if (idSegment.Length <= 2 || idSegment[0] != '{' || idSegment[^1] != '}')
        {
            return false;
        }

        prefix = itemPath[..lastSlash];
        return true;
    }

    // Declared actions are found by exact operationId. A declaration that doesn't resolve to a supported shape
    // (present, GET, body-less, typed response) throws: the declaration is a promise the schema must keep.
    private static List<ParsedActionOperation> ParseActions(JsonElement root, string[] actionOperations, RecordResolver resolver, List<ParsedRecord> queryRecords)
    {
        var actions = new List<ParsedActionOperation>();
        foreach (var actionId in actionOperations)
        {
            ParsedActionOperation? parsed = null;
            foreach (var path in root.GetProperty("paths").EnumerateObject())
            {
                foreach (var operation in path.Value.EnumerateObject())
                {
                    var operationId = operation.Value.TryGetProperty("operationId", out var idElement) ? idElement.GetString() : null;
                    if (operationId != actionId)
                    {
                        continue;
                    }

                    // Body-less GETs and empty-body POSTs (trigger-style actions like CheckDomain, whose
                    // request schema is a bare object) are supported; anything with a real payload is not.
                    var isPost = operation.Name.ToUpperInvariant() == "POST";
                    var hasBodyFields = operation.Value.TryGetProperty("requestBody", out var requestBody)
                                        && requestBody.TryGetProperty("content", out var content)
                                        && content.TryGetProperty("application/json", out var jsonContent)
                                        && jsonContent.TryGetProperty("schema", out var bodySchema)
                                        && (bodySchema.TryGetProperty("properties", out _) || bodySchema.TryGetProperty("$ref", out _));
                    if ((!isPost && operation.Name.ToUpperInvariant() != "GET") || hasBodyFields || (!isPost && operation.Value.TryGetProperty("requestBody", out _)))
                    {
                        throw new NotSupportedException($"Action operation '{actionId}' is not a body-less GET or empty-body POST; the action emitter only supports those.");
                    }

                    // An action's query parameters become the same typed request record a List's do, so the
                    // scoped client fills project_id here too and nothing the schema declares is dropped.
                    var queryRecordName = ReadQueryRecord(operation.Value, actionId, resolver, queryRecords);
                    if (isPost && queryRecordName is not null)
                    {
                        throw new NotSupportedException(
                            $"Action operation '{actionId}' is a POST declaring query parameter(s), which the emitter has no " +
                            "transport call for — CreateResourceAsync sends a body, not a query string. Leave the operation " +
                            "out with the reason recorded at its SchemaSource.ClientActionOperations entry, or teach the " +
                            "transport to POST with a query."
                        );
                    }

                    var responseTypeName = ReadResponseType(operation.Value, resolver)
                                           ?? throw new NotSupportedException($"Action operation '{actionId}' has no resolvable response type.");
                    var summary = operation.Value.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
                    parsed = new ParsedActionOperation(
                        actionId, path.Name.TrimStart('/'), responseTypeName, queryRecordName, HasRequiredQueryParameter(operation.Value),
                        summary, ReadBool(operation.Value, "deprecated"), isPost
                    );
                }
            }

            actions.Add(parsed ?? throw new NotSupportedException($"Action operation '{actionId}' was declared but not found in the schema."));
        }

        return actions;
    }

    /// <summary>
    ///     Whether the operation marks any <c>in: query</c> parameter <c>required</c>. Such a set is emitted without a
    ///     default, so the caller cannot omit the parameters that name the target — <c>AccessSecretVersionByPath</c>
    ///     identifies its secret through <c>project_id</c>, <c>secret_name</c> and <c>secret_path</c>, and a method
    ///     callable without them would compile and fetch something else.
    /// </summary>
    private static bool HasRequiredQueryParameter(JsonElement operation)
    {
        return operation.TryGetProperty("parameters", out var parameters)
               && parameters.EnumerateArray().Any(parameter =>
                   parameter.TryGetProperty("in", out var location) && location.GetString() == "query" && ReadBool(parameter, "required")
               );
    }

    // A List's query parameters (order_by, page, filters) — and an action's, which may instead be what names the
    // target — become a typed request record: one field per `in: query` parameter, with the same enum/scalar
    // resolution a body field gets. Path parameters (region/zone) are handled by the transport, so they're skipped
    // here. Returns null when there are no query parameters.
    private static string? ReadQueryRecord(JsonElement operation, string operationId, RecordResolver resolver, List<ParsedRecord> queryRecords)
    {
        if (!operation.TryGetProperty("parameters", out var parameters))
        {
            return null;
        }

        var fields = new List<ParsedRecordField>();
        foreach (var parameter in parameters.EnumerateArray())
        {
            if (!parameter.TryGetProperty("in", out var location) || location.GetString() != "query"
                                                                  || !parameter.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { } name
                                                                  || !parameter.TryGetProperty("schema", out var schema))
            {
                continue;
            }

            var csharpType = resolver.ResolveQueryParameterType(schema, CSharpNaming.NormalizeOperationId(operationId) + CSharpNaming.ToPascalCase(name));
            var deprecated = ReadBool(schema, "deprecated");
            var description = parameter.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : null;
            fields.Add(new ParsedRecordField(name, csharpType, null, deprecated, description));
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var recordName = $"{CSharpNaming.NormalizeOperationId(operationId)}Request";
        queryRecords.Add(new ParsedRecord(recordName, fields));
        return recordName;
    }

    /// <summary>
    ///     Reads the version from the URL paths of the groups actually emitted (<c>containers/v1/…</c>), since
    ///     Scaleway's schemas carry no <c>info.version</c>. Throws when they disagree: a service exposing two
    ///     versions at once needs a namespace per version, and silently picking one would mislabel the other.
    /// </summary>
    private static string ResolveApiVersion(string serviceName, List<ParsedResourceGroup> groups)
    {
        // Both paths are read: now that an item path may differ from its collection path, a group straddling two
        // versions would otherwise be labelled with whichever one the collection happened to use.
        var versions = groups
            .SelectMany(group => new[] { group.BasePath, group.ItemPath })
            .Select(path => path.Split('/'))
            .Where(segments => segments.Length > 1 && IsVersionSegment(segments[1]))
            .Select(segments => segments[1])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (versions.Count != 1)
        {
            var found = versions.Count == 0 ? "none" : string.Join(", ", versions);
            throw new InvalidOperationException($"Expected exactly one API version in the '{serviceName}' paths but found {found}.");
        }

        return FormatVersion(versions[0]);
    }

    private static bool IsVersionSegment(string segment)
    {
        return segment.Length > 1 && segment[0] == 'v' && char.IsDigit(segment[1]);
    }

    /// <summary>Turns a wire version into a namespace segment: <c>v1</c> → <c>V1</c>, <c>v1beta1</c> → <c>V1Beta1</c>.</summary>
    private static string FormatVersion(string version)
    {
        var formatted = new StringBuilder();
        var capitalizeNext = true;

        foreach (var character in version)
        {
            // Digits end a word, so the letter starting the next one (the "beta" in v1beta1) is capitalized.
            if (char.IsDigit(character))
            {
                formatted.Append(character);
                capitalizeNext = true;
                continue;
            }

            formatted.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
            capitalizeNext = false;
        }

        return formatted.ToString();
    }

    // Scaleway names operations "<Verb><Resource>" ("CreateContainer") and lists "List<Plural>" ("ListPolicies").
    private static bool TryMatchOperation(string operationId, string singular, string plural, out ApiOperationKind kind)
    {
        kind = default;
        if (operationId == $"List{plural}")
        {
            kind = ApiOperationKind.List;
            return true;
        }

        (string Prefix, ApiOperationKind Kind)[] verbs =
        [
            ("Create", ApiOperationKind.Create), ("Get", ApiOperationKind.Get),
            ("Update", ApiOperationKind.Update), ("Delete", ApiOperationKind.Delete)
        ];

        foreach (var (prefix, verbKind) in verbs)
        {
            if (operationId == $"{prefix}{singular}")
            {
                kind = verbKind;
                return true;
            }
        }

        return false;
    }

    private static string? ReadRequestRecord(JsonElement operation, string operationId, RecordResolver resolver)
    {
        if (!operation.TryGetProperty("requestBody", out var body)
            || !body.TryGetProperty("content", out var content)
            || !content.TryGetProperty("application/json", out var media)
            || !media.TryGetProperty("schema", out var schema)
            || !schema.TryGetProperty("properties", out _))
        {
            return null;
        }

        // Normalised so the request record name matches the normalised method name (CreateApiKeyAsync takes a
        // CreateApiKeyRequest, not a CreateAPIKeyRequest).
        var requestName = $"{CSharpNaming.NormalizeOperationId(operationId)}Request";
        resolver.ResolveObjectRecord(requestName, schema);
        return requestName;
    }

    // Create/Get/Update/Delete return the resource DTO; List returns a "{ <plural>: [DTO], total_count }" wrapper —
    // the transport unwraps to the array, so the generated method deals in the element DTO, not the wrapper.
    private static string? ReadResponseType(JsonElement operation, RecordResolver resolver)
    {
        if (!operation.TryGetProperty("responses", out var responses))
        {
            return null;
        }

        JsonElement schema = default;
        var found = false;
        foreach (var status in responses.EnumerateObject())
        {
            if (status.Name.StartsWith("2", StringComparison.Ordinal)
                && status.Value.TryGetProperty("content", out var content)
                && content.TryGetProperty("application/json", out var media)
                && media.TryGetProperty("schema", out schema))
            {
                found = true;
                break;
            }
        }

        if (!found || !schema.TryGetProperty("$ref", out var refElement))
        {
            return null;
        }

        var name = ShortName(refElement.GetString()!);
        if (name.StartsWith("List", StringComparison.Ordinal) && name.EndsWith("Response", StringComparison.Ordinal))
        {
            return resolver.ResolveListElement(name);
        }

        resolver.EnsureComponentRecord(name);
        return name;
    }

    // YamlDotNet's JSON-compatible serializer renders YAML booleans as strings, so a raw GetBoolean() throws.
    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString() == "true",
            _ => false
        };
    }

    /// <summary>
    ///     The name a component is known by in generated code: everything after its API version segment, so a nested
    ///     component keeps its parent (<c>scaleway.transactional_email.v1alpha1.Domain.Status</c> is
    ///     <c>DomainStatus</c>). Taking only the last segment made <c>Domain.Status</c> and <c>Email.Status</c> one
    ///     <c>Status</c>, and every reference to the domain's status resolved to the email statuses. A component without
    ///     a version segment (<c>google.protobuf.StringValue</c>) keeps its last segment.
    /// </summary>
    internal static string ShortName(string reference)
    {
        var segments = reference[(reference.LastIndexOf('/') + 1)..].Split('.');
        var versionIndex = Array.FindLastIndex(segments, segment => segment.Length > 1 && segment[0] == 'v' && char.IsDigit(segment[1]));
        return versionIndex >= 0 && versionIndex < segments.Length - 1 ? string.Concat(segments[(versionIndex + 1)..]) : segments[^1];
    }
}
