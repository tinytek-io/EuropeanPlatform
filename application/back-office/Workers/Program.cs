using SharedKernel.Configuration;

// Worker service is using WebApplication.CreateBuilder instead of Host.CreateDefaultBuilder to allow scaling to zero
var builder = WebApplication.CreateBuilder(args);

// The back-office owns no database, so its background work needs only telemetry and the Secret
// Manager configuration provider (a no-op locally), both wired by AddSharedTelemetry.
builder
    .AddDevelopmentPort()
    .AddSharedTelemetry();

builder.Services.AddWorkerServices();

var host = builder.Build();

await host.RunAsync();
