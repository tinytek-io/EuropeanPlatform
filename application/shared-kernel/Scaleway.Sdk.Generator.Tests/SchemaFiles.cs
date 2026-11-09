namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Locates the checked-in schemas and generated output from the test output path. Tests that assert against a
///     real schema read it rather than synthesising a resembling shape, so the claim is about the operation Scaleway
///     actually publishes.
/// </summary>
internal static class SchemaFiles
{
    public static string SchemasDirectory()
    {
        return Locate(Path.Combine("Scaleway.Sdk", "Schemas"));
    }

    public static string GeneratedDirectory()
    {
        return Locate(Path.Combine("Scaleway.Sdk", "Generated"));
    }

    public static string SchemaPath(string slug)
    {
        return Path.Combine(SchemasDirectory(), $"{slug}.yml");
    }

    public static IReadOnlyDictionary<string, string> AllSchemas()
    {
        return Directory.GetFiles(SchemasDirectory(), "*.yml")
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file), File.ReadAllText, StringComparer.Ordinal);
    }

    private static string Locate(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate '{relativePath}' from the test output path.");
    }
}
