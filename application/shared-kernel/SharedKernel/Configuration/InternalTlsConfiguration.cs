using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace SharedKernel.Configuration;

/// <summary>
///     In-cluster TLS: the workloads serve HTTPS only. The pod network is not trusted to carry
///     plaintext — workloads with unknown trust levels (CI runners, agent sandboxes) are planned
///     to run in this cluster, and Envoy/YARP already validate the public path down to the pod.
///     Terraform owns the internal CA and the per-workload leaf certificates; the leaf key
///     material reaches the workload through Secret Manager (InternalTls:CertificatePem /
///     InternalTls:KeyPem, surfaced by the Secret Manager configuration provider). Local
///     development keeps the plain-HTTP Aspire stack untouched.
/// </summary>
public static class InternalTlsConfiguration
{
    public const int HttpsPort = 8443;

    public static WebApplicationBuilder AddInternalTls(this WebApplicationBuilder builder)
    {
        if (!SharedInfrastructureConfiguration.IsRunningInCloud)
        {
            return builder;
        }

        builder.WebHost.ConfigureKestrel(options =>
            {
                // The certificate must exist at startup: serving plaintext because the secret was
                // missing would silently undo the in-cluster TLS guarantee — fail loud instead.
                var certificatePem = builder.Configuration["InternalTls:CertificatePem"]
                                     ?? throw new InvalidOperationException(
                                         "The 'InternalTls:CertificatePem' secret is required in cloud deployments (provisioned by Terraform in Secret Manager)."
                                     );
                var keyPem = builder.Configuration["InternalTls:KeyPem"]
                             ?? throw new InvalidOperationException(
                                 "The 'InternalTls:KeyPem' secret is required in cloud deployments (provisioned by Terraform in Secret Manager)."
                             );
                var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);

                // Any explicit Listen disables the implicit URL/port listeners (8080): HTTPS only.
                options.ListenAnyIP(HttpsPort, listen => listen.UseHttps(certificate));
            }
        );

        return builder;
    }
}
