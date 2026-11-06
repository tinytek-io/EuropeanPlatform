using BackOffice.Features.Shared;
using BackOffice.Integrations.Account;
using FluentValidation;
using JetBrains.Annotations;
using SharedKernel.Authentication.TokenGeneration;
using SharedKernel.Cqrs;
using SharedKernel.Domain;

namespace BackOffice.Features.Users;

// Mirrors account's GetBackOfficeUserSessions contract (see the dashboard queries for the mirror
// rules). The Id rides outside the API contract: the route template carries it.
[PublicAPI]
public sealed record GetBackOfficeUserSessionsQuery(int PageOffset = 0, int PageSize = 25) : IRequest<Result<BackOfficeUserSessionsResponse>>
{
    [JsonIgnore] // Removes from API contract
    public UserId Id { get; init; } = null!;
}

[PublicAPI]
public sealed record BackOfficeUserSessionsResponse(int TotalCount, int PageSize, int TotalPages, int CurrentPageOffset, BackOfficeUserSession[] Sessions);

[PublicAPI]
public sealed record BackOfficeUserSession(
    SessionId Id,
    TenantId TenantId,
    string TenantName,
    string? TenantLogoUrl,
    LoginMethod LoginMethod,
    DeviceType DeviceType,
    string UserAgent,
    string IpAddress,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActiveAt,
    DateTimeOffset? RevokedAt,
    SessionRevokedReason? RevokedReason,
    DateTimeOffset ExpiresAt
);

public sealed class GetBackOfficeUserSessionsQueryValidator : AbstractValidator<GetBackOfficeUserSessionsQuery>
{
    public GetBackOfficeUserSessionsQueryValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100.");
        RuleFor(x => x.PageOffset).GreaterThanOrEqualTo(0).WithMessage("Page offset must be greater than or equal to 0.");
    }
}

public sealed class GetBackOfficeUserSessionsHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBackOfficeUserSessionsQuery, Result<BackOfficeUserSessionsResponse>>
{
    public async Task<Result<BackOfficeUserSessionsResponse>> Handle(GetBackOfficeUserSessionsQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBackOfficeUserSessionsAsync(query, cancellationToken);
    }
}

// Mirrors account's GetBackOfficeUserLoginHistory contract.
[PublicAPI]
public sealed record GetBackOfficeUserLoginHistoryQuery : IRequest<Result<BackOfficeUserLoginHistoryResponse>>
{
    [JsonIgnore] // Removes from API contract
    public UserId Id { get; init; } = null!;
}

[PublicAPI]
public sealed record BackOfficeUserLoginHistoryResponse(BackOfficeUserLoginEntry[] Entries);

[PublicAPI]
public sealed record BackOfficeUserLoginEntry(
    LoginEventKind Kind,
    LoginMethod Method,
    LoginEventOutcome Outcome,
    DateTimeOffset OccurredAt,
    string? FailureReason,
    ExternalProviderType? ExternalProvider
);

public sealed class GetBackOfficeUserLoginHistoryHandler(AccountInternalApiClient accountInternalApiClient)
    : IRequestHandler<GetBackOfficeUserLoginHistoryQuery, Result<BackOfficeUserLoginHistoryResponse>>
{
    public async Task<Result<BackOfficeUserLoginHistoryResponse>> Handle(GetBackOfficeUserLoginHistoryQuery query, CancellationToken cancellationToken)
    {
        return await accountInternalApiClient.GetBackOfficeUserLoginHistoryAsync(query, cancellationToken);
    }
}
