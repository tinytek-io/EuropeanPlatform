using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SharedKernel.Integrations.BlobStorage;
using SharedKernel.Telemetry;

namespace SharedKernel.Configuration;

public static class SharedInfrastructureConfiguration
{
    // True when the process runs as a deployed workload rather than on a developer machine or in a
    // test host. Container images set ASPNETCORE_ENVIRONMENT=Production; local Aspire runs set
    // Development; test hosts leave it unset. The distinction it tracks is deployed versus
    // developer machine, not any specific cloud.
    public static readonly bool IsRunningInCloud =
        (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) == "Production";

    public static readonly string? ServiceVersion = ResolveServiceVersion();

    // Baked into the assembly at build time via /p:DeploymentCommitHash=<sha>
    // /p:DeploymentGithubActionId=<run_id> in the deployment workflows. The Directory.Build.props at
    // /application emits AssemblyMetadata attributes from those properties so each artifact carries
    // its own provenance -- pulling and running the image anywhere produces the same telemetry stamp.
    public static readonly string? DeploymentCommitHash = GetAssemblyMetadata("DeploymentCommitHash");

    public static readonly string? DeploymentGithubActionId = GetAssemblyMetadata("DeploymentGithubActionId");

    private static string? ResolveServiceVersion()
    {
        // The .NET SDK auto-appends "+<source revision id>" to AssemblyInformationalVersion when
        // building inside a git checkout. Strip it so application_Version stays clean (the commit
        // SHA travels separately as the deployment.commit_hash custom dimension).
        var informationalVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informationalVersion is null) return Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex < 0 ? informationalVersion : informationalVersion[..plusIndex];
    }

    private static string? GetAssemblyMetadata(string key)
    {
        return Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
    }

    /// <summary>
    ///     Configures an OTLP exporter for the project's Cockpit when the deploy wires it (each signal
    ///     gets its own endpoint plus a token header). When the Cockpit configuration is absent — local
    ///     development through the Aspire dashboard, or an environment without Cockpit — the exporter
    ///     falls back to its standard OTEL_EXPORTER_OTLP_* behavior.
    /// </summary>
    private static Action<OtlpExporterOptions> CockpitOtlpOptions(IHostApplicationBuilder builder, string signal)
    {
        return options =>
        {
            var endpoint = builder.Configuration[$"Cockpit:{signal}OtlpEndpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return;
            }

            var token = builder.Configuration["Cockpit:Token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"Cockpit:{signal}OtlpEndpoint is configured but Cockpit:Token is missing — the exporter cannot authenticate its telemetry pushes."
                );
            }

            options.Endpoint = new Uri(endpoint);
            options.Protocol = OtlpExportProtocol.HttpProtobuf;
            options.Headers = $"X-TOKEN={token}";
        };
    }

    extension(IHostApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddSharedInfrastructure<T>(string connectionName)
            where T : DbContext
        {
            builder
                .ConfigureDatabaseContext<T>(connectionName)
                .AddDefaultBlobStorage()
                .AddSharedTelemetry();

            // Deployed workloads read their secrets from Scaleway Secret Manager (added last so
            // they override chart placeholders); a no-op in local development and tests.
            builder.Configuration.AddScalewaySecretManager(TimeSpan.FromMinutes(5));

            builder.Services
                .ConfigureHttpClientDefaults(http =>
                    {
                        // 45s total request timeout for API-tier outbound calls (account-api, main-api).
                        // Sized to absorb a downstream container scale-from-zero: ~31s P99 container start
                        // plus ~12s P99 first-request slowness on account-api. Still tight enough to defend
                        // against slow-loris / DoS upstream. AppGateway sets a longer per-client timeout for
                        // its account-api refresh path because that path must deliver the rotated Set-Cookie
                        // to the browser.
                        // Retries are disabled: most 5xx errors during a bad release are persistent, so
                        // retrying triples the load during incidents and inflates the failure rate in
                        // Application Insights. Real transient errors are rare in same-region traffic.
                        http.AddStandardResilienceHandler(options =>
                            {
                                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
                                options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                            }
                        );
                        http.AddServiceDiscovery(); // Turn on service discovery by default
                    }
                );

            return builder;
        }

        // Wires OpenTelemetry tracing/logging/metrics with the OTLP exporter (collected by the
        // in-cluster Aspire dashboard on staging and Cockpit in production) without requiring a
        // DbContext. AppGateway and other database-less hosts should call this directly;
        // AddSharedInfrastructure<T> calls it as part of the full bundle.
        public IHostApplicationBuilder AddSharedTelemetry()
        {
            builder
                .AddConfigureOpenTelemetry()
                .AddOpenTelemetryExporters();

            // Database-less hosts (AppGateway) fetch their secrets (e.g. the token signing key) the
            // same way; the source deduplicates against the AddSharedInfrastructure call.
            builder.Configuration.AddScalewaySecretManager(TimeSpan.FromMinutes(5));

            if (ServiceVersion is not null)
            {
                builder.Services.AddOpenTelemetry().ConfigureResource(resource =>
                    resource.AddAttributes([new KeyValuePair<string, object>("service.version", ServiceVersion)])
                );
            }

            return builder;
        }
    }

    extension(IHostApplicationBuilder builder)
    {
        private IHostApplicationBuilder ConfigureDatabaseContext<T>(string connectionName)
            where T : DbContext
        {
            // The managed database connection string arrives through configuration: the Aspire
            // AppHost in local development, a Kubernetes secret when published — overridden at
            // runtime by the Scaleway Secret Manager provider. Resolved per context creation
            // instead of being captured here: registration happens before the Secret Manager
            // source is added, and capturing eagerly would also defeat its refresh timer.
            builder.Services.AddDbContext<T>((serviceProvider, options) =>
                {
                    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(connectionName)
                                           ?? throw new InvalidOperationException(
                                               $"The connection string '{connectionName}' is required but was not configured."
                                           );

                    options.UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__ef_migrations_history"))
                        .UseSnakeCaseNamingConvention();
                }
            );

            return builder;
        }

        public IHostApplicationBuilder AddDefaultBlobStorage()
        {
            IHostApplicationBuilder.RegisterS3BlobStorage(builder.Services);

            return builder;
        }

        /// <summary>
        ///     Register additional S3 storage accounts for BlobStorage using .NET Keyed services, when a
        ///     service needs to access multiple storage accounts (e.g. the back-office blob proxy).
        /// </summary>
        public IHostApplicationBuilder AddNamedBlobStorages(params string[] connectionNames)
        {
            foreach (var connectionName in connectionNames)
            {
                IHostApplicationBuilder.RegisterS3BlobStorage(builder.Services, connectionName);
            }

            return builder;
        }

        private static void RegisterS3BlobStorage(IServiceCollection services, string? connectionName = null)
        {
            // S3-compatible object storage (Scaleway Object Storage in production, SeaweedFS in local
            // development) configured from the `blob-storage` connection string. The key registers
            // the same client additionally under a named key for consumers of multiple accounts.
            services.TryAddSingleton<IBlobStorageClient>(IHostApplicationBuilder.CreateBlobStorageClient);
            if (connectionName is not null)
            {
                services.AddKeyedSingleton<IBlobStorageClient>(connectionName, (sp, _) => IHostApplicationBuilder.CreateBlobStorageClient(sp));
            }
        }

        private static S3BlobStorageClient CreateBlobStorageClient(IServiceProvider serviceProvider)
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("blob-storage")
                                   ?? throw new InvalidOperationException("The blob-storage connection string is required.");

            var options = S3BlobStorageOptions.Parse(connectionString);
            var s3Client = new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = options.ServiceUrl,
                    ForcePathStyle = options.ForcePathStyle,
                    AuthenticationRegion = options.Region
                }
            );

            return new S3BlobStorageClient(s3Client, options, serviceProvider.GetRequiredService<TimeProvider>());
        }

        private IHostApplicationBuilder AddConfigureOpenTelemetry()
        {
            builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
                {
                    // ReSharper disable once RedundantLambdaParameterType
                    options.Filter = (HttpContext httpContext) =>
                    {
                        var requestPath = httpContext.Request.Path.ToString();

                        if (EndpointTelemetryFilter.ExcludedPaths.Any(requestPath.StartsWith))
                        {
                            return false;
                        }

                        if (EndpointTelemetryFilter.ExcludedFileExtensions.Any(requestPath.EndsWith))
                        {
                            return false;
                        }

                        return true;
                    };
                }
            );

            builder.Logging.AddOpenTelemetry(logging =>
                {
                    logging.IncludeFormattedMessage = true;
                    logging.IncludeScopes = true;
                }
            );

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                    {
                        metrics.AddAspNetCoreInstrumentation()
                            .AddHttpClientInstrumentation()
                            .AddRuntimeInstrumentation();
                    }
                )
                .WithTracing(tracing =>
                    {
                        // We want to view all traces in development
                        if (builder.Environment.IsDevelopment()) tracing.SetSampler(new AlwaysOnSampler());

                        tracing
                            .AddProcessor(new DeploymentTagsProcessor())
                            .AddAspNetCoreInstrumentation(options =>
                                {
                                    options.EnrichWithHttpRequest = PublicHostTelemetryEnricher.Enrich;
                                    // 4xx is a client problem (validation, auth, missing route); the server handled it.
                                    // Mark OK so dashboards don't flag it; only 5xx is a real server error.
                                    options.EnrichWithHttpResponse = (activity, response) =>
                                    {
                                        if (response.StatusCode is >= 400 and < 500) activity.SetStatus(ActivityStatusCode.Ok);
                                    };
                                }
                            )
                            .AddGrpcClientInstrumentation()
                            .AddHttpClientInstrumentation();
                    }
                );

            return builder;
        }

        private IHostApplicationBuilder AddOpenTelemetryExporters()
        {
            // Deployed environments wire the Cockpit endpoints instead of the standard variable; a
            // signal without a Cockpit endpoint falls back to OTEL_EXPORTER_OTLP_* defaults.
            var cockpitEndpoints = new[] { "Logs", "Metrics", "Traces" }
                .Select(signal => builder.Configuration[$"Cockpit:{signal}OtlpEndpoint"])
                .Any(endpoint => !string.IsNullOrWhiteSpace(endpoint));

            var useOtlpExporter = cockpitEndpoints
                                  || !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                builder.Services
                    .Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter(CockpitOtlpOptions(builder, "Logs")))
                    .ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter(CockpitOtlpOptions(builder, "Metrics")))
                    .ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter(CockpitOtlpOptions(builder, "Traces")));
            }

            return builder;
        }
    }
}
