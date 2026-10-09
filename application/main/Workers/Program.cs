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

// Apply migrations to the database only when running locally
// Apply migrations on startup; EF Core's migration lock serializes concurrent replicas.
var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService<MainDbContext>>();
migrationService.ApplyMigrations();

var dataMigrationRunner = scope.ServiceProvider.GetRequiredService<DataMigrationRunner<MainDbContext>>();
await dataMigrationRunner.RunMigrationsAsync(lifetime.ApplicationStopping);

await host.RunAsync();
