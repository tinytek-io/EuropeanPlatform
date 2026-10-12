using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.Persistence;

namespace BackOffice.Features.Users;

// Mirrors account's GetBackOfficeUsers contract (see the dashboard queries for the mirror rules),
// including the query's normalization (Search is trimmed and lowercased).
[PublicAPI]
public sealed record GetBackOfficeUsersQuery(
    string? Search = null,
    UserRole[]? Roles = null,
    UserActivityFilter? Activity = null,
    SortableBackOfficeUserProperties OrderBy = SortableBackOfficeUserProperties.LastSeenAt,
    SortOrder SortOrder = SortOrder.Descending,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<BackOfficeUsersResponse>>
{
    public string? Search { get; } = Search?.Trim().ToLower();

    public UserRole[] Roles { get; } = Roles ?? [];
}

[PublicAPI]
public sealed record BackOfficeUsersResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, BackOfficeUserSummary[] Users);

[PublicAPI]
public sealed record BackOfficeUserSummary(
    UserId Id,
    TenantId TenantId,
    string TenantName,
    SubscriptionPlan TenantPlan,
    PlannedSubscriptionChange? TenantPlannedChange,
    bool TenantHasEverSubscribed,
    string Email,
    string? FirstName,
    string? LastName,
    string? Title,
    UserRole Role,
    bool EmailConfirmed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    string? AvatarUrl
);

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserActivityFilter
{
    ActiveLast24Hours,
    ActiveLast7Days,
    ActiveLast30Days,
    InactiveOver30Days
}

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortableBackOfficeUserProperties
{
    Name,
    Email,
    Role,
    LastSeenAt,
    CreatedAt
}

public sealed class GetBackOfficeUsersQueryValidator : AbstractValidator<GetBackOfficeUsersQuery>
{
    public GetBackOfficeUsersQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100).WithMessage("Search must be at most 100 characters.");
        RuleFor(x => x.Roles.Length).LessThanOrEqualTo(10).WithMessage("Roles filter must contain no more than 10 values.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetBackOfficeUsersHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBackOfficeUsersQuery, Result<BackOfficeUsersResponse>>
{
    public async Task<Result<BackOfficeUsersResponse>> Handle(GetBackOfficeUsersQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBackOfficeUsersAsync(query, cancellationToken);
    }
}
