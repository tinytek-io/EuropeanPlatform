namespace BackOffice.ExecutionContext;

/// <summary>
///     The staff member executing the current back-office operation. Built from the authenticated
///     BackOfficeIdentity principal — the same claims shape whether the identity came from the
///     mock EasyAuth selector or a staff OIDC provider's session cookie — so handlers never read
///     the raw principal and the admin gate is computed in one place.
/// </summary>
public interface IStaffExecutionContext
{
    bool IsAuthenticated { get; }

    /// <summary>The provider's stable subject identifier (the OIDC `sub` / EasyAuth oid).</summary>
    string? ObjectId { get; }

    string DisplayName { get; }

    string Email { get; }

    string[] Groups { get; }

    /// <summary>
    ///     Closed-by-default: when AdminsGroupId is unset, no one is an admin. This is the same
    ///     computation the BackOfficeAdmin authorization policy applies on protected endpoints, so
    ///     what the API reports agrees with what the policy grants.
    /// </summary>
    bool IsAdmin { get; }
}
