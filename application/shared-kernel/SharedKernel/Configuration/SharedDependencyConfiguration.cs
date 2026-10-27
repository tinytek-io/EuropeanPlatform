using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SharedKernel.Authentication;
using SharedKernel.Authentication.TokenGeneration;
using SharedKernel.Authentication.TokenSigning;
using SharedKernel.DomainEvents;
using SharedKernel.Integrations.Email;
using SharedKernel.Persistence;
using SharedKernel.PipelineBehaviors;
using SharedKernel.Platform;
using SharedKernel.Telemetry;

namespace SharedKernel.Configuration;

public static class SharedDependencyConfiguration
{
    // Ensure that enums are serialized as strings and use CamelCase
    public static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ITokenSigningClient GetTokenSigningService(IConfiguration? configuration = null)
    {
        // Signs with the symmetric key from configuration (Scaleway Secret Manager in deployed
        // workloads); falls back to the AUTHENTICATION_TOKEN_SIGNING_KEY environment variable and
        // during local development to the user-secrets key the AppHost generates.
        return new HmacTokenSigningClient(configuration);
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection AddSharedServices<T>(Assembly[] assemblies)
            where T : DbContext
        {
            // Even though the HttpContextAccessor is not available in Worker Services, it is still registered here because
            // workers register the same CommandHandlers as the API, which may require the HttpContext.
            // Consider making a generic IRequestContextProvider that can return the HttpContext only if it is available.
            services.AddHttpContextAccessor();

            return services
                .AddServiceDiscovery()
                .AddSingleton(sp => GetTokenSigningService(sp.GetService<IConfiguration>()))
                .AddCrossServiceDataProtection()
                .AddSingleton(Settings.Current)
                .AddTimeProvider()
                .AddAuthentication()
                .AddDefaultJsonSerializerOptions()
                .AddPersistenceHelpers<T>()
                .AddDefaultHealthChecks()
                .AddEmailClient()
                .AddMediatRPipelineBehaviors()
                .RegisterMediatRRequest(assemblies)
                .RegisterRepositories(assemblies);
        }

        // Database-less variant for BFF-style systems (the back-office calls other systems'
        // internal APIs instead of owning a database): everything except the persistence helpers.
        public IServiceCollection AddSharedServices(Assembly[] assemblies)
        {
            services.AddHttpContextAccessor();

            return services
                .AddServiceDiscovery()
                .AddSingleton(sp => GetTokenSigningService(sp.GetService<IConfiguration>()))
                .AddCrossServiceDataProtection()
                .AddSingleton(Settings.Current)
                .AddTimeProvider()
                .AddAuthentication()
                .AddDefaultJsonSerializerOptions()
                .AddDefaultHealthChecks()
                .AddEmailClient()
                .AddMediatRPipelineBehaviors(false) // No database: commands delegate to other systems' internal APIs.
                .RegisterMediatRRequest(assemblies)
                .RegisterRepositories(assemblies);
        }

        private IServiceCollection AddCrossServiceDataProtection()
        {
            // Antiforgery tokens cross service boundaries by design: the module-federated SPA page (and its
            // token) is minted by main-api while API calls land on account-api. That requires ONE keyring and
            // ONE application name everywhere. Locally every service shares the machine keyring; in cloud the
            // keyring persists in Object Storage — without it each container invents ephemeral keys and every
            // cross-service (or post-restart) token validation fails with 400 (found live: signup).
            // The key lifetime is pinned rather than left to the framework's implicit default so the coupling
            // is visible: keys must outlive the sessions that depend on them, and a refresh token is valid for
            // 90 days (RefreshTokenGenerator.ValidForHours).
            services.AddDataProtection()
                .SetApplicationName(Settings.Current.Branding.ProductName)
                .SetDefaultKeyLifetime(TimeSpan.FromDays(90));

            // The cloud keyring persistence (Object Storage) is registered by AddApiServices: only the
            // SPA-serving APIs mint and validate antiforgery tokens, and workers hold no token flows.
            return services;
        }

        private IServiceCollection AddTimeProvider()
        {
            services.TryAddSingleton(TimeProvider.System); // Use Try to allow tests to override with a fake TimeProvider
            return services;
        }

        private IServiceCollection AddAuthentication()
        {
            return services
                .AddScoped<IPasswordHasher<object>, PasswordHasher<object>>()
                .AddScoped<OneTimePasswordHelper>()
                .AddScoped<RefreshTokenGenerator>()
                .AddScoped<AccessTokenGenerator>()
                .AddScoped<AuthenticationTokenService>();
        }

        private IServiceCollection AddDefaultJsonSerializerOptions()
        {
            return services.Configure<JsonOptions>(options =>
                {
                    // Copy the default options from the DefaultJsonSerializerOptions to enforce consistency in serialization.
                    foreach (var jsonConverter in DefaultJsonSerializerOptions.Converters)
                    {
                        options.SerializerOptions.Converters.Add(jsonConverter);
                    }

                    options.SerializerOptions.PropertyNamingPolicy = DefaultJsonSerializerOptions.PropertyNamingPolicy;
                }
            );
        }

        private IServiceCollection AddPersistenceHelpers<T>() where T : DbContext
        {
            return services
                .AddScoped<IUnitOfWork, UnitOfWork>(provider => new UnitOfWork(provider.GetRequiredService<T>()))
                .AddScoped<IDomainEventCollector, DomainEventCollector>(provider =>
                    new DomainEventCollector(provider.GetRequiredService<T>())
                );
        }

        private IServiceCollection AddDefaultHealthChecks()
        {
            // Add a default liveness check to ensure the app is responsive
            services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
            return services;
        }

        private IServiceCollection AddEmailClient()
        {
            // Local development delivers through the Mailpit container; deployed environments
            // deliver through Scaleway Transactional Email's HTTP API. The API authenticates with
            // the workload's own IAM key (the same SCW_SECRET_KEY the Secret Manager configuration
            // provider uses), so unlike SMTP there are no separate credentials to provision, and
            // Scaleway blocks SMTP ports across its compute products anyway.
            if (!SharedInfrastructureConfiguration.IsRunningInCloud)
            {
                services
                    .AddSingleton(_ => PortAllocation.Load())
                    .AddTransient<IEmailClient, DevelopmentEmailClient>();
            }
            else
            {
                services.AddHttpClient<IEmailClient, ScalewayTransactionalEmailClient>(client =>
                    {
                        var region = Environment.GetEnvironmentVariable("SCW_DEFAULT_REGION") ?? "fr-par";
                        client.BaseAddress = new Uri($"https://api.scaleway.com/transactional-email/v1alpha1/regions/{region}/");
                        client.DefaultRequestHeaders.Add("X-Auth-Token", Environment.GetEnvironmentVariable("SCW_SECRET_KEY"));
                        client.Timeout = TimeSpan.FromSeconds(30);
                    }
                );
            }

            return services;
        }

        private IServiceCollection AddMediatRPipelineBehaviors(bool includePersistenceBehaviors = true)
        {
            // Order is important! First all Pre behaviors run, then the command is handled, and finally all Post behaviors run.
            // So Validation → Command → PublishDomainEvents → UnitOfWork → PublishTelemetryEvents.
            //
            // Database-less systems (the back-office BFF) pass includePersistenceBehaviors: false — their commands
            // delegate to other systems' internal APIs, so there is no DbContext to collect domain events from and no
            // unit of work to commit. Registering the persistence behaviors there would fail on the first command:
            // both constructor-inject IUnitOfWork/IDomainEventCollector, which only AddPersistenceHelpers<T> registers.
            services
                .AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationPipelineBehavior<,>)) // Pre
                .AddTransient(typeof(IPipelineBehavior<,>), typeof(PublishTelemetryEventsPipelineBehavior<,>)); // Post

            if (includePersistenceBehaviors)
            {
                services
                    .AddTransient(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkPipelineBehavior<,>)) // Post
                    .AddTransient(typeof(IPipelineBehavior<,>), typeof(PublishDomainEventsPipelineBehavior<,>)); // Post
            }

            return services
                .AddScoped<ITelemetryEventsCollector, TelemetryEventsCollector>()
                .AddScoped<ConcurrentCommandCounter>()
                .AddScoped<TelemetryClient>();
        }

        private IServiceCollection RegisterMediatRRequest(Assembly[] assemblies)
        {
            return services
                .AddMediatR(configuration => configuration.RegisterServicesFromAssemblies(assemblies))
                .AddValidatorsFromAssemblies(assemblies);
        }

        private IServiceCollection RegisterRepositories(Assembly[] assemblies)
        {
            // Scrutor will scan the assembly for all classes that implement the IRepository
            // and register them as a service in the container.
            return services
                .Scan(scan => scan
                    .FromAssemblies(assemblies)
                    .AddClasses(classes => classes.Where(type =>
                            type.BaseType is { IsGenericType: true } &&
                            (type.BaseType.GetGenericTypeDefinition() == typeof(RepositoryBase<,>) ||
                             type.BaseType.GetGenericTypeDefinition() == typeof(SoftDeletableRepositoryBase<,>))
                        ), false
                    )
                    .AsImplementedInterfaces()
                    .WithScopedLifetime()
                );
        }
    }
}
