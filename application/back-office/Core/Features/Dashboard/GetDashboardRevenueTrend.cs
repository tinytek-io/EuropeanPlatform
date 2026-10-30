using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardRevenueTrend contract (see GetDashboardTrends for the mirror rules).
[PublicAPI]
public sealed record GetDashboardRevenueTrendQuery(DashboardTrendPeriod Period = DashboardTrendPeriod.Last30Days)
    : IRequest<Result<BackOfficeDashboardRevenueTrendResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardRevenueTrendResponse(
    DashboardTrendPeriod Period,
    string? Currency,
    BackOfficeDashboardRevenueTrendPoint[] Points,
    BackOfficeDashboardRevenueTrendPoint[] PriorPoints
);

[PublicAPI]
public sealed record BackOfficeDashboardRevenueTrendPoint(DateOnly Date, decimal Revenue);

public sealed class GetDashboardRevenueTrendQueryValidator : AbstractValidator<GetDashboardRevenueTrendQuery>
{
    public GetDashboardRevenueTrendQueryValidator()
    {
        RuleFor(x => x.Period).Must(p => Enum.IsDefined(typeof(DashboardTrendPeriod), p)).WithMessage("Period must be one of Last7Days, Last30Days, or Last90Days.");
    }
}

public sealed class GetDashboardRevenueTrendHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardRevenueTrendQuery, Result<BackOfficeDashboardRevenueTrendResponse>>
{
    public async Task<Result<BackOfficeDashboardRevenueTrendResponse>> Handle(GetDashboardRevenueTrendQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardRevenueTrendAsync(query, cancellationToken);
    }
}
