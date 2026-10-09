using System.Security.Claims;
using Account;
using Account.Api;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Configuration;
using SharedKernel.Emails;
using SharedKernel.ExecutionContext;
using SharedKernel.OpenApi;
using SharedKernel.SinglePageApp;

// Build-time manifest emitter dispatcher. See FeatureFlagsManifestEmitter.cs.
if (args is ["--emit-feature-flags-manifest", var manifestPath])
{
    FeatureFlagsManifestEmitter.Emit(manifestPath);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Configure storage infrastructure like Database, BlobStorage, Logging, Telemetry, Entity Framework DB Context, etc.
builder
    .AddApiInfrastructure()
    .AddDevelopmentPort()
    .AddAccountInfrastructure();

// Configure dependency injection services like Repositories, MediatR, Pipelines, FluentValidation validators, etc.
builder.Services
    .AddApiServices([Assembly.GetExecutingAssembly(), Configuration.Assembly], ApiDocumentLayout.AccountAndBackOffice)
    .AddAccountServices()
    .AddBackOfficeDevStaticProxy();

// Back-office staff identity: a configured OIDC provider (BackOffice:Oidc) takes precedence;
// the mock identity selector applies only when no provider exists (local development and
// environments that explicitly opt in through MockEasyAuth:Enabled). Registered before Build.
var backOfficeOidcConfigured = builder.Configuration.IsBackOfficeOidcConfigured();
builder.Services.AddBackOfficeOidcIdentity(builder.Configuration);

var app = builder.Build();

// At runtime AppHost surfaces both hostnames. At build time (dotnet-getdocument invokes Program.Main
// to generate OpenAPI), neither is set; fall back to placeholders so Program startup completes and
// the OpenAPI emitter can run. Production parity is enforced by AppHost passing real values.
var appHostname = app.Configuration["Hostnames:App"] ?? "app.unconfigured.invalid";
var backOfficeHostname = app.Services.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

// Per-host bundle URLs. The process-wide PUBLIC_URL/CDN_URL env vars are set by AppHost for the
// user-facing host only, so the back-office host must inject its own to avoid embedding the account
// SPA's bundle URLs into back-office HTML. AppHost sets BACK_OFFICE_PUBLIC_URL/BACK_OFFICE_CDN_URL
// explicitly because back-office now binds a dedicated Kestrel port (not AppGateway's port), so
// host substitution alone would yield the wrong port. Falls back to host-substitution for any
// runtime that hasn't set the explicit vars yet.
var appPublicUrl = Environment.GetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey);
var appCdnUrl = Environment.GetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey);
var backOfficePublicUrl =
    Environment.GetEnvironmentVariable("BACK_OFFICE_PUBLIC_URL")
    ?? (appPublicUrl is null ? null : ReplaceHost(appPublicUrl, appHostname, backOfficeHostname));
var backOfficeCdnUrl = Environment.GetEnvironmentVariable("BACK_OFFICE_CDN_URL") ?? backOfficePublicUrl;

// The /login picker belongs to the MockEasyAuth identity selector: available during local
// development and on staging where MockEasyAuth:Enabled explicitly opts in, blocked everywhere
// else (removed from the auth-gate exemption list and short-circuited to 401 below to reject
// even authenticated requests). Superseded whenever an OIDC provider is configured above.
var mockEasyAuthEnabled = !backOfficeOidcConfigured
                          && (app.Configuration.GetSection("MockEasyAuth:Enabled").Get<bool>()
                              || SharedInfrastructureConfiguration.IsRunningInCloud is false);

// Runtime feature flags injected into the SPA HTML so the React shell can branch on capability without
// a separate config endpoint. Both flags must be wired into BOTH host-scoped SPAs because each one renders
// its own HTML from its own StaticRuntimeEnvironment; omitting them on a host renders the gate as "disabled".
// PUBLIC_MOCK_EASY_AUTH_ENABLED drives the SPA-side availability of the /login route: a production build of the
// back-office renders it only on hosts where the server actually offers the mock identity selector.
var runtimeEnvironment = new Dictionary<string, string>
{
    ["PUBLIC_GOOGLE_OAUTH_ENABLED"] = Environment.GetEnvironmentVariable("PUBLIC_GOOGLE_OAUTH_ENABLED") ?? "false",
    ["PUBLIC_SUBSCRIPTION_ENABLED"] = Environment.GetEnvironmentVariable("PUBLIC_SUBSCRIPTION_ENABLED") ?? "false",
    ["PUBLIC_MOCK_EASY_AUTH_ENABLED"] = mockEasyAuthEnabled ? "true" : "false"
};

string[] backOfficeUnauthenticatedPaths = mockEasyAuthEnabled ? ["/login"] : [];

if (backOfficeOidcConfigured)
{
    app.MapBackOfficeOidcEndpoints();
}

if (!mockEasyAuthEnabled)
{
    app.MapGet("/login", Results.Unauthorized).RequireHost(backOfficeHostname);
}

// Dev-only: forward back-office static-asset and HMR traffic on the back-office Kestrel listener to
// the rsbuild dev server. Registered before UseApiServices so the conditional branch short-circuits
// matching requests before the auth-gated SPA fallback.
app.UseBackOfficeDevStaticProxy(backOfficeHostname);

app.UseApiServices(); // Add common configuration for all APIs like Swagger, HSTS, and DeveloperExceptionPage.

// Back-office Kestrel listens on its own port and bypasses AppGateway, so the avatar/logo routes
// that AppGateway proxies on the user-facing host must be served here directly from blob storage.
app.MapBackOfficeBlobProxy(backOfficeHostname);

// One process serves both SPAs, host-scoped. This matches the deployed topology (a single
// account-api workload behind the cluster gateway, two hostnames routed to it) and local dev
// (dual Kestrel listeners): host-scoped fallback disambiguates because the gateway really delivers
// requests with the right Host header. Email static files are host-scoped the same way: the
// back-office host serves the *.preview.* artifacts (consumed by the back-office-only email
// preview page), the user-facing host does not.
app.UseWhen(
    context => context.Request.Host.Host.Equals(backOfficeHostname, StringComparison.OrdinalIgnoreCase),
    branch => branch.UseEmailStaticFiles("WebApp", true, appPublicUrl)
);
app.UseWhen(
    context => context.Request.Host.Host.Equals(appHostname, StringComparison.OrdinalIgnoreCase),
    branch => branch.UseEmailStaticFiles("WebApp", false)
);

app.UseHostScopedSinglePageAppFallback(
    new HostScopedSinglePageApp(
        appHostname,
        "WebApp",
        context => context.RequestServices.GetRequiredService<IExecutionContext>().UserInfo,
        appPublicUrl,
        appCdnUrl,
        environmentVariables: runtimeEnvironment
    ),
    new HostScopedSinglePageApp(
        backOfficeHostname,
        "BackOffice",
        BuildBackOfficeUserInfo,
        backOfficePublicUrl,
        backOfficeCdnUrl,
        BackOfficeIdentityDefaults.PolicyName,
        runtimeEnvironment,
        backOfficeUnauthenticatedPaths
    )
);

await app.RunAsync();
return;

static string ReplaceHost(string url, string oldHost, string newHost)
{
    var uri = new Uri(url);
    var builder = new UriBuilder(uri) { Host = uri.Host.Equals(oldHost, StringComparison.OrdinalIgnoreCase) ? newHost : uri.Host };
    return builder.Uri.ToString().TrimEnd('/');
}

static UserInfo BuildBackOfficeUserInfo(HttpContext context)
{
    var principal = context.User;
    if (principal.Identity?.IsAuthenticated != true)
    {
        return UserInfo.System;
    }

    var displayName = principal.FindFirstValue(ClaimTypes.Name);
    var groups = string.Join(',', principal.FindAll(BackOfficeIdentityDefaults.GroupsClaimType).Select(c => c.Value));

    return new UserInfo
    {
        IsAuthenticated = true,
        Locale = "en-US",
        FirstName = displayName,
        Role = string.IsNullOrEmpty(groups) ? null : groups
    };
}
