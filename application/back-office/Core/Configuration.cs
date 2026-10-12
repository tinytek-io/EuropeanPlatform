using BackOffice.Authentication;
using BackOffice.ExecutionContext;
using BackOffice.Integrations.Account;
using BackOffice.Integrations.InternalTls;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Authentication;
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
            builder.AddAccountInternalApiClient();

            return builder;
        }

        private IHostApplicationBuilder AddAccountInternalApiClient()
        {
            builder.Services.AddTransient<BackOfficeDelegationTokenGenerator>();
            builder.Services.AddTransient<BackOfficeDelegationHandler>();

            // Same address contract as the app-gateway's account client: the deploy passes
            // ACCOUNT_API_URL (the in-cluster Service), local development falls back to the
            // account API's port from the shared port allocation.
            builder.Services.AddHttpClient<AccountInternalApiClient>((serviceProvider, client) =>
                {
                    var accountApiUrl = Environment.GetEnvironmentVariable("ACCOUNT_API_URL");
                    client.BaseAddress = accountApiUrl is not null
                        ? new Uri(accountApiUrl)
                        : new Uri($"https://localhost:{serviceProvider.GetRequiredService<PortAllocation>().AccountApi}");
                    client.Timeout = TimeSpan.FromSeconds(45);
                }
            ).AddHttpMessageHandler<BackOfficeDelegationHandler>();

            return builder;
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

            // The staff identity scheme and the SPA-facing policies. The shared API stack
            // registers the platform's application scheme and the delegation contract; the staff
            // identity is this system's own: the handler resolves the principal from the mock
            // selector's EasyAuth-style headers or the OIDC session cookie.
            services
                .AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, BackOfficeIdentityHandler>(BackOfficeIdentityDefaults.AuthenticationScheme, _ => { });
            services.AddAuthorization(authOptions =>
                {
                    authOptions.AddPolicy(BackOfficeIdentityDefaults.PolicyName, policy =>
                        {
                            policy.AuthenticationSchemes = [BackOfficeIdentityDefaults.AuthenticationScheme];
                            policy.RequireAuthenticatedUser();
                        }
                    );
                    authOptions.AddPolicy(BackOfficeIdentityDefaults.AdminPolicyName, policy =>
                        {
                            policy.AuthenticationSchemes = [BackOfficeIdentityDefaults.AuthenticationScheme];
                            policy.RequireAuthenticatedUser();
                            policy.AddRequirements(new BackOfficeAdminRequirement());
                        }
                    );
                }
            );

            // Request-scoped staff identity: handlers read this instead of the raw principal,
            // whether the identity came from the mock EasyAuth selector or a staff OIDC provider.
            services.AddScoped<IStaffExecutionContext, StaffExecutionContext>();

            return services.AddSharedServices([Assembly]);
        }
    }
}
