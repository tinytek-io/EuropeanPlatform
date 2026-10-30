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
///     Masks the two identifiers most likely to appear in an API message or URL — emails and UUID values — so a
///     shared diagnostics file carries no personal or tenant identifiers. Deliberately narrow: it runs only over
///     free text and paths, never over the structural fields (<c>argument_name</c>, <c>reason</c>, <c>type</c>)
///     that are the whole point of the capture.
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
}

/// <summary>
///     Appends each captured error as one JSON line (JSONL) to <c>SCW_DIAGNOSTICS_PATH</c>. Append-per-record is
///     crash-safe; the lock serialises concurrent requests writing the same file.
/// </summary>
internal sealed class FileScalewayDiagnosticsSink(string path) : IScalewayDiagnosticsSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _writeLock = new();

    public void Record(ScalewayDiagnosticEvent diagnosticEvent)
    {
        // The timestamp is stamped here, not on the event, so the in-memory test sink stays deterministic.
        var line = new StringBuilder();
        line.Append("{\"timestamp\":\"").Append(DateTimeOffset.UtcNow.ToString("O")).Append("\",");
        var payload = JsonSerializer.Serialize(diagnosticEvent, JsonOptions);
        line.Append(payload.AsSpan(1)); // splice the event object's fields in after the timestamp

        lock (_writeLock)
        {
            File.AppendAllText(path, line.Append('\n').ToString());
        }
    }
}
