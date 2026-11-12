namespace Scaleway.Sdk;

/// <summary>
///     The Scaleway Project a resource belongs to. Distinct from <see cref="OrganizationId" /> on purpose: both
///     are bare UUIDs and Scaleway accepts either in the same position (the <c>ProjectIdentifier</c> one-of group),
///     so as raw strings they are silently interchangeable — passing one where the other belongs is accepted by
///     the compiler and rejected by the API. Parsing also rejects a non-UUID up front, rather than letting it
///     surface as a 400 partway through a deploy.
/// </summary>
public readonly record struct ProjectId
{
    private readonly Guid _value;

    private ProjectId(Guid value)
    {
        _value = value;
    }

    /// <summary>The raw wire value: the hyphenated lowercase UUID Scaleway expects.</summary>
    public string Value => _value.ToString();

    /// <summary>Parses a UUID into a ProjectId. Rejects a non-UUID here rather than letting it surface as a 400 mid-deploy.</summary>
    /// <param name="value">The hyphenated UUID Scaleway issued for the project.</param>
    /// <exception cref="ArgumentException">The value is not a UUID.</exception>
    public static ProjectId Parse(string? value)
    {
        return TryParse(value, out var projectId)
            ? projectId
            : throw new ArgumentException($"'{value}' is not a valid Scaleway project ID; expected a UUID.", nameof(value));
    }

    /// <summary>Parses without throwing, for input that may legitimately be absent or malformed.</summary>
    /// <param name="value">The hyphenated UUID Scaleway issued for the project.</param>
    /// <param name="result">The parsed id, or <c>default</c> when this returns false.</param>
    public static bool TryParse(string? value, out ProjectId result)
    {
        if (ScalewayIdentifier.TryParse(value, out var parsed))
        {
            result = new ProjectId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The wire value — the same string as <see cref="Value" />, so interpolation sends what Scaleway expects.</summary>
    public override string ToString()
    {
        return Value;
    }
}
