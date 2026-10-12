using BackOffice.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BackOffice.Tests.Authentication;

public sealed class BackOfficeOidcOptionsTests
{
    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
    }

    [Fact]
    public void IsBackOfficeOidcConfigured_WhenSectionMissing_ShouldReturnFalse()
    {
        var configuration = BuildConfiguration();

        configuration.IsBackOfficeOidcConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsBackOfficeOidcConfigured_WhenIncomplete_ShouldReturnFalse()
    {
        var configuration = BuildConfiguration(
            ("BackOffice:Oidc:Authority", "https://login.example.com/v2.0"),
            ("BackOffice:Oidc:ClientId", "client-id")
            // ClientSecret missing
        );

        configuration.IsBackOfficeOidcConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsBackOfficeOidcConfigured_WhenFullyConfigured_ShouldReturnTrue()
    {
        var configuration = BuildConfiguration(
            ("BackOffice:Oidc:Authority", "https://login.example.com/v2.0"),
            ("BackOffice:Oidc:ClientId", "client-id"),
            ("BackOffice:Oidc:ClientSecret", "secret")
        );

        configuration.IsBackOfficeOidcConfigured().Should().BeTrue();
    }

    [Fact]
    public void IsBackOfficeOidcConfigured_WhenSecretComesFromSecretManager_ShouldReturnTrue()
    {
        // Secret Manager flattens BackOffice__Oidc__ClientSecret to the same configuration key.
        var configuration = BuildConfiguration(
            ("BackOffice:Oidc:Authority", "https://login.example.com/v2.0"),
            ("BackOffice:Oidc:ClientId", "client-id"),
            ("BackOffice:Oidc:ClientSecret", "secret")
        );

        configuration.GetSection(BackOfficeOidcOptions.SectionName).Get<BackOfficeOidcOptions>()!.IsConfigured.Should().BeTrue();
    }
}
