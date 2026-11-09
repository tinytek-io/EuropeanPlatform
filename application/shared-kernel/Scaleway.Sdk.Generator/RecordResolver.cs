using System.Text.Json;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     Resolves <c>components.schemas</c> (and inline nested objects) into flat <see cref="ParsedRecord" />s,
///     following <c>$ref</c> recursively. Scaleway keys components by fully-qualified dotted name
///     (<c>scaleway.containers.v1beta1.Container</c>); we key records by the short final segment (<c>Container</c>).
/// </summary>
internal sealed class RecordResolver
{
    private readonly Dictionary<string, string> _componentNamesByShortName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParsedClientEnum> _enums = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParsedRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _schemasByShortName = new(StringComparer.Ordinal);

    public RecordResolver(JsonElement schemas)
    {
        foreach (var schema in schemas.EnumerateObject())
        {
            var shortName = OpenApiClientParser.ShortName(schema.Name);
            _schemasByShortName[shortName] = schema.Value;
            // Records are keyed by the short final segment; the dotted name Scaleway publishes is kept so a
            // generated type can point at the component it came from.
            _componentNamesByShortName[shortName] = schema.Name;
        }
    }

    public List<ParsedRecord> Records => _records.Values.ToList();

    public List<ParsedClientEnum> Enums => _enums.Values.ToList();

    public List<ParsedDroppedField> DroppedFields { get; } = [];

    public void EnsureComponentRecord(string shortName)
    {
        if (_records.ContainsKey(shortName) || _inProgress.Contains(shortName))
        {
            return;
        }

        if (_schemasByShortName.TryGetValue(shortName, out var schema))
        {
            ResolveObjectRecord(shortName, schema, _componentNamesByShortName.GetValueOrDefault(shortName));
        }
    }

    /// <summary>A List response is <c>{ &lt;plural&gt;: [DTO], total_count }</c>; resolve and return the element DTO.</summary>
    public string? ResolveListElement(string listResponseShortName)
    {
        if (!_schemasByShortName.TryGetValue(listResponseShortName, out var schema) || !schema.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.TryGetProperty("type", out var type) && type.GetString() == "array"
                                                                    && property.Value.TryGetProperty("items", out var items) && items.TryGetProperty("$ref", out var itemRef))
            {
                var elementName = OpenApiClientParser.ShortName(itemRef.GetString()!);
                EnsureComponentRecord(elementName);
                return elementName;
            }
        }

        return null;
    }

    /// <summary>Resolves an object schema to a flat record, keyed by <paramref name="name" />.</summary>
    /// <param name="name">The short C# type name to emit the record under.</param>
    /// <param name="objectSchema">The schema whose <c>properties</c> become the record's fields.</param>
    /// <param name="schemaComponent">
    ///     The dotted component name, passed only when this record <em>is</em> that component. A nested inline object
    ///     that normalizes onto a name a component also happens to use is a different type resolved from a different
    ///     schema, so claiming the component for it would point a reader at documentation for something else.
    /// </param>
    public void ResolveObjectRecord(string name, JsonElement objectSchema, string? schemaComponent = null)
    {
        // Add to the in-progress set as part of the guard: HashSet.Add returns false if the name is already being
        // resolved (a recursive/cyclic reference), which short-circuits alongside the already-resolved check.
        if (_records.ContainsKey(name) || !_inProgress.Add(name))
        {
            return;
        }

        var fields = CollectFields(name, objectSchema, true);
        _inProgress.Remove(name);
        _records[name] = new ParsedRecord(name, fields, ReadDescription(objectSchema), schemaComponent);
    }

    /// <summary>
    ///     Resolves a nested inline object to a record, collapsing the copies Scaleway denormalizes across an
    ///     entity and its create/update requests onto one type — the same way inline enums are collapsed — so a
    ///     response's <c>ScalingOption</c> is assignable to a request's. A same-named record with a different
    ///     shape keeps its unabbreviated context name instead.
    /// </summary>
    private string ResolveNestedRecord(string contextName, JsonElement objectSchema)
    {
        var normalized = NormalizeSharedName(contextName);
        if (!_records.ContainsKey(normalized))
        {
            ResolveObjectRecord(normalized, objectSchema);
            return normalized;
        }

        var candidate = CollectFields(normalized, objectSchema, false);
        if (HasSameShape(candidate, _records[normalized].Fields))
        {
            return normalized;
        }

        // Nothing was stripped, so there is no longer name to fall back to: a differently-shaped record (from a
        // request context that normalized onto this name) already claimed it. Returning either type silently
        // mislabels one of them — fail generation loudly instead.
        if (contextName == normalized)
        {
            throw new InvalidOperationException(
                $"Nested object '{contextName}' has the same name as an already-resolved record with a different shape. The naming scheme cannot distinguish them; disambiguate in the generator."
            );
        }

        ResolveObjectRecord(contextName, objectSchema);
        return contextName;
    }

    /// <summary>
    ///     Whether two field lists describe the same type. Deliberately compares name, emitted type and one-of group
    ///     while ignoring <see cref="ParsedRecordField.Description" />: Scaleway inlines the same nested object on an
    ///     entity and on its create/update requests and words the descriptions differently between them, so
    ///     comparing whole fields would stop collapsing them onto one record and emit a near-duplicate type per
    ///     context — the exact outcome <see cref="NormalizeSharedName" /> exists to prevent.
    /// </summary>
    private static bool HasSameShape(List<ParsedRecordField> candidate, List<ParsedRecordField> resolved)
    {
        return candidate.Count == resolved.Count && candidate.Zip(resolved).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.CSharpType == pair.Second.CSharpType && pair.First.OneOfGroup == pair.Second.OneOfGroup
        );
    }

    private static string? ReadDescription(JsonElement schema)
    {
        return schema.TryGetProperty("description", out var description) ? description.GetString() : null;
    }

    private List<ParsedRecordField> CollectFields(string name, JsonElement objectSchema, bool recordDrops)
    {
        var fields = new List<ParsedRecordField>();
        if (objectSchema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                var csharpType = ResolveType(property.Value, name + CSharpNaming.ToPascalCase(property.Name));
                if (csharpType is not null)
                {
                    // "x-one-of" marks the property as one alternative of a mutually-exclusive group.
                    var oneOfGroup = property.Value.TryGetProperty("x-one-of", out var group) ? group.GetString() : null;
                    fields.Add(new ParsedRecordField(property.Name, csharpType, oneOfGroup, Description: ReadDescription(property.Value)));
                }
                else if (recordDrops)
                {
                    // The generator can't type this property, so the record omits it. Record it rather than let it
                    // vanish silently — this is the tcp-probe class of gap.
                    DroppedFields.Add(new ParsedDroppedField(name, property.Name, DropReason(property.Value)));
                }
            }
        }

        return fields;
    }

    /// <summary>
    ///     Resolves a single scalar/enum schema (a query parameter's <c>schema</c>) to its C# type, registering any
    ///     inline enum the same way a body field's would be. Returns <c>string?</c> for shapes with no scalar type.
    /// </summary>
    public string ResolveQueryParameterType(JsonElement schema, string contextName)
    {
        return ResolveType(schema, contextName) ?? "string?";
    }

    // A best-effort explanation of why a property couldn't be typed, from a shallow look at its schema.
    private static string DropReason(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out _))
        {
            return "unresolved $ref";
        }

        var type = schema.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        return type switch
        {
            // The tcp/probe class: an object with no properties (Scaleway documents it in prose, not schema).
            "object" => "untyped object (no properties)",
            "array" => "array with an unsupported element type",
            null => "no type declared",
            _ => $"unsupported type '{type}'"
        };
    }

    private string? ResolveType(JsonElement element, string contextName)
    {
        if (element.TryGetProperty("$ref", out var refElement))
        {
            var shortName = OpenApiClientParser.ShortName(refElement.GetString()!);
            if (!_schemasByShortName.TryGetValue(shortName, out var target))
            {
                return null;
            }

            // A referenced enum is named after the component so every field pointing at it shares one type.
            if (target.TryGetProperty("enum", out var referencedValues))
            {
                return RegisterEnum(shortName, referencedValues);
            }

            // An object component becomes a named (shared) record; a scalar component is resolved inline against
            // its target schema.
            if (target.TryGetProperty("properties", out _) && !IsStringMap(target))
            {
                EnsureComponentRecord(shortName);
                return $"{shortName}?";
            }

            return ResolveType(target, contextName);
        }

        // An inline enum is named after the record + property that declares it.
        if (element.TryGetProperty("enum", out var inlineValues))
        {
            return RegisterEnum(contextName, inlineValues);
        }

        var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var format = element.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : null;

        switch (type)
        {
            case "string":
                return format == "date-time" ? "DateTimeOffset?" : "string?";
            case "integer":
                return "long?";
            // JSON Schema's "number" is a real, not a whole number — mapping it to long deserialized fine until
            // Scaleway returned a fractional one (k8s autoscaler's scale_down_utilization_threshold is 0.5), and
            // then threw mid-deploy. double covers both float and double formats without a second mapping.
            case "number":
                return "double?";
            case "boolean":
                return "bool?";
            case "array":
                return ResolveArrayType(element);
            case "object":
                if (IsStringMap(element))
                {
                    return "Dictionary<string, string>?";
                }

                // An object with properties is a record; one without is a marker (e.g. RDB ipam_config, meaning
                // "let Scaleway IPAM auto-assign") — emit it as an empty record so it can be set to {} rather than
                // silently dropped. This matches how Scaleway's own SDKs model these (an empty struct).
                return $"{ResolveNestedRecord(contextName, element)}?";
            default:
                return null;
        }
    }

    /// <summary>
    ///     Registers an enum's value set under a PascalCase type name and returns that type. Falls back to
    ///     <c>string?</c> when the set is empty or not string-valued, so an unusable enum never blocks generation.
    /// </summary>
    private string RegisterEnum(string name, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array)
        {
            return "string?";
        }

        var members = values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (members.Count == 0)
        {
            return "string?";
        }

        var typeName = CSharpNaming.ToPascalCase(NormalizeSharedName(name));

        // Same collapsed name but a different value set means these are genuinely distinct enums that merely share
        // a suffix; keep the second one under its unabbreviated context name rather than silently overwriting.
        if (_enums.TryGetValue(typeName, out var existing) && !existing.Values.SequenceEqual(members, StringComparer.Ordinal))
        {
            typeName = CSharpNaming.ToPascalCase(name);
        }

        _enums[typeName] = new ParsedClientEnum(typeName, members);
        return $"{typeName}?";
    }

    /// <summary>
    ///     Scaleway inlines the same logical enum or object on an entity and on its create/update requests, which
    ///     would emit three incompatible types for one concept (a response's <c>Privacy</c> or
    ///     <c>ScalingOption</c> could not be assigned to a request's). Stripping the request wrapper collapses
    ///     them onto the entity's name.
    /// </summary>
    private static string NormalizeSharedName(string name)
    {
        var normalized = name;
        foreach (var verb in (string[])["Create", "Update"])
        {
            if (normalized.StartsWith(verb, StringComparison.Ordinal))
            {
                normalized = normalized[verb.Length..];
                break;
            }
        }

        var requestIndex = normalized.IndexOf("Request", StringComparison.Ordinal);
        if (requestIndex >= 0)
        {
            normalized = normalized.Remove(requestIndex, "Request".Length);
        }

        return normalized.Length > 0 ? normalized : name;
    }

    // Scaleway models a string map either as an object with additionalProperties, or as an object whose only
    // property is a placeholder key like "<environment_variableKey>". Either way it's a Dictionary, not a record.
    private static bool IsStringMap(JsonElement objectSchema)
    {
        if (objectSchema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
        {
            return true;
        }

        if (objectSchema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name.StartsWith('<'))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string? ResolveArrayType(JsonElement element)
    {
        if (!element.TryGetProperty("items", out var items))
        {
            return null;
        }

        if (items.TryGetProperty("$ref", out var itemRef))
        {
            var elementName = OpenApiClientParser.ShortName(itemRef.GetString()!);

            // A $ref item isn't necessarily an object: an enum component becomes a list of the extensible enum,
            // and a scalar component resolves to its underlying array shape — treating either as a record would
            // emit an empty, useless type.
            if (_schemasByShortName.TryGetValue(elementName, out var target))
            {
                if (target.TryGetProperty("enum", out var referencedValues))
                {
                    // Named with its parent qualifier ("SecretVersion.Status" → SecretVersionStatus) so it lands
                    // on the same type the parent record's own field resolves to, not a bare near-duplicate.
                    return $"List<{RegisterEnum(QualifiedComponentName(itemRef.GetString()!), referencedValues).TrimEnd('?')}>?";
                }

                if (!target.TryGetProperty("properties", out _))
                {
                    return ResolveArrayItemScalar(target);
                }
            }

            EnsureComponentRecord(elementName);
            return $"List<{elementName}>?";
        }

        return ResolveArrayItemScalar(items);
    }

    /// <summary>
    ///     The component's name including its parent qualifier: every PascalCase segment after the version
    ///     (<c>scaleway.secret_manager.v1beta1.SecretVersion.Status</c> → <c>SecretVersionStatus</c>).
    /// </summary>
    private static string QualifiedComponentName(string reference)
    {
        var segments = reference.Split('.').Where(segment => segment.Length > 0 && char.IsUpper(segment[0]));
        return string.Concat(segments);
    }

    private static string? ResolveArrayItemScalar(JsonElement items)
    {
        var itemType = items.TryGetProperty("type", out var type) ? type.GetString() : null;
        return itemType switch
        {
            "string" => "string[]?",
            "integer" => "long[]?",
            // Same distinction as the scalar case: "number" is a real, and a long[] would reject 0.5.
            "number" => "double[]?",
            _ => null
        };
    }
}
