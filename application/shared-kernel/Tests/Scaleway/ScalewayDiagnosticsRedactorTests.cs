using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     A diagnostics file is documented as value-free and safe to share, so a recorded request path may keep the
///     operation's structure but none of the identifiers or query values the caller sent.
/// </summary>
public sealed class ScalewayDiagnosticsRedactorTests
{
    [Fact]
    public void RedactRequestPath_WhenPathIsRegionalWithNamedResources_ShouldMaskIdentifiersAndKeepStructure()
    {
        // Arrange
        var requestUri = new Uri("https://api.scaleway.com/secret-manager/v1beta1/regions/fr-par/secrets/database-password/versions/latest_enabled/access");

        // Act
        var redacted = ScalewayDiagnosticsRedactor.RedactRequestPath(requestUri);

        // Assert
        redacted.Should().Be("/secret-manager/v1beta1/regions/fr-par/secrets/<id>/versions/<id>/access");
    }

    [Fact]
    public void RedactRequestPath_WhenPathIsGlobal_ShouldMaskTheIdentifierAfterTheCollection()
    {
        // Arrange
        var requestUri = new Uri("https://api.scaleway.com/iam/v1alpha1/api-keys/SCWABCDEFGHIJKLMNOPQ");

        // Act
        var redacted = ScalewayDiagnosticsRedactor.RedactRequestPath(requestUri);

        // Assert
        redacted.Should().Be("/iam/v1alpha1/api-keys/<id>");
    }

    [Fact]
    public void RedactRequestPath_WhenQueryCarriesValues_ShouldKeepOnlyParameterNames()
    {
        // Arrange
        var requestUri = new Uri("https://api.scaleway.com/secret-manager/v1beta1/regions/fr-par/secrets?name=stripe-api-key&email=jane.doe%40example.com&page=1&page_size=100");

        // Act
        var redacted = ScalewayDiagnosticsRedactor.RedactRequestPath(requestUri);

        // Assert
        redacted.Should().Be("/secret-manager/v1beta1/regions/fr-par/secrets?name&email&page&page_size");
    }

    [Fact]
    public void RedactRequestPath_WhenUriIsNull_ShouldReturnEmpty()
    {
        // Act
        var redacted = ScalewayDiagnosticsRedactor.RedactRequestPath(null);

        // Assert
        redacted.Should().BeEmpty();
    }
}
