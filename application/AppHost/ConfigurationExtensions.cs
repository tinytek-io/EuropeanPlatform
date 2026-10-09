namespace AppHost;

public static class ConfigurationExtensions
{
    extension<TDestination>(IResourceBuilder<TDestination> builder) where TDestination : IResourceWithEnvironment
    {
        public IResourceBuilder<TDestination> WithUrlConfiguration(string hostname, int? gatewayPort, string applicationBasePath)
        {
            // The port is a localhost-only concern (no port is served on the public hostnames, TLS
            // terminates on 443 at the cluster gateway), so publish mode passes null.
            var baseUrl = gatewayPort is { } port ? $"https://{hostname}:{port}" : $"https://{hostname}";
            applicationBasePath = applicationBasePath.TrimEnd('/');

            return builder
                .WithEnvironment("PUBLIC_URL", baseUrl)
                .WithEnvironment("CDN_URL", baseUrl + applicationBasePath);
        }
    }
}
