using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace SharedKernel.Endpoints;

public class HealthEndpoints : IEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        // Both probe endpoints are called by the kubelet over plain HTTP directly to the pod — no
        // forwarded headers, no TLS. They must never run antiforgery validation: the middleware's
        // SecurePolicy.Always check refuses non-SSL requests, which would crash-loop the pod on its
        // own probes.
        routes.MapHealthChecks("/internal-api/ready").AllowAnonymous().DisableAntiforgery();

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        routes.MapHealthChecks("/internal-api/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") }).AllowAnonymous().DisableAntiforgery();
    }
}
