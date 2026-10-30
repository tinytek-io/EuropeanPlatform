using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BackOffice.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BackOffice.Tests.Authentication;

public sealed class BackOfficeIdentityHandlerTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HandleAuthenticate_WhenPrincipalNameHeaderMissing_ShouldReturnNoResult()
    {
        // Arrange
        var (handler, context) = await CreateHandlerAsync();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleAuthenticate_WhenPrincipalHeadersPresent_ShouldReturnSuccessWithClaims()
    {
        // Arrange
        var payload = BuildPrincipalPayload(
            new BackOfficeClientPrincipalClaim { Type = "groups", Value = "BackOfficeAdmins" },
            new BackOfficeClientPrincipalClaim { Type = "preferred_username", Value = "alice@example.com" }
        );
        var (handler, _) = await CreateHandlerAsync(headers =>
            {
                headers[BackOfficeIdentityDefaults.PrincipalNameHeader] = "Alice Anderson";
                headers[BackOfficeIdentityDefaults.PrincipalIdHeader] = "00000000-0000-0000-0000-0000000000a1";
                headers[BackOfficeIdentityDefaults.PrincipalPayloadHeader] = payload;
            }
        );

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        var principal = result.Principal!;
        principal.FindFirstValue(ClaimTypes.Name).Should().Be("Alice Anderson");
        principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("00000000-0000-0000-0000-0000000000a1");
        principal.Claims.Should().Contain(c => c.Type == "groups" && c.Value == "BackOfficeAdmins");
        principal.Claims.Should().Contain(c => c.Type == "preferred_username" && c.Value == "alice@example.com");
    }

    [Fact]
    public async Task HandleAuthenticate_WhenPayloadIsInvalidBase64_ShouldStillSucceedWithNameOnly()
    {
        // Arrange
        var (handler, _) = await CreateHandlerAsync(headers =>
            {
                headers[BackOfficeIdentityDefaults.PrincipalNameHeader] = "Alice Anderson";
                headers[BackOfficeIdentityDefaults.PrincipalPayloadHeader] = "not-valid-base64!@#";
            }
        );

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        var principal = result.Principal!;
        principal.FindFirstValue(ClaimTypes.Name).Should().Be("Alice Anderson");
        principal.Claims.Should().NotContain(c => c.Type == "groups");
    }

    [Fact]
    public async Task HandleChallenge_WhenAcceptIsHtml_ShouldRedirectToLogin()
    {
        // Arrange
        var (handler, context) = await CreateHandlerAsync(headers => headers.Accept = "text/html");
        context.Request.Path = "/api/back-office/me";
        context.Request.QueryString = new QueryString("?foo=bar");

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        var location = context.Response.Headers.Location.ToString();
        location.Should().StartWith($"{BackOfficeIdentityDefaults.LoginPath}?post_login_redirect_uri=");
        location.Should().Contain(Uri.EscapeDataString("/api/back-office/me?foo=bar"));
    }

    [Fact]
    public async Task HandleChallenge_WhenAcceptIsJson_ShouldReturnUnauthorized()
    {
        // Arrange
        var (handler, context) = await CreateHandlerAsync(headers => headers.Accept = "application/json");

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.Location.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleForbidden_WhenAcceptIsHtml_ShouldRedirectToAccessDenied()
    {
        // Arrange
        var (handler, context) = await CreateHandlerAsync(headers => headers.Accept = "text/html");

        // Act
        await handler.ForbidAsync(new AuthenticationProperties());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be(BackOfficeIdentityDefaults.AccessDeniedPath);
    }

    [Fact]
    public async Task HandleForbidden_WhenAcceptIsJson_ShouldReturnForbidden()
    {
        // Arrange
        var (handler, context) = await CreateHandlerAsync(headers => headers.Accept = "application/json");

        // Act
        await handler.ForbidAsync(new AuthenticationProperties());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Headers.Location.Should().BeEmpty();
    }

    private static string BuildPrincipalPayload(params BackOfficeClientPrincipalClaim[] claims)
    {
        var principal = new BackOfficeClientPrincipal
        {
            AuthenticationType = "aad",
            NameType = ClaimTypes.Name,
            RoleType = ClaimTypes.Role,
            Claims = claims
        };
        var json = JsonSerializer.Serialize(principal, WebJsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static async Task<(BackOfficeIdentityHandler Handler, HttpContext Context)> CreateHandlerAsync(Action<IHeaderDictionary>? configureHeaders = null)
    {
        var optionsMonitor = Options.Create(new AuthenticationSchemeOptions());
        var monitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(optionsMonitor.Value);
        var handler = new BackOfficeIdentityHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default);

        var context = new DefaultHttpContext();
        configureHeaders?.Invoke(context.Request.Headers);

        var scheme = new AuthenticationScheme(BackOfficeIdentityDefaults.AuthenticationScheme, BackOfficeIdentityDefaults.AuthenticationScheme, typeof(BackOfficeIdentityHandler));
        await handler.InitializeAsync(scheme, context);
        return (handler, context);
    }

    private sealed class TestOptionsMonitor<TOptions>(TOptions current) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; } = current;

        public TOptions Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<TOptions, string> listener)
        {
            return null;
        }
    }
}
