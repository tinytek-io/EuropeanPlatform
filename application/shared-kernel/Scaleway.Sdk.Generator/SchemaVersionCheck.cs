using System.Text.RegularExpressions;

namespace Scaleway.Sdk.Generator;

/// <summary>Where a Scaleway API version sits in the release progression: alpha, then beta, then GA.</summary>
public enum SchemaVersionStage
{
    /// <summary>An <c>alphaN</c> document.</summary>
    Alpha,

    /// <summary>A <c>betaN</c> document.</summary>
    Beta,

    /// <summary>A bare <c>vN</c> document, with no stage suffix.</summary>
    GeneralAvailability
}

/// <summary>
///     A Scaleway API version segment (<c>v1</c>, <c>v1beta1</c>, <c>v2alpha1</c>) parsed into something that can
///     be ordered, so "is anything newer published?" is a question about versions rather than about strings.
///     <para>
///         Ordering says nothing about maturity, and must not be read that way: IAM and Transactional Email are
///         both labelled <c>v1alpha1</c> and are both generally available, billed products. What the ordering
///         encodes is only the sequence in which Scaleway publishes documents at a URL.
///     </para>
/// </summary>
/// <param name="Major">The major version number.</param>
/// <param name="Stage">The release stage the suffix names.</param>
/// <param name="StageNumber">The number after the stage suffix, or <c>0</c> for a GA document that has none.</param>
public sealed record SchemaApiVersion(int Major, SchemaVersionStage Stage, int StageNumber) : IComparable<SchemaApiVersion>
{
    private static readonly Regex VersionPattern = new(@"^v(\d+)(?:(alpha|beta)(\d+))?$", RegexOptions.Compiled);

    /// <summary>The version as it appears in the document's URL.</summary>
    public string Text => Stage is SchemaVersionStage.GeneralAvailability
        ? $"v{Major}"
        : $"v{Major}{Stage.ToString().ToLowerInvariant()}{StageNumber}";

    public int CompareTo(SchemaApiVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var stage = Stage.CompareTo(other.Stage);
        return stage != 0 ? stage : StageNumber.CompareTo(other.StageNumber);
    }

    /// <summary>Parses a version segment, throwing on a shape Scaleway does not publish.</summary>
    /// <param name="version">The version segment, e.g. <c>v1beta1</c>.</param>
    public static SchemaApiVersion Parse(string version)
    {
        var match = VersionPattern.Match(version);
        if (!match.Success)
        {
            throw new InvalidOperationException($"'{version}' is not a Scaleway API version segment (expected vN, vNalphaM or vNbetaM).");
        }

        var major = int.Parse(match.Groups[1].Value);
        if (!match.Groups[2].Success)
        {
            return new SchemaApiVersion(major, SchemaVersionStage.GeneralAvailability, 0);
        }

        var stage = match.Groups[2].Value == "alpha" ? SchemaVersionStage.Alpha : SchemaVersionStage.Beta;
        return new SchemaApiVersion(major, stage, int.Parse(match.Groups[3].Value));
    }
}

/// <summary>Whether a schema document is served at a given version URL.</summary>
public enum SchemaVersionAvailability
{
    /// <summary>The document is served.</summary>
    Published,

    /// <summary>The document is definitively absent — upstream answered "not found".</summary>
    NotPublished,

    /// <summary>Upstream could not be asked, or answered something that is neither. No conclusion is available.</summary>
    Unknown
}

/// <summary>What kind of problem a pin has.</summary>
public enum SchemaVersionFindingKind
{
    /// <summary>Scaleway publishes a version above the pin, so the checked-in copy is a superseded API.</summary>
    NewerVersionPublished,

    /// <summary>The pinned document itself is gone, so a refresh would fail or fetch nothing.</summary>
    PinNotPublished,

    /// <summary>The probe could not answer, so nothing is known about this pin either way.</summary>
    ProbeInconclusive
}

/// <summary>A single problem found with one pin.</summary>
/// <param name="Kind">What kind of problem it is.</param>
/// <param name="Slug">The pin's product doc slug.</param>
/// <param name="Description">A one-line statement of the problem, naming the versions involved.</param>
public sealed record SchemaVersionFinding(SchemaVersionFindingKind Kind, string Slug, string Description);

/// <summary>The result of checking every pin against what Scaleway currently publishes.</summary>
/// <param name="Findings">Every problem found, most severe first.</param>
public sealed record SchemaVersionReport(IReadOnlyList<SchemaVersionFinding> Findings)
{
    /// <summary>
    ///     Whether any pin's state is unknown. Kept separate from the findings count because "nothing is wrong"
    ///     and "we could not tell" must never render the same way — a probe that silently degrades into a clean
    ///     bill of health is the failure this check exists to prevent, not a milder version of it.
    /// </summary>
    public bool HasInconclusive => Findings.Any(finding => finding.Kind is SchemaVersionFindingKind.ProbeInconclusive);

    /// <summary>A human-readable summary — one line per finding, then a count by kind.</summary>
    public string Render()
    {
        if (Findings.Count == 0)
        {
            return "No pin drift: every schema is pinned to the newest version Scaleway publishes.";
        }

        var lines = Findings.Select(finding => $"  {Describe(finding.Kind),-12} {finding.Slug}: {finding.Description}");
        var byKind = Findings.GroupBy(finding => finding.Kind).OrderBy(group => (int)group.Key);
        var summary = string.Join(", ", byKind.Select(group => $"{group.Count()} {Describe(group.Key).ToLowerInvariant()}"));
        return $"{string.Join('\n', lines)}\n{Findings.Count} finding(s): {summary}.";
    }

    private static string Describe(SchemaVersionFindingKind kind)
    {
        return kind switch
        {
            SchemaVersionFindingKind.NewerVersionPublished => "SUPERSEDED",
            SchemaVersionFindingKind.PinNotPublished => "MISSING",
            _ => "UNKNOWN"
        };
    }
}

/// <summary>
///     Asks whether each pin still names the newest document Scaleway publishes. This is the half of drift
///     <c>SchemaDriftComparer</c> cannot see (named rather than cross-referenced: developer-cli compiles this file
///     without it): that one compares a schema against its committed self, so a pin
///     that has been superseded for a year reports as perfectly stable — the document at the pinned URL genuinely
///     never changes, because Scaleway freezes superseded documents rather than retiring them.
///     <para>
///         The upstream probe is a seam (<c>probe</c>) rather than an HTTP call here: the generator does no
///         network I/O, the developer CLI supplies the real probe, and the tests supply a table.
///     </para>
/// </summary>
public static class SchemaVersionCheck
{
    // Scaleway has never published past betaN with N above 2; nine leaves headroom without costing requests,
    // because a run stops at its first absent number rather than probing every entry.
    private const int MaxStageNumber = 9;

    /// <summary>
    ///     Probes every pin and reports the ones that no longer name the newest published document.
    /// </summary>
    /// <param name="pins">The pins to check.</param>
    /// <param name="probe">Answers whether a (slug, version) document is served upstream.</param>
    public static async Task<SchemaVersionReport> CompareAsync(IEnumerable<SchemaPin> pins, Func<string, string, Task<SchemaVersionAvailability>> probe)
    {
        var findings = new List<SchemaVersionFinding>();

        foreach (var pin in pins)
        {
            var pinned = SchemaApiVersion.Parse(pin.Version);

            // The pinned document is probed first as a control. Every conclusion below reads "not published" as
            // evidence that no newer version exists, and that inference is worthless unless the probe can be shown
            // to return "published" for a document known to be there. www.scaleway.com answers 429 under load, and
            // a 429 counted as absent turns a stale pin into a clean bill of health — the exact silence this check
            // exists to break, reintroduced one layer up.
            var control = await probe(pin.Slug, pin.Version);
            if (control is SchemaVersionAvailability.NotPublished)
            {
                findings.Add(new SchemaVersionFinding(SchemaVersionFindingKind.PinNotPublished, pin.Slug, $"pinned to {pin.Version}, which Scaleway no longer serves"));
                continue;
            }

            if (control is SchemaVersionAvailability.Unknown)
            {
                findings.Add(new SchemaVersionFinding(SchemaVersionFindingKind.ProbeInconclusive, pin.Slug, $"upstream did not answer for {pin.Version}, so nothing is known about newer versions"));
                continue;
            }

            var (newest, unanswered) = await WalkCandidatesAsync(pin.Slug, pinned, probe);
            if (unanswered is not null)
            {
                findings.Add(new SchemaVersionFinding(SchemaVersionFindingKind.ProbeInconclusive, pin.Slug, $"upstream did not answer for {unanswered}, so newer versions could not be ruled out"));
                continue;
            }

            if (newest is not null)
            {
                findings.Add(new SchemaVersionFinding(SchemaVersionFindingKind.NewerVersionPublished, pin.Slug, $"pinned to {pin.Version} but {newest.Text} is published — recorded reason: {pin.Rationale}"));
            }
        }

        var ordered = findings
            .OrderBy(finding => (int)finding.Kind)
            .ThenBy(finding => finding.Slug, StringComparer.Ordinal)
            .ToList();
        return new SchemaVersionReport(ordered);
    }

    /// <summary>
    ///     The versions above <paramref name="pinned" /> worth asking about, grouped into runs that a caller stops
    ///     probing at the first absent entry.
    ///     <para>
    ///         The runs are what keep the request count survivable: a GA pin costs three probes rather than the
    ///         twenty-odd a flat candidate list would. The trade is that a stage with a hole in it
    ///         (<c>alpha2</c> absent, <c>alpha3</c> present) stops early — Scaleway does not skip numbers, and the
    ///         next major's run is probed regardless, which is where a real successor shows up.
    ///     </para>
    /// </summary>
    /// <param name="pinned">The pinned version to look above.</param>
    internal static IEnumerable<IReadOnlyList<SchemaApiVersion>> CandidateRuns(SchemaApiVersion pinned)
    {
        // One major ahead is enough: a product two majors past its pin has published the intermediate one, which
        // this reports, and reporting the nearer successor is what a reviewer acts on anyway.
        for (var major = pinned.Major; major <= pinned.Major + 1; major++)
        {
            foreach (var stage in (SchemaVersionStage[])[SchemaVersionStage.Alpha, SchemaVersionStage.Beta])
            {
                var run = Enumerable.Range(1, MaxStageNumber)
                    .Select(number => new SchemaApiVersion(major, stage, number))
                    .Where(candidate => candidate.CompareTo(pinned) > 0)
                    .ToArray();

                if (run.Length > 0)
                {
                    yield return run;
                }
            }

            var generalAvailability = new SchemaApiVersion(major, SchemaVersionStage.GeneralAvailability, 0);
            if (generalAvailability.CompareTo(pinned) > 0)
            {
                yield return [generalAvailability];
            }
        }
    }

    private static async Task<(SchemaApiVersion? Newest, string? Unanswered)> WalkCandidatesAsync(string slug, SchemaApiVersion pinned, Func<string, string, Task<SchemaVersionAvailability>> probe)
    {
        SchemaApiVersion? newest = null;

        foreach (var run in CandidateRuns(pinned))
        {
            foreach (var candidate in run)
            {
                var availability = await probe(slug, candidate.Text);
                if (availability is SchemaVersionAvailability.Unknown)
                {
                    return (newest, candidate.Text);
                }

                if (availability is SchemaVersionAvailability.NotPublished)
                {
                    break;
                }

                newest = candidate;
            }
        }

        return (newest, null);
    }
}
