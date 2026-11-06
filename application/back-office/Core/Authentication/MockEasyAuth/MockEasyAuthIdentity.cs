using System.Collections.Immutable;

namespace BackOffice.Authentication.MockEasyAuth;

public sealed record MockEasyAuthIdentity(string Id, string Name, string Email, string ObjectId, ImmutableArray<string> Groups);

public static class MockEasyAuthIdentities
{
    // Mirrors the two-option mock-login picker shipped in the back-office SPA. The "admin" identity
    // carries MockAdminsGroupId as its 'groups' claim, which AppHost wires into BackOffice:AdminsGroupId
    // so BackOfficeAdminRequirement satisfies in development without any manual configuration.
    public const string MockAdminsGroupId = "BackOfficeAdmins";

    public static readonly ImmutableArray<MockEasyAuthIdentity> Default =
    [
        new("admin", "Admin", "admin@dev.localhost", "00000000-0000-0000-0000-0000000000a1", [MockAdminsGroupId]),
        new("user", "User", "user@dev.localhost", "00000000-0000-0000-0000-0000000000a2", [])
    ];

    public static MockEasyAuthIdentity? Find(string id)
    {
        return Default.FirstOrDefault(identity => string.Equals(identity.Id, id, StringComparison.Ordinal));
    }
}
