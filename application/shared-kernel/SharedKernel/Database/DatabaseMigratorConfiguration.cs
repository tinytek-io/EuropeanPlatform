using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Configuration;

namespace SharedKernel.Database;

/// <summary>
///     Wires a dedicated database migrator host: the DbContext bound to the connection name from its
///     own configuration (Secret Manager path), plus the schema and data migration services. The
///     migrator runs against the structure-owner role — the runtime application user cannot change
///     database structure (least privilege). Used by the per-SCS migrator console apps that the
///     deploy runs as pre-upgrade Jobs.
/// </summary>
public static class DatabaseMigratorConfiguration
{
    public static IHostApplicationBuilder AddDatabaseMigrator<TContext>(this IHostApplicationBuilder builder, string connectionName)
        where TContext : DbContext
    {
        // The migrator reads its connection string from its own Secret Manager path — the same
        // logical name as the application's, but the path resolves it as the migrator role.
        builder.Configuration.AddScalewaySecretManager(TimeSpan.FromMinutes(5));

        // Identical EF options to the application (ConfigureDatabaseContext): migrations apply the
        // same history table and naming conventions.
        builder.Services.AddDbContext<TContext>((serviceProvider, options) =>
            {
                var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(connectionName)
                                       ?? throw new InvalidOperationException(
                                           $"The connection string '{connectionName}' is required but was not configured."
                                       );

                options.UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations_history"))
                    .UseSnakeCaseNamingConvention();
            }
        );

        builder.Services.AddTransient<DatabaseMigrationService<TContext>>();
        builder.Services.AddTransient<DataMigrationRunner<TContext>>();
        builder.Services.AddSingleton(TimeProvider.System);

        // The DbContext constructors take an execution context; a migrator has no request scope —
        // the background-worker context (system user, no tenant) covers it.
        builder.Services.AddWorkerServices();

        return builder;
    }
}
