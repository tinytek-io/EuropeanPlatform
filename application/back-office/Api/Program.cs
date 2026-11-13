using BackOffice;
using BackOffice.Api;
using BackOffice.Authentication;
using BackOffice.Authentication.MockSignIn;
using BackOffice.ExecutionContext;
using BackOffice.StaffIdentity;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication;
using SharedKernel.Configuration;
using SharedKernel.SinglePageApp;

var builder = WebApplication.CreateBuilder(args);

// A BFF over the feature systems' internal APIs whose own database holds only the staff identity
// state. AddBackOfficeInfrastructure wires the database, telemetry, the Secret Manager configuration provider (a no-op
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

// Back-office staff identity: with a staff identity file (StaffIdentity:File) the back-office signs
// staff in itself with an emailed one-time code; the mock identity selector applies only without one
// (local development and environments that explicitly opt in through MockStaffIdentity:Enabled).
builder.Services.AddStaffSession();

// The staff OIDC provider other tools sign staff in through (its endpoints are mapped below, with the sign-in).
builder.Services.AddStaffIdentityProvider();

var app = builder.Build();

// Decided on the built configuration (every source in place), and the file is loaded and validated
// now, so a missing or invalid staff identity file fails the start rather than the first sign-in.
var staffSignInEnabled = app.Configuration.IsStaffSignInEnabled();
app.Services.GetRequiredService<StaffDirectory>();

var backOfficeHostname = app.Services.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

// The SPA's bundle URLs come from the public URL (the back-office origin the browser sees through the
// app gateway: its port on localhost in development, the public hostname in deployed environments).
var backOfficePublicUrl =
    Environment.GetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey)
    ?? $"https://{backOfficeHostname}";
var backOfficeCdnUrl = Environment.GetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey) ?? backOfficePublicUrl;

// The /login picker belongs to the mock identity picker; the same decision registers its
// middleware below (see MockStaffIdentityMiddlewareExtensions.IsMockStaffIdentityEnabled).
var mockStaffIdentityEnabled = MockStaffIdentityMiddlewareExtensions.IsMockStaffIdentityEnabled(app.Configuration, app.Environment);

// Runtime feature flags injected into the SPA HTML so the React shell can branch on capability
// without a preview API call (same keys account's folded back-office hosting injected).
var runtimeEnvironment = new Dictionary<string, string>
{
    ["PUBLIC_SUBSCRIPTION_ENABLED"] = Environment.GetEnvironmentVariable("PUBLIC_SUBSCRIPTION_ENABLED") ?? "false",
    ["PUBLIC_MOCK_STAFF_IDENTITY_ENABLED"] = mockStaffIdentityEnabled ? "true" : "false",
    ["PUBLIC_STAFF_SIGN_IN_ENABLED"] = staffSignInEnabled ? "true" : "false"
};

// The sign-in page (the real one or the mock picker) is the one SPA route served without a session.
string[] backOfficeUnauthenticatedPaths = mockStaffIdentityEnabled || staffSignInEnabled ? ["/login"] : [];

if (staffSignInEnabled)
{
    app.MapStaffSessionEndpoints(backOfficeHostname);
    app.MapStaffIdentityProviderEndpoints(backOfficeHostname);
}

if (!mockStaffIdentityEnabled && !staffSignInEnabled)
{
    // Neither sign-in is configured: reject the sign-in page instead of pretending one exists.
    app.MapGet("/login", Results.Unauthorized).RequireHost(backOfficeHostname);
}

// Dev-only; short-circuits before the auth-gated SPA fallback (see AddBackOfficeDevStaticProxy).
app.UseBackOfficeDevStaticProxy(backOfficeHostname);

// The mock identity picker resolves the staff identity from its dev cookie; must run before
// authentication.
app.UseMockStaffIdentityWhenEnabled();

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
