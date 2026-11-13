using System.CommandLine;
using DeveloperCli.Installation;
using Spectre.Console;

namespace DeveloperCli.Commands.ScalewaySdk;

internal sealed class GenerateScalewaySdkCommand : Command
{
    public GenerateScalewaySdkCommand() : base("generate", "Regenerate Scaleway.Sdk/Generated, its manifest, and the README coverage table from the checked-in schemas")
    {
        SetAction(_ => Execute());
    }

    private static void Execute()
    {
        Prerequisite.Ensure(Prerequisite.Dotnet);

        var result = ScalewaySdkGenerator.Run("");
        AnsiConsole.WriteLine(result.StdOut.TrimEnd());
        if (!result.Success)
        {
            AnsiConsole.MarkupLine("[red]Generating Scaleway.Sdk failed.[/]");
            AnsiConsole.WriteLine(result.StdErr.TrimEnd());
            Environment.Exit(1);
        }

        AnsiConsole.MarkupLine("[green]Scaleway.Sdk regenerated.[/] Review the diff under application/shared-kernel/Scaleway.Sdk before committing.");
    }
}
