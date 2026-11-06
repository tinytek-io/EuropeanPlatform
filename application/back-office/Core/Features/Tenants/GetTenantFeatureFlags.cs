using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.Tenants;

// Mirrors account's GetTenantFeatureFlags contract (see the dashboard queries for the mirror
// rules). FeatureFlagScope and AbInclusionPin are the SharedKernel types account serializes.
[PublicAPI]
public sealed record GetTenantFeatureFlagsQuery : IRequest<Result<GetTenantFeatureFlagsResponse>>
{
    [JsonIgnore] // Removes from API contract
    public TenantId TenantId { get; init; } = null!;
}

[PublicAPI]
public sealed record GetTenantFeatureFlagsResponse(TenantFeatureFlagInfo[] Flags);

[PublicAPI]
public sealed record TenantFeatureFlagInfo(
    string FlagKey,
    FeatureFlagScope Scope,
    string Description,
    string? RequiredPlan,
    bool IsAbTestEligible,
    int? BucketStart,
    int? BucketEnd,
    int? RolloutPercentage,
    bool IsEnabled,
    FeatureFlagSource Source,
    bool IsBaseRowActive,
    int RolloutBucket,
    int? InclusionThresholdPercentage,
    bool DefaultEnabled,
    AbInclusionPin? TenantAbInclusionPin
);

public sealed class GetTenantFeatureFlagsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantFeatureFlagsQuery, Result<GetTenantFeatureFlagsResponse>>
{
    public async Task<Result<GetTenantFeatureFlagsResponse>> Handle(GetTenantFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantFeatureFlagsAsync(query, cancellationToken);
    }
}
