using BackOffice.Database;
using BackOffice.Features.SecurityChecks;
using BackOffice.StaffIdentity;
using BackOffice.Workers;
using SharedKernel.Configuration;
using SharedKernel.Database;

// Worker service is using WebApplication.CreateBuilder instead of Host.CreateDefaultBuilder to allow scaling to zero
var builder = WebApplication.CreateBuilder(args);

// The back-office database (the staff identity state), telemetry, and the Secret Manager
// configuration provider (a no-op locally).
builder
    .AddDevelopmentPort()
    .AddSharedInfrastructure<BackOfficeDbContext>("back-office-database");

builder.Services.AddWorkerServices();
builder.Services.AddTransient<DatabaseMigrationService<BackOfficeDbContext>>();
builder.Services.AddTransient<DataMigrationRunner<BackOfficeDbContext>>();

// The staff OIDC provider's client list (committed in the staff identity file) is synced into its store here: the
// workers run as one replica, so there is a single writer, and they hold the client secrets the API does not need.
builder.Services.AddStaffIdentityClientSync();

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

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

using (var scope = host.Services.CreateScope())
{
    // Schema migrations at startup are for local development only: in deployed environments the
    // dedicated migrator Job owns the schema (the runtime user is a readwrite role that cannot
    // create schema objects — least privilege).
    if (SharedInfrastructureConfiguration.IsRunningInCloud is false)
    {
        scope.ServiceProvider.GetRequiredService<DatabaseMigrationService<BackOfficeDbContext>>().ApplyMigrations();
    }

    // Data migrations run here as an idempotent no-op after the migrator Job has applied them, and
    // as the resilient fallback if they were not: tracked in __data_migrations_history.
    await scope.ServiceProvider.GetRequiredService<DataMigrationRunner<BackOfficeDbContext>>().RunMigrationsAsync(lifetime.ApplicationStopping);

    // Converge the OIDC clients to the committed list on every start. Must succeed before the worker runs: a
    // failure exits the process so the orchestrator notices, rather than serving with a stale client list.
    await scope.ServiceProvider.GetRequiredService<StaffIdentityClientReconciler>().ReconcileAsync(lifetime.ApplicationStopping);
}

await host.RunAsync();
