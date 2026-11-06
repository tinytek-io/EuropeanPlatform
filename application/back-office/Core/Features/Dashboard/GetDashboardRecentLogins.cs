using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardRecentLogins contract (see GetDashboardTrends for the mirror
// rules). LoginMethod comes from the shared contract mirrors.
[PublicAPI]
public sealed record GetDashboardRecentLoginsQuery(int Limit = 6)
    : IRequest<Result<BackOfficeDashboardRecentLoginsResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardRecentLoginsResponse(BackOfficeDashboardRecentLogin[] Logins);

[PublicAPI]
public sealed record BackOfficeDashboardRecentLogin(
    UserId? UserId,
    string Email,
    string? FirstName,
    string? LastName,
    string? AvatarUrl,
    TenantId? TenantId,
    string? TenantName,
    string? TenantLogoUrl,
    LoginMethod Method,
    DateTimeOffset OccurredAt
);

public sealed class GetDashboardRecentLoginsQueryValidator : AbstractValidator<GetDashboardRecentLoginsQuery>
{
    public GetDashboardRecentLoginsQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).WithMessage("Limit must be between 1 and 50.");
    }
}

public sealed class GetDashboardRecentLoginsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardRecentLoginsQuery, Result<BackOfficeDashboardRecentLoginsResponse>>
{
    public async Task<Result<BackOfficeDashboardRecentLoginsResponse>> Handle(GetDashboardRecentLoginsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardRecentLoginsAsync(query, cancellationToken);
    }
}
