namespace Scaleway.Sdk;

/// <summary>
///     A create was rejected because the resource already exists (Scaleway returns 400/409 with an
///     "already exists" body). Non-transient, but recoverable: a prior lost-response attempt created the
///     resource and our list hadn't caught up yet. The find-or-create path catches this, re-finds, and adopts.
/// </summary>
public sealed class ScalewayResourceAlreadyExistsException(string message, int statusCode = 0, string? errorType = null, IReadOnlyList<ScalewayErrorDetail>? details = null)
    : ScalewayApiException(message, statusCode, errorType, details);
