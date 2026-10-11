using BackOffice.Features.Me;
using MediatR;
using Microsoft.Extensions.Options;
using SharedKernel.ApiResults;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Endpoints;

namespace BackOffice.Api;

public sealed class MeEndpoints : IEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var backOfficeHost = routes.ServiceProvider.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

        var group = routes.MapGroup("/api/back-office")
            .WithTags("BackOffice")
            .RequireHost(backOfficeHost)
            .RequireAuthorization(BackOfficeIdentityDefaults.PolicyName)
            .ProducesValidationProblem();

        group.MapGet("/me", async Task<ApiResult<MeResponse>> ([AsParameters] GetMeQuery query, IMediator mediator)
            => await mediator.Send(query)
        ).Produces<MeResponse>();
    }
}
