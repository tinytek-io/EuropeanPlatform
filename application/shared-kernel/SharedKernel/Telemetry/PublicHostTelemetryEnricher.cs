using Microsoft.AspNetCore.Http;
using SharedKernel.Configuration;

namespace SharedKernel.Telemetry;

// Overrides the request tags OpenTelemetry's ASP.NET Core instrumentation captures at request start
// (before middleware runs), so they reflect the original caller rather than the immediate proxy peer:
//   - server.address / url.* use each service's own Request.Host, trimmed to "<service>.internal" for
//     internal ACA FQDNs, defaulted to https in Azure, with the redundant default :443/:80 stripped.
//     AppGateway shows the public hostname; backend services show "<service>.internal", which is the
//     conventional layered view in distributed tracing.
//   - client.address comes from X-Forwarded-For so the Application Insights geo lookup resolves to the real
//     browser IP instead of the Azure Container Apps ingress envoy in the cluster's region. The header
//     is set by the external ingress, which strips client-supplied values, so spoofing is not possible.
public static class PublicHostTelemetryEnricher
{
    public static void Enrich(Activity activity, HttpRequest request)
    {
        var parts = ResolvePublicUrlParts(request);
        if (parts is not null)
        {
            var (publicHost, publicScheme, fullUrl) = parts.Value;
            var (hostName, explicitPort) = ParseHost(publicHost);

            activity.SetTag("server.address", hostName);
            activity.SetTag("url.scheme", publicScheme);
            activity.SetTag("url.full", fullUrl);
            // Older semconv tag still consumed by some Azure Monitor mappings.
            activity.SetTag("http.url", fullUrl);
            // null skips server.port so Application Insights' URL builder doesn't render the redundant :443/:80.
            activity.SetTag("server.port", explicitPort);
        }

        var clientAddress = ResolveClientAddress(request);
        if (clientAddress is not null)
        {
            // Set only the OTel semconv tag. Azure Monitor maps client.address to the dedicated
            // ClientIp field (anonymized to 0.0.0.0 after geo lookup). Do NOT also set http.client_ip:
            // Azure Monitor treats it as a custom property, leaks the raw IP into customDimensions, and
            // bypasses anonymization -- a PII violation.
            activity.SetTag("client.address", clientAddress);
        }
    }

    private static string? ResolveClientAddress(HttpRequest request)
    {
        var forwardedFor = request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrEmpty(forwardedFor)) return null;

        // Leftmost entry is the original client; any entries after it are intermediate proxies.
        var separatorIndex = forwardedFor.IndexOf(',');
        var leftmost = (separatorIndex < 0 ? forwardedFor : forwardedFor[..separatorIndex]).Trim();
        return leftmost.Length == 0 ? null : leftmost;
    }

    // Trims the env-specific suffix from outbound ACA internal FQDNs so dependency spans show
    // e.g. "account-api.internal/..." instead of "account-api.internal.<env>.azurecontainerapps.io".
    public static void EnrichOutbound(Activity activity, HttpRequestMessage request)
    {
        if (request.RequestUri is null) return;

        var host = request.RequestUri.Host;
        var trimmedHost = TrimAzureContainerAppsSuffix(host);
        if (trimmedHost == host) return;

        var trimmedUrl = request.RequestUri.AbsoluteUri.Replace(host, trimmedHost, StringComparison.OrdinalIgnoreCase);

        activity.SetTag("server.address", trimmedHost);
        activity.SetTag("url.full", trimmedUrl);
    }

    public static string? BuildPublicUrl(HttpRequest request)
    {
        return ResolvePublicUrlParts(request)?.FullUrl;
    }

    private static (string Host, string Scheme, string FullUrl)? ResolvePublicUrlParts(HttpRequest request)
    {
        var hostValue = request.Host.Value ?? string.Empty;
        if (hostValue.Length == 0) return null;

        // Use Request.Host -- the host this service actually received -- and trim the env suffix
        // when it's an internal ACA FQDN. AppGateway's Request.Host is the public hostname set by
        // ACA's external ingress; backend services see "<service>.internal.<env>...", which the
        // trim reduces to "<service>.internal". This keeps each layer's URL truthful instead of
        // overwriting backends with the public host via X-Forwarded-Host.
        var publicHost = TrimAzureContainerAppsSuffix(hostValue);

        var publicScheme = request.Headers["X-Forwarded-Proto"].ToString();
        if (string.IsNullOrEmpty(publicScheme))
        {
            // The cluster gateway terminates TLS so the Kestrel-facing scheme is http; default
            // to https when deployed rather than mislabelling the public URL.
            publicScheme = SharedInfrastructureConfiguration.IsRunningInCloud ? "https" : request.Scheme;
        }

        publicHost = StripDefaultPort(publicHost, publicScheme);

        var fullUrl = $"{publicScheme}://{publicHost}{request.PathBase}{request.Path}{request.QueryString}";
        return (publicHost, publicScheme, fullUrl);
    }

    private static string TrimAzureContainerAppsSuffix(string host)
    {
        var internalIndex = host.IndexOf(".internal.", StringComparison.OrdinalIgnoreCase);
        if (internalIndex < 0 || !host.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase)) return host;
        return host[..(internalIndex + ".internal".Length)];
    }

    private static string StripDefaultPort(string host, string scheme)
    {
        var colonIndex = host.IndexOf(':');
        if (colonIndex < 0) return host;
        if (!int.TryParse(host[(colonIndex + 1)..], out var port)) return host;
        if ((scheme == "https" && port == 443) || (scheme == "http" && port == 80)) return host[..colonIndex];
        return host;
    }

    // Default ports were already stripped, so any remaining port is explicit and non-default.
    private static (string Host, int? Port) ParseHost(string hostHeader)
    {
        var separatorIndex = hostHeader.IndexOf(':');
        if (separatorIndex < 0) return (hostHeader, null);

        var hostName = hostHeader[..separatorIndex];
        return int.TryParse(hostHeader[(separatorIndex + 1)..], out var port)
            ? (hostName, port)
            : (hostName, null);
    }
}
