using BackOffice.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Database;

// The dedicated database migrator for the back-office self-contained system: applies schema and data
// migrations with the structure-owner role (the runtime application user cannot change structure),
// then exits — built to run as the deploy's pre-upgrade Job, never as a long-lived workload.
var builder = Host.CreateApplicationBuilder(args);

builder.AddDatabaseMigrator<BackOfficeDbContext>("back-office-database");

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService<BackOfficeDbContext>>();
migrationService.ApplyMigrations();

// Deployed environments run the application as a separate readwrite user; local development
// migrates and runs as the same user, so there is nothing to grant.
var applicationUser = host.Services.GetRequiredService<IConfiguration>()["Database:ApplicationUser"];
if (applicationUser is not null)
{
    migrationService.GrantApplicationUserAccess(applicationUser);
}

var dataMigrationRunner = scope.ServiceProvider.GetRequiredService<DataMigrationRunner<BackOfficeDbContext>>();
await dataMigrationRunner.RunMigrationsAsync(CancellationToken.None);
