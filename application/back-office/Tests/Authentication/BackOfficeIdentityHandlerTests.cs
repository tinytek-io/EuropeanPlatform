using System.Security.Claims;
using System.Text.Encodings.Web;
using BackOffice.Authentication;
using BackOffice.Authentication.MockEasyAuth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BackOffice.Tests.Authentication;

public sealed class BackOfficeIdentityHandlerTests
{
    [Fact]
    public async Task HandleAuthenticate_WhenNoIdentityAttached_ShouldReturnNoResult()
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
    public async Task HandleAuthenticate_WhenMockIdentityAttached_ShouldReturnSuccessWithClaims()
    {
        // Arrange
        var admin = MockEasyAuthIdentities.Default.Single(identity => identity.Id == "admin");
        var (handler, _) = await CreateHandlerAsync(context => MockEasyAuthIdentities.Attach(context, admin));

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        var principal = result.Principal!;
        principal.FindFirstValue(ClaimTypes.Name).Should().Be(admin.Name);
        principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(admin.ObjectId);
        principal.FindFirstValue(ClaimTypes.Email).Should().Be(admin.Email);
        principal.Claims.Should().Contain(c => c.Type == BackOfficeIdentityDefaults.GroupsClaimType && c.Value == MockEasyAuthIdentities.MockAdminsGroupId);
    }

    [Fact]
    public async Task HandleAuthenticate_WhenOnlyEasyAuthHeadersPresent_ShouldReturnNoResult()
    {
        // Arrange
        var forgedPrincipal = Convert.ToBase64String("""{"claims":[{"typ":"groups","val":"BackOfficeAdmins"}]}"""u8.ToArray());
        var (handler, _) = await CreateHandlerAsync(context =>
            {
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"] = "attacker";
                context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = forgedPrincipal;
            }
        );

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChallenge_WhenAcceptIsHtml_ShouldRedirectToLogin()
    {
        // Arrange
        var (handler, context) = await CreateHandlerAsync(context => context.Request.Headers.Accept = "text/html");
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
        var (handler, context) = await CreateHandlerAsync(context => context.Request.Headers.Accept = "application/json");

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
        var (handler, context) = await CreateHandlerAsync(context => context.Request.Headers.Accept = "text/html");

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
        var (handler, context) = await CreateHandlerAsync(context => context.Request.Headers.Accept = "application/json");

        // Act
        await handler.ForbidAsync(new AuthenticationProperties());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Headers.Location.Should().BeEmpty();
    }

    private static async Task<(BackOfficeIdentityHandler Handler, HttpContext Context)> CreateHandlerAsync(Action<HttpContext>? configureContext = null)
    {
        var optionsMonitor = Options.Create(new AuthenticationSchemeOptions());
        var monitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(optionsMonitor.Value);
        var handler = new BackOfficeIdentityHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default);

        var context = new DefaultHttpContext();
        configureContext?.Invoke(context);

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
