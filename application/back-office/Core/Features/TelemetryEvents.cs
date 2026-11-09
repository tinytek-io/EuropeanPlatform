using BackOffice.Features.StaffSignIn.Domain;
using SharedKernel.Telemetry;

// Ensure that TelemetryEvents are named in past tense and do not include personal data (emails stay out; the
// staff member id is the identifier from the committed staff identity file).
namespace BackOffice.Features;

public sealed class StaffSignInCodeBlocked(StaffSignInId staffSignInId, int retryCount)
    : TelemetryEvent(("staff_sign_in_id", staffSignInId), ("retry_count", retryCount));

public sealed class StaffSignInCodeExpired(StaffSignInId staffSignInId, int timeInSeconds)
    : TelemetryEvent(("staff_sign_in_id", staffSignInId), ("time_in_seconds", timeInSeconds));

public sealed class StaffSignInCodeFailed(StaffSignInId staffSignInId, int retryCount)
    : TelemetryEvent(("staff_sign_in_id", staffSignInId), ("retry_count", retryCount));

public sealed class StaffSignInCompleted(string staffMemberId, int timeInSeconds)
    : TelemetryEvent(("staff_member_id", staffMemberId), ("time_in_seconds", timeInSeconds));

public sealed class StaffSignInStarted(string staffMemberId)
    : TelemetryEvent(("staff_member_id", staffMemberId));
