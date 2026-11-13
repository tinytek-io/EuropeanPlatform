using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SharedKernel.Configuration;
using Xunit;

namespace SharedKernel.Tests.Configuration;

public sealed class InternalCaTests
{
    private const SslPolicyErrors NameMismatchAndUntrustedChain = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;

    private const string ServerAuthenticationUsage = "1.3.6.1.5.5.7.3.1";

    private const string ClientAuthenticationUsage = "1.3.6.1.5.5.7.3.2";

    private static readonly X509Certificate2 CertificateAuthority = CreateCertificateAuthority("internal CA");

    private static readonly InternalCa InternalCa = new(CertificateAuthority);

    [Fact]
    public void Validator_WhenLeafNamesTheDestinationHost_ShouldTrustTheForwardedHostConnection()
    {
        // Arrange: YARP forwarded the public Host, so the TLS target name mismatches the leaf.
        using var leaf = CreateLeaf(CertificateAuthority, "account-api-service", ServerAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator("account-api-service");

        // Act
        var trusted = validator(new object(), leaf, null, NameMismatchAndUntrustedChain);

        // Assert
        trusted.Should().BeTrue();
    }

    [Fact]
    public void Validator_WhenLeafBelongsToAnotherWorkload_ShouldReject()
    {
        // Arrange: main-api's certificate presented where account-api was dialed.
        using var leaf = CreateLeaf(CertificateAuthority, "main-api-service", ServerAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator("account-api-service");

        // Act
        var trusted = validator(new object(), leaf, null, NameMismatchAndUntrustedChain);

        // Assert
        trusted.Should().BeFalse();
    }

    [Theory]
    [InlineData(SslPolicyErrors.None)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void Validator_WhenLeafNamesTheForwardedHostButNotTheDestinationHost_ShouldReject(SslPolicyErrors policyErrors)
    {
        // Arrange
        using var leaf = CreateLeaf(CertificateAuthority, "app.public.test", ServerAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator("account-api-service");

        // Act
        var trusted = validator(new object(), leaf, null, policyErrors);

        // Assert
        trusted.Should().BeFalse();
    }

    [Fact]
    public void Validator_WhenLeafNamesTheDestinationHostWithoutNameMismatch_ShouldTrust()
    {
        // Arrange
        using var leaf = CreateLeaf(CertificateAuthority, "account-api-service", ServerAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator("account-api-service");

        // Act
        var trusted = validator(new object(), leaf, null, SslPolicyErrors.RemoteCertificateChainErrors);

        // Assert
        trusted.Should().BeTrue();
    }

    [Fact]
    public void Validator_WhenNameMismatchAndNoDestinationHost_ShouldReject()
    {
        // Arrange
        using var leaf = CreateLeaf(CertificateAuthority, "main-api-service", ServerAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator(null);

        // Act
        var trusted = validator(new object(), leaf, null, NameMismatchAndUntrustedChain);

        // Assert
        trusted.Should().BeFalse();
    }

    [Fact]
    public void Validator_WhenLeafIsSignedByAnotherCa_ShouldReject()
    {
        // Arrange
        using var otherCertificateAuthority = CreateCertificateAuthority("other CA");
        using var leaf = CreateLeaf(otherCertificateAuthority, "account-api-service", ServerAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator("account-api-service");

        // Act
        var trusted = validator(new object(), leaf, null, SslPolicyErrors.RemoteCertificateChainErrors);

        // Assert
        trusted.Should().BeFalse();
    }

    [Fact]
    public void Validator_WhenLeafIsOnlyValidForClientAuthentication_ShouldReject()
    {
        // Arrange
        using var leaf = CreateLeaf(CertificateAuthority, "account-api-service", ClientAuthenticationUsage);
        var validator = InternalCa.CreateServerCertificateValidator("account-api-service");

        // Act
        var trusted = validator(new object(), leaf, null, SslPolicyErrors.RemoteCertificateChainErrors);

        // Assert
        trusted.Should().BeFalse();
    }

    private static X509Certificate2 CreateCertificateAuthority(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string dnsName, string enhancedKeyUsage)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(dnsName);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(enhancedKeyUsage)], false));

        var serialNumber = RandomNumberGenerator.GetBytes(8);
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(1), serialNumber);
    }
}
