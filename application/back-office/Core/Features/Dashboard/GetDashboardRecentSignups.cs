using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardRecentSignups contract (see GetDashboardTrends for the mirror
// rules). TenantId/UserId are the SharedKernel strongly-typed IDs account serializes on the wire.
[PublicAPI]
public sealed record GetDashboardRecentSignupsQuery(int Limit = 6)
    : IRequest<Result<BackOfficeDashboardRecentSignupsResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardRecentSignupsResponse(BackOfficeDashboardRecentSignup[] Signups);

[PublicAPI]
public sealed record BackOfficeDashboardRecentSignup(
    TenantId TenantId,
    string Name,
    string? TenantLogoUrl,
    DateTimeOffset CreatedAt,
    BackOfficeDashboardRecentSignupOwner? Owner
);

[PublicAPI]
public sealed record BackOfficeDashboardRecentSignupOwner(UserId UserId, string? FirstName, string? LastName, string Email);

public sealed class GetDashboardRecentSignupsQueryValidator : AbstractValidator<GetDashboardRecentSignupsQuery>
{
    public GetDashboardRecentSignupsQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).WithMessage("Limit must be between 1 and 50.");
    }
}

public sealed class GetDashboardRecentSignupsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardRecentSignupsQuery, Result<BackOfficeDashboardRecentSignupsResponse>>
{
    public async Task<Result<BackOfficeDashboardRecentSignupsResponse>> Handle(GetDashboardRecentSignupsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardRecentSignupsAsync(query, cancellationToken);
    }
}
