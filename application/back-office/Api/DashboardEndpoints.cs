using BackOffice.Features.Dashboard;
using Microsoft.Extensions.Options;
using SharedKernel.ApiResults;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Endpoints;

namespace BackOffice.Api;

public sealed class DashboardEndpoints : IEndpoints
{
    private const string RoutesPrefix = "/api/back-office/dashboard";

    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        // BackOffice:Host is required (validated at startup via ValidateOnStart in
        // AddBackOfficeServices). The startup validation must stay in place so a missing/blank
        // value fails loudly rather than silently 404-ing back-office endpoints.
        var backOfficeHost = routes.ServiceProvider.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

        var group = routes.MapGroup(RoutesPrefix)
            .WithTags("BackOfficeDashboard")
            .RequireHost(backOfficeHost)
            .RequireAuthorization(BackOfficeIdentityDefaults.PolicyName)
            .ProducesValidationProblem();

        group.MapGet("/kpis", async Task<ApiResult<BackOfficeDashboardKpisResponse>> ([AsParameters] GetDashboardKpisQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardKpisResponse>();

        group.MapGet("/trends", async Task<ApiResult<BackOfficeDashboardTrendsResponse>> ([AsParameters] GetDashboardTrendsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardTrendsResponse>();

        group.MapGet("/mrr-trend", async Task<ApiResult<BackOfficeDashboardMrrTrendResponse>> ([AsParameters] GetDashboardMrrTrendQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardMrrTrendResponse>();

        group.MapGet("/revenue-trend", async Task<ApiResult<BackOfficeDashboardRevenueTrendResponse>> ([AsParameters] GetDashboardRevenueTrendQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRevenueTrendResponse>();

        group.MapGet("/plan-distribution", async Task<ApiResult<BackOfficeDashboardPlanDistributionResponse>> (IMediator mediator)
            => await mediator.Send(new GetDashboardPlanDistributionQuery())
        ).Produces<BackOfficeDashboardPlanDistributionResponse>();

        group.MapGet("/recent-signups", async Task<ApiResult<BackOfficeDashboardRecentSignupsResponse>> ([AsParameters] GetDashboardRecentSignupsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentSignupsResponse>();

        group.MapGet("/recent-logins", async Task<ApiResult<BackOfficeDashboardRecentLoginsResponse>> ([AsParameters] GetDashboardRecentLoginsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentLoginsResponse>();

        group.MapGet("/recent-payments", async Task<ApiResult<BackOfficeDashboardRecentPaymentsResponse>> ([AsParameters] GetDashboardRecentPaymentsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentPaymentsResponse>();

        group.MapGet("/recent-stripe-events", async Task<ApiResult<BackOfficeDashboardRecentStripeEventsResponse>> ([AsParameters] GetDashboardRecentStripeEventsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentStripeEventsResponse>();
    }
}
