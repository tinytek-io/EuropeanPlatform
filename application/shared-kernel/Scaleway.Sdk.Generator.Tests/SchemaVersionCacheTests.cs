using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     The pin check walks ~80 candidate URLs against an upstream that rate-limits, and almost all of the answers
///     are negatives that change on the order of months. Caching them is the half of being a good citizen that
///     does not depend on reading a refusal correctly — with one thing it must never do, which is remember a
///     refusal as if it were an answer.
/// </summary>
public sealed class SchemaVersionCacheTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"scaleway-schema-version-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, true);
        }
    }

    [Fact]
    public async Task Wrap_WhenAnAnswerWasCached_DoesNotAskUpstreamAgain()
    {
        var firstRun = await ProbeThrough(Load(), ("serverless-jobs", "v1alpha2", SchemaVersionAvailability.Published));

        var secondRun = new List<string>();
        var cache = Load();
        var availability = await cache.Wrap(Recording(secondRun, SchemaVersionAvailability.Published))("serverless-jobs", "v1alpha2");

        firstRun.Should().ContainSingle("the first run has nothing to reuse");
        secondRun.Should().BeEmpty("the answer was already on disk");
        availability.Should().Be(SchemaVersionAvailability.Published);
        cache.Reused.Should().Be(1);
        cache.Probed.Should().Be(0);
    }

    [Fact]
    public async Task Wrap_RemembersThatAVersionIsNotPublished()
    {
        // The negatives are the point: "no v2alpha1 exists" is most of the ~80 probes, and it is the answer least
        // likely to have changed since yesterday.
        await ProbeThrough(Load(), ("registry", "v2alpha1", SchemaVersionAvailability.NotPublished));

        var secondRun = new List<string>();
        var availability = await Load().Wrap(Recording(secondRun, SchemaVersionAvailability.Published))("registry", "v2alpha1");

        availability.Should().Be(SchemaVersionAvailability.NotPublished, "a cache that collapsed absence into 'unanswered' would re-probe every run");
        secondRun.Should().BeEmpty();
    }

    [Fact]
    public async Task Wrap_NeverRemembersAnUnknown()
    {
        // A rate limit, a timeout or a refusal carries no information. Holding one for a day would answer "we
        // could not tell" from disk without ever asking again — reintroducing exactly the silence the control
        // probe in SchemaVersionCheck exists to break, one layer further down.
        await ProbeThrough(Load(), ("serverless-jobs", "v1alpha2", SchemaVersionAvailability.Unknown));

        var secondRun = new List<string>();
        var availability = await Load().Wrap(Recording(secondRun, SchemaVersionAvailability.Published))("serverless-jobs", "v1alpha2");

        secondRun.Should().ContainSingle("an unanswered probe must leave the question open");
        availability.Should().Be(SchemaVersionAvailability.Published);
    }

    [Fact]
    public async Task Save_NeverWritesAnUnknownToDisk()
    {
        // The write-side half of the rule above, pinned on its own. Falsification is what forced this test to
        // exist: with only the round-trip test, storing an Unknown stayed green, because the read side filters
        // Unknowns out too and each guard hid the other. A run whose only probe went unanswered has learned
        // nothing and must write nothing.
        await ProbeThrough(Load(), ("serverless-jobs", "v1alpha2", SchemaVersionAvailability.Unknown));

        var path = Path.Combine(_cacheDirectory, "scaleway-schema-version-cache.json");
        var written = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";

        written.Should().NotContain("Unknown");
        written.Should().NotContain("serverless-jobs");
    }

    [Fact]
    public async Task Wrap_WhenAnAnswerIsOlderThanADay_AsksUpstreamAgain()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero));
        await ProbeThrough(Load(clock), ("vpc", "v3", SchemaVersionAvailability.NotPublished));

        clock.Advance(TimeSpan.FromHours(25));
        var secondRun = new List<string>();
        await Load(clock).Wrap(Recording(secondRun, SchemaVersionAvailability.NotPublished))("vpc", "v3");

        secondRun.Should().ContainSingle("a day-old answer is worth re-asking; a five-minute-old one is not");
    }

    [Fact]
    public async Task Wrap_AgesEachAnswerFromWhenItWasProbed_NotFromWhenTheFileWasLastWritten()
    {
        // The file accumulates answers across runs, so a single file-level timestamp would be refreshed by every
        // write and keep a week-old answer alive as long as somebody kept running the check.
        var clock = new TestClock(new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero));
        await ProbeThrough(Load(clock), ("vpc", "v3", SchemaVersionAvailability.NotPublished));

        clock.Advance(TimeSpan.FromHours(20));
        await ProbeThrough(Load(clock), ("registry", "v2", SchemaVersionAvailability.NotPublished));

        clock.Advance(TimeSpan.FromHours(6));
        var thirdRun = new List<string>();
        var cache = Load(clock);
        await cache.Wrap(Recording(thirdRun, SchemaVersionAvailability.NotPublished))("vpc", "v3");
        await cache.Wrap(Recording(thirdRun, SchemaVersionAvailability.NotPublished))("registry", "v2");

        thirdRun.Should().ContainSingle("26 hours old, it expired even though the file was rewritten 6 hours ago")
            .Which.Should().Be("vpc/v3");
    }

    [Fact]
    public async Task Load_WhenDisabled_NeitherReadsNorWrites()
    {
        await ProbeThrough(Load(), ("registry", "v2alpha1", SchemaVersionAvailability.NotPublished));

        var secondRun = new List<string>();
        var cache = Load(disabled: true);
        await cache.Wrap(Recording(secondRun, SchemaVersionAvailability.NotPublished))("registry", "v2alpha1");
        cache.Save();

        secondRun.Should().ContainSingle("SCW_SCHEMA_VERSION_CACHE_DISABLED=1 is how a caller forces a real probe");
        cache.Reused.Should().Be(0);
    }

    [Fact]
    public async Task Load_WhenDisabled_DoesNotCreateACacheFile()
    {
        var cache = Load(disabled: true);
        await cache.Wrap(Recording([], SchemaVersionAvailability.Published))("registry", "v1");
        cache.Save();

        Directory.Exists(_cacheDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Load_WhenTheFileIsCorrupt_TreatsItAsNoCacheRatherThanFailing()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(_cacheDirectory, "scaleway-schema-version-cache.json"), "{ not json");

        var probed = new List<string>();
        var availability = await Load().Wrap(Recording(probed, SchemaVersionAvailability.Published))("registry", "v1");

        availability.Should().Be(SchemaVersionAvailability.Published);
        probed.Should().ContainSingle();
    }

    [Fact]
    public async Task Load_WhenTheFileClaimsAnUnknown_IgnoresItRatherThanTrustingIt()
    {
        // Nothing writes one, so this is a hand-edited or truncated file. Trusting it would be the same failure as
        // caching one in the first place.
        Directory.CreateDirectory(_cacheDirectory);
        var payload = new
        {
            answers = new Dictionary<string, object>
            {
                ["registry/v2"] = new { availability = "Unknown", probed_at = DateTimeOffset.UtcNow.ToString("O") }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_cacheDirectory, "scaleway-schema-version-cache.json"), JsonSerializer.Serialize(payload));

        var probed = new List<string>();
        await Load().Wrap(Recording(probed, SchemaVersionAvailability.NotPublished))("registry", "v2");

        probed.Should().ContainSingle();
    }

    [Fact]
    public async Task Save_KeepsAnswersReusedFromAPreviousRun()
    {
        // A run that reuses one answer and probes another must not write a file holding only the new one, or the
        // cache would forget everything it did not re-ask.
        await ProbeThrough(Load(), ("registry", "v2", SchemaVersionAvailability.NotPublished));
        await ProbeThrough(Load(), ("vpc", "v3", SchemaVersionAvailability.NotPublished));

        var thirdRun = new List<string>();
        var cache = Load();
        await cache.Wrap(Recording(thirdRun, SchemaVersionAvailability.NotPublished))("registry", "v2");
        await cache.Wrap(Recording(thirdRun, SchemaVersionAvailability.NotPublished))("vpc", "v3");

        thirdRun.Should().BeEmpty();
        cache.Reused.Should().Be(2);
    }

    [Fact]
    public async Task Wrap_KeepsAnswersForOneVersionSeparateFromAnother()
    {
        await ProbeThrough(Load(), ("serverless-jobs", "v1alpha1", SchemaVersionAvailability.Published));

        var secondRun = new List<string>();
        var availability = await Load().Wrap(Recording(secondRun, SchemaVersionAvailability.NotPublished))("serverless-jobs", "v1alpha2");

        availability.Should().Be(SchemaVersionAvailability.NotPublished);
        secondRun.Should().ContainSingle("the pinned version and its successor are different questions");
    }

    private SchemaVersionCache Load(TimeProvider? timeProvider = null, bool disabled = false)
    {
        return SchemaVersionCache.Load(_cacheDirectory, disabled, timeProvider);
    }

    private static Func<string, string, Task<SchemaVersionAvailability>> Recording(List<string> probed, SchemaVersionAvailability answer)
    {
        return (slug, version) =>
        {
            probed.Add($"{slug}/{version}");
            return Task.FromResult(answer);
        };
    }

    private static async Task<List<string>> ProbeThrough(SchemaVersionCache cache, (string Slug, string Version, SchemaVersionAvailability Answer) probe)
    {
        var probed = new List<string>();
        await cache.Wrap(Recording(probed, probe.Answer))(probe.Slug, probe.Version);
        cache.Save();
        return probed;
    }

    /// <summary>A clock the test moves, so a 24-hour lifetime can be crossed without waiting for it.</summary>
    /// <param name="start">The moment the clock reads until it is advanced.</param>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan amount)
        {
            _now += amount;
        }
    }
}
