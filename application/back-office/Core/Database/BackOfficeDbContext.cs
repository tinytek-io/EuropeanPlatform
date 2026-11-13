using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using SharedKernel.EntityFramework;
using SharedKernel.ExecutionContext;

namespace BackOffice.Database;

/// <summary>
///     The back-office's own database: the staff identity state that cannot live in git — the OIDC provider's
///     applications (synced from the committed client list), authorizations and tokens, and the short-lived sign-in
///     codes. Staff and OIDC clients themselves are committed configuration, never rows edited here.
/// </summary>
public sealed class BackOfficeDbContext(DbContextOptions<BackOfficeDbContext> options, IExecutionContext executionContext, TimeProvider timeProvider)
    : SharedKernelDbContext<BackOfficeDbContext>(options, executionContext, timeProvider)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Registered before base, so its conventions (snake_case columns, string enums) cover OpenIddict's entities.
        modelBuilder.UseOpenIddict();

        base.OnModelCreating(modelBuilder);

        // OpenIddict names its tables explicitly in PascalCase, which the snake_case convention leaves alone.
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("openiddict_applications");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("openiddict_authorizations");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("openiddict_scopes");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("openiddict_tokens");
    }
}
