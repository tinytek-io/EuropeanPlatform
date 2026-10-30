using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>One entry of a Scaleway <c>invalid_arguments</c> error: which field, and why it was rejected.</summary>
public sealed record ScalewayErrorDetail(string? ArgumentName, string? Reason, string? HelpMessage);

/// <summary>
///     The structured body of a Scaleway API error. Scaleway returns
///     <c>{"type":"invalid_arguments","message":"…","details":[{"argument_name":"…","reason":"required"}]}</c>,
///     so the reason a call failed is machine-readable — including which fields it considers required. Parsing it
///     lets error handling key off <see cref="Type" /> / <see cref="Details" /> rather than substring-matching the
///     raw JSON, and produces a readable message instead of dumping the blob.
/// </summary>
public sealed record ScalewayError(string? Type, string? Message, IReadOnlyList<ScalewayErrorDetail> Details)
{
    private static readonly ScalewayError Empty = new(null, null, []);

    internal static ScalewayError Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            var type = ReadString(root, "type");
            // Most errors carry "message"; the registry push endpoint uses "error" — accept either.
            var message = ReadString(root, "message") ?? ReadString(root, "error");
            var details = new List<ScalewayErrorDetail>();
            if (root.TryGetProperty("details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in detailsElement.EnumerateArray())
                {
                    details.Add(new ScalewayErrorDetail(ReadString(detail, "argument_name"), ReadString(detail, "reason"), ReadString(detail, "help_message")));
                }
            }

            return new ScalewayError(type, message, details);
        }
        catch (JsonException)
        {
            // A non-JSON body (an HTML 502, say) carries no structure; the caller falls back to the raw text.
            return Empty;
        }
    }

    /// <summary>A one-line human summary, e.g. <c>invalid_arguments: organization_id (required)</c>.</summary>
    internal string? Describe()
    {
        if (Type is null && Message is null && Details.Count == 0)
        {
            return null;
        }

        var fields = string.Join(", ", Details.Where(detail => detail.ArgumentName is not null).Select(detail => $"{detail.ArgumentName} ({detail.Reason ?? "invalid"})"));
        var head = Type is not null ? $"{Type}: {Message}" : Message;
        return fields.Length > 0 ? $"{head} [{fields}]" : head;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
