using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.Tenants;

// Mirrors account's tenant admin command contracts (see the dashboard queries for the mirror
// rules). The TenantId rides the route, not the body; account owns the actual mutations.
[PublicAPI]
public sealed record SetTenantAbInclusionPinCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes from API contract
    public TenantId TenantId { get; init; } = null!;

    public AbInclusionPin? AbInclusionPin { get; init; }
}

public sealed class SetTenantAbInclusionPinHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<SetTenantAbInclusionPinCommand, Result>
{
    public async Task<Result> Handle(SetTenantAbInclusionPinCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.SetTenantAbInclusionPinAsync(command, cancellationToken);
    }
}

[PublicAPI]
public sealed record AcknowledgeBillingDriftCommand : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes from API contract
    public TenantId TenantId { get; init; } = null!;
}

public sealed class AcknowledgeBillingDriftHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<AcknowledgeBillingDriftCommand, Result>
{
    public async Task<Result> Handle(AcknowledgeBillingDriftCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.AcknowledgeBillingDriftAsync(command.TenantId, cancellationToken);
    }
}

[PublicAPI]
public sealed record ReconcileTenantWithStripeCommand : ICommand, IRequest<Result<ReconcileTenantWithStripeResponse>>
{
    [JsonIgnore] // Removes from API contract
    public TenantId TenantId { get; init; } = null!;
}

[PublicAPI]
public sealed record ReconcileTenantWithStripeResponse(
    int BillingEventsAppended,
    bool HasDriftDetected,
    int DriftDiscrepancyCount,
    DateTimeOffset ReconciledAt,
    ArchivedEventsAwaitingConfirmation? ArchivedEventsAwaitingConfirmation
);

[PublicAPI]
public sealed record ArchivedEventsAwaitingConfirmation(int Count, DateTimeOffset OldestOccurredAt, DateTimeOffset NewestOccurredAt);

public sealed class ReconcileTenantWithStripeHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<ReconcileTenantWithStripeCommand, Result<ReconcileTenantWithStripeResponse>>
{
    public async Task<Result<ReconcileTenantWithStripeResponse>> Handle(ReconcileTenantWithStripeCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.ReconcileTenantWithStripeAsync(command.TenantId, cancellationToken);
    }
}

[PublicAPI]
public sealed record ReplayArchivedTenantStripeEventsCommand : ICommand, IRequest<Result<ReplayArchivedTenantStripeEventsResponse>>
{
    [JsonIgnore] // Removes from API contract
    public TenantId TenantId { get; init; } = null!;
}

[PublicAPI]
public sealed record ReplayArchivedTenantStripeEventsResponse(int BillingEventsAppended, DateTimeOffset ReplayedAt);

public sealed class ReplayArchivedTenantStripeEventsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<ReplayArchivedTenantStripeEventsCommand, Result<ReplayArchivedTenantStripeEventsResponse>>
{
    public async Task<Result<ReplayArchivedTenantStripeEventsResponse>> Handle(ReplayArchivedTenantStripeEventsCommand command, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.ReplayArchivedTenantStripeEventsAsync(command.TenantId, cancellationToken);
    }
}
