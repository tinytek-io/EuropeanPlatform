using AppGateway;
using AppGateway.ApiAggregation;
using AppGateway.Filters;
using AppGateway.InternalTls;
using AppGateway.Middleware;
using AppGateway.Transformations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using SharedKernel.Configuration;
using SharedKernel.Platform;

var builder = WebApplication.CreateBuilder(args);

// Wire OpenTelemetry, Azure Monitor exporter, and Application Insights so AppGateway requests,
// dependencies, traces, and logs surface in the cluster's Application Insights workspace alongside the APIs.
builder.AddSharedTelemetry();

// The gateway has no DbContext, so AddSharedInfrastructure never ran for it — but deployed it
// still reads configuration from Scaleway Secret Manager (the internal CA, the token signing
// key). A no-op locally and in tests.
builder.Configuration.AddScalewaySecretManager(TimeSpan.FromMinutes(5));

builder.Services
    .AddOptions<HostnamesOptions>()
    .Bind(builder.Configuration.GetSection(HostnamesOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !string.IsNullOrWhiteSpace(o.App), "Hostnames:App must be configured.")
    .ValidateOnStart();

// PortAllocation reads .workspace/port.txt by walking up to the repo root for .git, which doesn't
// exist in Azure Container Apps (working dir /app). Locally we load the real allocation; in Azure we
// register a sentinel so downstream consumers (YARP filters, middleware, HttpClient factories) still
// resolve the dependency, while their {SERVICE}_API_URL env-var paths take priority over the unused
// port values.
builder.Services.AddSingleton(SharedInfrastructureConfiguration.IsRunningInCloud ? new PortAllocation(0) : PortAllocation.Load());

// In-cluster TLS: serve HTTPS only (Terraform-managed certificate from Secret Manager), and
// validate the APIs' internal-CA certificates when proxying or aggregating.
builder.AddInternalTls();
builder.Services.AddInternalCaTrust(builder.Configuration);

var reverseProxyBuilder = builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddConfigFilter<ClusterDestinationConfigFilter>()
    .AddConfigFilter<ApiExplorerRouteFilter>()
    .AddConfigFilter<HostMatchConfigFilter>()
    .AddConfigFilter<StorageBucketPrefixConfigFilter>()
    .AddTransforms(context => context.RequestTransforms.Add(context.Services.GetRequiredService<BlockInternalApiTransform>()));

// account-api serves two host-scoped SPAs (the app host and the back-office host) and
// disambiguates them by Request.Host; its endpoints therefore require the public host to be
// delivered intact. YARP's default suppresses the original host, so the account routes in
// appsettings.json set "RequestHeaderOriginalHost": true. The account-storage routes are
// excluded: they target S3, which needs the bucket's own host.

// Avatars and logos live in public-read S3 buckets, so the proxy forwards unsigned requests and
// needs no storage credentials or request transforms. HSTS applies when TLS terminates at the
// cluster gateway in front of this proxy; it is skipped in local development.
if (SharedInfrastructureConfiguration.IsRunningInCloud)
{
    builder.Services.AddSingleton<HttpStrictTransportSecurityTransform>();
    reverseProxyBuilder.AddTransforms(context =>
        context.ResponseTransforms.Add(context.Services.GetRequiredService<HttpStrictTransportSecurityTransform>())
    );
}

builder.WebHost.UseKestrel(option => option.AddServerHeader = false);

builder.Services.AddHttpClient("Account", (sp, client) =>
    {
        var ports = sp.GetRequiredService<PortAllocation>();
        var productionUrl = Environment.GetEnvironmentVariable("ACCOUNT_API_URL");
        client.BaseAddress = !string.IsNullOrEmpty(productionUrl)
            ? new Uri(productionUrl)
            : new Uri($"https://localhost:{ports.AccountApi}");
        // Allow cold-start refreshes to complete and deliver the new Set-Cookie back to the browser.
        // The browser has no client-side timeout, so the gateway is the authoritative timeout for refresh.
        client.Timeout = TimeSpan.FromSeconds(60);
    }
);

builder.Services
    .AddHttpClient()
    .AddHttpForwardHeaders() // Ensure the correct client IP addresses are set for downstream requests
    .AddOutputCache();

builder.Services
    .AddSingleton(SharedDependencyConfiguration.GetTokenSigningService(builder.Configuration))
    .AddSingleton<BlockInternalApiTransform>()
    .AddSingleton<LocalhostRedirectMiddleware>()
    .AddSingleton<AuthenticationCookieMiddleware>()
    .AddScoped<ApiAggregationService>();

var app = builder.Build();

app.ApiAggregationEndpoints();

app.UseForwardedHeaders() // Enable support for proxy headers such as X-Forwarded-For and X-Forwarded-Proto. Should run before other middleware.
    .UseMiddleware<LocalhostRedirectMiddleware>()
    .UseOutputCache()
    .UseMiddleware<AuthenticationCookieMiddleware>();

app.MapScalarApiReference("/openapi", options =>
    {
        options
            .WithOpenApiRoutePattern("/openapi/v1.json")
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
            .WithTitle($"{Settings.Current.Branding.ProductName} API");
    }
);

app.MapReverseProxy();

app.MapFallback((HttpContext context, IOptions<HostnamesOptions> hostnameOptions, PortAllocation ports) =>
    {
        var hostnames = hostnameOptions.Value;
        // Port is only meaningful for local Aspire stacks (non-standard ports). In Azure both the
        // request Host.Port and the sentinel PortAllocation(0) leave us at 0; drop the suffix so the
        // canonical URLs resolve to the standard https port behind ACA ingress.
        var port = context.Request.Host.Port ?? ports.AppGateway;
        var portSuffix = port == 0 ? string.Empty : $":{port}";
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Unknown host",
            Detail = $"The host '{context.Request.Host}' is not recognized. Use one of the canonical URLs.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.5",
            Extensions =
            {
                ["canonicalUrls"] = new[] { $"https://{hostnames.App}{portSuffix}" }
            }
        };

        return Results.Problem(problemDetails);
    }
);

await app.RunAsync();
