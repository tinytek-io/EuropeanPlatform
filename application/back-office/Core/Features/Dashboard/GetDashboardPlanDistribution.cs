using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardPlanDistribution contract (see GetDashboardTrends for the mirror
// rules). SubscriptionPlan comes from the shared contract mirrors.
[PublicAPI]
public sealed record GetDashboardPlanDistributionQuery : IRequest<Result<BackOfficeDashboardPlanDistributionResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardPlanDistributionResponse(
    long TotalTenants,
    BackOfficeDashboardPlanDistributionEntry[] Distribution
);

[PublicAPI]
public sealed record BackOfficeDashboardPlanDistributionEntry(SubscriptionPlan Plan, long Count, double Percentage);

public sealed class GetDashboardPlanDistributionHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardPlanDistributionQuery, Result<BackOfficeDashboardPlanDistributionResponse>>
{
    public async Task<Result<BackOfficeDashboardPlanDistributionResponse>> Handle(GetDashboardPlanDistributionQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardPlanDistributionAsync(cancellationToken);
    }
}
