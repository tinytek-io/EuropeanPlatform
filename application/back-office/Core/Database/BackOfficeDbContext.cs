using Microsoft.EntityFrameworkCore;
using SharedKernel.EntityFramework;
using SharedKernel.ExecutionContext;

namespace BackOffice.Database;

/// <summary>
///     The back-office's own database: the staff identity state that cannot live in git — the OIDC provider's
///     authorizations and tokens and the short-lived sign-in codes. Staff and OIDC clients themselves are
///     committed configuration, never rows edited here.
/// </summary>
public sealed class BackOfficeDbContext(DbContextOptions<BackOfficeDbContext> options, IExecutionContext executionContext, TimeProvider timeProvider)
    : SharedKernelDbContext<BackOfficeDbContext>(options, executionContext, timeProvider);
