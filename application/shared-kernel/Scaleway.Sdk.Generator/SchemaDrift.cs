namespace Scaleway.Sdk.Generator;

/// <summary>How a schema change affects the generated SDK, and hence whether it should gate a refresh.</summary>
public enum SchemaDriftKind
{
    /// <summary>
    ///     A change that breaks consumers or the generated surface — a removed operation/field/enum value, a
    ///     changed field type, or a version bump. Gates a schema refresh (the caller exits non-zero).
    /// </summary>
    Breaking,

    /// <summary>A purely additive change — a new operation, field, or enum value. Safe to adopt.</summary>
    Additive,

    /// <summary>An informational signal — something newly marked deprecated by Scaleway.</summary>
    Signal
}

/// <summary>A single classified difference between two versions of a service's parsed schema.</summary>
public sealed record SchemaDriftChange(SchemaDriftKind Kind, string Service, string Description);

/// <summary>The result of comparing two parsed schemas: every change, most-severe first.</summary>
public sealed record SchemaDriftReport(IReadOnlyList<SchemaDriftChange> Changes)
{
    public bool HasBreaking => Changes.Any(change => change.Kind == SchemaDriftKind.Breaking);

    /// <summary>A human-readable summary for console output — one line per change, then a count by kind.</summary>
    public string Render()
    {
        if (Changes.Count == 0)
        {
            return "No schema drift: the generated surface is unchanged.";
        }

        var lines = Changes.Select(change => $"  {change.Kind.ToString().ToUpperInvariant(),-9} {change.Service}: {change.Description}");
        var byKind = Changes.GroupBy(change => change.Kind).OrderBy(group => (int)group.Key);
        var summary = string.Join(", ", byKind.Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}"));
        return $"{string.Join('\n', lines)}\n{Changes.Count} change(s): {summary}.";
    }
}

/// <summary>
///     Classifies what changed between two <see cref="ParsedApi" />s of the same service — the generated surface
///     before and after a schema refresh. Scoped to what the SDK actually generates (the parsed model), so drift in
///     un-generated corners of the schema is correctly ignored: only changes that reach the generated clients are
///     reported. Comparing the parsed model rather than the raw YAML also skips reordering and formatting noise.
/// </summary>
public static class SchemaDriftComparer
{
    public static SchemaDriftReport Compare(ParsedApi previous, ParsedApi current)
    {
        var service = current.ServiceName;
        var changes = new List<SchemaDriftChange>();

        if (previous.ApiVersion != current.ApiVersion)
        {
            // The version is part of the namespace, so a bump moves every generated type — breaking for consumers.
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"API version changed {previous.ApiVersion} → {current.ApiVersion}"));
        }

        CompareOperations(previous, current, service, changes);
        CompareActions(previous, current, service, changes);
        CompareRecords(previous, current, service, changes);
        CompareEnums(previous, current, service, changes);

        // Most severe first so the report reads top-down and a caller can slice by kind.
        var ordered = changes
            .OrderBy(change => (int)change.Kind)
            .ThenBy(change => change.Description, StringComparer.Ordinal)
            .ToList();
        return new SchemaDriftReport(ordered);
    }

    private static void CompareOperations(ParsedApi previous, ParsedApi current, string service, List<SchemaDriftChange> changes)
    {
        var previousOps = previous.ResourceGroups.SelectMany(group => group.Operations).ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);
        var currentOps = current.ResourceGroups.SelectMany(group => group.Operations).ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);

        foreach (var (id, _) in previousOps.Where(operation => !currentOps.ContainsKey(operation.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"operation {id} removed"));
        }

        foreach (var (id, _) in currentOps.Where(operation => !previousOps.ContainsKey(operation.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, service, $"operation {id} added"));
        }

        foreach (var (id, currentOp) in currentOps.Where(operation => previousOps.ContainsKey(operation.Key)))
        {
            if (!previousOps[id].Deprecated && currentOp.Deprecated)
            {
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Signal, service, $"operation {id} deprecated"));
            }
        }
    }

    // An action's generated signature is its path parameters, its request record, its query record and whether that
    // record is required, and its response type; its HTTP method and path decide what the call does. A change to any of
    // them moves the method under its callers, so it is breaking — except a required query becoming optional, which
    // existing call sites still compile against.
    private static void CompareActions(ParsedApi previous, ParsedApi current, string service, List<SchemaDriftChange> changes)
    {
        var previousActions = previous.Actions.ToDictionary(action => action.OperationId, StringComparer.Ordinal);
        var currentActions = current.Actions.ToDictionary(action => action.OperationId, StringComparer.Ordinal);

        foreach (var (id, _) in previousActions.Where(action => !currentActions.ContainsKey(action.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"action {id} removed"));
        }

        foreach (var (id, _) in currentActions.Where(action => !previousActions.ContainsKey(action.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, service, $"action {id} added"));
        }

        foreach (var (id, currentAction) in currentActions.Where(action => previousActions.ContainsKey(action.Key)))
        {
            var previousAction = previousActions[id];

            void Breaking(string what, string? before, string? after)
            {
                if (before != after)
                {
                    changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"action {id} {what} changed {before ?? "none"} → {after ?? "none"}"));
                }
            }

            Breaking("HTTP method", previousAction.HttpMethod, currentAction.HttpMethod);
            Breaking("path", previousAction.PathTemplate, currentAction.PathTemplate);
            Breaking("request", previousAction.RequestRecordName, currentAction.RequestRecordName);
            Breaking("query", previousAction.QueryRecordName, currentAction.QueryRecordName);
            Breaking("response", previousAction.ResponseTypeName, currentAction.ResponseTypeName);

            if (previousAction.QueryRequired != currentAction.QueryRequired)
            {
                changes.Add(currentAction.QueryRequired
                    ? new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"action {id} query became required")
                    : new SchemaDriftChange(SchemaDriftKind.Additive, service, $"action {id} query became optional")
                );
            }

            if (!previousAction.Deprecated && currentAction.Deprecated)
            {
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Signal, service, $"action {id} deprecated"));
            }
        }
    }

    private static void CompareRecords(ParsedApi previous, ParsedApi current, string service, List<SchemaDriftChange> changes)
    {
        var previousRecords = AllRecords(previous).ToDictionary(record => record.Name, StringComparer.Ordinal);
        var currentRecords = AllRecords(current).ToDictionary(record => record.Name, StringComparer.Ordinal);

        foreach (var (name, _) in previousRecords.Where(record => !currentRecords.ContainsKey(record.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"record {name} removed"));
        }

        foreach (var (name, _) in currentRecords.Where(record => !previousRecords.ContainsKey(record.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, service, $"record {name} added"));
        }

        foreach (var (name, currentRecord) in currentRecords.Where(record => previousRecords.ContainsKey(record.Key)))
        {
            CompareFields(previousRecords[name], currentRecord, service, changes);
        }
    }

    private static void CompareFields(ParsedRecord previous, ParsedRecord current, string service, List<SchemaDriftChange> changes)
    {
        var previousFields = previous.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var currentFields = current.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);

        foreach (var (name, _) in previousFields.Where(field => !currentFields.ContainsKey(field.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"field {current.Name}.{name} removed"));
        }

        foreach (var (name, _) in currentFields.Where(field => !previousFields.ContainsKey(field.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, service, $"field {current.Name}.{name} added"));
        }

        foreach (var (name, currentField) in currentFields.Where(field => previousFields.ContainsKey(field.Key)))
        {
            var previousField = previousFields[name];
            if (previousField.CSharpType != currentField.CSharpType)
            {
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"field {current.Name}.{name} type changed {previousField.CSharpType} → {currentField.CSharpType}"));
            }

            if (!previousField.Deprecated && currentField.Deprecated)
            {
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Signal, service, $"field {current.Name}.{name} deprecated"));
            }
        }
    }

    private static void CompareEnums(ParsedApi previous, ParsedApi current, string service, List<SchemaDriftChange> changes)
    {
        var previousEnums = previous.Enums.ToDictionary(parsedEnum => parsedEnum.Name, StringComparer.Ordinal);
        var currentEnums = current.Enums.ToDictionary(parsedEnum => parsedEnum.Name, StringComparer.Ordinal);

        foreach (var (name, _) in previousEnums.Where(parsedEnum => !currentEnums.ContainsKey(parsedEnum.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"enum {name} removed"));
        }

        foreach (var (name, _) in currentEnums.Where(parsedEnum => !previousEnums.ContainsKey(parsedEnum.Key)))
        {
            changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, service, $"enum {name} added"));
        }

        foreach (var (name, currentEnum) in currentEnums.Where(parsedEnum => previousEnums.ContainsKey(parsedEnum.Key)))
        {
            var previousValues = previousEnums[name].Values.ToHashSet(StringComparer.Ordinal);
            var currentValues = currentEnum.Values.ToHashSet(StringComparer.Ordinal);

            foreach (var value in previousValues.Where(value => !currentValues.Contains(value)))
            {
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Breaking, service, $"enum {name} value '{value}' removed"));
            }

            foreach (var value in currentValues.Where(value => !previousValues.Contains(value)))
            {
                changes.Add(new SchemaDriftChange(SchemaDriftKind.Additive, service, $"enum {name} value '{value}' added"));
            }
        }
    }

    private static IEnumerable<ParsedRecord> AllRecords(ParsedApi api)
    {
        return api.Records.Concat(api.QueryRecords);
    }
}
