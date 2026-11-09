using System.Text.RegularExpressions;
using JetBrains.Annotations;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BackOffice.StaffIdentity;

/// <summary>The groups a staff member can hold: everyone signing in is staff; admins also unlock admin actions.</summary>
public static class StaffGroups
{
    public const string Staff = "staff";

    public const string Admins = "admins";

    public static readonly string[] All = [Staff, Admins];
}

/// <summary>One person who may sign in. <see cref="Id" /> is the OIDC subject other tools key accounts on.</summary>
public sealed record StaffMember(string Id, string Email, string Name, string[] Groups, bool Active);

/// <summary>A tool that signs staff in through the back-office's OIDC provider.</summary>
public sealed record StaffIdentityClient(
    string ClientId,
    string DisplayName,
    string[] RedirectUris,
    string[] PostLogoutRedirectUris,
    string[] AllowedGroups
);

/// <summary>
///     Who may sign in to the back-office and which tools may sign them in — the environment's committed staff
///     identity file (cloud-infrastructure/staff-identity/&lt;environment&gt;.yaml), the single source of truth for
///     user management. Loaded once at startup and validated strictly: a typo, a duplicate, or an unknown group
///     fails the start instead of silently locking someone out or letting someone in.
/// </summary>
public sealed partial class StaffDirectory
{
    private readonly Dictionary<string, StaffIdentityClient> _clientsById;
    private readonly Dictionary<string, StaffMember> _staffByEmail;
    private readonly Dictionary<string, StaffMember> _staffById;

    private StaffDirectory(StaffMember[] staff, StaffIdentityClient[] clients)
    {
        Staff = staff;
        Clients = clients;
        _staffById = staff.ToDictionary(member => member.Id);
        _staffByEmail = staff.ToDictionary(member => member.Email);
        _clientsById = clients.ToDictionary(client => client.ClientId);
    }

    public StaffMember[] Staff { get; }

    public StaffIdentityClient[] Clients { get; }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex StaffIdPattern { get; }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex ClientIdPattern { get; }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern { get; }

    /// <summary>The active staff member with this email (compared case-insensitively), or null.</summary>
    public StaffMember? FindActiveByEmail(string email)
    {
        return _staffByEmail.TryGetValue(email.Trim().ToLowerInvariant(), out var member) && member.Active ? member : null;
    }

    /// <summary>The active staff member with this id, or null — a deactivated person is never found.</summary>
    public StaffMember? FindActiveById(string id)
    {
        return _staffById.TryGetValue(id, out var member) && member.Active ? member : null;
    }

    public StaffIdentityClient? FindClient(string clientId)
    {
        return _clientsById.GetValueOrDefault(clientId);
    }

    public static StaffDirectory Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The staff identity file '{path}' does not exist.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parses and validates the file content, reporting every problem at once.</summary>
    public static StaffDirectory Parse(string yaml, string source = "the staff identity file")
    {
        StaffIdentityFile file;
        try
        {
            // Unknown keys are errors (no IgnoreUnmatchedProperties): `is_active: false` must not be ignored.
            var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            file = deserializer.Deserialize<StaffIdentityFile?>(yaml) ?? new StaffIdentityFile();
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException($"{source} is not valid: {exception.Message}", exception);
        }

        var problems = new List<string>();
        var staff = (file.Staff ?? []).Select((entry, index) => ToStaffMember(entry, index, problems)).ToArray();
        var clients = (file.Clients ?? []).Select((entry, index) => ToClient(entry, index, problems)).ToArray();

        problems.AddRange(Duplicates(staff.Select(member => member.Id)).Select(id => $"staff id '{id}' is used more than once (ids are never reused)."));
        problems.AddRange(Duplicates(staff.Select(member => member.Email)).Select(email => $"staff email '{email}' is used more than once."));
        problems.AddRange(Duplicates(clients.Select(client => client.ClientId)).Select(id => $"client id '{id}' is used more than once."));

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"{source} is not valid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", problems)}");
        }

        return new StaffDirectory(staff, clients);
    }

    private static StaffMember ToStaffMember(StaffEntry entry, int index, List<string> problems)
    {
        var label = $"staff[{index}]";
        var id = entry.Id?.Trim() ?? "";
        var email = entry.Email?.Trim().ToLowerInvariant() ?? "";
        var name = entry.Name?.Trim() ?? "";
        var groups = entry.Groups?.Select(group => group.Trim()).ToArray() ?? [];

        if (!StaffIdPattern.IsMatch(id)) problems.Add($"{label} needs an id of lowercase letters, digits, '.', '_' or '-' (got '{id}').");
        if (!EmailPattern.IsMatch(email)) problems.Add($"{label} ({id}) needs a valid email (got '{email}').");
        if (name.Length == 0) problems.Add($"{label} ({id}) needs a name.");
        if (!groups.Contains(StaffGroups.Staff)) problems.Add($"{label} ({id}) must be in the '{StaffGroups.Staff}' group.");
        problems.AddRange(groups.Where(group => !StaffGroups.All.Contains(group)).Select(group => $"{label} ({id}) has the unknown group '{group}' (known: {string.Join(", ", StaffGroups.All)})."));

        return new StaffMember(id, email, name, groups.Distinct().ToArray(), entry.Active ?? true);
    }

    private static StaffIdentityClient ToClient(ClientEntry entry, int index, List<string> problems)
    {
        var label = $"clients[{index}]";
        var clientId = entry.ClientId?.Trim() ?? "";
        var displayName = entry.DisplayName?.Trim() ?? "";
        var redirectUris = entry.RedirectUris?.Select(uri => uri.Trim()).ToArray() ?? [];
        var postLogoutRedirectUris = entry.PostLogoutRedirectUris?.Select(uri => uri.Trim()).ToArray() ?? [];
        var allowedGroups = entry.AllowedGroups?.Select(group => group.Trim()).ToArray() ?? [];

        if (!ClientIdPattern.IsMatch(clientId)) problems.Add($"{label} needs a clientId of lowercase letters, digits or '-' (got '{clientId}').");
        if (displayName.Length == 0) problems.Add($"{label} ({clientId}) needs a displayName.");
        if (redirectUris.Length == 0) problems.Add($"{label} ({clientId}) needs at least one redirectUri.");
        if (allowedGroups.Length == 0) problems.Add($"{label} ({clientId}) needs at least one allowedGroup.");
        problems.AddRange(redirectUris.Concat(postLogoutRedirectUris).Where(uri => !IsAllowedRedirectUri(uri)).Select(uri => $"{label} ({clientId}) has the redirect URI '{uri}', which must be absolute https (http only for localhost) without a fragment."));
        problems.AddRange(allowedGroups.Where(group => !StaffGroups.All.Contains(group)).Select(group => $"{label} ({clientId}) allows the unknown group '{group}' (known: {string.Join(", ", StaffGroups.All)})."));

        return new StaffIdentityClient(clientId, displayName, redirectUris, postLogoutRedirectUris, allowedGroups.Distinct().ToArray());
    }

    private static bool IsAllowedRedirectUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Fragment.Length > 0) return false;
        return parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && (parsed.IsLoopback || parsed.Host.EndsWith(".localhost", StringComparison.Ordinal) || parsed.Host == "localhost"));
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> values)
    {
        return values.Where(value => value.Length > 0).GroupBy(value => value).Where(group => group.Count() > 1).Select(group => group.Key);
    }

    // The file's shape. Mutable and nullable because YamlDotNet deserializes into settable properties; the
    // records above are what the rest of the back-office sees, after validation.
    [UsedImplicitly(ImplicitUseKindFlags.Assign | ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature, ImplicitUseTargetFlags.WithMembers)]
    private sealed class StaffIdentityFile
    {
        public List<StaffEntry>? Staff { get; set; }

        public List<ClientEntry>? Clients { get; set; }
    }

    [UsedImplicitly(ImplicitUseKindFlags.Assign | ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature, ImplicitUseTargetFlags.WithMembers)]
    private sealed class StaffEntry
    {
        public string? Id { get; set; }

        public string? Email { get; set; }

        public string? Name { get; set; }

        public List<string>? Groups { get; set; }

        public bool? Active { get; set; }
    }

    [UsedImplicitly(ImplicitUseKindFlags.Assign | ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature, ImplicitUseTargetFlags.WithMembers)]
    private sealed class ClientEntry
    {
        public string? ClientId { get; set; }

        public string? DisplayName { get; set; }

        public List<string>? RedirectUris { get; set; }

        public List<string>? PostLogoutRedirectUris { get; set; }

        public List<string>? AllowedGroups { get; set; }
    }
}
