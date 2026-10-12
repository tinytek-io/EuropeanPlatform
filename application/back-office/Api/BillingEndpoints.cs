using BackOffice.Features.BillingEvents;
using BackOffice.Features.BillingDrift;
using BackOffice.Features.Invoices;
using Microsoft.Extensions.Options;
using SharedKernel.ApiResults;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Endpoints;

namespace BackOffice.Api;

public sealed class BillingEndpoints : IEndpoints
{
    private const string RoutesPrefix = "/api/back-office";

    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var backOfficeHost = routes.ServiceProvider.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

        var billingEvents = routes.MapGroup($"{RoutesPrefix}/billing-events")
            .WithTags("BackOfficeBillingEvents")
            .RequireHost(backOfficeHost)
            .RequireAuthorization(BackOfficeIdentityDefaults.PolicyName)
            .ProducesValidationProblem();

        billingEvents.MapGet("/", async Task<ApiResult<BillingEventsResponse>> ([AsParameters] GetBackOfficeBillingEventsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BillingEventsResponse>();

        var invoices = routes.MapGroup($"{RoutesPrefix}/invoices")
            .WithTags("BackOfficeInvoices")
            .RequireHost(backOfficeHost)
            .RequireAuthorization(BackOfficeIdentityDefaults.PolicyName)
            .ProducesValidationProblem();

        invoices.MapGet("/", async Task<ApiResult<BackOfficeInvoicesResponse>> ([AsParameters] GetBackOfficeInvoicesQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeInvoicesResponse>();

        var billingDrift = routes.MapGroup($"{RoutesPrefix}/billing-drift")
            .WithTags("BackOfficeBillingDrift")
            .RequireHost(backOfficeHost)
            .RequireAuthorization(BackOfficeIdentityDefaults.PolicyName)
            .ProducesValidationProblem();

        billingDrift.MapGet("/summary", async Task<ApiResult<BillingDriftSummaryResponse>> ([AsParameters] GetBillingDriftSummaryQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BillingDriftSummaryResponse>();

        billingDrift.MapGet("/unsynced-summary", async Task<ApiResult<UnsyncedSubscriptionsSummaryResponse>> ([AsParameters] GetUnsyncedSubscriptionsSummaryQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<UnsyncedSubscriptionsSummaryResponse>();

        billingDrift.MapGet("/mrr-consistency-summary", async Task<ApiResult<DashboardMrrConsistencySummaryResponse>> ([AsParameters] GetDashboardMrrConsistencySummaryQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<DashboardMrrConsistencySummaryResponse>();
    }
}
