namespace AppGateway;

public sealed class HostnamesOptions
{
    public const string SectionName = "Hostnames";

    public const string WildcardKey = "*";

    public string App { get; init; } = string.Empty;

    public string BackOffice { get; init; } = string.Empty;

    // Wildcard routes (avatars, logos, email static) serve both public hostnames.
    public string[] AllHostnames => [App, BackOffice];

    public string? Resolve(string hostnameKey)
    {
        return hostnameKey switch
        {
            "App" => App,
            "BackOffice" => BackOffice,
            _ => null
        };
    }
}
