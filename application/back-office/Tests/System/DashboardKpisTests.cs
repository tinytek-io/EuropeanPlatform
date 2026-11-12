using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BackOffice.Tests.System;

// Exercises the dashboard KPIs route through the full BFF pipeline: the staff identity scheme,
// the SPA-facing authorization policy, the delegation handler's token minting, and account's
// internal API client (with its transport stubbed) — including error propagation from account's
// ProblemDetails onto the staff-facing response.
public sealed class DashboardKpisTests(BackOfficeWebApplicationFactory factory)
    : BackOfficeEndpointBaseTest(factory), IClassFixture<BackOfficeWebApplicationFactory>
{
    private const string KpisJson =
        """
        {"period":"Last30Days","totalTenants":3,"activeTenants":2,"trialTenants":1,"canceledTenants":0,
        "newTenantsInPeriod":1,"newTenantsDeltaPercent":50,"totalUsers":7,"activeUsersInPeriod":5,
        "blendedMonthlyRecurringRevenue":123.45,"blendedMonthlyRecurringRevenueDeltaPercent":10.0,
        "totalRevenue":999.99,"currency":"EUR","activeSessionsLast24Hours":4}
        """;

    [Fact]
    public async Task GetKpis_WhenAnonymous_ShouldReturnUnauthorizedWithoutForwarding()
    {
        using var client = CreateAnonymousClient();

        var response = await client.GetAsync("/api/back-office/dashboard/kpis");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Factory.AccountApi.Requests.Should().BeEmpty("an unauthenticated request must never reach account's internal API");
    }

    [Fact]
    public async Task GetKpis_WhenStaff_ShouldForwardWithDelegationTokenAndMapResponse()
    {
        Factory.AccountApi.Enqueue(HttpStatusCode.OK, KpisJson);
        using var client = CreateStaffClient(UserIdentity);

        var response = await client.GetAsync("/api/back-office/dashboard/kpis?period=Last30Days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var forwarded = Factory.AccountApi.SingleRequest();
        forwarded.Request.Method.Should().Be(HttpMethod.Get);
        forwarded.Request.RequestUri!.AbsolutePath.Should().Be("/internal-api/back-office/dashboard/kpis");
        forwarded.Request.RequestUri.Query.Should().Be("?period=Last30Days");
        forwarded.Request.Headers.Authorization.Should().NotBeNull("every internal API call carries the delegation token");
        forwarded.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        forwarded.Request.Headers.Authorization.Parameter.Should().NotBeNullOrEmpty();

        await AssertKpisAsync(response);
    }

    [Fact]
    public async Task GetKpis_WhenAccountRejects_ShouldPropagateStatusAndProblemDetails()
    {
        Factory.AccountApi.EnqueueProblem(HttpStatusCode.BadRequest, "Not allowed", ("FeatureFlag", "Unknown"));
        using var client = CreateStaffClient(UserIdentity);

        var response = await client.GetAsync("/api/back-office/dashboard/kpis");

        // The BFF re-emits account's status code and problem verbatim instead of masking a domain
        // 400 as a 500.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Be("Not allowed");
        // ApiResult camelCases the error property names when re-emitting them.
        body.GetProperty("errors").GetProperty("featureFlag")[0].GetString().Should().Be("Unknown");
    }

    private static async Task AssertKpisAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("period").GetString().Should().Be("Last30Days");
        body.GetProperty("totalTenants").GetInt64().Should().Be(3);
        body.GetProperty("currency").GetString().Should().Be("EUR");
    }
}
