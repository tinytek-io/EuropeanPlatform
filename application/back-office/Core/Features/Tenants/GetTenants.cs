using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.Persistence;

namespace BackOffice.Features.Tenants;

// Mirrors account's GetTenants contract (see the dashboard queries for the mirror rules),
// including the query's normalization (Search is trimmed and lowercased).
[PublicAPI]
public sealed record GetTenantsQuery(
    string? Search = null,
    SubscriptionPlan[]? Plans = null,
    TenantStatusFilter[]? Statuses = null,
    bool Unsynced = false,
    bool DriftDetected = false,
    SortableTenantProperties OrderBy = SortableTenantProperties.ModifiedAt,
    SortOrder SortOrder = SortOrder.Descending,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<TenantsResponse>>
{
    public string? Search { get; } = Search?.Trim().ToLower();

    public SubscriptionPlan[] Plans { get; } = Plans ?? [];

    public TenantStatusFilter[] Statuses { get; } = Statuses ?? [];
}

[PublicAPI]
public sealed record TenantsResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, TenantSummary[] Tenants);

[PublicAPI]
public sealed record TenantSummary(
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
    DateTimeOffset? ModifiedAt,
    TenantOwnerSummary? Owner
);

[PublicAPI]
public sealed record TenantOwnerSummary(UserId UserId, string? FirstName, string? LastName, string Email);

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TenantStatusFilter
{
    Active,
    Downgrading,
    Canceling,
    Canceled,
    Free
}

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortableTenantProperties
{
    Name,
    Plan,
    MonthlyRecurringRevenue,
    RenewalDate,
    Status,
    Country,
    CreatedAt,
    ModifiedAt
}

public sealed class GetTenantsQueryValidator : AbstractValidator<GetTenantsQuery>
{
    public GetTenantsQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100).WithMessage("Search must be no longer than 100 characters.");
        RuleFor(x => x.Plans.Length).LessThanOrEqualTo(10).WithMessage("Plans filter must contain no more than 10 values.");
        RuleFor(x => x.Statuses.Length).LessThanOrEqualTo(10).WithMessage("Statuses filter must contain no more than 10 values.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetTenantsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantsQuery, Result<TenantsResponse>>
{
    public async Task<Result<TenantsResponse>> Handle(GetTenantsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantsAsync(query, cancellationToken);
    }
}
