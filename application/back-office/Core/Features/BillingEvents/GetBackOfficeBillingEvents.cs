using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.Persistence;

namespace BackOffice.Features.BillingEvents;

// Mirrors account's GetBackOfficeBillingEvents contract (see the dashboard queries for the
// mirror rules), including the query's normalization (Search is trimmed and lowercased) so the
// BFF validates and forwards identically.
[PublicAPI]
public sealed record GetBackOfficeBillingEventsQuery(
    string? Search = null,
    BillingEventType[]? EventTypes = null,
    DateTimeOffset? OccurredFrom = null,
    DateTimeOffset? OccurredTo = null,
    TenantId? TenantId = null,
    SortableBillingEventProperties OrderBy = SortableBillingEventProperties.OccurredAt,
    SortOrder SortOrder = SortOrder.Descending,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<BillingEventsResponse>>
{
    public string? Search { get; } = Search?.Trim().ToLower();

    public BillingEventType[] EventTypes { get; } = EventTypes ?? [];
}

[PublicAPI]
public sealed record BillingEventsResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, BillingEventSummary[] BillingEvents);

[PublicAPI]
public sealed record BillingEventSummary(
    BillingEventId Id,
    TenantId TenantId,
    string TenantName,
    string? TenantLogoUrl,
    string? Country,
    BillingEventType EventType,
    SubscriptionPlan? FromPlan,
    SubscriptionPlan? ToPlan,
    decimal? AmountDelta,
    decimal? PreviousAmount,
    decimal? NewAmount,
    decimal CommittedMrr,
    string? Currency,
    DateTimeOffset OccurredAt
);

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortableBillingEventProperties
{
    OccurredAt,
    EventType,
    TenantName
}

public sealed class GetBackOfficeBillingEventsQueryValidator : AbstractValidator<GetBackOfficeBillingEventsQuery>
{
    public GetBackOfficeBillingEventsQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100).WithMessage("Search must be no longer than 100 characters.");
        RuleFor(x => x.EventTypes.Length).LessThanOrEqualTo(25).WithMessage("Event types filter must contain no more than 25 values.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetBackOfficeBillingEventsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBackOfficeBillingEventsQuery, Result<BillingEventsResponse>>
{
    public async Task<Result<BillingEventsResponse>> Handle(GetBackOfficeBillingEventsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBackOfficeBillingEventsAsync(query, cancellationToken);
    }
}
