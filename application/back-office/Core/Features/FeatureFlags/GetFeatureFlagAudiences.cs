using BackOffice.Features.Shared;
using BackOffice.Features.Tenants;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.FeatureFlags;
using SharedKernel.Persistence;

namespace BackOffice.Features.FeatureFlags;

// Mirrors account's GetFeatureFlagTenants contract (see the dashboard queries for the mirror
// rules): the flag-scoped tenant audience page, including override and evaluation fields.
[PublicAPI]
public sealed record GetFeatureFlagTenantsQuery(
    string? Search = null,
    SubscriptionPlan[]? Plans = null,
    FeatureFlagAudienceState? State = null,
    bool? HasOverride = null,
    SortableFeatureFlagTenantProperties OrderBy = SortableFeatureFlagTenantProperties.Name,
    SortOrder SortOrder = SortOrder.Ascending,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<GetFeatureFlagTenantsResponse>>
{
    [JsonIgnore] // Removes from API contract
    public string FlagKey { get; init; } = null!;

    public string? Search { get; } = Search?.Trim().ToLower();

    public SubscriptionPlan[] Plans { get; } = Plans ?? [];
}

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortableFeatureFlagTenantProperties
{
    Name,
    Plan,
    MonthlyRecurringRevenue,
    RenewalDate,
    IsEnabled,
    OverrideUpdatedAt,
    InclusionThresholdPercentage
}

[PublicAPI]
public sealed record GetFeatureFlagTenantsResponse(
    int TotalCount,
    int PageSize,
    int TotalPages,
    int CurrentPageOffset,
    int EnabledCount,
    int DisabledCount,
    int OverrideCount,
    FeatureFlagTenantInfo[] Tenants
);

// Field names mirror account's record; the tenant subset matches TenantSummary, override and
// evaluation fields come from account's feature-flag evaluation.
[PublicAPI]
public sealed record FeatureFlagTenantInfo(
    TenantId Id,
    string Name,
    string? LogoUrl,
    SubscriptionPlan Plan,
    decimal? MonthlyRecurringRevenue,
    decimal? ScheduledPriceAmount,
    string? Currency,
    DateTimeOffset? RenewalDate,
    PlannedSubscriptionChange? PlannedChange,
    bool HasEverSubscribed,
    string? Country,
    DateTimeOffset CreatedAt,
    TenantOwnerSummary? Owner,
    int RolloutBucket,
    bool IsEnabled,
    FeatureFlagSource Source,
    int? InclusionThresholdPercentage,
    bool DefaultEnabled,
    DateTimeOffset? OverrideEnabledAt,
    DateTimeOffset? OverrideDisabledAt,
    AbInclusionPin? TenantAbInclusionPin
);

public sealed class GetFeatureFlagTenantsValidator : AbstractValidator<GetFeatureFlagTenantsQuery>
{
    public GetFeatureFlagTenantsValidator()
    {
        RuleFor(x => x.FlagKey)
            .NotEmpty().WithMessage("Feature flag key must not be empty.")
            .Must(key => SharedKernel.FeatureFlags.FeatureFlags.Get(key) is not null).WithMessage("Feature flag key must exist in the registry.")
            .Must(key => SharedKernel.FeatureFlags.FeatureFlags.Get(key)?.Scope == FeatureFlagScope.Tenant).WithMessage("Feature flag must have tenant scope.");

        RuleFor(x => x.Search).MaximumLength(100).WithMessage("The search term must be at most 100 characters.");
        RuleFor(x => x.Plans.Length).LessThanOrEqualTo(10).WithMessage("Plans filter must contain no more than 10 values.");
        // Plan-gated flag detail page requests every tenant in one shot (PLAN_TENANT_LIST_CAP = 1000) so it can
        // group them by plan without pagination. Other call sites still use the default 25.
        RuleFor(x => x.PageSize).InclusiveBetween(1, 1000).WithMessage("Page size must be between 1 and 1000.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetFeatureFlagTenantsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetFeatureFlagTenantsQuery, Result<GetFeatureFlagTenantsResponse>>
{
    public async Task<Result<GetFeatureFlagTenantsResponse>> Handle(GetFeatureFlagTenantsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetFeatureFlagTenantsAsync(query, cancellationToken);
    }
}

// Mirrors account's GetFeatureFlagUsers contract: the flag-scoped user audience page.
[PublicAPI]
public sealed record GetFeatureFlagUsersQuery(
    string? Search = null,
    UserRole[]? Roles = null,
    FeatureFlagAudienceState? State = null,
    bool? HasOverride = null,
    SortableFeatureFlagUserProperties OrderBy = SortableFeatureFlagUserProperties.Name,
    SortOrder SortOrder = SortOrder.Ascending,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<GetFeatureFlagUsersResponse>>
{
    [JsonIgnore] // Removes from API contract
    public string FlagKey { get; init; } = null!;

    public string? Search { get; } = Search?.Trim().ToLower();

    public UserRole[] Roles { get; } = Roles ?? [];
}

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortableFeatureFlagUserProperties
{
    Name,
    TenantName,
    Role,
    LastSeenAt,
    IsEnabled,
    OverrideUpdatedAt,
    InclusionThresholdPercentage
}

[PublicAPI]
public sealed record GetFeatureFlagUsersResponse(
    int TotalCount,
    int PageSize,
    int TotalPages,
    int CurrentPageOffset,
    int EnabledCount,
    int DisabledCount,
    int OverrideCount,
    FeatureFlagUserInfo[] Users
);

[PublicAPI]
public sealed record FeatureFlagUserInfo(
    UserId Id,
    TenantId TenantId,
    string Email,
    string? FirstName,
    string? LastName,
    string? AvatarUrl,
    UserRole Role,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt,
    string TenantName,
    SubscriptionPlan TenantPlan,
    int RolloutBucket,
    bool IsEnabled,
    FeatureFlagSource Source,
    int? InclusionThresholdPercentage,
    bool DefaultEnabled,
    DateTimeOffset? OverrideEnabledAt,
    DateTimeOffset? OverrideDisabledAt,
    AbInclusionPin? UserAbInclusionPin
);

public sealed class GetFeatureFlagUsersValidator : AbstractValidator<GetFeatureFlagUsersQuery>
{
    public GetFeatureFlagUsersValidator()
    {
        RuleFor(x => x.FlagKey)
            .NotEmpty().WithMessage("Feature flag key must not be empty.")
            .Must(key => SharedKernel.FeatureFlags.FeatureFlags.Get(key) is not null).WithMessage("Feature flag key must exist in the registry.")
            .Must(key => SharedKernel.FeatureFlags.FeatureFlags.Get(key)?.Scope == FeatureFlagScope.User).WithMessage("Feature flag must have user scope.");

        RuleFor(x => x.Search).MaximumLength(100).WithMessage("The search term must be at most 100 characters.");
        RuleFor(x => x.Roles.Length).LessThanOrEqualTo(10).WithMessage("Roles filter must contain no more than 10 values.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetFeatureFlagUsersHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetFeatureFlagUsersQuery, Result<GetFeatureFlagUsersResponse>>
{
    public async Task<Result<GetFeatureFlagUsersResponse>> Handle(GetFeatureFlagUsersQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetFeatureFlagUsersAsync(query, cancellationToken);
    }
}
