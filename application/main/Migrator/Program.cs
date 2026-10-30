using Main.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Database;

// The dedicated database migrator for the main self-contained system: applies schema and data
// migrations with the structure-owner role (the runtime application user cannot change structure),
// then exits — built to run as the deploy's pre-upgrade Job, never as a long-lived workload.
var builder = Host.CreateApplicationBuilder(args);

builder.AddDatabaseMigrator<MainDbContext>("main-database");

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService<MainDbContext>>();
migrationService.ApplyMigrations();

var dataMigrationRunner = scope.ServiceProvider.GetRequiredService<DataMigrationRunner<MainDbContext>>();
await dataMigrationRunner.RunMigrationsAsync(CancellationToken.None);
