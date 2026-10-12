using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.FeatureFlags;

// Mirrors account's feature flag admin command contracts (see the dashboard queries for the
// mirror rules). The FlagKey rides the route, not the body; account owns the flag mutations.
[PublicAPI]
public sealed record ActivateFeatureFlagCommand(string FlagKey) : ICommand, IRequest<Result>;

public sealed class ActivateFeatureFlagHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<ActivateFeatureFlagCommand, Result>
{
    public async Task<Result> Handle(ActivateFeatureFlagCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.ActivateFeatureFlagAsync(command.FlagKey, cancellationToken);
    }
}

[PublicAPI]
public sealed record DeactivateFeatureFlagCommand(string FlagKey) : ICommand, IRequest<Result>;

public sealed class DeactivateFeatureFlagHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<DeactivateFeatureFlagCommand, Result>
{
    public async Task<Result> Handle(DeactivateFeatureFlagCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.DeactivateFeatureFlagAsync(command.FlagKey, cancellationToken);
    }
}

[PublicAPI]
public sealed record DeleteFeatureFlagCommand(string FlagKey) : ICommand, IRequest<Result>;

public sealed class DeleteFeatureFlagHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<DeleteFeatureFlagCommand, Result>
{
    public async Task<Result> Handle(DeleteFeatureFlagCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.DeleteFeatureFlagAsync(command.FlagKey, cancellationToken);
    }
}

[PublicAPI]
public sealed record SetTenantFeatureFlagInternalCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes this property from the API contract
    public string FlagKey { get; init; } = null!;

    public required TenantId TenantId { get; init; }

    public required bool Enabled { get; init; }
}

public sealed class SetTenantFeatureFlagInternalHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<SetTenantFeatureFlagInternalCommand, Result>
{
    public async Task<Result> Handle(SetTenantFeatureFlagInternalCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.SetTenantFeatureFlagAsync(command, cancellationToken);
    }
}

[PublicAPI]
public sealed record SetFeatureFlagRolloutPercentageCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes this property from the API contract
    public string FlagKey { get; init; } = null!;

    public required int RolloutPercentage { get; init; }
}

public sealed class SetFeatureFlagRolloutPercentageHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<SetFeatureFlagRolloutPercentageCommand, Result>
{
    public async Task<Result> Handle(SetFeatureFlagRolloutPercentageCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.SetFeatureFlagRolloutPercentageAsync(command, cancellationToken);
    }
}

[PublicAPI]
public sealed record RemoveTenantFeatureFlagOverrideCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes from API contract
    public string FlagKey { get; init; } = null!;

    public required TenantId TenantId { get; init; }
}

public sealed class RemoveTenantFeatureFlagOverrideHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<RemoveTenantFeatureFlagOverrideCommand, Result>
{
    public async Task<Result> Handle(RemoveTenantFeatureFlagOverrideCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.RemoveTenantFeatureFlagOverrideAsync(command.FlagKey, command.TenantId, cancellationToken);
    }
}

[PublicAPI]
public sealed record SetUserFeatureFlagInternalCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes from API contract
    public string FlagKey { get; init; } = null!;

    public required UserId UserId { get; init; }

    public required TenantId TenantId { get; init; }

    public required bool Enabled { get; init; }
}

public sealed class SetUserFeatureFlagInternalHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<SetUserFeatureFlagInternalCommand, Result>
{
    public async Task<Result> Handle(SetUserFeatureFlagInternalCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.SetUserFeatureFlagAsync(command, cancellationToken);
    }
}

[PublicAPI]
public sealed record RemoveUserFeatureFlagOverrideCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes from API contract
    public string FlagKey { get; init; } = null!;

    public required UserId UserId { get; init; }

    public required TenantId TenantId { get; init; }
}

public sealed class RemoveUserFeatureFlagOverrideHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<RemoveUserFeatureFlagOverrideCommand, Result>
{
    public async Task<Result> Handle(RemoveUserFeatureFlagOverrideCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.RemoveUserFeatureFlagOverrideAsync(command.FlagKey, command.UserId, command.TenantId, cancellationToken);
    }
}
