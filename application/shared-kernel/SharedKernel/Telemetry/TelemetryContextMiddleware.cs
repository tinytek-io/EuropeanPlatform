using Microsoft.AspNetCore.Http;

namespace SharedKernel.Telemetry;

public sealed class TelemetryContextMiddleware(OpenTelemetryEnricher openTelemetryEnricher)
    : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        openTelemetryEnricher.Apply();

        await next(context);
    }
}
