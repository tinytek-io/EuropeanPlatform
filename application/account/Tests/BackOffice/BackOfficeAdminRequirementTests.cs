using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication;
using Xunit;

namespace Account.Tests.BackOffice;

// Verifies the optional BackOffice:AdminsGroupId capability gate. When unset, no one is admin.
// When set, the principal must carry a 'groups' claim matching the configured value.
public sealed class BackOfficeAdminRequirementTests
{
    [Fact]
    public async Task HandleRequirement_WhenAdminsGroupIdUnset_ShouldFail()
    {
        var handler = CreateHandler(null);
        var requirement = new BackOfficeAdminRequirement();
        var context = CreateAuthorizationContext(requirement, ["BackOfficeAdmins"]);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirement_WhenPrincipalCarriesMatchingGroup_ShouldSucceed()
    {
        var handler = CreateHandler("BackOfficeAdmins");
        var requirement = new BackOfficeAdminRequirement();
        var context = CreateAuthorizationContext(requirement, ["BackOfficeAdmins"]);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirement_WhenPrincipalLacksMatchingGroup_ShouldFail()
    {
        var handler = CreateHandler("BackOfficeAdmins");
        var requirement = new BackOfficeAdminRequirement();
        var context = CreateAuthorizationContext(requirement, []);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private static BackOfficeAdminAuthorizationHandler CreateHandler(string? adminsGroupId)
    {
        var options = Options.Create(new BackOfficeHostOptions { Host = "back-office.test.localhost", AdminsGroupId = adminsGroupId });
        return new BackOfficeAdminAuthorizationHandler(options);
    }

    private static AuthorizationHandlerContext CreateAuthorizationContext(IAuthorizationRequirement requirement, string[] groups)
    {
        var claims = groups.Select(group => new Claim(BackOfficeDelegationDefaults.GroupsClaimType, group));
        var identity = new ClaimsIdentity(claims, BackOfficeDelegationDefaults.DelegationScheme);
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext([requirement], principal, null);
    }
}
