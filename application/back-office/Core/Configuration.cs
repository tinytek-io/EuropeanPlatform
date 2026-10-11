using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Configuration;

namespace BackOffice;

public static class Configuration
{
    public static Assembly Assembly => Assembly.GetExecutingAssembly();

    extension(IHostApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddBackOfficeInfrastructure()
        {
            // The back-office owns no database: it is a BFF over the feature systems' internal
            // APIs. The default blob client is still required: the shared Data Protection keyring
            // (antiforgery tokens minted here) persists in Object Storage, and AddApiServices
            // resolves IBlobStorageClient for the keyring repository in deployed environments.
            // AddSharedTelemetry wires OpenTelemetry plus the Secret Manager configuration
            // provider (a no-op locally).
            return builder
                .AddDefaultBlobStorage()
                .AddSharedTelemetry();
        }
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection AddBackOfficeServices()
        {
            // The shared AddApiServices binds the back-office host option only for the
            // AccountAndBackOffice document layout; this system always hosts the back-office
            // routes, so it binds and validates the option itself (fail loud when unset).
            services.AddOptions<BackOfficeHostOptions>()
                .BindConfiguration(BackOfficeHostOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return services.AddSharedServices([Assembly]);
        }
    }
}
