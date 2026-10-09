namespace Scaleway.Sdk;

/// <summary>
///     Account-level credentials for the Scaleway API: who you are, not what you are deploying into.
///     <see cref="AccessKey" />, <see cref="SecretKey" /> and <see cref="OrganizationId" /> are <c>required</c>
///     because no authenticated call can succeed without the first two, and the org-scoped IAM operations reject
///     requests without the third — a requirement Scaleway's schemas declare for only some of those operations,
///     so its absence used to surface as a 400 mid-deploy. Values are typically sourced from <c>SCW_*</c>
///     environment variables via <see cref="FromEnvironment" />, which validates them rather than yielding nulls.
/// </summary>
public sealed class ScalewayCredentialConfig
{
    /// <summary>Read from <c>SCW_ACCESS_KEY</c>. Used for container registry login.</summary>
    public required string AccessKey { get; init; }

    /// <summary>Read from <c>SCW_SECRET_KEY</c>. Sent as the <c>X-Auth-Token</c> header on every request.</summary>
    public required string SecretKey { get; init; }

    /// <summary>Read from <c>SCW_DEFAULT_ORGANIZATION_ID</c>.</summary>
    public required OrganizationId OrganizationId { get; init; }

    /// <summary>
    ///     Read from <c>SCW_DEFAULT_PROJECT_ID</c>. Optional here because project scope is a property of a
    ///     deployment rather than of an account: creating a project, listing projects, and the org-scoped IAM
    ///     operations all work without one.
    /// </summary>
    public ProjectId? DefaultProjectId { get; init; }

    /// <summary>Read from <c>SCW_DEFAULT_REGION</c>; defaults to <see cref="ScalewayRegion.FrPar" />.</summary>
    public ScalewayRegion DefaultRegion { get; init; } = ScalewayRegion.FrPar;

    /// <summary>Read from <c>SCW_DEFAULT_ZONE</c>; defaults to <see cref="ScalewayZone.FrPar1" />.</summary>
    public ScalewayZone DefaultZone { get; init; } = ScalewayZone.FrPar1;

    /// <summary>Read from <c>SCW_API_URL</c>; defaults to the public Scaleway API host.</summary>
    public string ApiUrl { get; init; } = "https://api.scaleway.com";

    /// <summary>
    ///     Whether <see cref="ApiUrl" /> points at a loopback address, which means a local mock rather than
    ///     Scaleway. Callers use it to decide what a run is allowed to do — skip reading real credentials, or
    ///     drop a "this costs money" warning that would not be true.
    /// </summary>
    public bool TargetsLocalMock => IsLoopback(ApiUrl);

    /// <summary>
    ///     The same question answered before a config exists, for callers that must decide whether to go looking
    ///     for credentials at all. Lives here because this type already owns what <c>SCW_API_URL</c> means; a
    ///     caller reading that variable itself would be reimplementing the transport's own configuration.
    /// </summary>
    public static bool EnvironmentTargetsLocalMock()
    {
        return IsLoopback(Environment.GetEnvironmentVariable("SCW_API_URL"));
    }

    private static bool IsLoopback(string? apiUrl)
    {
        return Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    /// <summary>
    ///     Builds a <see cref="ScalewayCredentialConfig" /> from <c>SCW_*</c> environment variables, with optional
    ///     explicit overrides for the values most commonly set per-environment. Throws when a required variable is
    ///     missing or malformed, naming the variable — the deploy cannot succeed without it, so failing here beats
    ///     failing against the API several steps later.
    /// </summary>
    public static ScalewayCredentialConfig FromEnvironment(
        string? accessKey = null,
        string? secretKey = null,
        string? defaultProjectId = null,
        ScalewayRegion? defaultRegion = null,
        string? organizationId = null)
    {
        return FromLookup(Environment.GetEnvironmentVariable, accessKey, secretKey, defaultProjectId, defaultRegion, organizationId);
    }

    // Takes the variable lookup as a parameter so the validation can be tested without mutating process-global
    // environment variables, which would race across parallel test runs.
    internal static ScalewayCredentialConfig FromLookup(
        Func<string, string?> lookup,
        string? accessKey = null,
        string? secretKey = null,
        string? defaultProjectId = null,
        ScalewayRegion? defaultRegion = null,
        string? organizationId = null)
    {
        return new ScalewayCredentialConfig
        {
            AccessKey = Required(accessKey ?? lookup("SCW_ACCESS_KEY"), "SCW_ACCESS_KEY"),
            SecretKey = Required(secretKey ?? lookup("SCW_SECRET_KEY"), "SCW_SECRET_KEY"),
            OrganizationId = RequiredOrganizationId(organizationId ?? lookup("SCW_DEFAULT_ORGANIZATION_ID")),
            DefaultProjectId = OptionalProjectId(defaultProjectId ?? lookup("SCW_DEFAULT_PROJECT_ID")),
            DefaultRegion = defaultRegion ?? ParseRegion(lookup("SCW_DEFAULT_REGION")),
            DefaultZone = ParseZone(lookup("SCW_DEFAULT_ZONE")),
            ApiUrl = lookup("SCW_API_URL") ?? "https://api.scaleway.com"
        };
    }

    private static string Required(string? value, string variableName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{variableName} is required but was not set.")
            : value;
    }

    private static OrganizationId RequiredOrganizationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("SCW_DEFAULT_ORGANIZATION_ID is required but was not set.");
        }

        // Passing the organisation *name* is the mistake this catches: it is what the Scaleway console shows
        // most prominently, and sending it produces a 400 that names no variable.
        return OrganizationId.TryParse(value, out var organizationId)
            ? organizationId
            : throw new InvalidOperationException($"SCW_DEFAULT_ORGANIZATION_ID is set to '{value}', which is not a valid UUID.");
    }

    private static ProjectId? OptionalProjectId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ProjectId.TryParse(value, out var projectId)
            ? projectId
            : throw new InvalidOperationException($"SCW_DEFAULT_PROJECT_ID is set to '{value}', which is not a valid UUID.");
    }

    private static ScalewayRegion ParseRegion(string? value)
    {
        return value is not null ? ScalewayRegionExtensions.ParseRegion(value) : ScalewayRegion.FrPar;
    }

    private static ScalewayZone ParseZone(string? value)
    {
        return value is not null ? ScalewayZoneExtensions.ParseZone(value) : ScalewayZone.FrPar1;
    }
}
