using BackOffice.ExecutionContext;
using JetBrains.Annotations;
using SharedKernel.Cqrs;

namespace BackOffice.Features.Me;

[PublicAPI]
public sealed record GetMeQuery : IRequest<Result<MeResponse>>;

[PublicAPI]
public sealed record MeResponse(string DisplayName, string Email, bool IsAdmin, string[] Groups);

public sealed class GetMeHandler(IStaffExecutionContext staffExecutionContext)
    : IRequestHandler<GetMeQuery, Result<MeResponse>>
{
    // The route group requires authorization, so the staff execution context is always
    // authenticated here; its IsAdmin is the same computation the BackOfficeAdmin policy applies,
    // so the UI's admin state agrees with what the policy actually grants.
    public Task<Result<MeResponse>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        var response = new MeResponse(
            staffExecutionContext.DisplayName,
            staffExecutionContext.Email,
            staffExecutionContext.IsAdmin,
            staffExecutionContext.Groups
        );

        return Task.FromResult<Result<MeResponse>>(response);
    }
}
