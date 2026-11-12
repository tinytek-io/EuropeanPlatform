using System.Security;
using JetBrains.Annotations;
using SharedKernel.Domain;
using SharedKernel.StronglyTypedIds;

namespace BackOffice.Features.StaffSignIn.Domain;

/// <summary>
///     One emailed one-time code for a staff member. Only the code's hash is stored; a code is single-use, valid for
///     <see cref="ValidForSeconds" />, and blocked after <see cref="MaxAttempts" /> wrong guesses. Who may sign in is
///     the staff identity file's decision, made again when the code is completed.
/// </summary>
public sealed class StaffSignIn : AggregateRoot<StaffSignInId>
{
    public const int MaxAttempts = 3;
    public const int ValidForSeconds = 300;

    private StaffSignIn(string staffMemberId, string email, string oneTimePasswordHash)
        : base(StaffSignInId.NewId())
    {
        StaffMemberId = staffMemberId;
        Email = email;
        OneTimePasswordHash = oneTimePasswordHash;
    }

    public string StaffMemberId { get; private set; }

    public string Email { get; private set; }

    public string OneTimePasswordHash { get; private set; }

    public int RetryCount { get; private set; }

    public bool Completed { get; private set; }

    public static StaffSignIn Create(string staffMemberId, string email, string oneTimePasswordHash)
    {
        return new StaffSignIn(staffMemberId, email.ToLowerInvariant(), oneTimePasswordHash);
    }

    public bool IsExpired(DateTimeOffset now)
    {
        if (CreatedAt > now)
        {
            throw new SecurityException($"StaffSignIn '{Id}' has CreatedAt in the future. Possible data tampering.");
        }

        return CreatedAt.AddSeconds(ValidForSeconds) < now;
    }

    public void RegisterInvalidPasswordAttempt()
    {
        RetryCount++;
    }

    public void MarkAsCompleted(DateTimeOffset now)
    {
        if (IsExpired(now) || RetryCount >= MaxAttempts)
        {
            throw new UnreachableException("This staff sign-in has expired.");
        }

        if (Completed) throw new UnreachableException("The staff sign-in has already been completed.");

        Completed = true;
    }
}

[PublicAPI]
[IdPrefix("stfsi")]
[JsonConverter(typeof(StronglyTypedIdJsonConverter<string, StaffSignInId>))]
public sealed record StaffSignInId(string Value) : StronglyTypedUlid<StaffSignInId>(Value)
{
    public override string ToString()
    {
        return Value;
    }
}
