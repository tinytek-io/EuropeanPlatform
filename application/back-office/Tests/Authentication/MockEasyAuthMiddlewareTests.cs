using System.Net;
using System.Text;
using System.Text.Json;
using BackOffice.Authentication;
using BackOffice.Authentication.MockEasyAuth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BackOffice.Tests.Authentication;

public sealed class MockEasyAuthMiddlewareTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Invoke_WhenLoginPathRequested_ShouldRedirectToMockLoginPage()
    {
        // Arrange
        var middleware = new MockEasyAuthMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Path = BackOfficeIdentityDefaults.LoginPath,
                Method = HttpMethods.Get,
                QueryString = new QueryString("?post_login_redirect_uri=/dashboard")
            }
        };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Redirect);
        context.Response.Headers.Location.ToString().Should().Be("/login?returnPath=%2Fdashboard");
    }

    [Fact]
    public async Task Invoke_WhenLoginPathRedirectTargetIsPickerItself_ShouldRedirectHome()
    {
        // Arrange
        var middleware = new MockEasyAuthMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Path = BackOfficeIdentityDefaults.LoginPath,
                Method = HttpMethods.Get,
                QueryString = new QueryString("?post_login_redirect_uri=/login")
            }
        };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Redirect);
        context.Response.Headers.Location.ToString().Should().Be("/login?returnPath=%2F");
    }

    [Fact]
    public async Task Invoke_WhenCallbackHitWithKnownIdentity_ShouldSetCookieAndRedirect()
    {
        // Arrange
        var middleware = new MockEasyAuthMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Path = BackOfficeIdentityDefaults.CallbackPath,
                Method = HttpMethods.Get,
                QueryString = new QueryString("?identity=admin&post_login_redirect_uri=/dashboard")
            }
        };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Redirect);
        context.Response.Headers.Location.ToString().Should().Be("/dashboard");
        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{MockEasyAuthCookie.CookieName}=admin");
    }

    [Fact]
    public async Task Invoke_WhenCallbackHitWithUnknownIdentity_ShouldReturnBadRequest()
    {
        // Arrange
        var middleware = new MockEasyAuthMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Path = BackOfficeIdentityDefaults.CallbackPath,
                Method = HttpMethods.Get,
                QueryString = new QueryString("?identity=does-not-exist")
            }
        };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invoke_WhenLogoutHit_ShouldClearCookieAndRedirect()
    {
        // Arrange
        var middleware = new MockEasyAuthMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Path = BackOfficeIdentityDefaults.LogoutPath,
                Method = HttpMethods.Get
            }
        };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Redirect);
        context.Response.Headers.SetCookie.ToString().Should().Contain($"{MockEasyAuthCookie.CookieName}=");
        context.Response.Headers.SetCookie.ToString().Should().Contain("expires=");
    }

    [Fact]
    public async Task Invoke_WhenCookieCarriesKnownIdentity_ShouldInjectClientPrincipalHeaders()
    {
        // Arrange
        var observed = new Dictionary<string, string>();
        var middleware = new MockEasyAuthMiddleware(ctx =>
            {
                observed[BackOfficeIdentityDefaults.PrincipalNameHeader] = ctx.Request.Headers[BackOfficeIdentityDefaults.PrincipalNameHeader].ToString();
                observed[BackOfficeIdentityDefaults.PrincipalIdHeader] = ctx.Request.Headers[BackOfficeIdentityDefaults.PrincipalIdHeader].ToString();
                observed[BackOfficeIdentityDefaults.PrincipalPayloadHeader] = ctx.Request.Headers[BackOfficeIdentityDefaults.PrincipalPayloadHeader].ToString();
                return Task.CompletedTask;
            }
        );

        var admin = MockEasyAuthIdentities.Default.Single(i => i.Id == "admin");
        var context = new DefaultHttpContext
        {
            Request =
            {
                Path = "/api/back-office/me",
                Method = HttpMethods.Get,
                Headers = { Cookie = $"{MockEasyAuthCookie.CookieName}={admin.Id}" }
            }
        };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        observed[BackOfficeIdentityDefaults.PrincipalNameHeader].Should().Be(admin.Name);
        observed[BackOfficeIdentityDefaults.PrincipalIdHeader].Should().Be(admin.ObjectId);

        var payloadBytes = Convert.FromBase64String(observed[BackOfficeIdentityDefaults.PrincipalPayloadHeader]);
        var json = Encoding.UTF8.GetString(payloadBytes);
        var principal = JsonSerializer.Deserialize<BackOfficeClientPrincipal>(json, WebJsonOptions);
        principal!.Claims.Should().Contain(c => c.Type == BackOfficeIdentityDefaults.GroupsClaimType && c.Value == "BackOfficeAdmins");
    }

    [Fact]
    public async Task Invoke_WhenNoCookie_ShouldNotInjectHeaders()
    {
        // Arrange
        var observed = new List<string>();
        var middleware = new MockEasyAuthMiddleware(ctx =>
            {
                if (ctx.Request.Headers.ContainsKey(BackOfficeIdentityDefaults.PrincipalNameHeader)) observed.Add("name");
                return Task.CompletedTask;
            }
        );
        var context = new DefaultHttpContext { Request = { Path = "/api/back-office/me", Method = HttpMethods.Get } };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        observed.Should().BeEmpty();
    }
}
