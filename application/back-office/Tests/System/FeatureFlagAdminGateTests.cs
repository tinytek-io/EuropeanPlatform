using System.Net;
using FluentAssertions;
using Xunit;

namespace BackOffice.Tests.System;

// Verifies the admin gate on the BFF's mutating routes: the SPA-facing AdminPolicy must reject
// non-admin staff before any internal API call is made, while an admin's command is forwarded
// with the delegation token attached.
public sealed class FeatureFlagAdminGateTests(BackOfficeWebApplicationFactory factory)
    : BackOfficeEndpointBaseTest(factory), IClassFixture<BackOfficeWebApplicationFactory>
{
    [Fact]
    public async Task ActivateFeatureFlag_WhenRegularStaff_ShouldBeForbiddenWithoutForwarding()
    {
        Factory.AccountApi.Enqueue(HttpStatusCode.NoContent);
        using var client = CreateStaffClient(UserIdentity);

        var response = await client.PutAsync("/api/back-office/feature-flags/new-ui/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Factory.AccountApi.Requests.Should().BeEmpty("a rejected admin command must never reach account's internal API");
    }

    [Fact]
    public async Task ActivateFeatureFlag_WhenAdmin_ShouldForwardCommandWithDelegationToken()
    {
        Factory.AccountApi.Enqueue(HttpStatusCode.NoContent);
        using var client = CreateStaffClient(AdminIdentity);

        var response = await client.PutAsync("/api/back-office/feature-flags/new-ui/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var forwarded = Factory.AccountApi.SingleRequest();
        forwarded.Request.Method.Should().Be(HttpMethod.Put);
        forwarded.Request.RequestUri!.AbsolutePath.Should().Be("/internal-api/back-office/feature-flags/new-ui/activate");
        forwarded.Request.Headers.Authorization.Should().NotBeNull();
        forwarded.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        forwarded.Request.Headers.Authorization.Parameter.Should().NotBeNullOrEmpty();
    }
}
