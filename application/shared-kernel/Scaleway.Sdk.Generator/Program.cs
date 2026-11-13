using System.Diagnostics;
using Scaleway.Sdk.Generator;

// Generates Scaleway.Sdk's typed API clients from the checked-in Scaleway OpenAPI schemas. Reads the local YAML
// schemas Scaleway publishes and emits into Scaleway.Sdk/Generated. Coverage tracks the checked-in schemas (refresh
// or add one with `scaleway-sdk schemas`, which downloads the YAML; regenerate with `scaleway-sdk generate`).
// The schemas live with the SDK they generate.
var schemasDirectory = GetArgument(args, "--schemas") ?? DefaultSiblingDirectory("Scaleway.Sdk", "Schemas");
var sdkOutputDirectory = GetArgument(args, "--sdk-output") ?? DefaultSiblingDirectory("Scaleway.Sdk", "Generated");

// The SDK README's coverage claim is rendered from the manifest rather than typed, so the README is an
// output of this run too — of its marked region only; the prose around it is hand-written.
var sdkReadmePath = GetArgument(args, "--sdk-readme") ?? Path.Combine(DefaultSiblingDirectory("Scaleway.Sdk"), "README.md");

// Drift mode: compare the working-tree schemas against their committed versions and gate on breaking
// changes, rather than generating. Run after `scaleway-sdk schemas` refreshes the YAML to see what Scaleway changed.
if (args.Contains("--check-drift"))
{
    var current = ReadSchemas(schemasDirectory);
    var report = SchemaDriftCheck.Compare(current, slug => ReadCommittedYaml(schemasDirectory, slug));
    Console.WriteLine(report.Render());

    // Informational: fields the schema declares but the SDK omits. Not gated (a standing gap, not drift), but shown
    // so it stays visible and can be closed.
    var dropped = SchemaDriftCheck.DroppedFields(current);
    if (dropped.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Generator gaps: {dropped.Count} field(s) the schema declares but the SDK omits:");
        foreach (var field in dropped)
        {
            Console.WriteLine($"  {field.RecordName}.{field.FieldName} — {field.Reason}");
        }
    }

    Environment.Exit(report.HasBreaking ? 1 : 0);
}

Console.WriteLine($"Schemas: {schemasDirectory}");
Console.WriteLine($"Output:  {sdkOutputDirectory}");

var emitted = 0;
foreach (var file in Directory.GetFiles(schemasDirectory, "*.yml").OrderBy(path => path))
{
    var slug = Path.GetFileNameWithoutExtension(file);
    var service = SchemaSource.ServiceForSlug(slug);
    var json = SchemaSource.YamlToJson(File.ReadAllText(file));
    emitted++;

    var clientGroups = SchemaSource.ClientResourceGroups(service);
    if (clientGroups.Length > 0)
    {
        var api = OpenApiClientParser.Parse(service, json, clientGroups, SchemaSource.ClientActionOperations(service));
        ClientEmitter.Emit(api, sdkOutputDirectory);
        Console.WriteLine($"{slug} -> {service}: {api.ResourceGroups.Sum(group => group.Operations.Count)} operation(s), {api.Records.Count} record(s)");
    }
}

// The generation manifest: what was emitted, per service, as a checked-in artifact. A CRUD operation the
// parser cannot match is skipped rather than fatal, so without this an operation Scaleway renames upstream just
// stops being generated with no signal anywhere. Written last, from the same parse the emitters used.
var manifestPath = Path.Combine(sdkOutputDirectory, GenerationManifest.FileName);
File.WriteAllText(manifestPath, GenerationManifest.Render(ReadSchemas(schemasDirectory)));
Console.WriteLine($"Manifest: {manifestPath}");

// Read back rather than render from the in-memory model: the README then states what the checked-in manifest says,
// which is the artifact the tests pin, instead of what this process happened to compute.
var coverage = CoverageTable.Render(GenerationManifest.Parse(File.ReadAllText(manifestPath)));
File.WriteAllText(sdkReadmePath, CoverageTable.ReplaceRegion(File.ReadAllText(sdkReadmePath), coverage));
Console.WriteLine($"Coverage: {sdkReadmePath}");

Console.WriteLine($"Done. Generated from {emitted} schema(s).");
return;

// Every checked-in schema, keyed by slug. Both the drift check and the manifest read the whole set, so they read
// it the same way.
static Dictionary<string, string> ReadSchemas(string schemasDirectory)
{
    var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(schemasDirectory, "*.yml"))
    {
        schemas[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);
    }

    return schemas;
}

// The committed (HEAD) version of a schema, or null if it's new. `./` resolves against the schemas directory,
// which lives inside the git repo. A non-zero exit (path not in HEAD) means the schema was added since the commit.
static string? ReadCommittedYaml(string schemasDirectory, string slug)
{
    var startInfo = new ProcessStartInfo("git", $"show HEAD:./{slug}.yml")
    {
        WorkingDirectory = schemasDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using var process = Process.Start(startInfo)!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : null;
}

static string? GetArgument(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return Path.GetFullPath(args[i + 1]);
        }
    }

    return null;
}

static string DefaultSiblingDirectory(string project, params string[] leaves)
{
    var projectDirectory = FindProjectDirectory(AppContext.BaseDirectory)
                           ?? throw new InvalidOperationException("Could not find generator project directory.");
    return Path.GetFullPath(Path.Combine([projectDirectory, "..", project, .. leaves]));
}

static string? FindProjectDirectory(string startDir)
{
    var dir = startDir;
    while (dir is not null)
    {
        if (Directory.GetFiles(dir, "*.csproj").Length > 0)
        {
            return dir;
        }

        dir = Path.GetDirectoryName(dir);
    }

    return null;
}
