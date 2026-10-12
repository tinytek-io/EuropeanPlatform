using BackOffice;
using BackOffice.Api;
using BackOffice.ExecutionContext;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Configuration;
using SharedKernel.SinglePageApp;

var builder = WebApplication.CreateBuilder(args);

// The back-office owns no database: it is a BFF over the feature systems' internal APIs.
// AddBackOfficeInfrastructure wires telemetry, the Secret Manager configuration provider (a no-op
// locally), the internal-CA trust for account's in-cluster TLS, and the account internal API
// client. AddDevelopmentPort chains on WebApplicationBuilder (KESTREL_PORT dev listener), so the
// calls stay unchained.
builder.AddBackOfficeInfrastructure();
builder.AddApiInfrastructure(); // Kestrel hardening and the development-only CORS policy.
builder.AddDevelopmentPort();
builder.AddInternalTls();

builder.Services
    .AddApiServices([Assembly.GetExecutingAssembly(), Configuration.Assembly])
    .AddBackOfficeServices()
    // Dev-only: forward static-asset and HMR traffic to the rsbuild dev server. Registered before
    // UseApiServices so the conditional branch short-circuits matching requests before the
    // auth-gated SPA fallback.
    .AddBackOfficeDevStaticProxy();

// Back-office staff identity: a configured OIDC provider (BackOffice:Oidc) takes precedence; the
// mock identity selector applies only when no provider exists (local development and environments
// that explicitly opt in through MockEasyAuth:Enabled). Registered before Build.
var backOfficeOidcConfigured = builder.Configuration.IsBackOfficeOidcConfigured();
builder.Services.AddBackOfficeOidcIdentity(builder.Configuration);

var app = builder.Build();

var backOfficeHostname = app.Services.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

// The SPA's bundle URLs come from the public URL (the back-office's own origin — the dedicated
// Kestrel port in development, the cluster gateway hostname in deployed environments).
var backOfficePublicUrl =
    Environment.GetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey)
    ?? $"https://{backOfficeHostname}";
var backOfficeCdnUrl = Environment.GetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey) ?? backOfficePublicUrl;

// The /login picker belongs to the MockEasyAuth identity selector: available during local
// development and on environments where MockEasyAuth:Enabled explicitly opts in, blocked everywhere
// else. Superseded whenever an OIDC provider is configured above.
var mockEasyAuthEnabled = !backOfficeOidcConfigured
                          && (app.Configuration.GetSection("MockEasyAuth:Enabled").Get<bool>()
                              || SharedInfrastructureConfiguration.IsRunningInCloud is false);

// Runtime feature flags injected into the SPA HTML so the React shell can branch on capability
// without a preview API call (same keys account's folded back-office hosting injected).
var runtimeEnvironment = new Dictionary<string, string>
{
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
    // No provider and no mock: reject the login picker instead of pretending it is configured.
    app.MapGet("/login", Results.Unauthorized).RequireHost(backOfficeHostname);
}

// Dev-only; short-circuits before the auth-gated SPA fallback (see AddBackOfficeDevStaticProxy).
app.UseBackOfficeDevStaticProxy(backOfficeHostname);

app.UseApiServices(); // Add common configuration for all APIs like Swagger, HSTS, and DeveloperExceptionPage.

// The SPA is served on the back-office host, behind the staff authorization policy (the fallback
// mints the antiforgery token and runtime env into the HTML).
app.UseHostScopedSinglePageAppFallback(
    new HostScopedSinglePageApp(
        backOfficeHostname,
        "WebApp",
        BuildStaffUserInfo,
        backOfficePublicUrl,
        backOfficeCdnUrl,
        BackOfficeIdentityDefaults.PolicyName,
        runtimeEnvironment,
        backOfficeUnauthenticatedPaths
    )
);

await app.RunAsync();
return;

// The staff SPA's user info: anonymous visits get UserInfo.System; the authenticated principal is
// surfaced through the staff execution context (the same source handlers read), with groups joined
// into the Role field the SPA renders.
static UserInfo BuildStaffUserInfo(HttpContext context)
{
    var staffExecutionContext = context.RequestServices.GetRequiredService<IStaffExecutionContext>();
    if (!staffExecutionContext.IsAuthenticated)
    {
        return UserInfo.System;
    }

    return new UserInfo
    {
        IsAuthenticated = true,
        Locale = "en-US",
        FirstName = staffExecutionContext.DisplayName,
        Role = staffExecutionContext.Groups.Length == 0 ? null : string.Join(',', staffExecutionContext.Groups)
    };
}
