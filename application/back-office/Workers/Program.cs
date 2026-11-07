using BackOffice.Features.SecurityChecks;
using BackOffice.Workers;
using SharedKernel.Configuration;

// Worker service is using WebApplication.CreateBuilder instead of Host.CreateDefaultBuilder to allow scaling to zero
var builder = WebApplication.CreateBuilder(args);

// The back-office owns no database, so its background work needs only telemetry and the Secret
// Manager configuration provider (a no-op locally), both wired by AddSharedTelemetry.
builder
    .AddDevelopmentPort()
    .AddSharedTelemetry();

builder.Services.AddWorkerServices();

// The security checks read the deployed environment's Scaleway resources, so they run only in the
// cloud; locally there is no edge or Load Balancer to check.
if (SharedInfrastructureConfiguration.IsRunningInCloud)
{
    builder.Services.AddOptions<SecurityChecksOptions>()
        .BindConfiguration(SecurityChecksOptions.SectionName)
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddHttpClient<ScalewaySecurityInventoryClient>(client =>
        {
            // The SDK builds absolute API paths on top of the API host.
            client.BaseAddress = new Uri("https://api.scaleway.com/");
            client.DefaultRequestHeaders.Add("X-Auth-Token", Environment.GetEnvironmentVariable("SCW_SECRET_KEY"));
            client.Timeout = TimeSpan.FromSeconds(30);
        }
    );

    builder.Services.AddHostedService<SecurityCheckWorker>();
}

var host = builder.Build();

await host.RunAsync();
