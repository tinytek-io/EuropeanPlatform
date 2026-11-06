using BackOffice.Features.Invoices;
using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.Tenants;

// Mirrors account's per-tenant read contracts (see the dashboard queries for the mirror rules):
// user counts, tenant users page, activity timeline, and payment history.
[PublicAPI]
public sealed record GetTenantUserCountsQuery(TenantId Id) : IRequest<Result<TenantUserCountsResponse>>;

[PublicAPI]
public sealed record TenantUserCountsResponse(int TotalUsers, int ActiveUsers, int PendingUsers);

public sealed class GetTenantUserCountsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantUserCountsQuery, Result<TenantUserCountsResponse>>
{
    public async Task<Result<TenantUserCountsResponse>> Handle(GetTenantUserCountsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantUserCountsAsync(query.Id, cancellationToken);
    }
}

[PublicAPI]
public sealed record GetTenantUsersQuery(
    string? Search = null,
    UserRole[]? Roles = null,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<TenantUsersResponse>>
{
    [JsonIgnore] // Removes from API contract
    public TenantId Id { get; init; } = null!;

    public string? Search { get; } = Search?.Trim().ToLower();

    public UserRole[] Roles { get; } = Roles ?? [];
}

[PublicAPI]
public sealed record TenantUsersResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, TenantUserSummary[] Users);

[PublicAPI]
public sealed record TenantUserSummary(
    UserId Id,
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

public sealed class GetTenantUsersQueryValidator : AbstractValidator<GetTenantUsersQuery>
{
    public GetTenantUsersQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100).WithMessage("Search must be no longer than 100 characters.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetTenantUsersHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantUsersQuery, Result<TenantUsersResponse>>
{
    public async Task<Result<TenantUsersResponse>> Handle(GetTenantUsersQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantUsersAsync(query, cancellationToken);
    }
}

[PublicAPI]
public sealed record GetTenantActivityQuery(TenantId Id) : IRequest<Result<TenantActivityResponse>>;

[PublicAPI]
public sealed record TenantActivityResponse(TenantActivityEvent[] Events);

[PublicAPI]
public sealed record TenantActivityEvent(DateTimeOffset Timestamp, string Name, string? Description);

public sealed class GetTenantActivityHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantActivityQuery, Result<TenantActivityResponse>>
{
    public async Task<Result<TenantActivityResponse>> Handle(GetTenantActivityQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantActivityAsync(query.Id, cancellationToken);
    }
}

[PublicAPI]
public sealed record GetTenantPaymentHistoryQuery(int PageOffset = 0, int PageSize = 25) : IRequest<Result<TenantPaymentHistoryResponse>>
{
    [JsonIgnore] // Removes from API contract
    public TenantId Id { get; init; } = null!;
}

[PublicAPI]
public sealed record TenantPaymentHistoryResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, TenantPaymentTransaction[] Transactions);

[PublicAPI]
public sealed record TenantPaymentTransaction(
    PaymentTransactionId Id,
    BackOfficeInvoiceRowKind RowKind,
    decimal Amount,
    decimal AmountExcludingTax,
    decimal TaxAmount,
    string Currency,
    PaymentTransactionStatus Status,
    DateTimeOffset Date,
    DateTimeOffset? RefundedAt,
    string? FailureReason,
    string? InvoiceUrl,
    string? CreditNoteUrl,
    DateTimeOffset? CreditNotedAt,
    SubscriptionPlan? Plan
);

public sealed class GetTenantPaymentHistoryQueryValidator : AbstractValidator<GetTenantPaymentHistoryQuery>
{
    public GetTenantPaymentHistoryQueryValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetTenantPaymentHistoryHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetTenantPaymentHistoryQuery, Result<TenantPaymentHistoryResponse>>
{
    public async Task<Result<TenantPaymentHistoryResponse>> Handle(GetTenantPaymentHistoryQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetTenantPaymentHistoryAsync(query, cancellationToken);
    }
}
