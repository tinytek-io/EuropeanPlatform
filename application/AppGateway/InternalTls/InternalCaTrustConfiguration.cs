using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using SharedKernel.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace AppGateway.InternalTls;

/// <summary>
///     Trust for the cluster's internal certificate authority. The APIs present Terraform-managed
///     certificates signed by the internal CA (InternalTls:CaPem, public material passed by the
///     deploy), which is not in the container's system trust store — both the YARP forwarder and
///     every IHttpClientFactory client validate the server certificate against it. Local
///     development keeps the default trust: localhost endpoints present the machine-trusted
///     ASP.NET development certificate.
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
                        "The 'InternalTls:CaPem' configuration value is required in cloud deployments (the deploy command passes the Terraform-managed internal CA)."
                    );
        var internalCa = new InternalCa(X509CertificateLoader.LoadCertificate(PemFirstCertificateBytes(caPem)));
        services.AddSingleton(internalCa);

        // YARP's forwarder creates its own HttpMessageInvoker (outside IHttpClientFactory).
        services.AddSingleton<IForwarderHttpClientFactory>(new InternalCaForwarderHttpClientFactory(internalCa));

        // Every IHttpClientFactory client (API aggregation) validates against the same CA.
        services.ConfigureHttpClientDefaults(clientBuilder =>
            clientBuilder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = internalCa.ValidateServerCertificate
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

        // The internal CA is not system-trusted, so the framework already flagged the chain; the
        // hostname mismatch, however, is ours to judge — no other error is acceptable.
        if (policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        chain ??= new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Clear();
        chain.ChainPolicy.CustomTrustStore.Add(Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(new X509Certificate2(certificate!));
    }
}

/// <summary>
///     Mirrors YARP's default forwarder handler settings (no proxy, no redirects, no cookies)
///     with the internal CA validation added; the default factory's handler is not extensible.
/// </summary>
public sealed class InternalCaForwarderHttpClientFactory(InternalCa internalCa) : IForwarderHttpClientFactory
{
    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = internalCa.ValidateServerCertificate
            }
        };

        return new HttpMessageInvoker(handler, true);
    }
}
