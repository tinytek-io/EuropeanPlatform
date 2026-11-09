using System.Globalization;
using System.Text.Json;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     A local memory of what <c>www.scaleway.com</c> answered, so checking the pins twice in a day costs one run
///     of probes rather than two. The pin check walks roughly eighty candidate URLs, and the overwhelming majority
///     of the answers are negatives — "no <c>v2alpha1</c> is published" changes on the order of months, and asking
///     again every run is volume Scaleway has no reason to serve. It rate-limits, which is how the check found its
///     own worst failure mode (see <see cref="SchemaVersionCheck" />); asking less is the half of the fix that
///     does not depend on reading a 429 correctly.
///     <para>
///         Same scheme as <c>ScalewayPricingCatalogClient</c>'s catalog cache — <c>~/.platformplatform</c>, a
///         24-hour lifetime, an environment variable that turns it off, and a corrupt or unreadable file treated
///         as no file at all — rather than a second scheme to learn. It differs in one way, and deliberately: an
///         answer carries its own timestamp instead of the file carrying one for all of them, because this file
///         accumulates answers across runs. A single file-level stamp is refreshed by every write, which would
///         keep a week-old answer alive indefinitely as long as somebody kept running the check.
///     </para>
/// </summary>
public sealed class SchemaVersionCache
{
    private const string CacheFileName = "scaleway-schema-version-cache.json";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    private readonly Dictionary<string, CachedAnswer> _answers;

    private readonly string _cacheDirectory;

    private readonly bool _disabled;

    private readonly TimeProvider _timeProvider;

    private bool _hasNewAnswers;

    private SchemaVersionCache(string cacheDirectory, bool disabled, TimeProvider timeProvider, Dictionary<string, CachedAnswer> answers)
    {
        _cacheDirectory = cacheDirectory;
        _disabled = disabled;
        _timeProvider = timeProvider;
        _answers = answers;
    }

    /// <summary>How many probes this run answered from the cache instead of asking upstream.</summary>
    public int Reused { get; private set; }

    /// <summary>How many probes this run actually sent upstream.</summary>
    public int Probed { get; private set; }

    /// <summary>
    ///     Reads the cache, dropping answers older than 24 hours. A missing, corrupt or unreadable file is not an
    ///     error: it means every probe is asked upstream, which is what happened before there was a cache.
    /// </summary>
    /// <param name="cacheDirectory">Where the cache file lives; defaults to <c>~/.platformplatform</c>.</param>
    /// <param name="disabled">
    ///     Skips both reading and writing. Defaults to whether <c>SCW_SCHEMA_VERSION_CACHE_DISABLED</c> is
    ///     <c>1</c>, which is how a caller forces a run to talk to upstream.
    /// </param>
    /// <param name="timeProvider">Ages answers and stamps new ones; defaults to <see cref="TimeProvider.System" />.</param>
    public static SchemaVersionCache Load(string? cacheDirectory = null, bool? disabled = null, TimeProvider? timeProvider = null)
    {
        var directory = cacheDirectory ?? DefaultCacheDirectory();
        var isDisabled = disabled ?? Environment.GetEnvironmentVariable("SCW_SCHEMA_VERSION_CACHE_DISABLED") == "1";
        var clock = timeProvider ?? TimeProvider.System;
        var answers = isDisabled ? [] : ReadAnswers(Path.Combine(directory, CacheFileName), clock.GetUtcNow());

        return new SchemaVersionCache(directory, isDisabled, clock, answers);
    }

    /// <summary>
    ///     Wraps an upstream probe so a question already answered is not asked again. The result is the same
    ///     <c>(slug, version)</c> probe <see cref="SchemaVersionCheck.CompareAsync" /> takes, which is what keeps
    ///     the caching out of the check's own logic.
    /// </summary>
    /// <param name="probe">The upstream probe to consult when the cache has no answer.</param>
    public Func<string, string, Task<SchemaVersionAvailability>> Wrap(Func<string, string, Task<SchemaVersionAvailability>> probe)
    {
        return async (slug, version) =>
        {
            var key = KeyFor(slug, version);

            if (_answers.TryGetValue(key, out var cached))
            {
                Reused++;
                return cached.Availability;
            }

            var availability = await probe(slug, version);
            Probed++;

            // Unknown is the absence of an answer rather than one: it is what a rate limit, a timeout or a refusal
            // produces. Storing it would hold "we could not tell" for a day and answer it from disk without ever
            // asking again — the silence the control probe exists to break, preserved rather than reported.
            if (availability is not SchemaVersionAvailability.Unknown)
            {
                _answers[key] = new CachedAnswer(availability, _timeProvider.GetUtcNow());
                _hasNewAnswers = true;
            }

            return availability;
        };
    }

    /// <summary>
    ///     Writes the answers gathered this run alongside the ones reused from the previous. Best-effort: a cache
    ///     that cannot be written costs the next run some requests and nothing else.
    /// </summary>
    public void Save()
    {
        if (_disabled || !_hasNewAnswers)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            var answers = _answers.ToDictionary(
                entry => entry.Key,
                entry => new Dictionary<string, string>
                {
                    ["availability"] = entry.Value.Availability.ToString(),
                    ["probed_at"] = entry.Value.ProbedAt.ToString("O", CultureInfo.InvariantCulture)
                }
            );

            var path = Path.Combine(_cacheDirectory, CacheFileName);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { answers }));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never fail a schema refresh over a cache write.
        }
    }

    private static Dictionary<string, CachedAnswer> ReadAnswers(string path, DateTimeOffset now)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("answers", out var answers))
            {
                return [];
            }

            var result = new Dictionary<string, CachedAnswer>(StringComparer.Ordinal);
            foreach (var answer in answers.EnumerateObject())
            {
                var availability = answer.Value.TryGetProperty("availability", out var availabilityProperty) ? availabilityProperty.GetString() : null;
                var probedAt = answer.Value.TryGetProperty("probed_at", out var probedAtProperty) ? probedAtProperty.GetString() : null;

                if (!Enum.TryParse<SchemaVersionAvailability>(availability, out var parsedAvailability) ||
                    !DateTimeOffset.TryParse(probedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedProbedAt))
                {
                    continue;
                }

                // An Unknown can only get here by being written by hand, and it must not be trusted any more than
                // one probed this run would be.
                if (parsedAvailability is not SchemaVersionAvailability.Unknown && now - parsedProbedAt <= CacheLifetime)
                {
                    result[answer.Name] = new CachedAnswer(parsedAvailability, parsedProbedAt);
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string KeyFor(string slug, string version)
    {
        return $"{slug}/{version}";
    }

    // The same directory the SDK's pricing catalog caches into, so a developer has one place to clear rather than
    // one per cache.
    private static string DefaultCacheDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformplatform");
    }

    private sealed record CachedAnswer(SchemaVersionAvailability Availability, DateTimeOffset ProbedAt);
}
