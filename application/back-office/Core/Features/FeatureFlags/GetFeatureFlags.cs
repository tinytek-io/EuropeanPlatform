using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.FeatureFlags;

// Mirrors account's GetFeatureFlags contract (see the dashboard queries for the mirror rules).
// FeatureFlagAdminLevel and FeatureFlagScope are the SharedKernel types account serializes.
[PublicAPI]
public sealed record GetFeatureFlagsQuery(bool IncludeDeleted = false) : IRequest<Result<GetFeatureFlagsResponse>>;

[PublicAPI]
public sealed record GetFeatureFlagsResponse(FeatureFlagInfo[] Flags);

[PublicAPI]
public sealed record FeatureFlagInfo(
    string Key,
    FeatureFlagScope Scope,
    FeatureFlagAdminLevel AdminLevel,
    string Description,
    bool IsAbTestEligible,
    bool ConfigurableByTenant,
    bool ConfigurableByUser,
    string? RequiredPlan,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? EnabledAt,
    DateTimeOffset? DisabledAt,
    int? RolloutBucketStart,
    int? RolloutBucketEnd,
    int? RolloutPercentage,
    bool IsActive,
    bool IsKillSwitchEnabled,
    bool IsStableModule,
    DateTimeOffset? OrphanedAt,
    DateTimeOffset? DeletedAt
);

public sealed class GetFeatureFlagsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetFeatureFlagsQuery, Result<GetFeatureFlagsResponse>>
{
    public async Task<Result<GetFeatureFlagsResponse>> Handle(GetFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetFeatureFlagsAsync(query, cancellationToken);
    }
}
