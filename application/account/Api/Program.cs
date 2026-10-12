using Account;
using Account.Api;
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
    .AddInternalTls()
    .AddAccountInfrastructure();

// Configure dependency injection services like Repositories, MediatR, Pipelines, FluentValidation validators, etc.
// The layout stays AccountAndBackOffice: it binds BackOfficeHostOptions (the internal API's
// DelegationAdmin gate reads BackOffice:AdminsGroupId) and keeps the internal back-office routes
// out of the account SPA's document (they carry the back-office group name; the staff-facing
// routes live in the back-office system).
builder.Services
    .AddApiServices([Assembly.GetExecutingAssembly(), Configuration.Assembly], ApiDocumentLayout.AccountAndBackOffice)
    .AddAccountServices();

var app = builder.Build();

// At runtime AppHost surfaces the hostname. At build time (dotnet-getdocument invokes Program.Main
// to generate OpenAPI), it is not set; fall back to a placeholder so Program startup completes and
// the OpenAPI emitter can run. Production parity is enforced by AppHost passing real values.
var appHostname = app.Configuration["Hostnames:App"] ?? "app.unconfigured.invalid";

// Email static files preview page only on the user-facing host.
app.UseWhen(
    context => context.Request.Host.Host.Equals(appHostname, StringComparison.OrdinalIgnoreCase),
    branch => branch.UseEmailStaticFiles("WebApp", false)
);

var appPublicUrl = Environment.GetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey);
var appCdnUrl = Environment.GetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey);

// Runtime feature flags injected into the SPA HTML so the React shell can branch on capability
// without a preview API call.
var runtimeEnvironment = new Dictionary<string, string>
{
    ["PUBLIC_GOOGLE_OAUTH_ENABLED"] = Environment.GetEnvironmentVariable("PUBLIC_GOOGLE_OAUTH_ENABLED") ?? "false",
    ["PUBLIC_SUBSCRIPTION_ENABLED"] = Environment.GetEnvironmentVariable("PUBLIC_SUBSCRIPTION_ENABLED") ?? "false"
};

app.UseApiServices(); // Add common configuration for all APIs like Swagger, HSTS, and DeveloperExceptionPage.

app.UseHostScopedSinglePageAppFallback(
    new HostScopedSinglePageApp(
        appHostname,
        "WebApp",
        context => context.RequestServices.GetRequiredService<IExecutionContext>().UserInfo,
        appPublicUrl,
        appCdnUrl,
        environmentVariables: runtimeEnvironment
    )
);

await app.RunAsync();
