using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using NSwag.Annotations;
using SharedKernel.Telemetry;

namespace SharedKernel.Endpoints;

public class TrackEndpoints : IEndpoints
{
    // <summary>
    //     Maps the track endpoint for client-side telemetry from the web application. The events
    //     are tracked through the OpenTelemetry TelemetryClient as OTel events (log records with
    //     an event.name attribute), which flow through OpenTelemetry to the dashboard/Cockpit. The
    //     endpoint exists to:
    //          * avoid exposing any telemetry credentials to the browser
    //          * limit the amount of data that can be sent
    // </summary>
    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/track", Track).AllowAnonymous().DisableAntiforgery();
    }

    [OpenApiIgnore]
    private static TrackResponse Track(List<TrackRequest> trackRequests, TelemetryClient telemetryClient, ILogger<TrackEndpoints> logger)
    {
        foreach (var trackRequest in trackRequests)
        {
            switch (trackRequest.Type)
            {
                case "pageview":
                    telemetryClient.TrackPageView(trackRequest.Name, trackRequest.Url, trackRequest.Properties);
                    break;
                case "pageviewPerformance":
                    telemetryClient.TrackPageViewPerformance(trackRequest.Name, trackRequest.Url, trackRequest.Measurements);
                    break;
                case "interaction":
                    telemetryClient.TrackInteraction(trackRequest.Name, trackRequest.Url, trackRequest.Properties);
                    break;
                case "exception":
                {
                    var error = trackRequest.Error;
                    telemetryClient.TrackClientException(error?.Name, error?.Message, error?.Stack, error?.ComponentStack, trackRequest.Url);
                    break;
                }
                default:
                    logger.LogWarning("Unsupported telemetry type: {Type}", trackRequest.Type);
                    break;
            }
        }

        return new TrackResponse(true, "Telemetry sent.");
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
