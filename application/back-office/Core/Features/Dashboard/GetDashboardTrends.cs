using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardTrends contract: the query, response, and validation messages stay
// in sync with the internal API's; the handler forwards over the internal API client.
[PublicAPI]
public sealed record GetDashboardTrendsQuery(DashboardTrendMetric Metric, DashboardTrendPeriod Period)
    : IRequest<Result<BackOfficeDashboardTrendsResponse>>;

[PublicAPI]
public enum DashboardTrendMetric
{
    NewTenants,
    NewUsers,
    LoginActivity
}

[PublicAPI]
public sealed record BackOfficeDashboardTrendsResponse(
    DashboardTrendMetric Metric,
    DashboardTrendPeriod Period,
    BackOfficeDashboardTrendPoint[] Points,
    BackOfficeDashboardTrendPoint[] PriorPoints
);

[PublicAPI]
public sealed record BackOfficeDashboardTrendPoint(DateOnly Date, long Value);

public sealed class GetDashboardTrendsQueryValidator : AbstractValidator<GetDashboardTrendsQuery>
{
    public GetDashboardTrendsQueryValidator()
    {
        RuleFor(x => x.Metric).Must(m => Enum.IsDefined(typeof(DashboardTrendMetric), m)).WithMessage("Metric must be one of NewTenants, NewUsers, or LoginActivity.");
        RuleFor(x => x.Period).Must(p => Enum.IsDefined(typeof(DashboardTrendPeriod), p)).WithMessage("Period must be one of Last7Days, Last30Days, or Last90Days.");
    }
}

public sealed class GetDashboardTrendsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardTrendsQuery, Result<BackOfficeDashboardTrendsResponse>>
{
    public async Task<Result<BackOfficeDashboardTrendsResponse>> Handle(GetDashboardTrendsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardTrendsAsync(query, cancellationToken);
    }
}
