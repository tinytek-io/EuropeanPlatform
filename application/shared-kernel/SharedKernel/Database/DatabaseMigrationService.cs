using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace SharedKernel.Database;

public partial class DatabaseMigrationService<TContext>(TContext dbContext, ILogger<DatabaseMigrationService<TContext>> logger)
    where TContext : DbContext
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleNamePattern { get; }

    public void ApplyMigrations()
    {
        logger.LogInformation("Applying database migrations. Version: {Version}",
            Assembly.GetExecutingAssembly().GetName().Version
        );

        dbContext.Database.CreateExecutionStrategy().Execute(() => { dbContext.Database.Migrate(); });
    }

    /// <summary>
    ///     Grants the runtime application user read and write access to every table and sequence the
    ///     migrator owns, now and in future migrations. The migrator creates the tables, so only it can
    ///     grant on them: a database privilege set before the first migration covers no tables at all.
    /// </summary>
    public void GrantApplicationUserAccess(string applicationUser)
    {
        // Role names cannot be SQL parameters; the pattern admits only plain lowercase identifiers.
        if (!RoleNamePattern.IsMatch(applicationUser))
        {
            throw new InvalidOperationException($"The application user '{applicationUser}' is not a plain lowercase PostgreSQL role name.");
        }

        logger.LogInformation("Granting the application user {ApplicationUser} access to the migrated tables", applicationUser);

        var role = $"\"{applicationUser}\"";
        var grantStatements = $"""
                               GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
                               GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {role};
                               ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role};
                               ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {role};
                               """;
        dbContext.Database.CreateExecutionStrategy().Execute(() => dbContext.Database.ExecuteSqlRaw(grantStatements));
    }
}
