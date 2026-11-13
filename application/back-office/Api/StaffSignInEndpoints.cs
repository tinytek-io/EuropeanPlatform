using BackOffice.Authentication;
using BackOffice.Features.StaffSignIn.Commands;
using BackOffice.Features.StaffSignIn.Domain;
using Microsoft.Extensions.Options;
using SharedKernel.ApiResults;
using SharedKernel.Authentication;
using SharedKernel.Endpoints;

namespace BackOffice.Api;

/// <summary>
///     The back-office's own staff sign-in: an emailed one-time code exchanged for the staff session cookie. Always
///     mapped, so the API contract does not depend on configuration: without a staff identity file the directory is
///     empty, so a start never sends a code and a completion never succeeds.
/// </summary>
public sealed class StaffSignInEndpoints : IEndpoints
{
    private const string RoutesPrefix = "/api/back-office/staff-sign-in";

    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var backOfficeHost = routes.ServiceProvider.GetRequiredService<IOptions<BackOfficeHostOptions>>().Value.Host;

        var group = routes.MapGroup(RoutesPrefix).WithTags("StaffSignIn").RequireHost(backOfficeHost).RequireAuthorization(BackOfficeIdentityDefaults.PolicyName).ProducesValidationProblem();

        group.MapPost("/start", async Task<ApiResult<StartStaffSignInResponse>> (StartStaffSignInCommand command, IMediator mediator)
            => await mediator.Send(command)
        ).Produces<StartStaffSignInResponse>().AllowAnonymous();

        group.MapPost("/{id}/complete", async Task<ApiResult> (StaffSignInId id, CompleteStaffSignInCommand command, IMediator mediator)
            => await mediator.Send(command with { Id = id })
        ).AllowAnonymous();
    }
}
