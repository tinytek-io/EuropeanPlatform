using Account.Features.BackOffice.BillingDrift.Queries;
using Account.Features.BackOffice.BillingEvents.Queries;
using Account.Features.BackOffice.Dashboard.Queries;
using Account.Features.BackOffice.Invoices.Queries;
using Account.Features.FeatureFlags.Queries;
using Account.Features.Tenants.BackOffice.Commands;
using Account.Features.Tenants.BackOffice.Queries;
using Account.Features.Users.BackOffice.Commands;
using Account.Features.Users.BackOffice.Queries;
using SharedKernel.ApiResults;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Domain;
using SharedKernel.Endpoints;

namespace Account.Api.InternalApi;

// The internal API surface the standalone back-office BFF consumes: the same queries as account's
// folded /api/back-office/* routes, exposed under /internal-api/back-office/* for the BFF's typed
// clients. Every route requires the delegation policy — a platform-signed short-lived JWT minted by
// the BFF from the signed-in staff principal — so in-cluster callers cannot reach back-office
// data by injecting EasyAuth-style headers. In-cluster only: the gateway blocks /internal-api/*
// from public traffic. No OpenAPI group: the SPA-facing documents must not leak the internal
// contract. Mutating routes combine the delegation policy with the admin requirement when they
// arrive.
public sealed class BackOfficeInternalEndpoints : IEndpoints
{
    private const string RoutesPrefix = "/internal-api/back-office";

    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(RoutesPrefix)
            .WithTags("BackOfficeInternal")
            .RequireAuthorization(BackOfficeIdentityDefaults.DelegationPolicyName)
            .ProducesValidationProblem();

        var dashboard = group.MapGroup("/dashboard").WithTags("BackOfficeInternalDashboard");
        dashboard.MapGet("/kpis", async Task<ApiResult<BackOfficeDashboardKpisResponse>> ([AsParameters] GetDashboardKpisQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardKpisResponse>();
        dashboard.MapGet("/trends", async Task<ApiResult<BackOfficeDashboardTrendsResponse>> ([AsParameters] GetDashboardTrendsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardTrendsResponse>();
        dashboard.MapGet("/mrr-trend", async Task<ApiResult<BackOfficeDashboardMrrTrendResponse>> ([AsParameters] GetDashboardMrrTrendQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardMrrTrendResponse>();
        dashboard.MapGet("/revenue-trend", async Task<ApiResult<BackOfficeDashboardRevenueTrendResponse>> ([AsParameters] GetDashboardRevenueTrendQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRevenueTrendResponse>();
        dashboard.MapGet("/plan-distribution", async Task<ApiResult<BackOfficeDashboardPlanDistributionResponse>> ([AsParameters] GetDashboardPlanDistributionQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardPlanDistributionResponse>();
        dashboard.MapGet("/recent-signups", async Task<ApiResult<BackOfficeDashboardRecentSignupsResponse>> ([AsParameters] GetDashboardRecentSignupsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentSignupsResponse>();
        dashboard.MapGet("/recent-logins", async Task<ApiResult<BackOfficeDashboardRecentLoginsResponse>> ([AsParameters] GetDashboardRecentLoginsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentLoginsResponse>();
        dashboard.MapGet("/recent-payments", async Task<ApiResult<BackOfficeDashboardRecentPaymentsResponse>> ([AsParameters] GetDashboardRecentPaymentsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentPaymentsResponse>();
        dashboard.MapGet("/recent-stripe-events", async Task<ApiResult<BackOfficeDashboardRecentStripeEventsResponse>> ([AsParameters] GetDashboardRecentStripeEventsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardRecentStripeEventsResponse>();

        var billingEvents = group.MapGroup("/billing-events").WithTags("BackOfficeInternalBillingEvents");
        billingEvents.MapGet("/", async Task<ApiResult<BillingEventsResponse>> ([AsParameters] GetBackOfficeBillingEventsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BillingEventsResponse>();

        var invoices = group.MapGroup("/invoices").WithTags("BackOfficeInternalInvoices");
        invoices.MapGet("/", async Task<ApiResult<BackOfficeInvoicesResponse>> ([AsParameters] GetBackOfficeInvoicesQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeInvoicesResponse>();

        var billingDrift = group.MapGroup("/billing-drift").WithTags("BackOfficeInternalBillingDrift");
        billingDrift.MapGet("/summary", async Task<ApiResult<BillingDriftSummaryResponse>> ([AsParameters] GetBillingDriftSummaryQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BillingDriftSummaryResponse>();
        billingDrift.MapGet("/unsynced-summary", async Task<ApiResult<UnsyncedSubscriptionsSummaryResponse>> ([AsParameters] GetUnsyncedSubscriptionsSummaryQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<UnsyncedSubscriptionsSummaryResponse>();
        billingDrift.MapGet("/mrr-consistency-summary", async Task<ApiResult<DashboardMrrConsistencySummaryResponse>> ([AsParameters] GetDashboardMrrConsistencySummaryQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<DashboardMrrConsistencySummaryResponse>();

        var users = group.MapGroup("/users").WithTags("BackOfficeInternalUsers");
        users.MapGet("/", async Task<ApiResult<BackOfficeUsersResponse>> ([AsParameters] GetBackOfficeUsersQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeUsersResponse>();
        users.MapGet("/{id}", async Task<ApiResult<BackOfficeUserDetailResponse>> (UserId id, IMediator mediator)
            => await mediator.Send(new GetBackOfficeUserDetailQuery(id))
        ).Produces<BackOfficeUserDetailResponse>();
        users.MapGet("/{id}/sessions", async Task<ApiResult<BackOfficeUserSessionsResponse>> (UserId id, [AsParameters] GetBackOfficeUserSessionsQuery query, IMediator mediator)
            => await mediator.Send(query with { Id = id })
        ).Produces<BackOfficeUserSessionsResponse>();
        users.MapGet("/{id}/login-history", async Task<ApiResult<BackOfficeUserLoginHistoryResponse>> (UserId id, [AsParameters] GetBackOfficeUserLoginHistoryQuery query, IMediator mediator)
            => await mediator.Send(query with { Id = id })
        ).Produces<BackOfficeUserLoginHistoryResponse>();
        users.MapGet("/{id}/feature-flags", async Task<ApiResult<GetUserFeatureFlagsResponse>> (UserId id, IMediator mediator)
            => await mediator.Send(new GetUserFeatureFlagsQuery { UserId = id })
        ).Produces<GetUserFeatureFlagsResponse>();

        // Mutating internal routes: the BFF presents a short-lived delegation token instead of an
        // antiforgery token (both authenticate the caller; the antiforgery check exists for the
        // browser's ambient-cookie flows), so the routes opt out of antiforgery while keeping the
        // admin gate. Admin mutations carry the delegation-admin policy; a tenant-scoped write that
        // later needs only staff membership can use the plain delegation policy.
        users.MapPut("/{id}/ab-inclusion-pin", async Task<ApiResult> (UserId id, SetUserAbInclusionPinCommand command, IMediator mediator)
            => await mediator.Send(command with { UserId = id })
        ).DisableAntiforgery().RequireAuthorization(BackOfficeIdentityDefaults.DelegationAdminPolicyName);

        var tenants = group.MapGroup("/tenants").WithTags("BackOfficeInternalTenants");
        tenants.MapGet("/", async Task<ApiResult<TenantsResponse>> ([AsParameters] GetTenantsQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<TenantsResponse>();
        tenants.MapGet("/{id}", async Task<ApiResult<TenantDetailResponse>> (TenantId id, IMediator mediator)
            => await mediator.Send(new GetTenantDetailQuery(id))
        ).Produces<TenantDetailResponse>();
        tenants.MapGet("/{id}/user-counts", async Task<ApiResult<TenantUserCountsResponse>> (TenantId id, IMediator mediator)
            => await mediator.Send(new GetTenantUserCountsQuery(id))
        ).Produces<TenantUserCountsResponse>();
        tenants.MapGet("/{id}/users", async Task<ApiResult<TenantUsersResponse>> (TenantId id, [AsParameters] GetTenantUsersQuery query, IMediator mediator)
            => await mediator.Send(query with { Id = id })
        ).Produces<TenantUsersResponse>();
        tenants.MapGet("/{id}/activity", async Task<ApiResult<TenantActivityResponse>> (TenantId id, IMediator mediator)
            => await mediator.Send(new GetTenantActivityQuery(id))
        ).Produces<TenantActivityResponse>();
        tenants.MapGet("/{id}/payment-history", async Task<ApiResult<TenantPaymentHistoryResponse>> (TenantId id, [AsParameters] GetTenantPaymentHistoryQuery query, IMediator mediator)
            => await mediator.Send(query with { Id = id })
        ).Produces<TenantPaymentHistoryResponse>();
        tenants.MapGet("/{id}/feature-flags", async Task<ApiResult<GetTenantFeatureFlagsResponse>> (TenantId id, IMediator mediator)
            => await mediator.Send(new GetTenantFeatureFlagsQuery { TenantId = id })
        ).Produces<GetTenantFeatureFlagsResponse>();
        tenants.MapPut("/{id}/ab-inclusion-pin", async Task<ApiResult> (TenantId id, SetTenantAbInclusionPinCommand command, IMediator mediator)
            => await mediator.Send(command with { TenantId = id })
        ).DisableAntiforgery().RequireAuthorization(BackOfficeIdentityDefaults.DelegationAdminPolicyName);
        tenants.MapPost("/{id}/reconcile-with-stripe", async Task<ApiResult<ReconcileTenantWithStripeResponse>> (TenantId id, IMediator mediator)
                => await mediator.Send(new ReconcileTenantWithStripeCommand { TenantId = id })
            ).DisableAntiforgery().RequireAuthorization(BackOfficeIdentityDefaults.DelegationAdminPolicyName)
            .Produces<ReconcileTenantWithStripeResponse>();
        tenants.MapPost("/{id}/replay-archived-stripe-events", async Task<ApiResult<ReplayArchivedTenantStripeEventsResponse>> (TenantId id, IMediator mediator)
                => await mediator.Send(new ReplayArchivedTenantStripeEventsCommand { TenantId = id })
            ).DisableAntiforgery().RequireAuthorization(BackOfficeIdentityDefaults.DelegationAdminPolicyName)
            .Produces<ReplayArchivedTenantStripeEventsResponse>();
        tenants.MapPost("/{id}/drift/acknowledge", async Task<ApiResult> (TenantId id, IMediator mediator)
            => await mediator.Send(new AcknowledgeBillingDriftCommand { TenantId = id })
        ).DisableAntiforgery().RequireAuthorization(BackOfficeIdentityDefaults.DelegationAdminPolicyName);
    }
}
