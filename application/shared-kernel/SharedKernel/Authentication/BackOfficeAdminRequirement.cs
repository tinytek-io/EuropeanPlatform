using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace SharedKernel.Authentication;

public sealed class BackOfficeAdminRequirement : IAuthorizationRequirement;

// Authorization handler enforcing the optional back-office admin capability. Easy Auth gates the
// container app to authenticated tenant users; this requirement adds an opt-in admin gate on top.
// When BackOffice:AdminsGroupId is unset, no one passes (admin features are off). When set, the
// principal must carry a 'groups' claim matching the configured value.
public sealed class BackOfficeAdminAuthorizationHandler(IOptions<BackOfficeHostOptions> options)
    : AuthorizationHandler<BackOfficeAdminRequirement>
{
    private readonly BackOfficeHostOptions _options = options.Value;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, BackOfficeAdminRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminsGroupId)) return Task.CompletedTask;

        var hasGroup = context.User.Claims.Any(claim =>
            claim.Type == BackOfficeDelegationDefaults.GroupsClaimType &&
            string.Equals(claim.Value, _options.AdminsGroupId, StringComparison.Ordinal)
        );

        if (hasGroup) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
