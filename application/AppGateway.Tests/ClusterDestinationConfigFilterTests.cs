using AppGateway.Filters;
using FluentAssertions;
using SharedKernel.Configuration;
using Xunit;

namespace AppGateway.Tests;

public sealed class ClusterDestinationConfigFilterTests
{
    [Theory]
    [InlineData("account-storage")]
    [InlineData("main-storage")]
    public void ResolveClusterAddress_WhenLocalStorageCluster_ShouldTargetTheObjectStorageEndpointRoot(string clusterId)
    {
        // Arrange
        var ports = new PortAllocation(9200);

        // Act
        var address = ClusterDestinationConfigFilter.ResolveClusterAddress(clusterId, ports);

        // Assert
        address.Should().Be($"http://127.0.0.1:{ports.Blob}");
    }
}
