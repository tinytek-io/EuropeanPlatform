using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.Tenants;

// Mirrors account's GetTenantDetail contract (see the dashboard queries for the mirror rules).
[PublicAPI]
public sealed record GetTenantDetailQuery(TenantId Id) : IRequest<Result<TenantDetailResponse>>;

[PublicAPI]
public sealed record TenantDetailResponse(
    TenantId Id,
    string Name,
    SubscriptionPlan Plan,
    SubscriptionPlan? ScheduledPlan,
    decimal? ScheduledPriceAmount,
    bool CancelAtPeriodEnd,
    decimal? MonthlyRecurringRevenue,
    string? Currency,
    DateTimeOffset? RenewalDate,
    DateTimeOffset? SubscribedSince,
    bool HasEverSubscribed,
    string? BillingName,
    string? TaxId,
    BillingAddressResponse? BillingAddress,
    PaymentMethodResponse? PaymentMethod,
    decimal? LifetimeValue,
    TenantState State,
    SuspensionReason? SuspensionReason,
    DateTimeOffset? SuspendedAt,
    string? LogoUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ModifiedAt,
    bool HasDriftDetected,
    DateTimeOffset? DriftCheckedAt,
    DriftDiscrepancy[] DriftDiscrepancies,
    string? StripeCustomerUrl,
    AbInclusionPin? AbInclusionPin
);

[PublicAPI]
public sealed record BillingAddressResponse(
    string? Line1,
    string? Line2,
    string? PostalCode,
    string? City,
    string? State,
    string? Country
);

[PublicAPI]
public sealed record PaymentMethodResponse(string Brand, string Last4, int ExpMonth, int ExpYear);

public sealed class GetTenantDetailHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantDetailQuery, Result<TenantDetailResponse>>
{
    public async Task<Result<TenantDetailResponse>> Handle(GetTenantDetailQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantDetailAsync(query.Id, cancellationToken);
    }
}
