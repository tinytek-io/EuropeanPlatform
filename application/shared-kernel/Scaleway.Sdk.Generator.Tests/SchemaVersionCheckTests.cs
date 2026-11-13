using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     The check that asks whether a pin still names the newest document Scaleway publishes. Upstream is supplied
///     as a table of published version URLs, so these run offline and the interesting cases (a rate limit, a
///     withdrawn document) are reachable at all.
/// </summary>
public sealed class SchemaVersionCheckTests
{
    [Fact]
    public async Task CompareAsync_WhenThePinIsTheNewestPublishedVersion_ReportsNothing()
    {
        var report = await Check("v1", ["v1"]);

        report.Findings.Should().BeEmpty();
        report.HasInconclusive.Should().BeFalse();
        report.Render().Should().Be("No pin drift: every schema is pinned to the newest version Scaleway publishes.");
    }

    [Fact]
    public async Task CompareAsync_WhenANewerVersionIsPublished_ReportsItAsSuperseded()
    {
        // The serverless-jobs shape: both documents serve 200, so only a probe above the pin can tell them apart.
        var report = await Check("v1alpha1", ["v1alpha1", "v1alpha2"]);

        report.Findings.Should().ContainSingle().Which.Kind.Should().Be(SchemaVersionFindingKind.NewerVersionPublished);
        report.Findings[0].Description.Should().Contain("pinned to v1alpha1 but v1alpha2 is published");
    }

    [Fact]
    public async Task CompareAsync_ReportsTheNewestPublishedVersion_NotTheFirstOneAboveThePin()
    {
        var report = await Check("v1alpha1", ["v1alpha1", "v1alpha2", "v1alpha3", "v1beta1", "v1"]);

        report.Findings.Should().ContainSingle().Which.Description.Should().Contain("but v1 is published");
    }

    [Fact]
    public async Task CompareAsync_FindsAGeneralAvailabilitySuccessorToABetaPin()
    {
        // Scaleway publishes no v1beta2 between them, so a walk that stopped at the first absent candidate within
        // the stage would miss the GA document entirely. Each stage is its own run for exactly this reason.
        var report = await Check("v1beta1", ["v1beta1", "v1"]);

        report.Findings.Should().ContainSingle().Which.Description.Should().Contain("but v1 is published");
    }

    [Fact]
    public async Task CompareAsync_FindsANextMajorSuccessorToAGeneralAvailabilityPin()
    {
        var report = await Check("v1", ["v1", "v2alpha1"]);

        report.Findings.Should().ContainSingle().Which.Description.Should().Contain("but v2alpha1 is published");
    }

    [Fact]
    public async Task CompareAsync_WhenUpstreamCannotAnswerForThePin_ReportsInconclusiveRatherThanUpToDate()
    {
        // The failure this check exists to prevent, one layer up: www.scaleway.com answers 429 under load, and a
        // refusal counted as "not published" turns every stale pin into a clean bill of health. The pinned document
        // is probed as a control precisely so a probe that cannot answer is not allowed to conclude anything.
        var report = await Check("v1alpha1", ["v1alpha1", "v1alpha2"], "v1alpha1");

        report.HasInconclusive.Should().BeTrue();
        report.Findings.Should().ContainSingle().Which.Kind.Should().Be(SchemaVersionFindingKind.ProbeInconclusive);
        report.Findings[0].Description.Should().Contain("nothing is known about newer versions");
    }

    [Fact]
    public async Task CompareAsync_WhenUpstreamCannotAnswerForACandidate_ReportsInconclusiveRatherThanUpToDate()
    {
        var report = await Check("v1alpha1", ["v1alpha1"], "v1alpha2");

        report.HasInconclusive.Should().BeTrue();
        report.Findings.Should().ContainSingle().Which.Description.Should().Contain("newer versions could not be ruled out");
    }

    [Fact]
    public async Task CompareAsync_WhenThePinnedDocumentIsGone_ReportsItAsMissing()
    {
        var report = await Check("v1alpha1", ["v1"]);

        report.Findings.Should().ContainSingle().Which.Kind.Should().Be(SchemaVersionFindingKind.PinNotPublished);
        report.Findings[0].Description.Should().Contain("which Scaleway no longer serves");
    }

    [Fact]
    public async Task CompareAsync_ChecksEveryPin()
    {
        SchemaPin[] pins =
        [
            new("first", "v1", "because"),
            new("second", "v1alpha1", "because")
        ];

        // Only the second pin has a successor upstream; the first is at the newest version it has.
        string[] published = ["first/v1", "second/v1alpha1", "second/v1alpha2"];

        var report = await SchemaVersionCheck.CompareAsync(pins, (slug, version) =>
            Task.FromResult(published.Contains($"{slug}/{version}") ? SchemaVersionAvailability.Published : SchemaVersionAvailability.NotPublished)
        );

        report.Findings.Should().ContainSingle().Which.Slug.Should().Be("second");
    }

    [Fact]
    public async Task CompareAsync_CostsAHandfulOfProbesPerPin_NotOnePerCandidateVersion()
    {
        var probed = new List<string>();

        await SchemaVersionCheck.CompareAsync([new SchemaPin("slug", "v1", "because")], (_, version) =>
            {
                probed.Add(version);
                return Task.FromResult(version == "v1" ? SchemaVersionAvailability.Published : SchemaVersionAvailability.NotPublished);
            }
        );

        // A run stops at its first absent entry, so a GA pin costs the control plus one alpha, one beta and one GA
        // of the next major. Flattening the runs would multiply this by the stage-number ceiling, against an
        // upstream that rate-limits — the budget is a property worth pinning, not an implementation detail.
        probed.Should().Equal("v1", "v2alpha1", "v2beta1", "v2");
    }

    [Fact]
    public void CandidateRuns_NeverProbeThePinOrAnythingBelowIt()
    {
        var pinned = SchemaApiVersion.Parse("v1beta1");

        var candidates = SchemaVersionCheck.CandidateRuns(pinned).SelectMany(run => run).ToArray();

        candidates.Should().NotBeEmpty();
        candidates.Should().OnlyContain(candidate => candidate.CompareTo(pinned) > 0);
    }

    [Theory]
    [InlineData("v1alpha1", "v1alpha2")]
    [InlineData("v1alpha9", "v1beta1")]
    [InlineData("v1beta1", "v1")]
    [InlineData("v1", "v2alpha1")]
    public void SchemaApiVersion_OrdersAlphaBelowBetaBelowGeneralAvailability(string lower, string higher)
    {
        SchemaApiVersion.Parse(lower).CompareTo(SchemaApiVersion.Parse(higher)).Should().BeNegative();
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v1alpha1")]
    [InlineData("v2beta3")]
    [InlineData("v10")]
    public void SchemaApiVersion_RoundTripsThroughTheUrlSegment(string version)
    {
        SchemaApiVersion.Parse(version).Text.Should().Be(version);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("v")]
    [InlineData("v1gamma1")]
    [InlineData("v1alpha")]
    [InlineData("V1")]
    public void SchemaApiVersion_RejectsAShapeScalewayDoesNotPublish(string version)
    {
        var parse = () => SchemaApiVersion.Parse(version);

        parse.Should().Throw<InvalidOperationException>().WithMessage($"*'{version}'*");
    }

    [Fact]
    public void Render_DistinguishesUpToDateFromCouldNotTell()
    {
        var inconclusive = new SchemaVersionReport([new SchemaVersionFinding(SchemaVersionFindingKind.ProbeInconclusive, "jobs", "upstream did not answer")]);

        var rendered = inconclusive.Render();

        rendered.Should().Contain("UNKNOWN      jobs: upstream did not answer");
        rendered.Should().Contain("1 finding(s): 1 unknown.");
        rendered.Should().NotContain("No pin drift");
    }

    /// <summary>
    ///     Runs the check for a single pin against a fixed set of published versions.
    /// </summary>
    /// <param name="pinnedVersion">The version the pin names.</param>
    /// <param name="publishedVersions">Every version upstream serves; anything else answers "not published".</param>
    /// <param name="unanswerable">A version upstream refuses to answer for, standing in for a rate limit.</param>
    private static Task<SchemaVersionReport> Check(string pinnedVersion, string[] publishedVersions, string? unanswerable = null)
    {
        return SchemaVersionCheck.CompareAsync([new SchemaPin("slug", pinnedVersion, "because")], (_, version) =>
            Task.FromResult(version == unanswerable
                ? SchemaVersionAvailability.Unknown
                : publishedVersions.Contains(version)
                    ? SchemaVersionAvailability.Published
                    : SchemaVersionAvailability.NotPublished
            )
        );
    }
}
