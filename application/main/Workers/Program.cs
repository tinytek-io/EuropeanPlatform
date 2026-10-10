using Main;
using Main.Database;
using SharedKernel.Configuration;
using SharedKernel.Database;

// Worker service is using WebApplication.CreateBuilder instead of Host.CreateDefaultBuilder to allow scaling to zero
var builder = WebApplication.CreateBuilder(args);

// Configure storage infrastructure like Database, BlobStorage, Logging, Telemetry, Entity Framework DB Context, etc.
builder
    .AddDevelopmentPort()
    .AddMainInfrastructure();

// Configure dependency injection services like Repositories, MediatR, Pipelines, FluentValidation validators, etc.
builder.Services
    .AddWorkerServices()
    .AddMainServices();

builder.Services.AddTransient<DatabaseMigrationService<MainDbContext>>();
builder.Services.AddTransient<DataMigrationRunner<MainDbContext>>();

var host = builder.Build();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

using var scope = host.Services.CreateScope();

// Schema migrations at startup only when the migrator connection string is absent (local
// development). In deployed environments the pre-upgrade migration Job owns the schema, and the
// runtime user is a readwrite role that cannot create schema objects (least privilege).
var configuration = host.Services.GetRequiredService<IConfiguration>();
if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("main-database-migrator")))
{
    var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService<MainDbContext>>();
    migrationService.ApplyMigrations();
}

var dataMigrationRunner = scope.ServiceProvider.GetRequiredService<DataMigrationRunner<MainDbContext>>();
await dataMigrationRunner.RunMigrationsAsync(lifetime.ApplicationStopping);

// The pre-deploy migration Job runs this image with MIGRATE_ONLY=true: schema + data migrations
// complete before any new pods roll out (helm pre-upgrade hook). Exit here so the Job finishes
// successfully instead of starting the hosted services.
if (Environment.GetEnvironmentVariable("MIGRATE_ONLY") == "true")
{
    return;
}

await host.RunAsync();
