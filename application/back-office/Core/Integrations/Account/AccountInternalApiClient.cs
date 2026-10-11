using System.Net.Http.Json;
using BackOffice.Features.Dashboard;
using SharedKernel.Configuration;

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
    public async Task<BackOfficeDashboardKpisResponse> GetDashboardKpisAsync(DashboardTrendPeriod period, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"/internal-api/back-office/dashboard/kpis?period={period}", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<BackOfficeDashboardKpisResponse>(SharedDependencyConfiguration.DefaultJsonSerializerOptions, cancellationToken)
               ?? throw new InvalidOperationException("Account's internal API returned an empty dashboard KPIs response.");
    }
}
