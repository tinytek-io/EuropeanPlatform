using System.Collections;
using System.Net.Http.Json;
using System.Text.Json;
using BackOffice.Features.BillingDrift;
using BackOffice.Features.BillingEvents;
using BackOffice.Features.Dashboard;
using BackOffice.Features.FeatureFlags;
using BackOffice.Features.Invoices;
using BackOffice.Features.Tenants;
using BackOffice.Features.Users;
using SharedKernel.Configuration;
using SharedKernel.Cqrs;
using SharedKernel.Domain;
using SharedKernel.Validation;

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
    public async Task<Result<BackOfficeDashboardKpisResponse>> GetDashboardKpisAsync(GetDashboardKpisQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardKpisResponse>("/dashboard/kpis", cancellationToken,
            ("period", query.Period)
        );
    }

    public async Task<Result<BackOfficeDashboardTrendsResponse>> GetDashboardTrendsAsync(GetDashboardTrendsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardTrendsResponse>("/dashboard/trends", cancellationToken,
            ("metric", query.Metric),
            ("period", query.Period)
        );
    }

    public async Task<Result<BackOfficeDashboardMrrTrendResponse>> GetDashboardMrrTrendAsync(GetDashboardMrrTrendQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardMrrTrendResponse>("/dashboard/mrr-trend", cancellationToken,
            ("period", query.Period)
        );
    }

    public async Task<Result<BackOfficeDashboardRevenueTrendResponse>> GetDashboardRevenueTrendAsync(GetDashboardRevenueTrendQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRevenueTrendResponse>("/dashboard/revenue-trend", cancellationToken,
            ("period", query.Period)
        );
    }

    public async Task<Result<BackOfficeDashboardPlanDistributionResponse>> GetDashboardPlanDistributionAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardPlanDistributionResponse>("/dashboard/plan-distribution", cancellationToken);
    }

    public async Task<Result<BackOfficeDashboardRecentSignupsResponse>> GetDashboardRecentSignupsAsync(GetDashboardRecentSignupsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentSignupsResponse>("/dashboard/recent-signups", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<Result<BackOfficeDashboardRecentLoginsResponse>> GetDashboardRecentLoginsAsync(GetDashboardRecentLoginsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentLoginsResponse>("/dashboard/recent-logins", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<Result<BackOfficeDashboardRecentPaymentsResponse>> GetDashboardRecentPaymentsAsync(GetDashboardRecentPaymentsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentPaymentsResponse>("/dashboard/recent-payments", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<Result<BackOfficeDashboardRecentStripeEventsResponse>> GetDashboardRecentStripeEventsAsync(GetDashboardRecentStripeEventsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeDashboardRecentStripeEventsResponse>("/dashboard/recent-stripe-events", cancellationToken,
            ("limit", query.Limit)
        );
    }

    public async Task<Result<BillingEventsResponse>> GetBackOfficeBillingEventsAsync(GetBackOfficeBillingEventsQuery query, CancellationToken cancellationToken)
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

    public async Task<Result<BackOfficeInvoicesResponse>> GetBackOfficeInvoicesAsync(GetBackOfficeInvoicesQuery query, CancellationToken cancellationToken)
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

    public async Task<Result<BillingDriftSummaryResponse>> GetBillingDriftSummaryAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<BillingDriftSummaryResponse>("/billing-drift/summary", cancellationToken);
    }

    public async Task<Result<UnsyncedSubscriptionsSummaryResponse>> GetUnsyncedSubscriptionsSummaryAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<UnsyncedSubscriptionsSummaryResponse>("/billing-drift/unsynced-summary", cancellationToken);
    }

    public async Task<Result<DashboardMrrConsistencySummaryResponse>> GetDashboardMrrConsistencySummaryAsync(CancellationToken cancellationToken)
    {
        return await GetAsync<DashboardMrrConsistencySummaryResponse>("/billing-drift/mrr-consistency-summary", cancellationToken);
    }

    public async Task<Result<BackOfficeUsersResponse>> GetBackOfficeUsersAsync(GetBackOfficeUsersQuery query, CancellationToken cancellationToken)
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

    public async Task<Result<BackOfficeUserDetailResponse>> GetBackOfficeUserDetailAsync(UserId id, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUserDetailResponse>($"/users/{id.Value}", cancellationToken);
    }

    public async Task<Result<BackOfficeUserSessionsResponse>> GetBackOfficeUserSessionsAsync(GetBackOfficeUserSessionsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUserSessionsResponse>($"/users/{query.Id.Value}/sessions", cancellationToken,
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<Result<BackOfficeUserLoginHistoryResponse>> GetBackOfficeUserLoginHistoryAsync(GetBackOfficeUserLoginHistoryQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<BackOfficeUserLoginHistoryResponse>($"/users/{query.Id.Value}/login-history", cancellationToken);
    }

    public async Task<Result<GetUserFeatureFlagsResponse>> GetUserFeatureFlagsAsync(GetUserFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<GetUserFeatureFlagsResponse>($"/users/{query.UserId.Value}/feature-flags", cancellationToken);
    }

    public async Task<Result<TenantsResponse>> GetTenantsAsync(GetTenantsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<TenantsResponse>("/tenants", cancellationToken,
            ("search", query.Search),
            ("plans", query.Plans),
            ("statuses", query.Statuses),
            ("unsynced", query.Unsynced),
            ("driftDetected", query.DriftDetected),
            ("orderBy", query.OrderBy),
            ("sortOrder", query.SortOrder),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<Result<TenantDetailResponse>> GetTenantDetailAsync(TenantId id, CancellationToken cancellationToken)
    {
        return await GetAsync<TenantDetailResponse>($"/tenants/{id.Value}", cancellationToken);
    }

    public async Task<Result<TenantUserCountsResponse>> GetTenantUserCountsAsync(TenantId id, CancellationToken cancellationToken)
    {
        return await GetAsync<TenantUserCountsResponse>($"/tenants/{id.Value}/user-counts", cancellationToken);
    }

    public async Task<Result<TenantUsersResponse>> GetTenantUsersAsync(GetTenantUsersQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<TenantUsersResponse>($"/tenants/{query.Id.Value}/users", cancellationToken,
            ("search", query.Search),
            ("roles", query.Roles),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<Result<TenantActivityResponse>> GetTenantActivityAsync(TenantId id, CancellationToken cancellationToken)
    {
        return await GetAsync<TenantActivityResponse>($"/tenants/{id.Value}/activity", cancellationToken);
    }

    public async Task<Result<TenantPaymentHistoryResponse>> GetTenantPaymentHistoryAsync(GetTenantPaymentHistoryQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<TenantPaymentHistoryResponse>($"/tenants/{query.Id.Value}/payment-history", cancellationToken,
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<Result<GetTenantFeatureFlagsResponse>> GetTenantFeatureFlagsAsync(GetTenantFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<GetTenantFeatureFlagsResponse>($"/tenants/{query.TenantId.Value}/feature-flags", cancellationToken);
    }

    public async Task<Result> SetUserAbInclusionPinAsync(SetUserAbInclusionPinCommand command, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/users/{command.UserId.Value}/ab-inclusion-pin", command, cancellationToken);
    }

    public async Task<Result> SetTenantAbInclusionPinAsync(SetTenantAbInclusionPinCommand command, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/tenants/{command.TenantId.Value}/ab-inclusion-pin", command, cancellationToken);
    }

    public async Task<Result> AcknowledgeBillingDriftAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Post, $"/tenants/{tenantId.Value}/drift/acknowledge", null, cancellationToken);
    }

    public async Task<Result<ReconcileTenantWithStripeResponse>> ReconcileTenantWithStripeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        return await SendAsync<ReconcileTenantWithStripeResponse>(HttpMethod.Post, $"/tenants/{tenantId.Value}/reconcile-with-stripe", null, cancellationToken);
    }

    public async Task<Result<ReplayArchivedTenantStripeEventsResponse>> ReplayArchivedTenantStripeEventsAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        return await SendAsync<ReplayArchivedTenantStripeEventsResponse>(HttpMethod.Post, $"/tenants/{tenantId.Value}/replay-archived-stripe-events", null, cancellationToken);
    }

    public async Task<Result<GetFeatureFlagsResponse>> GetFeatureFlagsAsync(GetFeatureFlagsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<GetFeatureFlagsResponse>("/feature-flags", cancellationToken,
            ("includeDeleted", query.IncludeDeleted)
        );
    }

    public async Task<Result<GetFeatureFlagTenantsResponse>> GetFeatureFlagTenantsAsync(GetFeatureFlagTenantsQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<GetFeatureFlagTenantsResponse>($"/feature-flags/{Uri.EscapeDataString(query.FlagKey)}/tenants", cancellationToken,
            ("search", query.Search),
            ("plans", query.Plans),
            ("state", query.State),
            ("hasOverride", query.HasOverride),
            ("orderBy", query.OrderBy),
            ("sortOrder", query.SortOrder),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<Result<GetFeatureFlagUsersResponse>> GetFeatureFlagUsersAsync(GetFeatureFlagUsersQuery query, CancellationToken cancellationToken)
    {
        return await GetAsync<GetFeatureFlagUsersResponse>($"/feature-flags/{Uri.EscapeDataString(query.FlagKey)}/users", cancellationToken,
            ("search", query.Search),
            ("roles", query.Roles),
            ("state", query.State),
            ("hasOverride", query.HasOverride),
            ("orderBy", query.OrderBy),
            ("sortOrder", query.SortOrder),
            ("pageOffset", query.PageOffset),
            ("pageSize", query.PageSize)
        );
    }

    public async Task<Result> ActivateFeatureFlagAsync(string flagKey, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/feature-flags/{Uri.EscapeDataString(flagKey)}/activate", null, cancellationToken);
    }

    public async Task<Result> DeactivateFeatureFlagAsync(string flagKey, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/feature-flags/{Uri.EscapeDataString(flagKey)}/deactivate", null, cancellationToken);
    }

    public async Task<Result> DeleteFeatureFlagAsync(string flagKey, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Delete, $"/feature-flags/{Uri.EscapeDataString(flagKey)}", null, cancellationToken);
    }

    public async Task<Result> SetTenantFeatureFlagAsync(SetTenantFeatureFlagInternalCommand command, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/feature-flags/{Uri.EscapeDataString(command.FlagKey)}/tenant-override", command, cancellationToken);
    }

    public async Task<Result> SetFeatureFlagRolloutPercentageAsync(SetFeatureFlagRolloutPercentageCommand command, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/feature-flags/{Uri.EscapeDataString(command.FlagKey)}/rollout-percentage", command, cancellationToken);
    }

    public async Task<Result> RemoveTenantFeatureFlagOverrideAsync(string flagKey, TenantId tenantId, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Delete, $"/feature-flags/{Uri.EscapeDataString(flagKey)}/tenant-override", null, cancellationToken,
            ("tenantId", tenantId.Value)
        );
    }

    public async Task<Result> SetUserFeatureFlagAsync(SetUserFeatureFlagInternalCommand command, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Put, $"/feature-flags/{Uri.EscapeDataString(command.FlagKey)}/user-override", command, cancellationToken);
    }

    public async Task<Result> RemoveUserFeatureFlagOverrideAsync(string flagKey, UserId userId, TenantId tenantId, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Delete, $"/feature-flags/{Uri.EscapeDataString(flagKey)}/user-override", null, cancellationToken,
            ("userId", userId.Value),
            ("tenantId", tenantId.Value)
        );
    }

    private async Task<Result<T>> GetAsync<T>(string path, CancellationToken cancellationToken, params (string Key, object? Value)[] parameters)
    {
        // The paths are relative to the internal back-office route prefix; the client's BaseAddress
        // is the account API root (the in-cluster Service in deployed environments, localhost in
        // development).
        var response = await httpClient.GetAsync(BuildUri($"/internal-api/back-office{path}", parameters), cancellationToken);
        return await ToResultAsync<T>(response, HttpMethod.Get, path, cancellationToken);
    }

    // Commands: same contract rules as GetAsync (internal route prefix, diagnosable failures).
    // Result-only commands succeed with an empty body; response-carrying commands deserialize it.
    // The body serializes with the shared options so [JsonIgnore] contract members (the route-
    // carried ids) stay out of the payload, exactly like account's SPA-facing routes.
    private async Task<Result> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var response = await SendInternalAsync(method, path, body, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return Result.Success();
        }

        return await ToErrorResultAsync(response, method, path, cancellationToken);
    }

    // Same as SendAsync, with query parameters (e.g. the DELETE-with-ids routes).
    private async Task<Result> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, params (string Key, object? Value)[] parameters)
    {
        return await SendAsync(method, BuildUri(path, parameters), body, cancellationToken);
    }

    private async Task<Result<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var response = await SendInternalAsync(method, path, body, cancellationToken);
        return await ToResultAsync<T>(response, method, path, cancellationToken);
    }

    private async Task<Result<T>> ToResultAsync<T>(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return await ToErrorResultAsync<T>(response, method, path, cancellationToken);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(SharedDependencyConfiguration.DefaultJsonSerializerOptions, cancellationToken)
                    ?? throw new InvalidOperationException($"Account's internal API returned an empty {typeof(T).Name} response.");

        return value; // Implicit conversion to a successful Result<T>.
    }

    // Account's domain failures arrive as ProblemDetails (ApiResult's error shape — the same shape
    // this system's endpoints emit). They map one-to-one onto Result failures so the BFF re-emits
    // account's status code, message, and validation errors verbatim instead of masking a domain
    // 400 as a 500. Non-ProblemDetails bodies (proxy errors, empty bodies) fail loudly instead.
    private async Task<Result> ToErrorResultAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var problem = await TryReadProblemDetailsAsync(response, method, path, cancellationToken);
        return new Result(response.StatusCode, ToErrorMessage(problem), false, ToErrorDetails(problem));
    }

    private async Task<Result<T>> ToErrorResultAsync<T>(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var problem = await TryReadProblemDetailsAsync(response, method, path, cancellationToken);
        return new Result<T>(response.StatusCode, ToErrorMessage(problem), false, ToErrorDetails(problem));
    }

    private static ErrorMessage? ToErrorMessage(UpstreamProblemDetails problem)
    {
        return problem.Detail is null ? null : new ErrorMessage(problem.Detail);
    }

    private static ErrorDetail[] ToErrorDetails(UpstreamProblemDetails problem)
    {
        return problem.Errors?
            .SelectMany(error => error.Value.Select(message => new ErrorDetail(error.Key, message)))
            .ToArray() ?? [];
    }

    private async Task<UpstreamProblemDetails> TryReadProblemDetailsAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            var problem = JsonSerializer.Deserialize<UpstreamProblemDetails>(body, SharedDependencyConfiguration.DefaultJsonSerializerOptions);
            if (problem is not null && problem.Status == (int)response.StatusCode)
            {
                return problem;
            }
        }
        catch (JsonException)
        {
            // Fall through to the loud failure below.
        }

        throw new HttpRequestException(
            $"Account's internal API responded {(int)response.StatusCode} for {method} {path}: {body}",
            null,
            response.StatusCode
        );
    }

    private async Task<HttpResponseMessage> SendInternalAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"/internal-api/back-office{path}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SharedDependencyConfiguration.DefaultJsonSerializerOptions);
        }

        return await httpClient.SendAsync(request, cancellationToken);
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

    // The ProblemDetails shape account's internal API emits for failures (ApiResult.GetProblemDetailsAsJson).
    private sealed record UpstreamProblemDetails(
        int? Status,
        string? Detail,
        Dictionary<string, string[]>? Errors
    );
}
