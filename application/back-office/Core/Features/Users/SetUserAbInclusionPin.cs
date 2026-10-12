using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.Users;

// Mirrors account's SetUserAbInclusionPin command contract (see the dashboard queries for the
// mirror rules). The UserId rides the route, not the body.
[PublicAPI]
public sealed record SetUserAbInclusionPinCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes from API contract
    public UserId UserId { get; init; } = null!;

    public AbInclusionPin? AbInclusionPin { get; init; }
}

public sealed class SetUserAbInclusionPinHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<SetUserAbInclusionPinCommand, Result>
{
    public async Task<Result> Handle(SetUserAbInclusionPinCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.SetUserAbInclusionPinAsync(command, cancellationToken);
    }
}
