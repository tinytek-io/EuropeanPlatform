using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NSwag.Annotations;
using SharedKernel.Telemetry;

namespace SharedKernel.Endpoints;

public class TrackEndpoints : IEndpoints
{
    private const int MaxRequestBodyBytes = 256 * 1024;

    // The SPA sends at most 25 events per request (telemetryClient.ts EVENTS_PER_REQUEST).
    private const int MaxEventsPerRequest = 25;

    private const int MaxPropertiesPerEvent = 25;

    private const int MaxAttributeNameLength = 100;

    private const int MaxAttributeValueLength = 2048;

    private const int MaxStackLength = 8192;

    // <summary>
    //     Maps the track endpoint for client-side telemetry from the web application. The events
    //     are tracked through the OpenTelemetry TelemetryClient as OTel events (log records with
    //     an event.name attribute), which flow through OpenTelemetry to the dashboard/Cockpit. The
    //     endpoint exists to:
    //          * avoid exposing any telemetry credentials to the browser
    //          * limit the amount of data that can be sent (the limits below; the endpoint is
    //            anonymous, so it must not double as a way to inflate telemetry ingest)
    // </summary>
    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/track", Track).AllowAnonymous().DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
    }

    [OpenApiIgnore]
    private static TrackResponse Track(List<TrackRequest> trackRequests, TelemetryClient telemetryClient, ILogger<TrackEndpoints> logger)
    {
        foreach (var trackRequest in trackRequests.Take(MaxEventsPerRequest))
        {
            var name = Truncate(trackRequest.Name, MaxAttributeValueLength);
            var url = Truncate(trackRequest.Url, MaxAttributeValueLength);
            switch (trackRequest.Type)
            {
                case "pageview":
                    telemetryClient.TrackPageView(name, url, Limit(trackRequest.Properties, value => Truncate(value, MaxAttributeValueLength)));
                    break;
                case "pageviewPerformance":
                    telemetryClient.TrackPageViewPerformance(name, url, Limit(trackRequest.Measurements, value => value));
                    break;
                case "interaction":
                    telemetryClient.TrackInteraction(name, url, Limit(trackRequest.Properties, value => Truncate(value, MaxAttributeValueLength)));
                    break;
                case "exception":
                {
                    var error = trackRequest.Error;
                    telemetryClient.TrackClientException(
                        Truncate(error?.Name, MaxAttributeValueLength), Truncate(error?.Message, MaxAttributeValueLength),
                        Truncate(error?.Stack, MaxStackLength), Truncate(error?.ComponentStack, MaxStackLength), url
                    );
                    break;
                }
                default:
                    logger.LogWarning("Unsupported telemetry type: {Type}", trackRequest.Type);
                    break;
            }
        }

        return new TrackResponse(true, "Telemetry sent.");
    }

    private static Dictionary<string, TValue>? Limit<TValue>(Dictionary<string, TValue>? values, Func<TValue, TValue> limitValue)
    {
        return values?
            .Where(entry => entry.Key.Length <= MaxAttributeNameLength)
            .Take(MaxPropertiesPerEvent)
            .ToDictionary(entry => entry.Key, entry => limitValue(entry.Value));
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int maxLength)
    {
        return value is null || value.Length <= maxLength ? value : value[..maxLength];
    }
}

[PublicAPI]
public record TrackResponse(bool Success, string Message);

[PublicAPI]
public record TrackRequest(
    string Type,
    string Name,
    string? Url,
    Dictionary<string, string>? Properties,
    Dictionary<string, double>? Measurements,
    TrackError? Error
);

[PublicAPI]
public record TrackError(string? Name, string? Message, string? Stack, string? ComponentStack);
