using System.Security.Claims;
using BackOffice.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication;

namespace BackOffice.ExecutionContext;

/// <summary>
///     Request-scoped staff execution context, resolved lazily from the authenticated
///     BackOfficeIdentity principal (mirroring SharedKernel's HttpExecutionContext pattern: built
///     on first access, cached for the request's lifetime). Unauthenticated requests carry an
///     anonymous context — endpoints that need a staff member stay behind the back-office
///     authorization policies, which guarantee an authenticated principal by the time handlers run.
/// </summary>
public sealed class StaffExecutionContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<BackOfficeHostOptions> options
) : IStaffExecutionContext
{
    private readonly BackOfficeHostOptions _options = options.Value;

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal is { Identity.IsAuthenticated: true };

    public string? ObjectId => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string DisplayName => Principal?.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public string Email => Principal?.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

    public string[] Groups => Principal?.Claims
        .Where(claim => claim.Type == BackOfficeIdentityDefaults.GroupsClaimType)
        .Select(claim => claim.Value)
        .ToArray() ?? [];

    public bool IsAdmin => !string.IsNullOrWhiteSpace(_options.AdminsGroupId) &&
                           Groups.Contains(_options.AdminsGroupId);
}
