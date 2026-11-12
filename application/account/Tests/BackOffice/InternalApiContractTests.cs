using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Tests.Contracts;
using Xunit;

namespace Account.Tests.BackOffice;

// The provider side of the internal API contract the back-office BFF consumes. Account owns the
// contract: when a response shape changes, this test rewrites the committed contract file and fails
// so the change is reviewed; the back-office's InternalApiContractTests then fails until its mirror
// types match the new file.
public sealed class InternalApiContractTests(BackOfficeWebApplicationFactory factory) : BackOfficeEndpointBaseTest(factory), IClassFixture<BackOfficeWebApplicationFactory>
{
    public const string ContractFileName = "account-internal-api.contract.json";

    [Fact]
    public void InternalBackOfficeResponses_ShouldMatchTheCommittedContract()
    {
        // Arrange
        var responseTypes = Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/internal-api/back-office", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>())
            .Where(metadata => metadata is { StatusCode: StatusCodes.Status200OK, Type: not null } && metadata.Type != typeof(void))
            .Select(metadata => metadata.Type!)
            .ToArray();
        var contractPath = JsonContractShape.ContractFilePath(ContractFileName);
        var committedContract = File.Exists(contractPath) ? File.ReadAllText(contractPath) : string.Empty;

        // Act
        var actualContract = JsonContractShape.Describe(responseTypes);

        // Assert
        responseTypes.Should().NotBeEmpty();
        if (actualContract != committedContract)
        {
            File.WriteAllText(contractPath, actualContract);
        }

        actualContract.Should().Be(committedContract, $"the internal API contract changed; {ContractFileName} was rewritten — review and commit it, then update the back-office mirror types");
    }
}
