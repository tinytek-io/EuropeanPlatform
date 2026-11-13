using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using BackOffice.Authentication;
using BackOffice.Features.StaffSignIn.Commands;
using BackOffice.Tests.StaffSignIn;
using BackOffice.Tests.System;
using FluentAssertions;
using Meziantou.Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using SharedKernel.Tests;
using Xunit;

namespace BackOffice.Tests.StaffIdentity;

[DisableParallelization]
public sealed class StaffIdentityProviderTests(StaffSignInWebApplicationFactory factory) : IClassFixture<StaffSignInWebApplicationFactory>
{
    private const string Scopes = "openid profile email groups";

    [Fact]
    public async Task Discovery_WhenRequested_ShouldPublishTheProviderEndpointsAndScopes()
    {
        // Act
        var response = await CreateClient().GetAsync("/.well-known/openid-configuration");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var configuration = await response.Content.ReadFromJsonAsync<JsonElement>();
        configuration.GetProperty("authorization_endpoint").GetString().Should().EndWith("/connect/authorize");
        configuration.GetProperty("token_endpoint").GetString().Should().EndWith("/connect/token");
        configuration.GetProperty("userinfo_endpoint").GetString().Should().EndWith("/connect/userinfo");
        configuration.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString()).Should().Contain("groups");
        configuration.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(algorithm => algorithm.GetString()).Should().Contain("RS256");
    }

    [Fact]
    public async Task Authorize_WhenNotSignedIn_ShouldSendTheBrowserToTheSignInWithTheRequest()
    {
        // Act
        var response = await CreateClient().GetAsync(AuthorizeUrl("state-1"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith("/login?returnPath=");
        Uri.UnescapeDataString(location["/login?returnPath=".Length..]).Should().Be(AuthorizeUrl("state-1"));
    }

    [Fact]
    public async Task CodeFlow_WhenAllowedStaffMemberSignsIn_ShouldIssueTokensAndUserInfoForThem()
    {
        // Arrange
        var sessionCookie = await SignIn(StaffSignInWebApplicationFactory.AdminEmail);

        // Act
        var code = await Authorize(sessionCookie, "state-2");
        var tokens = await ExchangeCode(code);

        // Assert
        tokens.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokenResponse = await tokens.Content.ReadFromJsonAsync<JsonElement>();
        var idToken = ReadJwtPayload(tokenResponse.GetProperty("id_token").GetString()!);
        idToken.GetProperty("sub").GetString().Should().Be("jane.doe");
        idToken.GetProperty("email").GetString().Should().Be(StaffSignInWebApplicationFactory.AdminEmail);
        idToken.GetProperty("preferred_username").GetString().Should().Be("jane.doe");
        idToken.GetProperty("groups").EnumerateArray().Select(group => group.GetString()).Should().BeEquivalentTo("staff", "admins");
        idToken.GetProperty("aud").GetString().Should().Be(StaffSignInWebApplicationFactory.ClientId);

        var userInfoClient = CreateClient();
        userInfoClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResponse.GetProperty("access_token").GetString());
        var userInfo = await userInfoClient.GetFromJsonAsync<JsonElement>("/connect/userinfo");
        userInfo.GetProperty("sub").GetString().Should().Be("jane.doe");
        userInfo.GetProperty("name").GetString().Should().Be("Jane Doe");
        userInfo.GetProperty("groups").EnumerateArray().Select(group => group.GetString()).Should().Contain("admins");
    }

    [Fact]
    public async Task ExchangeCode_WhenCodeIsUsedTwice_ShouldRefuseTheSecondExchange()
    {
        // Arrange
        var sessionCookie = await SignIn(StaffSignInWebApplicationFactory.AdminEmail);
        var code = await Authorize(sessionCookie, "state-3");
        (await ExchangeCode(code)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await ExchangeCode(code);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task ExchangeCode_WhenClientSecretIsWrong_ShouldRefuseTheExchange()
    {
        // Arrange
        var sessionCookie = await SignIn(StaffSignInWebApplicationFactory.AdminEmail);
        var code = await Authorize(sessionCookie, "state-4");

        // Act
        var response = await ExchangeCode(code, "not-the-secret");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_client");
    }

    [Fact]
    public async Task Authorize_WhenStaffMemberIsOutsideTheAllowedGroups_ShouldReturnAccessDeniedToTheClient()
    {
        // Arrange
        var sessionCookie = await SignIn(StaffSignInWebApplicationFactory.SignInTestEmails[0]);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

        // Act
        var response = await client.GetAsync(AuthorizeUrl("state-5"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(StaffSignInWebApplicationFactory.ClientRedirectUri);
        var query = HttpUtility.ParseQueryString(location.Query);
        query["error"].Should().Be("access_denied");
        query["state"].Should().Be("state-5");
        query["code"].Should().BeNull();
    }

    [Fact]
    public async Task EndSession_WhenSignedIn_ShouldEndTheSessionAndRedirect()
    {
        // Arrange
        var sessionCookie = await SignIn(StaffSignInWebApplicationFactory.SignInTestEmails[1]);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

        // Act
        var response = await client.GetAsync("/connect/logout");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login");
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie => cookie.StartsWith($"{StaffSessionDefaults.CookieName}=;"));
    }

    private static string AuthorizeUrl(string state)
    {
        return "/connect/authorize"
               + $"?client_id={StaffSignInWebApplicationFactory.ClientId}"
               + $"&redirect_uri={Uri.EscapeDataString(StaffSignInWebApplicationFactory.ClientRedirectUri)}"
               + "&response_type=code"
               + $"&scope={Uri.EscapeDataString(Scopes)}"
               + $"&state={state}"
               + "&nonce=nonce-value";
    }

    private async Task<string> Authorize(string sessionCookie, string state)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", sessionCookie);
        var response = await client.GetAsync(AuthorizeUrl(state));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        query["state"].Should().Be(state);
        return query["code"]!;
    }

    private async Task<HttpResponseMessage> ExchangeCode(string code, string clientSecret = StaffSignInWebApplicationFactory.ClientSecret)
    {
        return await CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = StaffSignInWebApplicationFactory.ClientRedirectUri,
                    ["client_id"] = StaffSignInWebApplicationFactory.ClientId,
                    ["client_secret"] = clientSecret
                }
            )
        );
    }

    private async Task<string> SignIn(string email)
    {
        var client = CreateClient();
        var start = await client.PostAsJsonAsync("/api/back-office/staff-sign-in/start", new StartStaffSignInCommand(email));
        var staffSignInId = (await start.DeserializeResponse<StartStaffSignInResponse>())!.StaffSignInId;
        var complete = await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(factory.LastCodeSentTo(email)));
        complete.EnsureSuccessStatusCode();
        return complete.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith($"{StaffSessionDefaults.CookieName}=")).Split(';')[0];
    }

    private static JsonElement ReadJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement;
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{BackOfficeWebApplicationFactory.BackOfficeHost}"), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        return client;
    }
}
