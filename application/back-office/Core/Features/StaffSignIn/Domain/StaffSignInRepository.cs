using BackOffice.Database;
using Microsoft.EntityFrameworkCore;
using SharedKernel.Domain;
using SharedKernel.Persistence;

namespace BackOffice.Features.StaffSignIn.Domain;

public interface IStaffSignInRepository : IAppendRepository<StaffSignIn, StaffSignInId>
{
    void Update(StaffSignIn aggregate);

    /// <summary>
    ///     The sign-ins started for this email at or after <paramref name="since" />, which caps how many codes can be
    ///     requested in a window. SQLite cannot translate DateTimeOffset comparisons, so the time filter runs in memory;
    ///     the email filter keeps the materialized set small.
    /// </summary>
    Task<StaffSignIn[]> GetStartedSinceAsync(string email, DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed class StaffSignInRepository(BackOfficeDbContext backOfficeDbContext)
    : RepositoryBase<StaffSignIn, StaffSignInId>(backOfficeDbContext), IStaffSignInRepository
{
    public async Task<StaffSignIn[]> GetStartedSinceAsync(string email, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var signIns = await DbSet.Where(signIn => signIn.Email == email.ToLowerInvariant()).ToArrayAsync(cancellationToken);
        return signIns.Where(signIn => signIn.CreatedAt >= since).ToArray();
    }
}
