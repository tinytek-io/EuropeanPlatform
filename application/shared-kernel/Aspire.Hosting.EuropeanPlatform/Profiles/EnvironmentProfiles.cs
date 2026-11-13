using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.EuropeanPlatform.Profiles;

/// <summary>What an environment is for. It selects the environment's sizing, and it is fixed once the environment exists.</summary>
public enum EnvironmentType
{
    Review,
    Test,
    Staging,
    Production
}

/// <summary>The part a workload plays in a self-contained system or the platform; each role has its own sizing.</summary>
public enum WorkloadRole
{
    Api,
    Workers,
    Gateway,
    Migrator
}

/// <summary>
///     How a workload behaves, relative to the norm for its role. Named by usage rather than hardware, so each
///     environment type meets the same class with its own numbers. <see cref="Standard" /> unless a workload differs.
/// </summary>
public enum WorkloadClass
{
    Standard,
    MemoryIntensive
}

/// <summary>What one role and class gets in one environment type.</summary>
/// <param name="Replicas">The pod count; null for a migrator, which runs once per deploy as a Job.</param>
/// <param name="MemoryRequest">The Kubernetes memory request, e.g. <c>96Mi</c>.</param>
/// <param name="MemoryLimit">The Kubernetes memory limit, never below the request.</param>
public sealed record WorkloadSizing(int? Replicas, string MemoryRequest, string MemoryLimit);

/// <summary>
///     The platform team's sizing matrix (<c>cloud-infrastructure/scaleway/environment-profiles.yaml</c>): per role, per
///     usage class, per environment type. Terraform reads the same file's <c>infrastructure</c> section for the
///     cluster and database. Loading validates the whole matrix, so a missing or malformed entry fails the publish
///     with every problem listed, not just the first, rather than deploying a half-sized environment.
/// </summary>
public sealed partial class EnvironmentProfiles
{
    private readonly Dictionary<(WorkloadRole, WorkloadClass, EnvironmentType), WorkloadSizing> _sizing;

    private EnvironmentProfiles(Dictionary<(WorkloadRole, WorkloadClass, EnvironmentType), WorkloadSizing> sizing)
    {
        _sizing = sizing;
    }

    [GeneratedRegex("^([1-9][0-9]*)(Mi|Gi)$")]
    private static partial Regex MemoryQuantityPattern { get; }

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex KebabCasePattern { get; }

    /// <summary>The repository's profile matrix, located from the AppHost's directory.</summary>
    public static string DefaultPath(string appHostDirectory)
    {
        return Path.GetFullPath(Path.Combine(appHostDirectory, "..", "..", "cloud-infrastructure", "scaleway", "environment-profiles.yaml"));
    }

    public static EnvironmentProfiles Load(string path)
    {
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parses and validates a profile matrix; <paramref name="source" /> names it in the error.</summary>
    public static EnvironmentProfiles Parse(string yaml, string source)
    {
        ProfileDocument document;
        try
        {
            // Unknown keys are errors (no IgnoreUnmatchedProperties): a misspelt `memoryLimt` must not be ignored.
            document = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build().Deserialize<ProfileDocument?>(yaml)
                       ?? new ProfileDocument();
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException($"{source} is not a valid profile matrix: {exception.Message}", exception);
        }

        var problems = new List<string>();
        var sizing = new Dictionary<(WorkloadRole, WorkloadClass, EnvironmentType), WorkloadSizing>();

        foreach (var environmentType in Enum.GetValues<EnvironmentType>())
        {
            if (!document.Infrastructure.ContainsKey(KeyOf(environmentType)))
            {
                problems.Add($"infrastructure has no '{KeyOf(environmentType)}' entry.");
            }
        }

        problems.AddRange(document.Infrastructure.Keys.Where(key => !TryParse<EnvironmentType>(key, out _)).Select(key => $"infrastructure.{key} is not an environment type."));

        foreach (var (roleKey, classes) in document.Workloads)
        {
            if (!TryParse<WorkloadRole>(roleKey, out var role))
            {
                problems.Add($"workloads.{roleKey} is not a workload role ({Names<WorkloadRole>()}).");
                continue;
            }

            foreach (var (classKey, environments) in classes)
            {
                if (!TryParse<WorkloadClass>(classKey, out var workloadClass))
                {
                    problems.Add($"workloads.{roleKey}.{classKey} is not a workload class ({Names<WorkloadClass>()}).");
                    continue;
                }

                problems.AddRange(environments.Keys.Where(key => !TryParse<EnvironmentType>(key, out _)).Select(key => $"workloads.{roleKey}.{classKey}.{key} is not an environment type."));
                foreach (var environmentType in Enum.GetValues<EnvironmentType>())
                {
                    var path = $"workloads.{roleKey}.{classKey}.{KeyOf(environmentType)}";
                    if (!environments.TryGetValue(KeyOf(environmentType), out var entry))
                    {
                        problems.Add($"{path} is missing: every class sizes every environment type.");
                        continue;
                    }

                    var entryProblems = Validate(path, role, entry);
                    if (entryProblems.Count == 0)
                    {
                        sizing[(role, workloadClass, environmentType)] = new WorkloadSizing(entry.Replicas, entry.MemoryRequest!, entry.MemoryLimit!);
                    }

                    problems.AddRange(entryProblems);
                }
            }
        }

        foreach (var role in Enum.GetValues<WorkloadRole>())
        {
            if (!document.Workloads.TryGetValue(KeyOf(role), out var classes) || !classes.ContainsKey(KeyOf(WorkloadClass.Standard)))
            {
                problems.Add($"workloads.{KeyOf(role)}.standard is missing: every role needs its standard class.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"{source} is not a valid profile matrix:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", problems)}");
        }

        return new EnvironmentProfiles(sizing);
    }

    /// <summary>The sizing for a role and class in an environment type.</summary>
    /// <exception cref="InvalidOperationException">The matrix has no entry for that class of the role.</exception>
    public WorkloadSizing For(WorkloadRole role, WorkloadClass workloadClass, EnvironmentType environmentType)
    {
        return _sizing.TryGetValue((role, workloadClass, environmentType), out var sizing)
            ? sizing
            : throw new InvalidOperationException($"The profile matrix sizes no '{KeyOf(workloadClass)}' class for the {KeyOf(role)} role; add workloads.{KeyOf(role)}.{KeyOf(workloadClass)}.");
    }

    /// <summary>Reads an environment type as the tier files and the matrix spell it (<c>staging</c>).</summary>
    public static EnvironmentType ParseEnvironmentType(string value)
    {
        return TryParse<EnvironmentType>(value, out var environmentType)
            ? environmentType
            : throw new InvalidOperationException($"'{value}' is not an environment type ({Names<EnvironmentType>()}).");
    }

    private static List<string> Validate(string path, WorkloadRole role, SizingDocument entry)
    {
        var problems = new List<string>();
        if (role == WorkloadRole.Migrator && entry.Replicas is not null)
        {
            problems.Add($"{path}.replicas does not apply: a migrator runs once per deploy as a Job.");
        }

        if (role != WorkloadRole.Migrator && entry.Replicas is not >= 1)
        {
            problems.Add($"{path}.replicas must be at least 1.");
        }

        var request = Mebibytes(path, "memoryRequest", entry.MemoryRequest, problems);
        var limit = Mebibytes(path, "memoryLimit", entry.MemoryLimit, problems);
        if (request > limit)
        {
            problems.Add($"{path}.memoryRequest ({entry.MemoryRequest}) is above its memoryLimit ({entry.MemoryLimit}).");
        }

        return problems;
    }

    private static int Mebibytes(string path, string setting, string? quantity, List<string> problems)
    {
        var match = MemoryQuantityPattern.Match(quantity ?? string.Empty);
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value) * (match.Groups[2].Value == "Gi" ? 1024 : 1);
        }

        problems.Add($"{path}.{setting} must be a quantity in Mi or Gi, e.g. 96Mi; it is '{quantity}'.");
        return 0;
    }

    // The file spells enum members in kebab case (memory-intensive), the way Kubernetes and Terraform names read.
    private static string KeyOf<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        return KebabCasePattern.Replace(value.ToString(), "$1-$2").ToLowerInvariant();
    }

    private static bool TryParse<TEnum>(string key, out TEnum value) where TEnum : struct, Enum
    {
        var match = Enum.GetValues<TEnum>().Where(candidate => KeyOf(candidate) == key).ToArray();
        value = match.FirstOrDefault();
        return match.Length == 1;
    }

    private static string Names<TEnum>() where TEnum : struct, Enum
    {
        return string.Join(", ", Enum.GetValues<TEnum>().Select(KeyOf));
    }

    // The file's shape. Mutable and nullable because YamlDotNet deserializes into settable properties; the validated
    // sizing above is what the platform uses.
    [UsedImplicitly(ImplicitUseKindFlags.Assign | ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature, ImplicitUseTargetFlags.WithMembers)]
    private sealed class ProfileDocument
    {
        // Terraform's half; the SDK checks only that every environment type has one.
        public Dictionary<string, object> Infrastructure { get; set; } = [];

        public Dictionary<string, Dictionary<string, Dictionary<string, SizingDocument>>> Workloads { get; set; } = [];
    }

    [UsedImplicitly(ImplicitUseKindFlags.Assign | ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature, ImplicitUseTargetFlags.WithMembers)]
    private sealed class SizingDocument
    {
        public int? Replicas { get; set; }

        public string? MemoryRequest { get; set; }

        public string? MemoryLimit { get; set; }
    }
}
