using System.Collections;
using System.Net.Http.Json;
using BackOffice.Features.BillingDrift;
using BackOffice.Features.BillingEvents;
using BackOffice.Features.Dashboard;
using BackOffice.Features.Invoices;
using BackOffice.Features.Users;
using SharedKernel.Configuration;
using SharedKernel.Domain;

namespace BackOffice.Integrations.Account;

/// <summary>
///     Typed client for account's internal API (/internal-api/back-office/*). Every call carries the
///     staff delegation token (see BackOfficeDelegationHandler) and is validated against the
///     internal CA. The response contract mirrors account's; account owns the data, the back-office
///     owns the staff-facing contract. The shared serializer options keep the string-enum contract
///     identical on both sides.
/// </summary>
public sealed class AccountInternalApiClient(HttpClient httpClient)
{
    public async Task<BackOfficeDashboardKpisResponse> GetDashboardKpisAsync(GetDashboardKpisQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardKpisResponse>("/dashboard/kpis", cancellationToken,
            ("period", query.Period)
        );
    }

    public async Task<BackOfficeDashboardTrendsResponse> GetDashboardTrendsAsync(GetDashboardTrendsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardTrendsResponse>("/dashboard/trends", cancellationToken,
            ("metric", query.Metric),
            ("period", query.Period)
        );
    }

    public async Task<BackOfficeDashboardMrrTrendResponse> GetDashboardMrrTrendAsync(GetDashboardMrrTrendQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardMrrTrendResponse>("/dashboard/mrr-trend", cancellationToken,
            ("period", query.Period)
        );
    }

    public async Task<BackOfficeDashboardRevenueTrendResponse> GetDashboardRevenueTrendAsync(GetDashboardRevenueTrendQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRevenueTrendResponse>("/dashboard/revenue-trend", cancellationToken,
            ("period", query.Period)
        );
    }

    public async Task<BackOfficeDashboardPlanDistributionResponse> GetDashboardPlanDistributionAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardPlanDistributionResponse>("/dashboard/plan-distribution", cancellationToken);
    }

    public async Task<BackOfficeDashboardRecentSignupsResponse> GetDashboardRecentSignupsAsync(GetDashboardRecentSignupsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentSignupsResponse>("/dashboard/recent-signups", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<BackOfficeDashboardRecentLoginsResponse> GetDashboardRecentLoginsAsync(GetDashboardRecentLoginsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentLoginsResponse>("/dashboard/recent-logins", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<BackOfficeDashboardRecentPaymentsResponse> GetDashboardRecentPaymentsAsync(GetDashboardRecentPaymentsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentPaymentsResponse>("/dashboard/recent-payments", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<BackOfficeDashboardRecentStripeEventsResponse> GetDashboardRecentStripeEventsAsync(GetDashboardRecentStripeEventsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentStripeEventsResponse>("/dashboard/recent-stripe-events", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<BillingEventsResponse> GetBackOfficeBillingEventsAsync(GetBackOfficeBillingEventsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BillingEventsResponse>("/billing-events", cancellationToken,
            ("search", query.Search),
            ("eventTypes", query.EventTypes),
            ("occurredFrom", query.OccurredFrom),
            ("occurredTo", query.OccurredTo),
            ("tenantId", query.TenantId?.Value),
            ("orderBy", query.OrderBy),
            ("sortOrder", query.SortOrder),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<BackOfficeInvoicesResponse> GetBackOfficeInvoicesAsync(GetBackOfficeInvoicesQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeInvoicesResponse>("/invoices", cancellationToken,
            ("search", query.Search),
            ("statuses", query.Statuses),
            ("orderBy", query.OrderBy),
            ("sortOrder", query.SortOrder),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<BillingDriftSummaryResponse> GetBillingDriftSummaryAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<BillingDriftSummaryResponse>("/billing-drift/summary", cancellationToken);
    }

    public async Task<UnsyncedSubscriptionsSummaryResponse> GetUnsyncedSubscriptionsSummaryAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<UnsyncedSubscriptionsSummaryResponse>("/billing-drift/unsynced-summary", cancellationToken);
    }

    public async Task<DashboardMrrConsistencySummaryResponse> GetDashboardMrrConsistencySummaryAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<DashboardMrrConsistencySummaryResponse>("/billing-drift/mrr-consistency-summary", cancellationToken);
    }

    public async Task<BackOfficeUsersResponse> GetBackOfficeUsersAsync(GetBackOfficeUsersQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUsersResponse>("/users", cancellationToken,
            ("search", query.Search),
            ("roles", query.Roles),
            ("activity", query.Activity),
            ("orderBy", query.OrderBy),
            ("sortOrder", query.SortOrder),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<BackOfficeUserDetailResponse> GetBackOfficeUserDetailAsync(UserId id, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUserDetailResponse>($"/users/{id.Value}", cancellationToken);
    }

    public async Task<BackOfficeUserSessionsResponse> GetBackOfficeUserSessionsAsync(GetBackOfficeUserSessionsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUserSessionsResponse>($"/users/{query.Id.Value}/sessions", cancellationToken,
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<BackOfficeUserLoginHistoryResponse> GetBackOfficeUserLoginHistoryAsync(GetBackOfficeUserLoginHistoryQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUserLoginHistoryResponse>($"/users/{query.Id.Value}/login-history", cancellationToken);
    }

    public async Task<GetUserFeatureFlagsResponse> GetUserFeatureFlagsAsync(GetUserFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<GetUserFeatureFlagsResponse>($"/users/{query.UserId.Value}/feature-flags", cancellationToken);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken, params (string Key, object? Value)[] parameters)
    {
        // The paths are relative to the internal back-office route prefix; the client's BaseAddress
        // is the account API root (the in-cluster Service in deployed environments, localhost in
        // development).
        var response = await httpClient.GetAsync(BuildUri($"/internal-api/back-office{path}", parameters), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Fail loud with the upstream status and body (ProblemDetails) so the exception in
            // logs/Cockpit pinpoints the internal call that failed, not just a status code.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Account's internal API responded {(int)response.StatusCode} for GET {path}: {body}",
                null,
                response.StatusCode
            );
        }

        return await response.Content.ReadFromJsonAsync<T>(SharedDependencyConfiguration.DefaultJsonSerializerOptions, cancellationToken)
               ?? throw new InvalidOperationException($"Account's internal API returned an empty {typeof(T).Name} response.");
    }

    // Query building mirrors what the SPA-facing endpoints accept: nulls drop out, enums travel as
    // their string values, arrays repeat the key, and DateTimeOffset uses the round-trip ("O")
    // format the default model binder parses.
    private static string BuildUri(string path, (string Key, object? Value)[] parameters)
    {
        var pairs = new List<string>();
        foreach (var (key, value) in parameters)
        {
            switch (value)
            {
                case null:
                    break;
                case Enum enumValue:
                    pairs.Add($"{key}={Uri.EscapeDataString(enumValue.ToString())}");
                    break;
                case DateTimeOffset dateTimeOffset:
                    pairs.Add($"{key}={Uri.EscapeDataString(dateTimeOffset.ToString("O"))}");
                    break;
                case IEnumerable values and not string:
                    foreach (var item in values)
                    {
                        if (item is not null)
                        {
                            pairs.Add($"{key}={Uri.EscapeDataString(item.ToString()!)}");
                        }
                    }

                    break;
                default:
                    pairs.Add($"{key}={Uri.EscapeDataString(value.ToString()!)}");
                    break;
            }
        }

        return pairs.Count == 0 ? path : $"{path}?{string.Join("&", pairs)}";
    }
}
