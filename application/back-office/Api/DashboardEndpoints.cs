using BackOffice.Features.Dashboard;
using MediatR;
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
    }
}
