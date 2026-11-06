using BackOffice.Integrations.Account;
using FluentAssertions;
using SharedKernel.Cqrs;
using SharedKernel.Tests.Contracts;
using Xunit;

namespace BackOffice.Tests.Integrations;

// The consumer side of account's internal API contract: the response types the back-office's
// account client deserializes must have exactly the shape account committed (account's own
// InternalApiContractTests owns and rewrites the file). A mismatch here means a mirror type in
// the back-office drifted from account's response.
public sealed class InternalApiContractTests
{
    private const string ContractFileName = "account-internal-api.contract.json";

    [Fact]
    public void AccountClientResponseTypes_ShouldMatchAccountsCommittedContract()
    {
        // Arrange
        var responseTypes = typeof(AccountInternalApiClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.ReturnType)
            .Where(returnType => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            .Select(returnType => returnType.GetGenericArguments()[0])
            .Where(resultType => resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Result<>))
            .Select(resultType => resultType.GetGenericArguments()[0])
            .Where(responseType => !responseType.IsGenericParameter)
            .ToArray();
        var committedContract = File.ReadAllText(JsonContractShape.ContractFilePath(ContractFileName));

        // Act
        var actualContract = JsonContractShape.Describe(responseTypes);

        // Assert
        responseTypes.Should().NotBeEmpty();
        actualContract.Should().Be(committedContract, $"the back-office mirror types must match account's {ContractFileName}");
    }
}
