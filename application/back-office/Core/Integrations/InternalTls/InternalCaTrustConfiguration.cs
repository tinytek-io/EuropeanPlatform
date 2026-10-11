using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Configuration;

namespace BackOffice.Integrations.InternalTls;

/// <summary>
///     Trust for the cluster's internal certificate authority, mirroring the app-gateway's
///     InternalCaTrustConfiguration: the feature systems present Terraform-managed certificates
///     signed by the internal CA (InternalTls:CaPem from Secret Manager), which is not in the
///     container's system trust store. The back-office BFF's account client validates the server
///     certificate against it. Local development keeps the default trust: localhost endpoints
///     present the machine-trusted ASP.NET development certificate.
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
                    ?? throw new InvalidOperationException(
                        "The 'InternalTls:CaPem' configuration value is required in cloud deployments (provisioned by Terraform in Secret Manager)."
                    );

        services.AddSingleton(new InternalCa(X509CertificateLoader.LoadCertificate(PemFirstCertificateBytes(caPem))));

        // Every IHttpClientFactory client validates against the internal CA.
        services.ConfigureHttpClientDefaults(clientBuilder =>
            clientBuilder.ConfigurePrimaryHttpMessageHandler(serviceProvider => new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = serviceProvider.GetRequiredService<InternalCa>().ValidateServerCertificate
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
    public bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors policyErrors)
    {
        if (policyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        chain ??= new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Clear();
        chain.ChainPolicy.CustomTrustStore.Add(Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(new X509Certificate2(certificate!));
    }
}
