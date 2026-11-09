using System.CommandLine;
using System.Net;
using System.Reflection;
using DeveloperCli.Installation;
using Scaleway.Sdk;
using Scaleway.Sdk.Generator;
using Spectre.Console;

namespace DeveloperCli.Commands.ScalewaySdk;

internal sealed class RefreshScalewaySchemasCommand : Command
{
    private const int ProbeAttempts = 3;

    // How long to wait between upstream probes, and how long to back off when one is refused. www.scaleway.com
    // rate-limits: probing the version ladder in parallel returns 429 for everything, and a 429 read as "absent"
    // reports every stale pin as current. Pacing keeps the answers real; SchemaVersionCheck's control probe is
    // what catches it when they are not.
    private static readonly TimeSpan ProbePacing = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan ProbeBackoff = TimeSpan.FromSeconds(5);

    // Every request this command makes says who is making it and that it is not production traffic:
    // "Scaleway.Sdk-dotnet/{version} development/{version}". www.scaleway.com serves the schema documents for
    // free and has to decide what to rate-limit; a named, low-volume development tool is a decision it can make,
    // and an unnamed one is not. The mechanism is the SDK's own, used the way a consumer would use it.
    private static readonly HttpClient HttpClient = ScalewayUserAgent.ApplyTo(
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
        ScalewayUserAgent.ProductToken("development", Assembly.GetExecutingAssembly())
    );

    public RefreshScalewaySchemasCommand() : base("schemas", "Download the pinned Scaleway OpenAPI schemas (YAML) the SDK is generated from, and report how they drifted")
    {
        var checkOnlyOption = new Option<bool>("--check-only") { Description = "Skip the download and only report drift between the working-tree schemas and their committed versions" };

        Options.Add(checkOnlyOption);

        SetAction(async parseResult => await Execute(parseResult.GetValue(checkOnlyOption)));
    }

    private static async Task Execute(bool checkOnly)
    {
        Prerequisite.Ensure(Prerequisite.Dotnet);

        EnsureEveryPinHasARationale();

        var schemasDirectory = Path.Combine(Configuration.ApplicationFolder, "shared-kernel", "Scaleway.Sdk", "Schemas");
        Directory.CreateDirectory(schemasDirectory);

        if (!checkOnly)
        {
            foreach (var pin in SchemaPins.All)
            {
                var targetPath = Path.Combine(schemasDirectory, $"{pin.Slug}.yml");

                var yaml = await HttpClient.GetStringAsync(SchemaPins.SchemaUrl(pin.Slug, pin.Version));
                await File.WriteAllTextAsync(targetPath, yaml);
                AnsiConsole.MarkupLine($"[green]✓[/] {pin.Slug} {pin.Version} ({yaml.Length:N0} bytes)");
            }

            AnsiConsole.MarkupLine($"[green]Downloaded {SchemaPins.All.Count} schema(s) to {schemasDirectory}[/]");
        }

        await ReportPinDrift();

        ReportSchemaDrift();

        AnsiConsole.MarkupLine("[grey]Regenerate the SDK with: dotnet run --project developer-cli -- scaleway-sdk generate[/]");
    }

    /// <summary>
    ///     A pin whose rationale is blank is indistinguishable from a forgotten one, which is the whole reason the
    ///     rationale exists. The type makes omitting it a compile error; this catches the empty string it cannot.
    /// </summary>
    private static void EnsureEveryPinHasARationale()
    {
        var missing = SchemaPins.PinsWithoutRationale();
        if (missing.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine($"[red]{missing.Count} schema pin(s) carry no rationale: {string.Join(", ", missing.Select(pin => pin.Slug))}.[/]");
        AnsiConsole.MarkupLine("[red]Record why that version was chosen in SchemaPins.All — a version alone cannot be reviewed.[/]");
        Environment.Exit(1);
    }

    /// <summary>
    ///     Whether each pin still names the newest document Scaleway publishes. This is the half of drift the
    ///     generator's <c>--check-drift</c> cannot see: it compares a schema against its committed self, so a pin
    ///     superseded upstream reports as perfectly stable — Scaleway freezes superseded documents at their URL
    ///     rather than retiring them, so the pinned document genuinely never changes again.
    ///     <para>
    ///         Reported, not gated: a superseded pin is a known, reviewable fact (its rationale says why it is
    ///         pinned), and exiting non-zero would make the refresh command unusable until it is bumped. Breaking
    ///         document drift still gates — that one is a surprise, this one is not.
    ///     </para>
    ///     <para>
    ///         Answers are cached for a day (<see cref="SchemaVersionCache" />), so running this twice in an
    ///         afternoon asks upstream once. Set <c>SCW_SCHEMA_VERSION_CACHE_DISABLED=1</c> to probe regardless.
    ///     </para>
    /// </summary>
    private static async Task ReportPinDrift()
    {
        AnsiConsole.MarkupLine("\n[blue]Schema pins against what Scaleway publishes today:[/]");

        var cache = SchemaVersionCache.Load();
        var report = await SchemaVersionCheck.CompareAsync(SchemaPins.All, cache.Wrap(ProbeVersion));
        cache.Save();

        AnsiConsole.WriteLine(report.Render());
        AnsiConsole.MarkupLine($"[grey]{cache.Probed} probe(s) sent to www.scaleway.com; {cache.Reused} answer(s) reused from the local cache.[/]");

        if (report.HasInconclusive)
        {
            AnsiConsole.MarkupLine("[yellow]Some pins could not be checked — treat this as unknown, not as up to date.[/]");
        }
    }

    /// <summary>
    ///     Whether a schema document is served at a version URL. Only an explicit 200 or 404 is an answer;
    ///     everything else — a refusal, a timeout, a rate limit — is <see cref="SchemaVersionAvailability.Unknown" />
    ///     rather than "absent", because reporting a stale pin as current is the one outcome this must not produce.
    /// </summary>
    /// <param name="slug">The product doc slug.</param>
    /// <param name="version">The candidate version segment.</param>
    private static async Task<SchemaVersionAvailability> ProbeVersion(string slug, string version)
    {
        for (var attempt = 1; attempt <= ProbeAttempts; attempt++)
        {
            await Task.Delay(ProbePacing);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, SchemaPins.SchemaUrl(slug, version));
                using var response = await HttpClient.SendAsync(request);

                if (response.StatusCode is HttpStatusCode.OK)
                {
                    return SchemaVersionAvailability.Published;
                }

                if (response.StatusCode is HttpStatusCode.NotFound)
                {
                    return SchemaVersionAvailability.NotPublished;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Degrading to Unknown is the point: a probe that cannot reach upstream must not be able to
                // conclude anything, and it must not take the refresh down either.
            }

            if (attempt < ProbeAttempts)
            {
                await Task.Delay(ProbeBackoff);
            }
        }

        return SchemaVersionAvailability.Unknown;
    }

    /// <summary>
    ///     The generator's <c>--check-drift</c> mode compares the working-tree schemas against their committed
    ///     versions, so the only moment it says anything is right after a refresh has overwritten them — which is
    ///     why it hangs off this command rather than off a build or a pull-request check, where the two are equal by
    ///     construction and the report is empty every time. A breaking change gates the refresh: the generator exits
    ///     non-zero and so does this command, because a removed operation or a version bump moves the generated
    ///     surface under existing callers and has to be looked at before anything is regenerated.
    /// </summary>
    private static void ReportSchemaDrift()
    {
        AnsiConsole.MarkupLine("\n[blue]Schema drift since the committed schemas:[/]");
        var drift = ScalewaySdkGenerator.Run("--check-drift");
        AnsiConsole.WriteLine(drift.StdOut.TrimEnd());

        if (!drift.Success)
        {
            AnsiConsole.MarkupLine("\n[red]Breaking schema drift — review the diff before regenerating.[/]");
            Environment.Exit(1);
        }
    }
}
