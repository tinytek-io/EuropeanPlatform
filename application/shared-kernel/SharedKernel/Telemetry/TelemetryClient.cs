using System.Text;
using SharedKernel.ExecutionContext;

namespace SharedKernel.Telemetry;

/// <summary>
///     OpenTelemetry-backed telemetry client — the replacement for the Application Insights
///     TelemetryClient removed in the Scaleway conversion. Telemetry events, client-side telemetry
///     forwarded by the SPA through /api/track, and worker-emitted events are recorded as
///     OpenTelemetry events: log records with an `event.name` attribute, following the OpenTelemetry
///     semantic conventions for events. Every property travels as a flat attribute — never a
///     nested dictionary — so the OTLP exporters configured by
///     SharedInfrastructureConfiguration.AddSharedTelemetry (the Aspire dashboard in local
///     development, Scaleway Cockpit when deployed) index each value individually. Exceptions use
///     the `exception.*` log conventions, URLs use `url.full`, and the execution context (tenant,
///     user, role) rides along on every event. The client IP is deliberately not attached: a raw IP
///     in a log attribute is PII (see PublicHostTelemetryEnricher).
/// </summary>
public sealed class TelemetryClient(ILogger<TelemetryClient> logger, IExecutionContext executionContext)
{
    // Attributes only the platform sets: the event name and the execution context. Supplied
    // properties can never set them, so an anonymous /api/track caller cannot forge an event's
    // type or stamp it with another tenant or user.
    private static readonly HashSet<string> ReservedAttributeNames = ["event.name", "tenant.id", "tenant.subscription_plan", "user.id", "user.role"];

    /// <summary>Tracks a custom telemetry event (past-tense name, event.* properties).</summary>
    public void TrackEvent(string eventName, IReadOnlyDictionary<string, string> properties)
    {
        // Event names follow the attribute naming rules: the PascalCase event name is emitted in
        // snake_case (LoginCompleted -> login_completed).
        LogEvent(LogLevel.Information, ToSnakeCase(eventName), eventName, WithExecutionContext(properties));
    }

    /// <summary>Tracks a client-side page view forwarded by the SPA through /api/track.</summary>
    public void TrackPageView(string name, string? url, IReadOnlyDictionary<string, string>? properties)
    {
        var attributes = WithExecutionContext(properties);
        attributes["page.name"] = name;
        AddUrl(attributes, url);
        LogEvent(LogLevel.Information, "page_view", $"page_view: {name}", attributes);
    }

    /// <summary>Tracks client-side page-view performance timings forwarded by the SPA.</summary>
    public void TrackPageViewPerformance(string name, string? url, IReadOnlyDictionary<string, double>? measurements)
    {
        var attributes = WithExecutionContext(measurements);
        attributes["page.name"] = name;
        AddUrl(attributes, url);
        LogEvent(LogLevel.Information, "page_view_performance", $"page_view_performance: {name}", attributes);
    }

    /// <summary>Tracks a client-side user interaction forwarded by the SPA through /api/track.</summary>
    public void TrackInteraction(string name, string? url, IReadOnlyDictionary<string, string>? properties)
    {
        var attributes = WithExecutionContext(properties);
        attributes["interaction.name"] = name;
        AddUrl(attributes, url);
        LogEvent(LogLevel.Information, "interaction", $"interaction: {name}", attributes);
    }

    /// <summary>Tracks a client-side exception forwarded by the SPA through /api/track.</summary>
    public void TrackClientException(string? typeName, string? message, string? stack, string? componentStack, string? url)
    {
        var attributes = ExecutionContextAttributes();
        if (typeName is not null)
        {
            attributes["exception.type"] = typeName;
        }

        if (message is not null)
        {
            attributes["exception.message"] = message;
        }

        if (stack is not null)
        {
            attributes["exception.stacktrace"] = stack;
        }

        if (componentStack is not null)
        {
            attributes["component_stack"] = componentStack;
        }

        AddUrl(attributes, url);

        var displayMessage = typeName is null ? message ?? "Client exception" : message is null ? typeName : $"{typeName}: {message}";
        LogEvent(LogLevel.Error, "exception", displayMessage, attributes);
    }

    // Emits one OpenTelemetry event: a log record whose state is a flat attribute list headed by
    // `event.name`. The state deliberately carries no {OriginalFormat} entry — the OTel SDK
    // exports it verbatim as an attribute — and the formatter supplies the display body instead.
    private void LogEvent(LogLevel level, string eventName, string displayMessage, IReadOnlyDictionary<string, object?> attributes)
    {
        var state = new List<KeyValuePair<string, object?>>(attributes.Count + 1) { new("event.name", eventName) };
        foreach (var (key, value) in attributes)
        {
            state.Add(new KeyValuePair<string, object?>(key, value));
        }

        logger.Log(level, default, state, null, (_, _) => displayMessage);
    }

    private static void AddUrl(Dictionary<string, object?> attributes, string? url)
    {
        if (url is not null)
        {
            attributes["url.full"] = url;
        }
    }

    // Tenant/user context travels as flat attributes on every event, mirroring the tags
    // OpenTelemetryEnricher sets on activities — the OTel replacement for the Application Insights
    // telemetry initializer that used to stamp every telemetry item.
    private Dictionary<string, object?> WithExecutionContext<TValue>(IReadOnlyDictionary<string, TValue>? values)
        where TValue : notnull
    {
        var attributes = ExecutionContextAttributes();
        if (values is not null)
        {
            foreach (var (key, value) in values)
            {
                if (ReservedAttributeNames.Contains(key)) continue;
                attributes[key] = value;
            }
        }

        return attributes;
    }

    // An explicit tenant scope (TenantScopedTelemetryContext.Set) takes precedence over the injected
    // context, so events emitted while processing another tenant's data are attributed to that tenant.
    private Dictionary<string, object?> ExecutionContextAttributes()
    {
        var attributes = new Dictionary<string, object?>();
        var effectiveExecutionContext = TenantScopedTelemetryContext.Current ?? executionContext;

        if (effectiveExecutionContext.TenantId is not null)
        {
            attributes["tenant.id"] = effectiveExecutionContext.TenantId.Value;
        }

        if (effectiveExecutionContext.UserInfo.SubscriptionPlan is not null)
        {
            attributes["tenant.subscription_plan"] = effectiveExecutionContext.UserInfo.SubscriptionPlan;
        }

        if (effectiveExecutionContext.UserInfo.Id is not null)
        {
            attributes["user.id"] = effectiveExecutionContext.UserInfo.Id.Value;
        }

        if (effectiveExecutionContext.UserInfo.Role is not null)
        {
            attributes["user.role"] = effectiveExecutionContext.UserInfo.Role;
        }

        return attributes;
    }

    // Event names follow the attribute naming rules (lowercase words): the PascalCase event name
    // is emitted in snake_case.
    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (char.IsUpper(character) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
