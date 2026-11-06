using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SharedKernel.Configuration;

/// <summary>
///     Trust for the cluster's internal certificate authority. The APIs present Terraform-managed
///     certificates signed by the internal CA (InternalTls:CaPem, public material), which is not in
///     the container's system trust store, so every IHttpClientFactory client validates the server
///     certificate against it. These clients keep the destination's own Host, so the standard
///     hostname check applies (the app-gateway's YARP forwarder, which forwards public Host
///     headers, passes its destination host explicitly). Local development keeps the default
///     trust: localhost endpoints present the machine-trusted ASP.NET development certificate.
/// </summary>
public static class InternalCaTrustConfiguration
{
    public static IServiceCollection AddInternalCaTrust(this IServiceCollection services, IConfiguration configuration)
    {
        if (!SharedInfrastructureConfiguration.IsRunningInCloud)
        {
            return services;
        }

        // The CA must exist at startup: silently falling back to default validation would reject
        // every internal call (the CA is not system-trusted) — fail loud instead.
        var caPem = configuration["InternalTls:CaPem"]
                    ?? throw new InvalidOperationException("The 'InternalTls:CaPem' configuration value is required in cloud deployments (the Terraform-managed internal CA certificate).");
        var internalCa = new InternalCa(X509CertificateLoader.LoadCertificate(PemFirstCertificateBytes(caPem)));
        services.AddSingleton(internalCa);

        services.ConfigureHttpClientDefaults(clientBuilder =>
            clientBuilder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = internalCa.CreateServerCertificateValidator(null)
                    }
                }
            )
        );

        return services;
    }

    // The bundle may contain multiple PEM blocks (root plus intermediates); the first certificate
    // is the CA the leaves chain to.
    private static byte[] PemFirstCertificateBytes(string pem)
    {
        var start = pem.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
        var end = pem.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("No certificate found in the InternalTls:CaPem configuration value.");
        }

        return Convert.FromBase64String(pem[(start + "-----BEGIN CERTIFICATE-----".Length)..end]);
    }
}

public sealed record InternalCa(X509Certificate2 Certificate)
{
    private static readonly Oid ServerAuthenticationUsage = new("1.3.6.1.5.5.7.3.1");

    /// <summary>
    ///     Validates a server certificate against the internal CA. A certificate is trusted only when
    ///     it chains to the internal CA (or the system trust store), carries the server-auth usage,
    ///     and names the host the connection dials. YARP forwards several account routes with the
    ///     ORIGINAL public Host header (RequestHeaderOriginalHost: account-api serves two host-scoped
    ///     SPAs and disambiguates them by Request.Host), and that host becomes the TLS target name, so
    ///     the framework reports a name mismatch for every such request. The forwarder therefore
    ///     passes its destination host, and the certificate must name that host instead: one
    ///     workload's certificate can never stand in for another workload.
    /// </summary>
    public RemoteCertificateValidationCallback CreateServerCertificateValidator(string? destinationHost)
    {
        return (_, certificate, _, policyErrors) => IsTrusted(certificate, policyErrors, destinationHost);
    }

    private bool IsTrusted(X509Certificate? certificate, SslPolicyErrors policyErrors, string? destinationHost)
    {
        if (certificate is null) return false;

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var nameMismatch = policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
        if (nameMismatch && (destinationHost is null || !leaf.MatchesHostname(destinationHost))) return false;

        var remainingErrors = policyErrors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
        if (remainingErrors == SslPolicyErrors.None) return true;
        if (remainingErrors != SslPolicyErrors.RemoteCertificateChainErrors) return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(ServerAuthenticationUsage);
        return chain.Build(leaf);
    }
}
