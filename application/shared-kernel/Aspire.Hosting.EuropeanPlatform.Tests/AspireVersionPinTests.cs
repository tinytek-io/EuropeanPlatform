using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Aspire.Hosting.EuropeanPlatform.Tests;

/// <summary>
///     Aspire's version is pinned where each tool reads it: the AppHost SDK line, the Aspire packages and mise.toml's
///     CLI. They are released together, so they must agree; update-packages bumps all of them, and this catches a pin
///     edited by hand.
/// </summary>
public sealed class AspireVersionPinTests
{
    private static readonly string SdkVersion = Regex.Match(
        File.ReadAllText(RepositoryFiles.PathOf("application", "AppHost", "AppHost.csproj")), @"<Project\s+Sdk=""Aspire\.AppHost\.Sdk/([^""]+)"""
    ).Groups[1].Value;

    [Fact]
    public void AspirePackages_WhenComparedWithTheAppHostSdk_ShouldShareItsVersion()
    {
        // Arrange
        var packages = XDocument.Load(RepositoryFiles.PathOf("application", "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(package => package.Attribute("Include")!.Value.StartsWith("Aspire.", StringComparison.Ordinal))
            .ToDictionary(package => package.Attribute("Include")!.Value, package => package.Attribute("Version")!.Value);

        // Act
        // A package Aspire ships only as a preview (Aspire.Hosting.Kubernetes) carries the release's version plus a suffix.
        var mismatched = packages.Where(package => package.Value.Split('-')[0] != SdkVersion).ToArray();

        // Assert
        SdkVersion.Should().NotBeEmpty();
        packages.Should().ContainKey("Aspire.Hosting");
        mismatched.Should().BeEmpty($"every Aspire package belongs to the AppHost SDK's release ({SdkVersion})");
    }

    [Fact]
    public void MiseAspireCli_WhenComparedWithTheAppHostSdk_ShouldShareItsVersion()
    {
        // Arrange
        var miseConfiguration = File.ReadAllText(RepositoryFiles.PathOf("mise.toml"));

        // Act
        var cliVersion = Regex.Match(miseConfiguration, @"^aspire = ""([^""]+)""", RegexOptions.Multiline).Groups[1].Value;

        // Assert
        cliVersion.Should().Be(SdkVersion, "the deploy refuses an Aspire CLI that differs from the AppHost SDK");
    }

    [Fact]
    public void DotnetToolManifest_WhenListingTools_ShouldNotPinASecondAspireCli()
    {
        // Arrange
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PathOf("application", "dotnet-tools.json")));

        // Act
        var tools = manifest.RootElement.GetProperty("tools").EnumerateObject().Select(tool => tool.Name).ToArray();

        // Assert
        tools.Should().NotContain("aspire.cli", "the deploy and the Aspire MCP server run the CLI on PATH, which mise.toml pins");
    }
}
