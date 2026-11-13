namespace Aspire.Hosting.EuropeanPlatform.Tests;

/// <summary>Locates the repository's own files from the test output, for tests that check the committed configuration.</summary>
internal static class RepositoryFiles
{
    public static string PathOf(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "cloud-infrastructure")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("The repository root (the folder holding cloud-infrastructure) was not found above the test output.");
        return Path.Combine([root, .. relativePath]);
    }
}
