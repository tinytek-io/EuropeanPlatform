using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;

namespace BackOffice.Authentication.MockSignIn;

public sealed record MockStaffIdentity(string Id, string Name, string Email, string SubjectId, ImmutableArray<string> Groups);

public static class MockStaffIdentities
{
    // Mirrors the two-option mock-login picker shipped in the back-office SPA. The "admin" identity
    // carries MockAdminsGroupId as its 'groups' claim, which AppHost wires into BackOffice:AdminsGroupId
    // so BackOfficeAdminRequirement satisfies in development without any manual configuration.
    public const string MockAdminsGroupId = "BackOfficeAdmins";

    public static readonly ImmutableArray<MockStaffIdentity> Default =
    [
        new("admin", "Admin", "admin@dev.localhost", "00000000-0000-0000-0000-0000000000a1", [MockAdminsGroupId]),
        new("user", "User", "user@dev.localhost", "00000000-0000-0000-0000-0000000000a2", [])
    ];

    // The mock selector hands the cookie-resolved identity to BackOfficeIdentityHandler in-process.
    // HttpContext.Items cannot be populated from the network, unlike request headers.
    private static readonly object HttpContextItemKey = new();

    public static void Attach(HttpContext context, MockStaffIdentity identity)
    {
        context.Items[HttpContextItemKey] = identity;
    }

    public static MockStaffIdentity? GetAttached(HttpContext context)
    {
        return context.Items.TryGetValue(HttpContextItemKey, out var identity) ? identity as MockStaffIdentity : null;
    }

    public static MockStaffIdentity? Find(string id)
    {
        return Default.FirstOrDefault(identity => string.Equals(identity.Id, id, StringComparison.Ordinal));
    }
}
