namespace Scaleway.Sdk;

/// <summary>
///     A non-transient Scaleway API error (e.g. a 400 the retry must not swallow). Carries the parsed error body so
///     the actual reason reaches the deploy log — and exposes it structurally: <see cref="ErrorType" /> and
///     <see cref="Details" /> let callers key off <c>type</c> / <c>argument_name</c> / <c>reason</c> (e.g. read
///     which fields Scaleway itself considers required) instead of scraping the message.
/// </summary>
public class ScalewayApiException(string message, int statusCode = 0, string? errorType = null, IReadOnlyList<ScalewayErrorDetail>? details = null)
    : Exception(message)
{
    /// <summary>The HTTP status, or 0 when the failure carried none (a body that could not be parsed).</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>
    ///     Scaleway's own <c>type</c> discriminator (e.g. <c>invalid_arguments</c>), for keying off instead of scraping
    ///     the message.
    /// </summary>
    public string? ErrorType { get; } = errorType;

    /// <summary>Per-argument reasons Scaleway returned. Empty rather than null when the error carried none.</summary>
    public IReadOnlyList<ScalewayErrorDetail> Details { get; } = details ?? [];
}
