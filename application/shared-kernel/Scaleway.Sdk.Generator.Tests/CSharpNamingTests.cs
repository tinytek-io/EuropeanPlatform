using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

public sealed class CSharpNamingTests
{
    [Theory]
    [InlineData("DNSStage", "dnsStageId")]
    [InlineData("APIKey", "apiKeyId")]
    [InlineData("VPC", "vpcId")]
    [InlineData("PrivateNetwork", "privateNetworkId")]
    [InlineData("Lb", "lbId")]
    public void IdentifierParameter_WhenTheGroupNameStartsWithAnAcronym_ShouldLowerCaseTheWholeAcronym(string resourceGroupName, string expected)
    {
        // Act
        var parameter = CSharpNaming.IdentifierParameter(resourceGroupName);

        // Assert
        parameter.Should().Be(expected);
    }
}
