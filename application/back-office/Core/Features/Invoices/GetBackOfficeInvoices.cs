using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.Persistence;

namespace BackOffice.Features.Invoices;

// Mirrors account's GetBackOfficeInvoices contract (see the dashboard queries for the mirror
// rules), including the query's normalization and the row-kind semantics of the invoice ledger.
[PublicAPI]
public sealed record GetBackOfficeInvoicesQuery(
    string? Search = null,
    BackOfficeInvoiceStatusFilter[]? Statuses = null,
    SortableBackOfficeInvoiceProperties OrderBy = SortableBackOfficeInvoiceProperties.Date,
    SortOrder SortOrder = SortOrder.Descending,
    int PageOffset = 0,
    int PageSize = 25
) : IRequest<Result<BackOfficeInvoicesResponse>>
{
    public string? Search { get; } = Search?.Trim().ToLower();

    public BackOfficeInvoiceStatusFilter[] Statuses { get; } = Statuses ?? [];
}

[PublicAPI]
public sealed record BackOfficeInvoicesResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, BackOfficeInvoiceSummary[] Invoices);

[PublicAPI]
public sealed record BackOfficeInvoiceSummary(
    PaymentTransactionId Id,
    BackOfficeInvoiceRowKind RowKind,
    TenantId TenantId,
    string TenantName,
    string? TenantLogoUrl,
    DateTimeOffset Date,
    SubscriptionPlan? Plan,
    decimal Amount,
    decimal AmountExcludingTax,
    decimal TaxAmount,
    string Currency,
    PaymentTransactionStatus Status,
    string? FailureReason,
    string? InvoiceUrl,
    string? CreditNoteUrl,
    DateTimeOffset? CreditNotedAt,
    DateTimeOffset? RefundedAt
);

/// <summary>
///     Each payment projects to one Invoice row (always) plus an optional reversal row — either
///     CreditNote (when a credit note was issued) or Refund (the edge case of a refund without a
///     credit note). Mirrors account's semantics.
/// </summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackOfficeInvoiceRowKind
{
    Invoice,
    CreditNote,
    Refund
}

/// <summary>
///     Filter values of the invoices toolbar. <see cref="Paid" />, <see cref="Failed" />, and
///     <see cref="Pending" /> match Invoice rows by their original payment outcome;
///     <see cref="Refunded" /> matches RowKind=Refund rows; <see cref="HasCreditNote" /> matches
///     RowKind=CreditNote rows. Mirrors account's semantics.
/// </summary>
[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackOfficeInvoiceStatusFilter
{
    Paid,
    Refunded,
    Failed,
    Pending,
    HasCreditNote
}

[PublicAPI]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortableBackOfficeInvoiceProperties
{
    Date,
    TenantName,
    Total,
    Status
}

public sealed class GetBackOfficeInvoicesQueryValidator : AbstractValidator<GetBackOfficeInvoicesQuery>
{
    public GetBackOfficeInvoicesQueryValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100).WithMessage("Search must be no longer than 100 characters.");
        RuleFor(x => x.Statuses.Length).LessThanOrEqualTo(10).WithMessage("Status filter must contain no more than 10 values.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetBackOfficeInvoicesHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBackOfficeInvoicesQuery, Result<BackOfficeInvoicesResponse>>
{
    public async Task<Result<BackOfficeInvoicesResponse>> Handle(GetBackOfficeInvoicesQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBackOfficeInvoicesAsync(query, cancellationToken);
    }
}
