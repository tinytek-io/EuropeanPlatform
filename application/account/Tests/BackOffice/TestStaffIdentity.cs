namespace Account.Tests.BackOffice;

// Fixed staff principals the internal-API tests mint delegation tokens from. The staff identity
// machinery (including the mock login picker) is owned by the back-office system, so these local
// stand-ins mirror its admin/user identities without coupling the account tests to that system:
// the internal API only ever sees the delegation token built from these claims.
public sealed record TestStaffIdentity(string Id, string Name, string Email, string ObjectId, string[] Groups);

public static class TestStaffIdentities
{
    // Mirrors the group id AppHost wires into BackOffice:AdminsGroupId, so the DelegationAdmin
    // gate resolves admin status the same way it does in development.
    public const string AdminsGroupId = "BackOfficeAdmins";

    public static readonly TestStaffIdentity Admin = new("admin", "Admin", "admin@dev.localhost", "00000000-0000-0000-0000-0000000000a1", [AdminsGroupId]);

    public static readonly TestStaffIdentity User = new("user", "User", "user@dev.localhost", "00000000-0000-0000-0000-0000000000a2", []);
}
