using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using MediatR;
using SharedKernel.Cqrs;

namespace BackOffice.Features.Dashboard;

// Mirrors account's DashboardTrendPeriod: serialized as strings, so the values must stay in sync
// with the internal API's contract. The internal API is the single source of the data; this mirror
// exists so the SPA-facing OpenAPI document describes the same shape account returns.
public enum DashboardTrendPeriod
{
    Last7Days,
    Last30Days,
    Last90Days
}

[PublicAPI]
public sealed record GetDashboardKpisQuery(DashboardTrendPeriod Period = DashboardTrendPeriod.Last30Days)
    : IRequest<Result<BackOfficeDashboardKpisResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardKpisResponse(
    DashboardTrendPeriod Period,
    long TotalTenants,
    long ActiveTenants,
    long TrialTenants,
    long CanceledTenants,
    long NewTenantsInPeriod,
    long? NewTenantsDeltaPercent,
    long TotalUsers,
    long ActiveUsersInPeriod,
    decimal BlendedMonthlyRecurringRevenue,
    decimal? BlendedMonthlyRecurringRevenueDeltaPercent,
    decimal TotalRevenue,
    string? Currency,
    long ActiveSessionsLast24Hours
);

public sealed class GetDashboardKpisHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardKpisQuery, Result<BackOfficeDashboardKpisResponse>>
{
    public async Task<Result<BackOfficeDashboardKpisResponse>> Handle(GetDashboardKpisQuery query, CancellationToken cancellationToken)
    {
        var response = await accountInternalApiClient.GetDashboardKpisAsync(query.Period, cancellationToken);
        return response;
    }
}
