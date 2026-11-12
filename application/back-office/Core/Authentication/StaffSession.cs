using System.Security.Claims;
using BackOffice.StaffIdentity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharedKernel.OpenIdConnect;

namespace BackOffice.Authentication;

public static class StaffSessionDefaults
{
    // The session cookie a completed staff sign-in issues. The back-office identity handler authenticates it, so the
    // back-office authorization policies stay pinned to the single identity scheme.
    public const string Scheme = "BackOfficeStaffSession";

    public const string CookieName = "__Host-BackOfficeSession";
}

/// <summary>
///     Where the environment's staff identity file is. Set in every deployed environment (the file ships with the
///     back-office); in local development only when the real sign-in is opted into — otherwise the mock identity
///     selector applies.
/// </summary>
public sealed class StaffIdentityOptions
{
    public const string SectionName = "StaffIdentity";

    /// <summary>Absolute, or relative to the application folder (staff-identity/&lt;environment&gt;.yaml when deployed).</summary>
    public string? File { get; init; }
}

public static class StaffSessionExtensions
{
    /// <summary>True when the back-office signs staff in itself (a staff identity file is configured).</summary>
    public static bool IsStaffSignInEnabled(this IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.GetSection(StaffIdentityOptions.SectionName).Get<StaffIdentityOptions>()?.File);
    }

    /// <summary>
    ///     The configured staff identity file, or nobody without one. Configuration is read when the directory is first
    ///     resolved, not at registration, so every configuration source (including a test host's) is in place; resolve
    ///     it at startup, so a missing or invalid file fails the start.
    /// </summary>
    public static IServiceCollection AddStaffDirectory(this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider =>
            {
                var file = serviceProvider.GetRequiredService<IConfiguration>().GetSection(StaffIdentityOptions.SectionName).Get<StaffIdentityOptions>()?.File;
                if (string.IsNullOrWhiteSpace(file)) return StaffDirectory.Empty;
                return StaffDirectory.Load(Path.IsPathRooted(file) ? file : Path.Combine(AppContext.BaseDirectory, file));
            }
        );
        return services;
    }

    /// <summary>
    ///     Registers the staff directory and the session cookie. Without a staff identity file the directory holds
    ///     nobody: the sign-in endpoints are then not mapped and no session cookie is ever issued.
    /// </summary>
    public static IServiceCollection AddStaffSession(this IServiceCollection services)
    {
        services.AddStaffDirectory();

        services.AddAuthentication().AddCookie(StaffSessionDefaults.Scheme, cookie =>
            {
                cookie.Cookie.Name = StaffSessionDefaults.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax, so the session reaches the OIDC authorize endpoint when another tool redirects a signed-in
                // staff member here (a cross-site top-level GET).
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.SlidingExpiration = true;
                // Challenge and forbidden responses stay with the back-office identity handler.
                cookie.LoginPath = null;
                cookie.AccessDeniedPath = null;
                cookie.Events.OnValidatePrincipal = context => RevalidateAgainstStaffDirectory(context, context.HttpContext.RequestServices.GetRequiredService<StaffDirectory>());
            }
        );

        return services;
    }

    /// <summary>
    ///     The browser-facing sign-in and sign-out routes: the identity handler sends unauthenticated browsers to the
    ///     login path (the one the SPA already redirects through), which forwards to the SPA's sign-in page. Mapped only
    ///     with a staff identity file; otherwise the mock identity selector owns these paths.
    /// </summary>
    public static IEndpointRouteBuilder MapStaffSessionEndpoints(this IEndpointRouteBuilder routes, string backOfficeHost)
    {
        routes.MapGet(BackOfficeIdentityDefaults.LoginPath, (string? postLoginRedirectUri) =>
                {
                    var candidate = postLoginRedirectUri ?? "/";
                    var returnPath = ReturnPathHelper.IsValidRelativePath(candidate) ? candidate : "/";
                    return Results.Redirect($"/login?returnPath={Uri.EscapeDataString(returnPath)}");
                }
            )
            .RequireHost(backOfficeHost)
            .AllowAnonymous()
            .ExcludeFromDescription();

        routes.MapGet(BackOfficeIdentityDefaults.LogoutPath, async (HttpContext context) =>
                {
                    await context.SignOutAsync(StaffSessionDefaults.Scheme);
                    return Results.Redirect("/login");
                }
            )
            .RequireHost(backOfficeHost)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return routes;
    }

    public static ClaimsPrincipal CreatePrincipal(StaffMember member)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, member.Id),
            new(ClaimTypes.Name, member.Name),
            new(ClaimTypes.Email, member.Email)
        };
        claims.AddRange(member.Groups.Select(group => new Claim(BackOfficeIdentityDefaults.GroupsClaimType, group)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, StaffSessionDefaults.Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static Task SignInAsync(HttpContext context, StaffMember member)
    {
        return context.SignInAsync(StaffSessionDefaults.Scheme, CreatePrincipal(member));
    }

    // The staff identity file decides on every request, not only at sign-in: a deactivated person loses access on
    // their next request after the deploy that deactivated them, and a group change applies the same way.
    private static async Task RevalidateAgainstStaffDirectory(CookieValidatePrincipalContext context, StaffDirectory directory)
    {
        var staffMemberId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var member = staffMemberId is null ? null : directory.FindActiveById(staffMemberId);
        if (member is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(StaffSessionDefaults.Scheme);
            return;
        }

        var current = context.Principal!.FindAll(BackOfficeIdentityDefaults.GroupsClaimType).Select(claim => claim.Value).Order();
        var isUnchanged = current.SequenceEqual(member.Groups.Order())
                          && context.Principal.FindFirstValue(ClaimTypes.Name) == member.Name
                          && context.Principal.FindFirstValue(ClaimTypes.Email) == member.Email;
        if (isUnchanged) return;

        context.ReplacePrincipal(CreatePrincipal(member));
        context.ShouldRenew = true;
    }
}
