using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Spectre.Console;

namespace DeveloperCli.Commands.ScalewaySdk;

/// <summary>
///     Runs Scaleway.Sdk.Generator. It is built on its own first, so a compile failure cannot be misread as the
///     generator's own non-zero exit (breaking schema drift exits non-zero too).
/// </summary>
internal static class ScalewaySdkGenerator
{
    private static string GeneratorProject => Path.Combine(Configuration.ApplicationFolder, "shared-kernel", "Scaleway.Sdk.Generator");

    public static ProcessResult Run(string arguments)
    {
        var build = ProcessHelper.ExecuteQuietly($"dotnet build {GeneratorProject} --nologo --verbosity quiet", Configuration.ApplicationFolder);
        if (!build.Success)
        {
            AnsiConsole.MarkupLine("[red]Could not build Scaleway.Sdk.Generator.[/]");
            AnsiConsole.WriteLine(build.CombinedOutput);
            Environment.Exit(1);
        }

        return ProcessHelper.ExecuteQuietly($"dotnet run --project {GeneratorProject} --no-build -- {arguments}", Configuration.ApplicationFolder);
    }
}
