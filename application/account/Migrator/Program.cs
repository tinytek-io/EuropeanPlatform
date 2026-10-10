using Account.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Database;

// The dedicated database migrator for the account self-contained system: applies schema and data
// migrations with the structure-owner role (the runtime application user cannot change structure),
// then exits — built to run as the deploy's pre-upgrade Job, never as a long-lived workload.
var builder = Host.CreateApplicationBuilder(args);

builder.AddDatabaseMigrator<AccountDbContext>("account-database");

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService<AccountDbContext>>();
migrationService.ApplyMigrations();

var dataMigrationRunner = scope.ServiceProvider.GetRequiredService<DataMigrationRunner<AccountDbContext>>();
await dataMigrationRunner.RunMigrationsAsync(CancellationToken.None);
