using System.Collections.Immutable;

namespace SharedKernel.Telemetry;

/// <summary>
///     Paths and file extensions excluded from request telemetry. Shared by the OpenTelemetry
///     ASP.NET Core instrumentation filter so scanner probes and static assets do not pollute traces.
/// </summary>
public static class EndpointTelemetryFilter
{
    public static readonly ImmutableHashSet<string> ExcludedPaths = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        // Internal endpoints
        "/swagger", "/internal-api/live", "/internal-api/ready", "/api/track",
        // Common scanner-probe path prefixes -- folders that look like accidentally-deployed sources or developer tooling.
        // We don't serve any of these, so any request matching is bot traffic.
        "/config/", "/src/", "/web/", "/env/", "/server/", "/portal/", "/docker/", "/backend/", "/.well-known/", "/.git/", "/sitemap",
        "/wp-", "/Properties/", "/appsettings.", "/.vercel/", "/.vscode/", "/.idea/", "/secured/", "/tsconfig."
    );

    public static readonly ImmutableHashSet<string> ExcludedFileExtensions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        // Static assets the SPA serves successfully -- noise on every page load
        ".js", ".css", ".png", ".jpg", ".ico", ".map", ".svg", ".woff", ".woff2", "webp",
        // Common scanner-probe file extensions -- secrets, source code, and editor artifacts no legitimate route would have
        ".env", ".yml", ".yaml", ".properties", ".bak", ".old", ".ini", ".config", ".rb", ".py", ".ts", ".sql", ".sh", ".htaccess", ".php", ".local", ".envrc", ".sample", "~"
    );
}
