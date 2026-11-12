using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scaleway.Sdk;

/// <summary>One rejected field in a captured error: the field, why, and a redacted hint.</summary>
internal sealed record ScalewayDiagnosticDetail(string? ArgumentName, string? Reason, string? HelpMessage);

/// <summary>
///     A single captured API error, structured for a developer or agent to improve the generator. A 400 here means
///     the generated client sent something the real API rejected — a schema-to-reality gap the checked-in schema
///     doesn't encode. Values are never captured: the request contributes field <em>names</em> only, and free text
///     is redacted, so the record is safe to share.
/// </summary>
internal sealed record ScalewayDiagnosticEvent(
    string Method,
    string Path,
    int Status,
    string Category,
    string? ErrorType,
    IReadOnlyList<ScalewayDiagnosticDetail> Details,
    IReadOnlyList<string> RequestFields
);

/// <summary>Where captured errors go. Null on the client means capture is off (no file I/O, no overhead).</summary>
internal interface IScalewayDiagnosticsSink
{
    void Record(ScalewayDiagnosticEvent diagnosticEvent);
}

/// <summary>
///     Keeps personal and tenant identifiers out of a shared diagnostics file. Free text (an API's help message) has
///     its emails and UUID values masked; request paths keep only their structure (<see cref="RedactRequestPath" />).
///     Neither touches the structural fields (<c>argument_name</c>, <c>reason</c>, <c>type</c>) that are the whole
///     point of the capture.
/// </summary>
internal static class ScalewayDiagnosticsRedactor
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Uuid = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    internal static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Uuid.Replace(Email.Replace(text, "<email>"), "<uuid>");
    }

    /// <summary>
    ///     The request path with every value removed, keeping only what shows which operation was called. Query
    ///     values (a secret-name filter, a percent-encoded email) drop to their parameter names. Path identifiers
    ///     are masked by position, not by shape — a secret or bucket name looks like any other word: Scaleway paths
    ///     read <c>/{product}/{version}[/regions|zones/{locality}]/{collection}/{id}/{collection}/{id}…</c>, so
    ///     every segment after a collection is an identifier, and actions (<c>/access</c>, <c>/kubeconfig</c>) land
    ///     on collection positions and stay visible. The locality is kept: it is not personal and often explains
    ///     the error.
    /// </summary>
    internal static string RedactRequestPath(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return "";
        }

        if (!requestUri.IsAbsoluteUri)
        {
            requestUri = new Uri(new Uri($"{ScalewayApiClient.DefaultApiUrl}/"), requestUri);
        }

        var segments = requestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var redacted = new List<string>(segments.Length);
        var resourceStart = 0;

        // The product and version lead every management API path; a regional or zonal one follows with its locality.
        if (segments.Length >= 2)
        {
            redacted.Add(segments[0]);
            redacted.Add(segments[1]);
            resourceStart = 2;
            if (segments is [_, _, "regions" or "zones", _, ..])
            {
                redacted.Add(segments[2]);
                redacted.Add(segments[3]);
                resourceStart = 4;
            }
        }

        for (var index = resourceStart; index < segments.Length; index++)
        {
            redacted.Add((index - resourceStart) % 2 == 0 ? segments[index] : "<id>");
        }

        var path = "/" + string.Join('/', redacted);
        var query = requestUri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return path;
        }

        var parameterNames = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2)[0])
            .Distinct(StringComparer.Ordinal);
        return $"{path}?{string.Join('&', parameterNames)}";
    }
}

/// <summary>
///     Appends each captured error as one JSON line (JSONL) to <c>SCW_DIAGNOSTICS_PATH</c>. Append-per-record is
///     crash-safe; the lock serialises concurrent requests writing the same file. Best-effort: the capture runs while
///     an API error is being raised, so a path that cannot be written (a missing directory, no permission, a full disk)
///     drops the record instead of replacing that error — and its status, type and field details — with an I/O one.
/// </summary>
internal sealed class FileScalewayDiagnosticsSink(string path) : IScalewayDiagnosticsSink
{
    private readonly Lock _writeLock = new();

    public void Record(ScalewayDiagnosticEvent diagnosticEvent)
    {
        // The timestamp is stamped here, not on the event, so the in-memory test sink stays deterministic.
        var line = new StringBuilder();
        line.Append("{\"timestamp\":\"").Append(DateTimeOffset.UtcNow.ToString("O")).Append("\",");
        var payload = JsonSerializer.Serialize(diagnosticEvent, ScalewaySdkJsonContext.Default.ScalewayDiagnosticEvent);
        line.Append(payload.AsSpan(1)); // splice the event object's fields in after the timestamp

        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(path, line.Append('\n').ToString());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Dropped on purpose; see the summary.
        }
    }
}
