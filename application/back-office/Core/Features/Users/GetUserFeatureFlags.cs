using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;

namespace BackOffice.Features.Users;

// Mirrors account's GetUserFeatureFlags contract (see the dashboard queries for the mirror rules).
// FeatureFlagScope and AbInclusionPin are the SharedKernel types account serializes on the wire.
[PublicAPI]
public sealed record GetUserFeatureFlagsQuery : IRequest<Result<GetUserFeatureFlagsResponse>>
{
    [JsonIgnore] // Removes from API contract
    public UserId UserId { get; init; } = null!;
}

[PublicAPI]
public sealed record GetUserFeatureFlagsResponse(UserFeatureFlagInfo[] Flags);

[PublicAPI]
public sealed record UserFeatureFlagInfo(
    string FlagKey,
    FeatureFlagScope Scope,
    string Description,
    bool IsAbTestEligible,
    int? BucketStart,
    int? BucketEnd,
    int? RolloutPercentage,
    bool IsEnabled,
    FeatureFlagSource Source,
    bool IsBaseRowActive,
    int RolloutBucket,
    TenantId TenantId,
    int? InclusionThresholdPercentage,
    bool DefaultEnabled,
    AbInclusionPin? UserAbInclusionPin
);

public sealed class GetUserFeatureFlagsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetUserFeatureFlagsQuery, Result<GetUserFeatureFlagsResponse>>
{
    public async Task<Result<GetUserFeatureFlagsResponse>> Handle(GetUserFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetUserFeatureFlagsAsync(query, cancellationToken);
    }
}
