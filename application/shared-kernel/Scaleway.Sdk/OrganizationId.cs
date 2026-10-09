namespace Scaleway.Sdk;

/// <summary>
///     The Scaleway Organization the credentials belong to. Required by the org-scoped IAM operations
///     (applications, policies, groups, users) — Scaleway's schemas declare it required for only some of them,
///     so its absence surfaces as a 400 rather than a schema error. Distinct from <see cref="ProjectId" /> on
///     purpose; see that type for why.
/// </summary>
public readonly record struct OrganizationId
{
    private readonly Guid _value;

    private OrganizationId(Guid value)
    {
        _value = value;
    }

    /// <summary>The raw wire value: the hyphenated lowercase UUID Scaleway expects.</summary>
    public string Value => _value.ToString();

    /// <summary>
    ///     Parses a UUID into a OrganizationId. Rejects a non-UUID here rather than letting it surface as a 400
    ///     mid-deploy.
    /// </summary>
    /// <param name="value">The hyphenated UUID Scaleway issued for the organization.</param>
    /// <exception cref="ArgumentException">The value is not a UUID.</exception>
    public static OrganizationId Parse(string? value)
    {
        return TryParse(value, out var organizationId)
            ? organizationId
            : throw new ArgumentException($"'{value}' is not a valid Scaleway organization ID; expected a UUID.", nameof(value));
    }

    /// <summary>Parses without throwing, for input that may legitimately be absent or malformed.</summary>
    /// <param name="value">The hyphenated UUID Scaleway issued for the organization.</param>
    /// <param name="result">The parsed id, or <c>default</c> when this returns false.</param>
    public static bool TryParse(string? value, out OrganizationId result)
    {
        if (ScalewayIdentifier.TryParse(value, out var parsed))
        {
            result = new OrganizationId(parsed);
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
