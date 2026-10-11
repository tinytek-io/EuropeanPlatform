using System.Reflection;
using BackOffice.Authentication;
using BackOffice.ExecutionContext;
using BackOffice.Integrations.Account;
using BackOffice.Integrations.InternalTls;
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
            builder
                .AddDefaultBlobStorage()
                .AddSharedTelemetry();

            // The account internal API client validates account's in-cluster TLS against the
            // Terraform-managed internal CA (a no-op locally, where the ASP.NET development
            // certificate is machine-trusted).
            builder.Services.AddInternalCaTrust(builder.Configuration);

            // The typed client for account's internal API: every call carries the staff
            // delegation token minted from the signed-in principal.
            IHostApplicationBuilder.AddAccountInternalApiClient(builder);

            return builder;
        }

        private static void AddAccountInternalApiClient(IHostApplicationBuilder hostApplicationBuilder)
        {
            hostApplicationBuilder.Services.AddTransient<BackOfficeDelegationTokenGenerator>();
            hostApplicationBuilder.Services.AddTransient<BackOfficeDelegationHandler>();

            // Same address contract as the app-gateway's account client: the deploy passes
            // ACCOUNT_API_URL (the in-cluster Service), local development falls back to the
            // account API's port from the shared port allocation.
            hostApplicationBuilder.Services.AddHttpClient<AccountInternalApiClient>((serviceProvider, client) =>
                {
                    var accountApiUrl = Environment.GetEnvironmentVariable("ACCOUNT_API_URL");
                    client.BaseAddress = accountApiUrl is not null
                        ? new Uri(accountApiUrl)
                        : new Uri($"https://localhost:{serviceProvider.GetRequiredService<PortAllocation>().AccountApi}");
                    client.Timeout = TimeSpan.FromSeconds(45);
                }
            ).AddHttpMessageHandler<BackOfficeDelegationHandler>();
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

            // Request-scoped staff identity: handlers read this instead of the raw principal,
            // whether the identity came from the mock EasyAuth selector or a staff OIDC provider.
            services.AddScoped<IStaffExecutionContext, StaffExecutionContext>();

            return services.AddSharedServices([Assembly]);
        }
    }
}
