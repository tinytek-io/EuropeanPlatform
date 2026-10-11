using System.Reflection;
using BackOffice;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Configuration;

var builder = WebApplication.CreateBuilder(args);

// The back-office owns no database: it is a BFF over the feature systems' internal APIs.
// AddBackOfficeInfrastructure wires telemetry and the Secret Manager configuration provider
// (a no-op locally); AddInternalTls serves HTTPS only in deployed environments.
// AddDevelopmentPort chains on WebApplicationBuilder (KESTREL_PORT dev listener), so the
// calls stay unchained: AddBackOfficeInfrastructure/AddInternalTls are IHostApplicationBuilder
// extensions.
builder.AddBackOfficeInfrastructure();
builder.AddApiInfrastructure(); // Kestrel hardening and the development-only CORS policy.
builder.AddDevelopmentPort();
builder.AddInternalTls();

// The shared API stack registers the back-office identity scheme, its policy, the mock EasyAuth
// selector (development and explicitly opted-in environments), antiforgery, and the health
// endpoints. The single OpenAPI document serves the SPA contract at cutover.
builder.Services
    .AddApiServices([Assembly.GetExecutingAssembly(), Configuration.Assembly])
    .AddBackOfficeServices();

var app = builder.Build();

var backOfficeHostname = app.Services.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

// Back-office staff identity: a configured OIDC provider (BackOffice:Oidc) takes precedence; the
// mock identity selector applies only when no provider exists (local development and environments
// that explicitly opt in through MockEasyAuth:Enabled).
var backOfficeOidcConfigured = builder.Configuration.IsBackOfficeOidcConfigured();
if (backOfficeOidcConfigured)
{
    app.MapBackOfficeOidcEndpoints();
}

if (!backOfficeOidcConfigured && app.Configuration.GetSection("MockEasyAuth:Enabled").Get<bool>() is false
                              && SharedInfrastructureConfiguration.IsRunningInCloud)
{
    // No provider and no mock: reject the login picker instead of pretending it is configured.
    app.MapGet("/login", Results.Unauthorized).RequireHost(backOfficeHostname);
}

app.UseApiServices(); // Add common configuration for all APIs like Swagger, HSTS, and DeveloperExceptionPage.

await app.RunAsync();
