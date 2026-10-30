using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.Users;

// Mirrors account's GetBackOfficeUserDetail contract (see the dashboard queries for the mirror
// rules). AbInclusionPin is the SharedKernel type account serializes on the wire.
[PublicAPI]
public sealed record GetBackOfficeUserDetailQuery(UserId Id) : IRequest<Result<BackOfficeUserDetailResponse>>;

[PublicAPI]
public sealed record BackOfficeUserDetailResponse(
    UserId Id,
    TenantId TenantId,
    string TenantName,
    string Email,
    string? FirstName,
    string? LastName,
    string? Title,
    UserRole Role,
    bool EmailConfirmed,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset? LastSeenAt,
    string? AvatarUrl,
    BackOfficeUserTenantMembership[] TenantMemberships,
    AbInclusionPin? AbInclusionPin
);

// A "tenant membership" is another user record sharing the same email in a different tenant. Mirrors
// account's semantics (see GetBackOfficeUserDetail there).
[PublicAPI]
public sealed record BackOfficeUserTenantMembership(
    UserId UserId,
    TenantId TenantId,
    string TenantName,
    string? TenantLogoUrl,
    SubscriptionPlan Plan,
    PlannedSubscriptionChange? PlannedChange,
    bool HasEverSubscribed,
    decimal? MonthlyRecurringRevenue,
    decimal? ScheduledPriceAmount,
    string? Currency,
    DateTimeOffset? RenewalDate,
    string? Country,
    UserRole Role,
    bool EmailConfirmed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt
);

public sealed class GetBackOfficeUserDetailHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBackOfficeUserDetailQuery, Result<BackOfficeUserDetailResponse>>
{
    public async Task<Result<BackOfficeUserDetailResponse>> Handle(GetBackOfficeUserDetailQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBackOfficeUserDetailAsync(query.Id, cancellationToken);
    }
}
