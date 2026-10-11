using Account.Features.BackOffice.Dashboard.Queries;
using SharedKernel.ApiResults;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Endpoints;

namespace Account.Api.InternalApi;

// The internal API surface the standalone back-office BFF consumes: the same queries and commands
// as account's folded /api/back-office/* routes, exposed under /internal-api/back-office/* for
// the BFF's typed clients. Every route requires the delegation policy — a platform-signed
// short-lived JWT minted by the BFF from the signed-in staff principal — so in-cluster callers
// cannot reach back-office data by injecting EasyAuth-style headers. In-cluster only: the gateway
// blocks /internal-api/* from public traffic. No OpenAPI group: the SPA-facing documents must not
// leak the internal contract.
public sealed class BackOfficeInternalEndpoints : IEndpoints
{
    private const string RoutesPrefix = "/internal-api/back-office";

    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(RoutesPrefix)
            .WithTags("BackOfficeInternal")
            .RequireAuthorization(BackOfficeIdentityDefaults.DelegationPolicyName)
            .ProducesValidationProblem();

        group.MapGet("/dashboard/kpis", async Task<ApiResult<BackOfficeDashboardKpisResponse>> (
            [AsParameters] GetDashboardKpisQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<BackOfficeDashboardKpisResponse>();
    }
}
