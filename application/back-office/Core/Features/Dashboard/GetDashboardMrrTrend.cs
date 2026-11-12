using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardMrrTrend contract (see GetDashboardTrends for the mirror rules).
[PublicAPI]
public sealed record GetDashboardMrrTrendQuery(DashboardTrendPeriod Period = DashboardTrendPeriod.Last30Days)
    : IRequest<Result<BackOfficeDashboardMrrTrendResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardMrrTrendResponse(
    DashboardTrendPeriod Period,
    string? Currency,
    BackOfficeDashboardMrrTrendPoint[] Points,
    BackOfficeDashboardMrrTrendPoint[] PriorPoints
);

[PublicAPI]
public sealed record BackOfficeDashboardMrrTrendPoint(DateOnly Date, decimal MonthlyRecurringRevenue);

public sealed class GetDashboardMrrTrendQueryValidator : AbstractValidator<GetDashboardMrrTrendQuery>
{
    public GetDashboardMrrTrendQueryValidator()
    {
        RuleFor(x => x.Period).Must(p => Enum.IsDefined(typeof(DashboardTrendPeriod), p)).WithMessage("Period must be one of Last7Days, Last30Days, or Last90Days.");
    }
}

public sealed class GetDashboardMrrTrendHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardMrrTrendQuery, Result<BackOfficeDashboardMrrTrendResponse>>
{
    public async Task<Result<BackOfficeDashboardMrrTrendResponse>> Handle(GetDashboardMrrTrendQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardMrrTrendAsync(query, cancellationToken);
    }
}
