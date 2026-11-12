using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     Scaleway.Sdk is consumed outside any hosting framework (a workload in a container, the developer CLI), so it must
///     not pick up the application stack's dependencies. This reflects over the assembly the solution build produced;
///     it does not prove the project compiles on its own, only that what compiled references none of them.
/// </summary>
public sealed class ScalewaySdkArchitectureTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes = ["Aspire", "MediatR", "Microsoft.Extensions.Logging"];

    [Fact]
    public void ScalewaySdk_WhenListingReferencedAssemblies_ShouldNameNoHostingOrApplicationStackAssembly()
    {
        // Arrange
        var sdkAssembly = typeof(ScalewayClient).Assembly;

        // Act
        var forbiddenReferences = sdkAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        // Assert
        forbiddenReferences.Should().BeEmpty();
    }
}
