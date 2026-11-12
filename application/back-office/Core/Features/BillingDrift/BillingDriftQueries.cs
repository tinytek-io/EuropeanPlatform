using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;

namespace BackOffice.Features.BillingDrift;

// Mirrors account's billing drift summary contracts (see the dashboard queries for the mirror
// rules). These three summaries feed the dashboard's drift banner.
[PublicAPI]
public sealed record GetBillingDriftSummaryQuery : IRequest<Result<BillingDriftSummaryResponse>>;

[PublicAPI]
public sealed record BillingDriftSummaryResponse(int SubscriptionsWithDriftCount);

public sealed class GetBillingDriftSummaryHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBillingDriftSummaryQuery, Result<BillingDriftSummaryResponse>>
{
    public async Task<Result<BillingDriftSummaryResponse>> Handle(GetBillingDriftSummaryQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBillingDriftSummaryAsync(cancellationToken);
    }
}

[PublicAPI]
public sealed record GetUnsyncedSubscriptionsSummaryQuery : IRequest<Result<UnsyncedSubscriptionsSummaryResponse>>;

[PublicAPI]
public sealed record UnsyncedSubscriptionsSummaryResponse(int UnsyncedSubscriptionsCount);

public sealed class GetUnsyncedSubscriptionsSummaryHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetUnsyncedSubscriptionsSummaryQuery, Result<UnsyncedSubscriptionsSummaryResponse>>
{
    public async Task<Result<UnsyncedSubscriptionsSummaryResponse>> Handle(GetUnsyncedSubscriptionsSummaryQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetUnsyncedSubscriptionsSummaryAsync(cancellationToken);
    }
}

[PublicAPI]
public sealed record GetDashboardMrrConsistencySummaryQuery : IRequest<Result<DashboardMrrConsistencySummaryResponse>>;

[PublicAPI]
public sealed record DashboardMrrConsistencySummaryResponse(decimal KpiMonthlyRecurringRevenue, decimal TrendLatestMonthlyRecurringRevenue, string? Currency);

public sealed class GetDashboardMrrConsistencySummaryHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardMrrConsistencySummaryQuery, Result<DashboardMrrConsistencySummaryResponse>>
{
    public async Task<Result<DashboardMrrConsistencySummaryResponse>> Handle(GetDashboardMrrConsistencySummaryQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardMrrConsistencySummaryAsync(cancellationToken);
    }
}
