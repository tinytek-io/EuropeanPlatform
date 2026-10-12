using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BackOffice.Tests.System;

// /api/back-office/me is the one route that resolves entirely from the staff identity (no
// upstream call): it proves the staff execution context agrees with the BackOfficeAdmin policy —
// the UI's admin state and what the policy actually grant must match.
public sealed class MeTests(BackOfficeWebApplicationFactory factory)
    : BackOfficeEndpointBaseTest(factory), IClassFixture<BackOfficeWebApplicationFactory>
{
    [Fact]
    public async Task GetMe_WhenAdmin_ShouldReturnAdminProfile()
    {
        using var client = CreateStaffClient(AdminIdentity);

        var response = await client.GetAsync("/api/back-office/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("displayName").GetString().Should().Be("Admin");
        body.GetProperty("email").GetString().Should().Be("admin@dev.localhost");
        body.GetProperty("isAdmin").GetBoolean().Should().BeTrue();
        body.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).Should().BeEquivalentTo("BackOfficeAdmins");
        Factory.AccountApi.Requests.Should().BeEmpty("the staff profile resolves locally from the signed-in identity");
    }

    [Fact]
    public async Task GetMe_WhenRegularStaff_ShouldReturnNonAdminProfile()
    {
        using var client = CreateStaffClient(UserIdentity);

        var response = await client.GetAsync("/api/back-office/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("displayName").GetString().Should().Be("User");
        body.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
        body.GetProperty("groups").EnumerateArray().Should().BeEmpty();
    }
}
