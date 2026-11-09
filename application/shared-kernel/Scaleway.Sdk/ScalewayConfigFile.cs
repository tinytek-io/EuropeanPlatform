using System.Text.RegularExpressions;

namespace Scaleway.Sdk;

/// <summary>
///     One named set of Scaleway settings in the shared configuration file. Every value is optional, as in the file
///     itself: a named profile inherits whatever it leaves out from the file's top-level (default) profile.
/// </summary>
/// <param name="AccessKey">The API key's access key (<c>access_key</c>).</param>
/// <param name="SecretKey">The API key's secret key (<c>secret_key</c>), sent as <c>X-Auth-Token</c>.</param>
/// <param name="DefaultOrganizationId">The organization (<c>default_organization_id</c>).</param>
/// <param name="DefaultProjectId">The project resources land in by default (<c>default_project_id</c>).</param>
/// <param name="DefaultRegion">The region, such as <c>fr-par</c> (<c>default_region</c>).</param>
/// <param name="DefaultZone">The zone, such as <c>fr-par-1</c> (<c>default_zone</c>).</param>
public sealed record ScalewayProfile(
    string? AccessKey,
    string? SecretKey,
    string? DefaultOrganizationId = null,
    string? DefaultProjectId = null,
    string? DefaultRegion = null,
    string? DefaultZone = null
);

/// <summary>
///     Reads and writes the Scaleway configuration file (<c>config.yaml</c>) that the scw CLI, the Go SDK, and the
///     Terraform provider share, so a profile written here is the profile they use and the scw binary is not needed
///     to manage one.
/// </summary>
/// <remarks>
///     The file is YAML, but only the shape those tools write is supported: top-level <c>key: value</c> settings
///     (the default profile) and a <c>profiles:</c> map of named profiles holding the same keys, indented with
///     spaces. That keeps the package free of a YAML dependency. Writing edits the file in place, so other
///     profiles, other settings, and the comments the scw CLI puts in the file all survive.
/// </remarks>
public static partial class ScalewayConfigFile
{
    /// <summary>The name that selects the file's top-level settings rather than an entry under <c>profiles:</c>.</summary>
    public const string DefaultProfileName = "default";

    private const string ProfilesKey = "profiles";

    private static readonly (string Key, Func<ScalewayProfile, string?> Value)[] ProfileKeys =
    [
        ("access_key", profile => profile.AccessKey),
        ("secret_key", profile => profile.SecretKey),
        ("default_organization_id", profile => profile.DefaultOrganizationId),
        ("default_project_id", profile => profile.DefaultProjectId),
        ("default_region", profile => profile.DefaultRegion),
        ("default_zone", profile => profile.DefaultZone)
    ];

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ProfileNamePattern { get; }

    /// <summary>
    ///     Where the tools look for the file: <c>SCW_CONFIG_PATH</c> when set, otherwise
    ///     <c>$XDG_CONFIG_HOME/scw/config.yaml</c>, otherwise <c>~/.config/scw/config.yaml</c> (on Windows too).
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable("SCW_CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;

            var configurationHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configurationHome))
            {
                configurationHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(configurationHome, "scw", "config.yaml");
        }
    }

    /// <summary>
    ///     Reads a profile, with the values it leaves out taken from the default profile, as the scw CLI resolves it.
    ///     Returns null when the file or the named profile does not exist.
    /// </summary>
    /// <param name="profileName">
    ///     The profile to read. Null selects the active one: <c>SCW_PROFILE</c> when set, otherwise the file's
    ///     <c>active_profile</c>, otherwise the default profile.
    /// </param>
    /// <param name="path">The file to read; defaults to <see cref="DefaultPath" />.</param>
    public static ScalewayProfile? ReadProfile(string? profileName = null, string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;

        var (defaultSettings, profiles) = Parse(File.ReadAllLines(path));

        profileName ??= Environment.GetEnvironmentVariable("SCW_PROFILE") is { Length: > 0 } environmentProfile
            ? environmentProfile
            : defaultSettings.GetValueOrDefault("active_profile") ?? DefaultProfileName;

        Dictionary<string, string> namedSettings = [];
        if (profileName != DefaultProfileName && !profiles.TryGetValue(profileName, out namedSettings!))
        {
            return null;
        }

        string? Lookup(string key)
        {
            return namedSettings.GetValueOrDefault(key) ?? defaultSettings.GetValueOrDefault(key);
        }

        return new ScalewayProfile(
            Lookup("access_key"),
            Lookup("secret_key"),
            Lookup("default_organization_id"),
            Lookup("default_project_id"),
            Lookup("default_region"),
            Lookup("default_zone")
        );
    }

    /// <summary>
    ///     Creates or updates a named profile under <c>profiles:</c>. Only the profile's non-null values are written;
    ///     its other keys, every other profile, and the rest of the file are left as they are. The file is created
    ///     when missing, readable by its owner only, and replaced atomically so a failed write never truncates it.
    /// </summary>
    /// <param name="profileName">The profile to write: letters, digits, <c>-</c> and <c>_</c>, and not <c>default</c>.</param>
    /// <param name="profile">The values to set.</param>
    /// <param name="path">The file to write; defaults to <see cref="DefaultPath" />.</param>
    public static void WriteProfile(string profileName, ScalewayProfile profile, string? path = null)
    {
        if (!ProfileNamePattern.IsMatch(profileName) || profileName == DefaultProfileName)
        {
            throw new ArgumentException($"'{profileName}' is not a profile name this file can hold.", nameof(profileName));
        }

        var values = ProfileKeys
            .Select(entry => (entry.Key, Value: entry.Value(profile)))
            .Where(entry => entry.Value is not null)
            .Select(entry => (entry.Key, Value: entry.Value!))
            .ToArray();
        if (values.Any(entry => entry.Value.Length == 0 || entry.Value.Any(character => char.IsWhiteSpace(character) || character is '#' or '"' or '\'')))
        {
            throw new ArgumentException("Profile values must be non-empty and contain no whitespace, quotes, or '#'.", nameof(profile));
        }

        path ??= DefaultPath;
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        SetProfileValues(lines, profileName, values);
        WriteOwnerOnly(path, string.Join('\n', lines) + "\n");
    }

    private static (Dictionary<string, string> DefaultSettings, Dictionary<string, Dictionary<string, string>> Profiles) Parse(IEnumerable<string> lines)
    {
        var defaultSettings = new Dictionary<string, string>();
        var profiles = new Dictionary<string, Dictionary<string, string>>();
        var inProfiles = false;
        Dictionary<string, string>? currentProfile = null;
        var currentProfileIndent = 0;

        foreach (var line in lines)
        {
            if (!TryReadEntry(line, out var indent, out var key, out var value)) continue;

            if (indent == 0)
            {
                inProfiles = key == ProfilesKey && value.Length == 0;
                currentProfile = null;
                if (!inProfiles) defaultSettings[key] = value;
                continue;
            }

            if (!inProfiles) continue;

            if (value.Length == 0 && (currentProfile is null || indent <= currentProfileIndent))
            {
                currentProfile = profiles[key] = [];
                currentProfileIndent = indent;
            }
            else if (currentProfile is not null && indent > currentProfileIndent)
            {
                currentProfile[key] = value;
            }
        }

        return (defaultSettings, profiles);
    }

    private static void SetProfileValues(List<string> lines, string profileName, (string Key, string Value)[] values)
    {
        var profilesLine = lines.FindIndex(line => TryReadEntry(line, out var indent, out var key, out var value) && indent == 0 && key == ProfilesKey && value.Length == 0);
        if (profilesLine < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"{ProfilesKey}:");
            AppendProfile(lines, lines.Count, profileName, values, 2);
            return;
        }

        // The profiles section runs until the next top-level setting; trailing blank and comment lines stay outside it.
        var sectionEnd = lines.FindIndex(profilesLine + 1, line => TryReadEntry(line, out var indent, out _, out _) && indent == 0);
        if (sectionEnd < 0) sectionEnd = lines.Count;
        var lastSectionEntry = profilesLine;
        for (var index = profilesLine + 1; index < sectionEnd; index++)
        {
            if (TryReadEntry(lines[index], out _, out _, out _)) lastSectionEntry = index;
        }

        var headerLine = -1;
        var headerIndent = 2;
        for (var index = profilesLine + 1; index <= lastSectionEntry; index++)
        {
            if (TryReadEntry(lines[index], out var indent, out var key, out var value) && value.Length == 0 && key == profileName)
            {
                headerLine = index;
                headerIndent = indent;
                break;
            }
        }

        if (headerLine < 0)
        {
            // A new profile takes the indent of the first existing one, so the map stays consistent.
            var firstEntry = lines.FindIndex(profilesLine + 1, line => TryReadEntry(line, out _, out _, out _));
            var siblingIndent = firstEntry > 0 && firstEntry <= lastSectionEntry ? lines[firstEntry].Length - lines[firstEntry].TrimStart(' ').Length : 2;
            AppendProfile(lines, lastSectionEntry + 1, profileName, values, siblingIndent);
            return;
        }

        var blockEnd = headerLine + 1;
        var lastValueLine = headerLine;
        var valueIndent = headerIndent + 2;
        var existingLines = new Dictionary<string, int>();
        for (; blockEnd <= lastSectionEntry; blockEnd++)
        {
            var line = lines[blockEnd];
            var lineIndent = line.Length - line.TrimStart(' ').Length;
            if (line.Trim().Length > 0 && lineIndent <= headerIndent) break;
            if (!TryReadEntry(line, out var indent, out var key, out _)) continue;

            existingLines[key] = blockEnd;
            lastValueLine = blockEnd;
            valueIndent = indent;
        }

        var insertAt = lastValueLine + 1;
        foreach (var (key, value) in values)
        {
            var entry = $"{new string(' ', valueIndent)}{key}: {value}";
            if (existingLines.TryGetValue(key, out var existingLine))
            {
                lines[existingLine] = entry;
            }
            else
            {
                lines.Insert(insertAt++, entry);
            }
        }
    }

    private static void AppendProfile(List<string> lines, int position, string profileName, (string Key, string Value)[] values, int indent)
    {
        var block = new List<string> { $"{new string(' ', indent)}{profileName}:" };
        block.AddRange(values.Select(entry => $"{new string(' ', indent + 2)}{entry.Key}: {entry.Value}"));
        lines.InsertRange(position, block);
    }

    // A "key: value" or "key:" line; blank and comment lines are not entries.
    private static bool TryReadEntry(string line, out int indent, out string key, out string value)
    {
        indent = line.Length - line.TrimStart(' ').Length;
        key = value = "";
        var content = line.Trim();
        if (content.Length == 0 || content.StartsWith('#')) return false;

        var separator = content.IndexOf(':');
        if (separator <= 0) return false;

        key = content[..separator].Trim();
        value = content[(separator + 1)..].Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
        return true;
    }

    private static void WriteOwnerOnly(string path, string content)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(folder))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var temporaryPath = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var ownerOnly = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) ownerOnly.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(temporaryPath, ownerOnly))
        {
            writer.Write(content);
        }

        File.Move(temporaryPath, path, true);
    }
}
