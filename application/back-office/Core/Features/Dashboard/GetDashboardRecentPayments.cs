using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.Dashboard;

// Mirrors account's GetDashboardRecentPayments contract (see GetDashboardTrends for the mirror
// rules). PaymentTransactionId and PaymentTransactionStatus come from the shared contract mirrors.
[PublicAPI]
public sealed record GetDashboardRecentPaymentsQuery(int Limit = 6)
    : IRequest<Result<BackOfficeDashboardRecentPaymentsResponse>>;

[PublicAPI]
public sealed record BackOfficeDashboardRecentPaymentsResponse(BackOfficeDashboardRecentPayment[] Payments);

[PublicAPI]
public sealed record BackOfficeDashboardRecentPayment(
    PaymentTransactionId Id,
    TenantId TenantId,
    string TenantName,
    string? TenantLogoUrl,
    DateTimeOffset Date,
    SubscriptionPlan? Plan,
    decimal Amount,
    string Currency,
    PaymentTransactionStatus Status
);

public sealed class GetDashboardRecentPaymentsQueryValidator : AbstractValidator<GetDashboardRecentPaymentsQuery>
{
    public GetDashboardRecentPaymentsQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).WithMessage("Limit must be between 1 and 50.");
    }
}

public sealed class GetDashboardRecentPaymentsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetDashboardRecentPaymentsQuery, Result<BackOfficeDashboardRecentPaymentsResponse>>
{
    public async Task<Result<BackOfficeDashboardRecentPaymentsResponse>> Handle(GetDashboardRecentPaymentsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetDashboardRecentPaymentsAsync(query, cancellationToken);
    }
}
