using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardRecentStripeEvents contract (see GetDashboardTrends for the mirror
// rules). BillingEventId and BillingEventType come from the shared contract mirrors.
[PublicAPI]
public sealed record GetDashboardRecentStripeEventsQuery(int Limit = 6)
    : IRequest<Result<BackOfficeDashboardRecentStripeEventsResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardRecentStripeEventsResponse(BackOfficeDashboardStripeEvent[] Events);

[PublicAPI]
public sealed record BackOfficeDashboardStripeEvent(
    BillingEventId Id,
    TenantId TenantId,
    string TenantName,
    string? TenantLogoUrl,
    BillingEventType Type,
    SubscriptionPlan? FromPlan,
    SubscriptionPlan? ToPlan,
    decimal? AmountDelta,
    string? Currency,
    DateTimeOffset OccurredAt
);

public sealed class GetDashboardRecentStripeEventsQueryValidator : AbstractValidator<GetDashboardRecentStripeEventsQuery>
{
    public GetDashboardRecentStripeEventsQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).WithMessage("Limit must be between 1 and 50.");
    }
}

public sealed class GetDashboardRecentStripeEventsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardRecentStripeEventsQuery, Result<BackOfficeDashboardRecentStripeEventsResponse>>
{
    public async Task<Result<BackOfficeDashboardRecentStripeEventsResponse>> Handle(GetDashboardRecentStripeEventsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardRecentStripeEventsAsync(query, cancellationToken);
    }
}
