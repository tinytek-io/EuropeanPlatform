using System.Net;
using System.Net.Security;
using AppGateway.Filters;
using SharedKernel.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace AppGateway.InternalTls;

public static class InternalCaForwarderConfiguration
{
    // YARP's forwarder creates its own HttpMessageInvoker (outside IHttpClientFactory), so it gets
    // its own internal-CA validation on top of AddInternalCaTrust.
    public static IServiceCollection AddInternalCaForwarder(this IServiceCollection services)
    {
        if (!SharedInfrastructureConfiguration.IsRunningInCloud)
        {
            return services;
        }

        return services.AddSingleton<IForwarderHttpClientFactory>(serviceProvider =>
            new InternalCaForwarderHttpClientFactory(serviceProvider.GetRequiredService<InternalCa>(), serviceProvider.GetRequiredService<PortAllocation>())
        );
    }
}

/// <summary>
///     Mirrors YARP's default forwarder handler settings (no proxy, no redirects, no cookies)
///     with the internal CA validation added; the default factory's handler is not extensible.
/// </summary>
public sealed class InternalCaForwarderHttpClientFactory(InternalCa internalCa, PortAllocation ports) : IForwarderHttpClientFactory
{
    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context)
    {
        // YARP creates one client per cluster, and every cluster has exactly one destination.
        var destinationHost = new Uri(ClusterDestinationConfigFilter.ResolveClusterAddress(context.ClusterId, ports)).Host;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = internalCa.CreateServerCertificateValidator(destinationHost)
            }
        };

        return new HttpMessageInvoker(handler, true);
    }
}
