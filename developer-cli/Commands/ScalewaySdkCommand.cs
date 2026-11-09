using System.CommandLine;
using DeveloperCli.Commands.ScalewaySdk;

namespace DeveloperCli.Commands;

public sealed class ScalewaySdkCommand : Command
{
    public ScalewaySdkCommand() : base("scaleway-sdk", "Maintain Scaleway.Sdk: regenerate its typed clients, or refresh the OpenAPI schemas they are generated from")
    {
        Subcommands.Add(new GenerateScalewaySdkCommand());
        Subcommands.Add(new RefreshScalewaySchemasCommand());
    }
}
