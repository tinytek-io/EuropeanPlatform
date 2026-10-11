using System.Net;
using System.Net.Sockets;
using AppHost;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Projects;
using SharedKernel.Authentication.MockEasyAuth;
using SharedKernel.Configuration;

// Docker volume name prefix, derived from branding.productName in platform-settings.jsonc so the
// AppHost and the developer CLI's stop command resolve the same value with no hardcoded literal in
// either. A rebrand flows through automatically when productName changes.
var dockerVolumePrefix = DockerVolumeNaming.ResolveVolumePrefix();

// Read the port allocation before CreateBuilder so we can set Aspire's dashboard env vars
// (ASPNETCORE_URLS, DOTNET_DASHBOARD_OTLP_ENDPOINT_URL, etc.) before Aspire reads them.
var ports = PortAllocation.Load();

OverrideAspireDashboardEnvironmentVariables(ports);

var builder = DistributedApplication.CreateBuilder(args);

// Publish mode targets a real cluster, so the localhost-only concerns below (port probing, dev
// certificates, Azurite bootstrapping) must not run. CheckPortAvailability in particular calls
// Environment.Exit(1) when Aspire is already running locally, which would abort every publish.
var isPublishMode = builder.ExecutionContext.IsPublishMode;

if (!isPublishMode)
{
    CheckPortAvailability(ports);
}

IResourceBuilder<KubernetesEnvironmentResource>? kubernetes = null;

if (isPublishMode)
{
    // frontend-build is a build-only container: Aspire wants its output composed into a consuming
    // image via PublishWithStaticFiles/PublishWithContainerFiles rather than referenced as a service.
    // ExcludeFromManifest keeps it out of publish entirely; without it publish would abort on that
    // validation, and until the frontend packaging is remodelled there is no consuming image to
    // compose into.
    builder.Pipeline.DisableBuildOnlyContainerValidation();

    kubernetes = builder.AddKubernetesEnvironment("kubernetes")
        .WithHelm(helm => helm
            .WithChartName("platformplatform")
            .WithChartDescription("PlatformPlatform self-contained systems on Kubernetes")
        );

    // Images publish to the Scaleway Container Registry created by the bootstrap Terraform module
    // (cloud-infrastructure/scaleway/bootstrap). Non-secret environment values are resolved from
    // AppHost configuration at publish time, e.g. appsettings.staging.json via
    // `aspire publish --environment staging`. Secrets (connection strings, API keys) stay deploy-time
    // inputs in the chart's values.yaml.
    var registryEndpoint = builder.Configuration["Registry:Endpoint"]
                           ?? throw new InvalidOperationException("Publish mode requires Registry:Endpoint (e.g. rg.fr-par.scw.cloud) in AppHost configuration.");
    var registryRepository = builder.Configuration["Registry:Repository"]
                             ?? throw new InvalidOperationException("Publish mode requires Registry:Repository (the Scaleway registry namespace) in AppHost configuration.");
    builder.AddContainerRegistry("scaleway-registry", registryEndpoint, registryRepository);
}

// Deployed workloads read their secrets from Scaleway Secret Manager: each workload runs with its
// own IAM key (a deploy-time secret parameter) whose policy only allows its own path. The
// per-workload keys come from the environment module's workload_secret_manager_keys outputs.
var secretManagerProjectId = isPublishMode
    ? builder.Configuration["Scaleway:ProjectId"]
      ?? throw new InvalidOperationException("Publish mode requires Scaleway:ProjectId in AppHost configuration.")
    : string.Empty;

// Transactional Email is sent through its HTTP API (see ScalewayTransactionalEmailClient), which
// authenticates with the workload's IAM key — no SMTP settings or separate credentials exist.
// Development delivers to the Mailpit container instead.
var senderEmailAddress = isPublishMode
    ? builder.Configuration["Email:Sender"]
      ?? throw new InvalidOperationException("Publish mode requires Email:Sender (the From address) in AppHost configuration.")
    : string.Empty;

var appHostname = builder.Configuration["Hostnames:App"] ?? "app.dev.localhost";
var backOfficeHostname = builder.Configuration["Hostnames:BackOffice"] ?? "back-office.dev.localhost";

// Public hostnames: localhost in development; real DNS names in publish mode, where TLS
// terminates at the cluster gateway instead of the Kestrel dev certificate.
var appPublicHostname = isPublishMode
    ? builder.Configuration["Hostnames:App"]
      ?? throw new InvalidOperationException("Publish mode requires Hostnames:App (the public app hostname) in AppHost configuration.")
    : appHostname;
var backOfficePublicHostname = isPublishMode
    ? builder.Configuration["Hostnames:BackOffice"]
      ?? throw new InvalidOperationException("Publish mode requires Hostnames:BackOffice (the public back-office hostname) in AppHost configuration.")
    : backOfficeHostname;

var appBaseUrl = $"https://{appHostname}:{ports.AppGateway}";
// Localhost mirrors the Azure post-split topology: back-office traffic bypasses AppGateway and
// hits the consolidated account-api process directly on a dedicated Kestrel port.
var backOfficeBaseUrl = $"https://{backOfficeHostname}:{ports.BackOfficeApi}";
// In publish mode the scheme+hostname is the public URL seen by browsers; internally the gateway
// forwards plain HTTP to the workloads.
var appPublicUrl = $"https://{appPublicHostname}";
var backOfficePublicUrl = $"https://{backOfficePublicHostname}";

// Staging runs the MockEasyAuth identity selector until a proper OIDC provider for staff exists;
// production leaves MockEasyAuth:Enabled unset so the middleware and the /login picker stay off.
var mockEasyAuthEnabled = isPublishMode && builder.Configuration.GetSection("MockEasyAuth:Enabled").Get<bool>();

// cert-manager contact email for the Let's Encrypt issuer (publish mode only).
var acmeEmail = isPublishMode
    ? builder.Configuration["Acme:Email"]
      ?? throw new InvalidOperationException("Publish mode requires Acme:Email (the ACME account contact) in AppHost configuration.")
    : string.Empty;

// Object storage URLs that AppGateway proxies avatars/logos through (publish mode only).
var accountStorageUrl = isPublishMode
    ? builder.Configuration["Storage:AccountUrl"]
      ?? throw new InvalidOperationException("Publish mode requires Storage:AccountUrl in AppHost configuration.")
    : string.Empty;
var mainStorageUrl = isPublishMode
    ? builder.Configuration["Storage:MainUrl"]
      ?? throw new InvalidOperationException("Publish mode requires Storage:MainUrl in AppHost configuration.")
    : string.Empty;

var certificatePassword = await builder.CreateSslCertificateIfNotExists();

SecretManagerHelper.GenerateAuthenticationTokenSigningKey("authentication-token-signing-key");

var (googleOAuthConfigured, googleOAuthClientId, googleOAuthClientSecret) = ConfigureGoogleOAuthParameters();

var (stripeConfigured, stripePublishableKey, stripeApiKey, stripeWebhookSecret) = ConfigureStripeParameters();
var stripeFullyConfigured = stripeConfigured && builder.Configuration["Parameters:stripe-webhook-secret"] is not null and not "not-configured";

// Local development runs PostgreSQL as a container. Publish targets a managed database, so both
// databases become externally supplied connection strings (same pattern as blob-storage below:
// the values are filled from infrastructure outputs at deploy time).
IResourceBuilder<IResourceWithConnectionString> accountDatabase;
IResourceBuilder<IResourceWithConnectionString> mainDatabase;

if (isPublishMode)
{
    accountDatabase = builder.AddConnectionString("account-database");
    mainDatabase = builder.AddConnectionString("main-database");
}
else
{
    var postgresPassword = builder.CreateStablePassword("postgres-password");
    var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: ports.Postgres)
        .WithDataVolume($"{dockerVolumePrefix}{ports.VolumeNameInfix}-postgres-data")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithArgs("-c", "wal_level=logical");

    accountDatabase = postgres.AddDatabase("account-database", "account");
    mainDatabase = postgres.AddDatabase("main-database", "main");
}

// Object storage is S3 (Scaleway Object Storage when published, MinIO in local development).
// MinIO implements the same API, so the connection string shape is identical in both modes;
// publish mode consumes it as an externally supplied deploy-time secret.
IResourceBuilder<IResourceWithConnectionString> blobStorage;

if (isPublishMode)
{
    blobStorage = builder.AddConnectionString("blob-storage");
}
else
{
    // Fixed MinIO root credentials, safe for local development like the Azurite key they replace.
    const string minioAccessKey = "minioadmin";
    const string minioSecretKey = "minioadmin";

    // MinIO is a local development S3 emulator; buckets are created lazily by the S3 client on
    // first use, so no bootstrap task is needed.
    builder
        .AddContainer("minio", "minio/minio")
        .WithHttpEndpoint(ports.Blob, 9000)
        .WithArgs("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", minioAccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", minioSecretKey)
        .WithVolume($"{dockerVolumePrefix}{ports.VolumeNameInfix}-minio-data", "/data")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithUrlForEndpoint("http", u => u.DisplayText = "Object storage (MinIO)");

    blobStorage = builder.AddConnectionString(
        "blob-storage",
        $"ServiceUrl=http://localhost:{ports.Blob};AccessKey={minioAccessKey};SecretKey={minioSecretKey};ForcePathStyle=true;Region=fr-par"
    );
}

// Mailpit is a local dev SMTP sink; in publish mode email is delivered by a transactional email
// service, so there is nothing to deploy.
if (!isPublishMode)
{
    builder
        .AddContainer("mail-server", "axllent/mailpit")
        .WithHttpEndpoint(ports.MailpitHttp, 8025)
        .WithEndpoint(ports.MailpitSmtp, 1025)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithUrlForEndpoint("http", u => u.DisplayText = "Read mail here");
}

var frontendBuild = builder
    .AddJavaScriptApp("frontend-build", "../")
    .WithEnvironment("CERTIFICATE_PASSWORD", certificatePassword)
    .WithEnvironment("MAIN_STATIC_PORT", ports.MainStatic.ToString())
    .WithEnvironment("ACCOUNT_STATIC_PORT", ports.AccountStatic.ToString())
    .WithEnvironment("BACK_OFFICE_STATIC_PORT", ports.BackOfficeStatic.ToString());

// The SPA ships inside the API images (dotnet publish composes the WebApp dist), so the build-only
// JS container must not reach the cluster: without this its generated Deployment runs a one-shot
// build and crash-loops on restart, and the pipeline builds and pushes a multi-GB image nothing
// consumes. Run mode is unaffected (the container still serves local development).
frontendBuild.ExcludeFromManifest();

var accountWorkers = builder
    .AddProject<Account_Workers>("account-workers")
    .WithReference(accountDatabase)
    .WithReference(blobStorage)
    // The BillingDriftWorker resolves StripeClientFactory which reads these. Without them the worker
    // process sees UnconfiguredStripeClient even when Stripe is configured at the API level, and every
    // stale subscription logs a warn + fail line through ProcessPendingStripeEvents on every worker start.
    .WithEnvironment("Stripe__SubscriptionEnabled", stripeFullyConfigured ? "true" : "false")
    .WithEnvironment("Stripe__ApiKey", stripeApiKey)
    .WithEnvironment("Stripe__WebhookSecret", stripeWebhookSecret)
    .WithEnvironment("Stripe__PublishableKey", stripePublishableKey)
    // Mock Stripe is a developer-machine convenience and throws when enabled in deployed environments.
    .WithEnvironment("Stripe__AllowMockProvider", isPublishMode ? "false" : "true")
    .WaitFor(accountDatabase);

// KESTREL_PORT is a development-only listener: AddDevelopmentPort binds it in Development only,
// so in containers the image default (8080) applies and workers need no inbound listener at all.
if (!isPublishMode)
{
    accountWorkers.WithEnvironment("KESTREL_PORT", ports.AccountWorkers.ToString());
}

var accountApi = builder
    .AddProject<Account_Api>("account-api")
    // Back-office bundle URLs target the public back-office origin (dedicated Kestrel port on
    // localhost, cluster gateway hostname in publish mode).
    .WithEnvironment("BACK_OFFICE_PUBLIC_URL", backOfficePublicUrl)
    .WithEnvironment("BACK_OFFICE_CDN_URL", backOfficePublicUrl)
    .WithUrlConfiguration(appHostname, isPublishMode ? null : ports.AppGateway, "/account")
    // Google OAuth's redirect_uri whitelist requires literal 'localhost' in development, not
    // subdomains like 'app.dev.localhost'; the callback then 301's via LocalhostRedirectMiddleware
    // back to the canonical dev hostname. In publish mode the public URL is the real one.
    .WithEnvironment("OAUTH_PUBLIC_URL", isPublishMode ? appPublicUrl : "https://localhost:" + ports.AppGateway)
    .WithEnvironment("Hostnames__App", appPublicHostname)
    .WithEnvironment("BackOffice__Host", backOfficePublicHostname)
    .WithEnvironment("BackOffice__AdminsGroupId", MockEasyAuthIdentities.MockAdminsGroupId)
    .WithReference(accountDatabase)
    .WithReference(blobStorage)
    .WithEnvironment("OAuth__Google__ClientId", googleOAuthClientId)
    .WithEnvironment("OAuth__Google__ClientSecret", googleOAuthClientSecret)
    // The mock OAuth/Stripe providers are developer-machine conveniences; the Stripe mock throws
    // when enabled in deployed environments, so publish mode pins both off explicitly.
    .WithEnvironment("OAuth__AllowMockProvider", isPublishMode ? "false" : "true")
    .WithEnvironment("Stripe__SubscriptionEnabled", stripeFullyConfigured ? "true" : "false")
    .WithEnvironment("Stripe__ApiKey", stripeApiKey)
    .WithEnvironment("Stripe__WebhookSecret", stripeWebhookSecret)
    .WithEnvironment("Stripe__PublishableKey", stripePublishableKey)
    .WithEnvironment("Stripe__AllowMockProvider", isPublishMode ? "false" : "true")
    .WithEnvironment("PUBLIC_GOOGLE_OAUTH_ENABLED", googleOAuthConfigured ? "true" : "false")
    // Force-on so newcomers see the back-office billing UI without Stripe configured. Set to "false" (or
    // change back to `stripeFullyConfigured ? "true" : "false"`) to hide all billing/revenue/Stripe data.
    .WithEnvironment("PUBLIC_SUBSCRIPTION_ENABLED", "true")
    .WaitFor(accountWorkers);

// Second Kestrel port for back-office.dev.localhost so localhost mirrors the Azure post-split
// topology where back-office has its own external ingress and AppGateway is not in the path.
// BACK_OFFICE_STATIC_PORT drives the BackOfficeDevStaticProxy to the rsbuild dev server; both are
// development-only listeners (bound by AddDevelopmentPort), so they stay out of published charts.
if (!isPublishMode)
{
    accountApi
        .WithEnvironment("KESTREL_PORT", ports.AccountApi.ToString())
        .WithEnvironment("BACK_OFFICE_KESTREL_PORT", ports.BackOfficeApi.ToString())
        .WithEnvironment("BACK_OFFICE_STATIC_PORT", ports.BackOfficeStatic.ToString());
}

// Back-office OIDC test harness (development only): runs Authelia — a small open-source OIDC
// provider — and points the back-office staff sign-in at it. This exercises the same generic
// flow a production provider will use: discovery, authorization code + PKCE, the callback at
// /.auth/login/aad/callback, and the groups claim for the admin gate. Gated behind
// Authelia:Enabled so the default dev stack keeps the MockEasyAuth login picker; enable with
// AUTHELIA__ENABLED=true and run the back-office-oidc e2e spec (BACK_OFFICE_OIDC_ENABLED=true).
if (!isPublishMode && builder.Configuration.GetSection("Authelia:Enabled").Get<bool>())
{
    var autheliaRedis = builder.AddContainer("authelia-redis", "redis:7-alpine");

    // Authelia's client registration needs the back-office callback URL, whose port comes from
    // .workspace/port.txt — generate the effective configuration (from the template checked in
    // next to this AppHost) into obj/ at startup, keeping the repo clean of derived files.
    var autheliaConfigFolder = Path.Combine(builder.AppHostDirectory, "obj", "authelia");
    Directory.CreateDirectory(autheliaConfigFolder);
    // The TLS serving key and the OIDC signing key are generated here (dev-only material, never
    // committed — see AutheliaTls); the template carries a placeholder for the signing key.
    AutheliaTls.EnsureDevSecrets(autheliaConfigFolder);
    var autheliaCallbackUrl = $"https://{backOfficeHostname}:{ports.BackOfficeApi}/.auth/login/aad/callback";
    var autheliaConfiguration = File.ReadAllText(Path.Combine(builder.AppHostDirectory, "Authelia", "configuration.template.yml"))
        .Replace("REDIRECT_URI_PLACEHOLDER", autheliaCallbackUrl)
        .Replace("OIDC_JWKS_KEY_PLACEHOLDER", AutheliaTls.GetOidcSigningKeyYamlBlock(autheliaConfigFolder));
    File.WriteAllText(Path.Combine(autheliaConfigFolder, "configuration.yml"), autheliaConfiguration);
    foreach (var autheliaFile in new[] { "users_database.yml" })
    {
        File.Copy(Path.Combine(builder.AppHostDirectory, "Authelia", autheliaFile), Path.Combine(autheliaConfigFolder, autheliaFile), true);
    }

    builder
        .AddContainer("authelia", "authelia/authelia:4.38")
        .WithHttpEndpoint(9091, 9091, "https")
        .WithBindMount(autheliaConfigFolder, "/config")
        .WithEnvironment("AUTHELIA_SESSION_REDIS_HOST", "authelia-redis")
        .WithEnvironment("AUTHELIA_SESSION_REDIS_PORT", "6379")
        .WaitFor(autheliaRedis);

    accountApi
        .WithEnvironment("BackOffice__Oidc__Authority", "https://authelia.dev.localhost:9091")
        .WithEnvironment("BackOffice__Oidc__ClientId", "back-office")
        .WithEnvironment("BackOffice__Oidc__ClientSecret", "dev-only-back-office-client-secret")
        // The harness certificate is self-signed: the OIDC backchannel (server-side discovery
        // and token requests) must tolerate it. Dev-harness only; production providers have
        // trusted certificates and leave this unset.
        .WithEnvironment("BackOffice__Oidc__DevAllowUntrustedCertificate", "true")
        // The mock login picker would offer a bypass around the real provider's login screen.
        .WithEnvironment("PUBLIC_MOCK_EASY_AUTH_ENABLED", "false");
}

// The container listens on the image default port 8080; the Service fronts it on 80. The
// endpoints are marked external because the Cilium Gateway routes public traffic to them (both
// the app hostname via YARP and the back-office hostname, which bypasses YARP just like the
// Azure post-split topology).
if (isPublishMode)
{
    accountApi
        .WithHttpsEndpoint(8443, 8443, "https")
        .WithExternalHttpEndpoints()
        .WithKubernetesProbes("/internal-api/ready", "/internal-api/live");

    if (mockEasyAuthEnabled)
    {
        accountApi.WithEnvironment("MockEasyAuth__Enabled", "true");
    }
}

var mainWorkers = builder
    .AddProject<Main_Workers>("main-workers")
    .WithReference(mainDatabase)
    .WithReference(blobStorage)
    .WaitFor(mainDatabase);

if (!isPublishMode)
{
    mainWorkers.WithEnvironment("KESTREL_PORT", ports.MainWorkers.ToString());
}

var mainApi = builder
    .AddProject<Main_Api>("main-api")
    .WithUrlConfiguration(appHostname, isPublishMode ? null : ports.AppGateway, "")
    .WithReference(mainDatabase)
    .WithReference(blobStorage)
    .WithEnvironment("PUBLIC_GOOGLE_OAUTH_ENABLED", googleOAuthConfigured ? "true" : "false")
    .WithEnvironment("PUBLIC_SUBSCRIPTION_ENABLED", stripeFullyConfigured ? "true" : "false")
    .WaitFor(mainWorkers);

if (!isPublishMode)
{
    mainApi.WithEnvironment("KESTREL_PORT", ports.MainApi.ToString());
}

// Same port contract as account-api: container and Service both on the internal TLS port.
if (isPublishMode)
{
    mainApi
        .WithHttpsEndpoint(8443, 8443, "https")
        .WithKubernetesProbes("/internal-api/ready", "/internal-api/live");
}

// The standalone back-office system: a BFF over the feature systems' internal APIs, owning the
// staff identity outright. Deployed dark during the migration — the edge still routes the
// back-office hostname to account's folded routes — until the cutover flips the Gateway route.
var backOfficeApi = builder
    .AddProject<BackOffice_Api>("back-office-api")
    .WithEnvironment("BackOffice__Host", backOfficePublicHostname)
    .WithEnvironment("BackOffice__AdminsGroupId", MockEasyAuthIdentities.MockAdminsGroupId);

if (!isPublishMode)
{
    backOfficeApi.WithEnvironment("KESTREL_PORT", ports.BackOfficeSystemApi.ToString());
}

if (isPublishMode)
{
    backOfficeApi
        .WithHttpsEndpoint(8443, 8443, "https")
        .WithKubernetesProbes("/internal-api/ready", "/internal-api/live")
        // Staging runs the MockEasyAuth identity selector until a proper OIDC provider for staff
        // exists, mirroring account's folded back-office during the migration.
        .WithEnvironment("MockEasyAuth__Enabled", mockEasyAuthEnabled ? "true" : "false")
        // The BFF calls account's internal API (/internal-api/back-office/*) over in-cluster TLS,
        // carrying the signed-in staff member's delegation token.
        .WithEnvironment("ACCOUNT_API_URL", accountApi.GetEndpoint("https"));
}

var appGateway = builder
    .AddProject<AppGateway>("app-gateway")
    .WithReference(frontendBuild)
    .WithReference(accountApi)
    .WithReference(mainApi)
    .WaitFor(accountApi)
    .WaitFor(frontendBuild)
    .WithEnvironment("Hostnames__App", appPublicHostname)
    .WithUrls(context =>
        {
            // Replace the auto-published "https" endpoint URL with three explicit dashboard URLs.
            // DisplayOrder: higher values sort higher in the list (Web App > Back Office > Open API).
            context.Urls.Clear();
#pragma warning disable CS0618 // DisplayOrder is a field until a future Aspire version re-adds it as a property
            context.Urls.Add(new ResourceUrlAnnotation { Url = appBaseUrl, DisplayText = "Web App", DisplayOrder = 300 });
            context.Urls.Add(new ResourceUrlAnnotation { Url = backOfficeBaseUrl, DisplayText = "Back Office", DisplayOrder = 200 });
            context.Urls.Add(new ResourceUrlAnnotation { Url = $"{appBaseUrl}/openapi", DisplayText = "Open API", DisplayOrder = 100 });
#pragma warning restore CS0618
        }
    );

// ASPNETCORE_URLS is the dev HTTPS listener; in containers the image default port 8080 applies.
if (!isPublishMode)
{
    appGateway.WithEnvironment("ASPNETCORE_URLS", "https://localhost:" + ports.AppGateway);
}

if (isPublishMode)
{
    // ClusterDestinationConfigFilter resolves YARP's upstream clusters from these URLs; the
    // EndpointReferences resolve to the in-cluster Services (e.g. http://account-api-service:80).
    // The storage URLs proxy avatars/logos from Object Storage; the bucket prefix rewrites those
    // proxy paths to the physical bucket names (Scaleway's bucket namespace is shared across all
    // customers of a region, so deployed buckets carry an environment prefix). The endpoint is
    // external because the Cilium Gateway routes the public app hostname here. The gateway serves
    // no health endpoints, so probes hit its Scalar OpenAPI page instead.
    appGateway
        .WithHttpsEndpoint(8443, 8443, "https")
        .WithExternalHttpEndpoints()
        .WithEnvironment("ACCOUNT_API_URL", accountApi.GetEndpoint("https"))
        .WithEnvironment("MAIN_API_URL", mainApi.GetEndpoint("https"))
        .WithEnvironment("ACCOUNT_STORAGE_URL", accountStorageUrl)
        .WithEnvironment("MAIN_STORAGE_URL", mainStorageUrl)
        .WithEnvironment("BLOB_STORAGE_BUCKET_PREFIX", builder.Configuration["Storage:BucketPrefix"] ?? string.Empty)
        .WithKubernetesProbes("/openapi", "/openapi");
}

// Dedicated database migrators: run-once console apps applying schema + data migrations with the
// structure-owner role. Publish-only (local development migrates at worker startup) and deployed
// as Jobs, not Deployments: the chart patch turns the generated workload into a pre-upgrade Job.
IResourceBuilder<ProjectResource> accountMigrator = null!;
IResourceBuilder<ProjectResource> mainMigrator = null!;

if (isPublishMode)
{
    accountMigrator = builder.AddProject<Account_Migrator>("account-migrator");
    mainMigrator = builder.AddProject<Main_Migrator>("main-migrator");
}

// Deployed workloads share the HMAC token-signing key (tokens are issued and validated by the
// APIs and validated by the gateway) and the Transactional Email SMTP settings.
if (isPublishMode)
{
    var workloadBuilders = new (string Name, IResourceBuilder<IResourceWithEnvironment> Resource)[]
    {
        ("account-workers", accountWorkers),
        ("account-api", accountApi),
        ("main-workers", mainWorkers),
        ("main-api", mainApi),
        ("app-gateway", appGateway),
        ("back-office-api", backOfficeApi),
        ("account-migrator", accountMigrator),
        ("main-migrator", mainMigrator)
    };

    foreach (var (name, workload) in workloadBuilders)
    {
        var secretManagerKey = builder.AddParameter($"{name}-secret-manager-key", true);

        // The chart carries no ASPNETCORE_/DOTNET_ENVIRONMENT, and SharedKernel reads the raw env
        // var (not the host environment) for IsRunningInCloud — so publish mode pins it explicitly.
        // Both spellings cover ASP.NET hosts and generic-host workers.
        workload
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
            .WithEnvironment("SCW_SECRET_KEY", secretManagerKey)
            .WithEnvironment("SecretManager__Path", $"/{name}")
            .WithEnvironment("SCW_DEFAULT_PROJECT_ID", secretManagerProjectId)
            .WithEnvironment("SCW_DEFAULT_REGION", "fr-par")
            .WithEnvironment("SENDER_EMAIL_ADDRESS", senderEmailAddress);

        // Cockpit telemetry endpoints (per signal) arrive as plain environment values from the
        // deploy pipeline; the authenticating token is the workload's Secret Manager secret.
        // Absent locally (dashboard OTLP) and in environments without Cockpit.
        foreach (var signal in new[] { "Metrics", "Logs", "Traces" })
        {
            var endpoint = builder.Configuration[$"Cockpit:{signal}OtlpEndpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                workload.WithEnvironment($"Cockpit__{signal}OtlpEndpoint", endpoint);
            }
        }

        // Single-writer Data Protection keyring: main-api owns key generation and rotation (it mints
        // the antiforgery tokens in the module-federated shell that account-api validates), every
        // other API consumes the shared ring read-only.
        if (name == "main-api")
        {
            workload.WithEnvironment("DATA_PROTECTION_KEY_RING_OWNER", "true");
        }
    }
}

// The three workloads that serve traffic in the cluster: internal cert-manager certificate,
// HTTPS-only Kestrel (SharedKernel's AddInternalTls), and probes/env/routing on 8443.
string[] internalTlsWorkloads = ["account-api", "main-api", "app-gateway", "back-office-api"];

// Public ingress: one Cilium (Envoy) Gateway with two hostnames. The app hostname terminates at
// the YARP gateway; the back-office hostname bypasses it and hits account-api directly,
// mirroring the Azure post-split topology. cert-manager is installed BY THE CHART (same
// upstream v1.20.2 the cluster-ops steps used to apply by hand, with CRDs and the Gateway API
// shim enabled) and owns the production issuer: DNS/TLS bring-up is complete and the staging
// endpoint's rate-limit protection is no longer needed.
if (isPublishMode && kubernetes is not null)
{
    var certManager = kubernetes.AddCertManager("cert-manager");
    var issuer = certManager
        .AddIssuer("letsencrypt")
        .WithLetsEncryptProduction(acmeEmail)
        .WithHttp01Solver(); // ACME challenges route through the Gateway (HTTPRoute on /.well-known/acme-challenge)

    // The internal CA comes from Terraform (the deploy passes it as the InternalTls:CaPem
    // configuration value): the chart turns it into the ConfigMap Envoy's BackendTLSPolicy
    // validation consumes, and the app-gateway reads it directly from configuration.

    var publicGateway = kubernetes
        .AddGateway("public-gateway")
        .WithGatewayClass("cilium")
        .WithHostname(appPublicHostname)
        .WithHostname(backOfficePublicHostname)
        .WithTls(issuer);

    publicGateway
        .WithRoute(appPublicHostname, "/", appGateway.GetEndpoint("https"))
        .WithRoute(backOfficePublicHostname, "/", accountApi.GetEndpoint("https"));

    // Two patches to the generated chart, applied right after chart generation so helm consumes
    // them (chart-consuming steps run after final actions):
    //  * every Deployment rolls out with maxUnavailable: 1 — with single-replica workloads the last
    //    ready pod terminates before its replacement passes readiness, so deploys surfaced as edge
    //    504 windows. The patch pins maxUnavailable: 0 (maxSurge stays 1).
    //  * one migration Job per self-contained system, running the dedicated migrator image to
    //    completion (schema + data migrations) BEFORE any new pod rolls out. It is a pre-upgrade/
    //    pre-install hook, and the migrator's config map and secret are promoted to hook resources
    //    too — regular resources are applied only after hooks, so without that the Job's pod would
    //    start before its config exists (whenever the migrator is new in a release, or always on a
    //    first install).
    builder.Pipeline.WithFinalAction("publish-kubernetes", PatchGeneratedChart);
}

AddStripeCliContainer();

await builder.Build().RunAsync();

// Generated per self-contained system; the RESOURCE/VALUES_KEY tokens are replaced per migrator.
// The image/config/secret are the migrator's own — a console app that applies schema + data
// migrations and exits (it has no run mode to select; it is not a worker). Helm's own {{ ... }}
// braces stay literal (raw string, no interpolation).
const string jobTemplate = """
                           ---
                           apiVersion: "batch/v1"
                           kind: "Job"
                           metadata:
                             name: "{{ .Chart.Name }}-RESOURCE-job"
                             labels:
                               app.kubernetes.io/name: "{{ .Chart.Name }}"
                               app.kubernetes.io/component: "RESOURCE"
                               app.kubernetes.io/instance: "{{ .Release.Name }}"
                             annotations:
                               # Runs BEFORE any pod of the release rolls out, upgrade or fresh
                               # install: the upgrade blocks until migrations complete, and a
                               # failed migration aborts the deploy with the old version still
                               # serving. The config map and secret below are hook resources
                               # themselves (applied at weight -2, before this Job at -1) because
                               # regular release resources are applied after hooks.
                               "helm.sh/hook": "pre-upgrade,pre-install"
                               # The previous Job is deleted just before this one is created (default policy).
                               "helm.sh/hook-delete-policy": "before-hook-creation"
                               "helm.sh/hook-weight": "-1"
                           spec:
                             backoffLimit: 2
                             template:
                               metadata:
                                 labels:
                                   app.kubernetes.io/name: "{{ .Chart.Name }}"
                                   app.kubernetes.io/component: "RESOURCE"
                                   app.kubernetes.io/instance: "{{ .Release.Name }}"
                               spec:
                                 restartPolicy: Never
                                 containers:
                                   - name: "RESOURCE"
                                     image: "{{ .Values.parameters.VALUES_KEY.VALUES_KEY_image }}"
                                     envFrom:
                                       - configMapRef:
                                           name: "RESOURCE-config"
                                       - secretRef:
                                           name: "RESOURCE-secrets"
                                     imagePullPolicy: "IfNotPresent"
                           """;

// Hook annotations for a migrator's config map and secret (inserted into their metadata): hook
// resources are applied before the regular release resources and before lower-weight hooks, so
// the migration Job (weight -1) can reference them even when they are new in this release.
// before-hook-creation recreates them for every hook run so their values always match the
// release being deployed.
const string hookResourceAnnotations = """
                                         annotations:
                                           "helm.sh/hook": "pre-upgrade,pre-install"
                                           "helm.sh/hook-delete-policy": "before-hook-creation"
                                           "helm.sh/hook-weight": "-2"
                                       """;

// Runs as the final action of the chart-generation step (publish-kubernetes): by then the Helm
// chart exists, and every step that consumes it — helm-deploy-kubernetes in a deploy — runs
// after final actions complete.
async Task PatchGeneratedChart(PipelineStepContext context)
{
    var templatesFolder = Path.Combine(builder.AppHostDirectory, "aspire-output", "templates");
    if (!Directory.Exists(templatesFolder))
    {
        throw new InvalidOperationException($"Chart templates folder not found at '{templatesFolder}'.");
    }

    await PatchRolloutStrategyAsync(templatesFolder);
    await AddMigrationJobsAsync(templatesFolder);
    PatchGatewayRoutesForwardedProto(templatesFolder);
    AddInternalTlsManifests(templatesFolder);
    PatchWorkloadsForInternalTls(templatesFolder);
}

// The internal TLS trust chain, written as chart manifests because the Aspire publisher has no
// typed support for BackendTLSPolicy: Terraform owns the internal CA and the per-workload leaf
// certificates (see cloud-infrastructure/scaleway/environment), the leaf key material reaches the
// workloads through Secret Manager, and the CA — public material — reaches this publish pipeline
// as the InternalTls:CaPem configuration value. The chart needs it in two places: the ConfigMap
// Envoy validates against, and (referenced by name) the BackendTLSPolicies.
void AddInternalTlsManifests(string templatesFolder)
{
    var caPem = builder.Configuration["InternalTls:CaPem"];
    if (string.IsNullOrWhiteSpace(caPem))
    {
        throw new InvalidOperationException("InternalTls:CaPem is not set — the deploy command passes the Terraform-managed internal CA certificate. Without it Envoy cannot validate the workloads' TLS.");
    }

    var internalTlsFolder = Path.Combine(templatesFolder, "internal-tls");
    Directory.CreateDirectory(internalTlsFolder);

    var caConfigMap = $"""
                       ---
                       apiVersion: "v1"
                       kind: "ConfigMap"
                       metadata:
                         name: "internal-root-ca-bundle"
                       data:
                         # Cilium's BackendTLSPolicy validation reads the CA from the literal
                         # `ca.crt` key, whatever the policy references.
                         ca.crt: |
                           {caPem.Replace("\n", "\n    ")}
                       """;
    File.WriteAllText(Path.Combine(internalTlsFolder, "internal-ca.yaml"), caConfigMap);

    const string backendTlsPolicyTemplate = """
                                            ---
                                            apiVersion: "gateway.networking.k8s.io/v1"
                                            kind: "BackendTLSPolicy"
                                            metadata:
                                              name: "WORKLOAD-service-tls"
                                            spec:
                                              targetRefs:
                                                - group: ""
                                                  kind: "Service"
                                                  name: "WORKLOAD-service"
                                              validation:
                                                hostname: "WORKLOAD-service.default.svc.cluster.local"
                                                caCertificateRefs:
                                                  - group: ""
                                                    kind: "ConfigMap"
                                                    name: "internal-root-ca-bundle"
                                            """;
    // main-api is only dialed by YARP (which trusts the CA itself); Envoy reaches the
    // app-gateway and account-api directly, so those two carry the policy.
    var policies = string.Join("\n", internalTlsWorkloads.Where(w => w != "main-api").Select(w => backendTlsPolicyTemplate.Replace("WORKLOAD", w)));
    File.WriteAllText(Path.Combine(internalTlsFolder, "backend-tls-policies.yaml"), policies);

    Console.WriteLine($"Internal TLS patch: CA, {internalTlsWorkloads.Length} leaf certificates, trust bundle, and backend TLS policies added.");
}

// The publisher generates probes and port declarations from the publish-mode endpoint, but the
// generated deployment still carries the plain-HTTP contract (scheme HTTP, port 8080) and no
// certificate mounts — this patch completes the HTTPS-only conversion per workload.
void PatchWorkloadsForInternalTls(string templatesFolder)
{
    foreach (var workload in internalTlsWorkloads)
    {
        var deploymentPath = Path.Combine(templatesFolder, workload, "deployment.yaml");
        var yaml = File.ReadAllText(deploymentPath);

        // Probes and the declared port follow the endpoint: HTTPS on 8443.
        yaml = yaml.Replace("scheme: \"HTTP\"", "scheme: \"HTTPS\"");
        yaml = yaml.Replace("port: 8080", "port: 8443");
        yaml = yaml.Replace("- name: \"http\"\n              protocol: \"TCP\"\n              containerPort: 8080",
            "- name: \"https\"\n              protocol: \"TCP\"\n              containerPort: 8443"
        );

        File.WriteAllText(deploymentPath, yaml);
        Console.WriteLine($"Internal TLS patch: {workload} serves HTTPS only on 8443 (probes, ports, certificate mount).");
    }
}

// The public requests' TLS terminates twice: at the Scaleway edge (its certificate) and again at
// the Gateway (the cert-manager certificate — the edge origin connects with TLS through the
// TCP-passthrough LB, see the edge backend stage). Cilium's Envoy does not set X-Forwarded-Proto
// when forwarding, so nothing tells the APIs that the request arrived encrypted — and everything
// from Envoy inward (YARP, the APIs) is plain HTTP on the pod network. The APIs' ForwardedHeaders
// middleware trusts the pod network and honors that header, and antiforgery's SecurePolicy.Always
// refuses requests that do not present as SSL (found live: crash-loop on SecurePolicy.Always).
// The rewrite is honest: every request reaching the Gateway terminated TLS at its https listener,
// and the Gateway is not reachable from inside the cluster.
void PatchGatewayRoutesForwardedProto(string templatesFolder)
{
    var gatewayRoutesFolder = Path.Combine(templatesFolder, "public-gateway");
    var routeFiles = Directory.GetFiles(gatewayRoutesFolder, "*-route.yaml");
    if (routeFiles.Length == 0)
    {
        throw new InvalidOperationException("No HTTPRoute templates found under public-gateway — the generated chart layout changed, update the forwarded-proto patch.");
    }

    foreach (var routeFile in routeFiles)
    {
        var yaml = File.ReadAllText(routeFile);
        if (yaml.Contains("requestHeaderModifier"))
        {
            continue;
        }

        var backendRefsIndex = yaml.IndexOf("      backendRefs:", StringComparison.Ordinal);
        if (backendRefsIndex < 0)
        {
            throw new InvalidOperationException($"No backendRefs block found in {Path.GetFileName(routeFile)} — the generated chart layout changed, update the forwarded-proto patch.");
        }

        var forwardedProtoFilter = """
                                         filters:
                                           - type: "RequestHeaderModifier"
                                             requestHeaderModifier:
                                               set:
                                                 - name: "X-Forwarded-Proto"
                                                   value: "https"
                                   """;
        yaml = yaml.Insert(backendRefsIndex, forwardedProtoFilter + Environment.NewLine);
        File.WriteAllText(routeFile, yaml);
        Console.WriteLine($"Forwarded-proto patch: {Path.GetFileName(routeFile)} restores X-Forwarded-Proto: https.");
    }
}

async Task PatchRolloutStrategyAsync(string templatesFolder)
{
    var deployments = Directory.GetFiles(templatesFolder, "deployment.yaml", SearchOption.AllDirectories);
    if (deployments.Length == 0)
    {
        throw new InvalidOperationException("No deployment.yaml found in the generated chart — the chart layout changed, update the rollout patch.");
    }

    var patched = 0;
    foreach (var deployment in deployments)
    {
        var yaml = await File.ReadAllTextAsync(deployment);
        if (!yaml.Contains("maxUnavailable: 1"))
        {
            continue;
        }

        await File.WriteAllTextAsync(deployment, yaml.Replace("maxUnavailable: 1", "maxUnavailable: 0"));
        patched++;
    }

    Console.WriteLine($"Rollout patch: {patched} of {deployments.Length} deployments pinned to maxUnavailable: 0.");
}

async Task AddMigrationJobsAsync(string templatesFolder)
{
    // One migration Job per migrator workload: the publisher generates a Deployment for every
    // project resource, but a migrator runs to completion and exits — as a Deployment it would
    // crash-loop forever. The patch deletes the generated Deployment and replaces it with a
    // pre-upgrade/pre-install hook Job, and promotes the migrator's config map and secret to
    // hook resources so they exist before the Job's pod starts.
    var migratorWorkloads = Directory.GetDirectories(templatesFolder, "*-migrator");
    if (migratorWorkloads.Length == 0)
    {
        throw new InvalidOperationException("No *-migrator workload found in the generated chart — the chart layout changed, update the migration patch.");
    }

    foreach (var migratorFolder in migratorWorkloads)
    {
        var migrator = Path.GetFileName(migratorFolder); // account-migrator
        File.Delete(Path.Combine(migratorFolder, "deployment.yaml"));

        await PromoteToHookResourceAsync(Path.Combine(migratorFolder, "config.yaml"), migrator);
        await PromoteToHookResourceAsync(Path.Combine(migratorFolder, "secrets.yaml"), migrator);

        var job = jobTemplate
            .Replace("RESOURCE", migrator)
            .Replace("VALUES_KEY", migrator.Replace('-', '_'));

        await File.WriteAllTextAsync(Path.Combine(migratorFolder, "job.yaml"), job);
        Console.WriteLine($"Migration patch: {migrator} Job added (pre-upgrade/pre-install hook).");
    }
}

// The publisher emits every workload's config map and secret with a single metadata labels block;
// the hook annotations are inserted right before it (same indentation).
async Task PromoteToHookResourceAsync(string yamlFile, string migrator)
{
    var yaml = await File.ReadAllTextAsync(yamlFile);
    var labelsIndex = yaml.IndexOf("  labels:", StringComparison.Ordinal);
    if (labelsIndex < 0)
    {
        throw new InvalidOperationException($"No metadata labels block found in {Path.GetFileName(yamlFile)} for {migrator} — the generated chart layout changed, update the migration patch.");
    }

    yaml = yaml.Insert(labelsIndex, hookResourceAnnotations + Environment.NewLine);
    await File.WriteAllTextAsync(yamlFile, yaml);
}


return;

void AddStripeCliContainer()
{
    // stripe-cli forwards webhook events to localhost; staging uses a real Stripe webhook endpoint, so
// the forwarder stays out of the published topology.
    if (stripeConfigured && !isPublishMode)
    {
        builder
            .AddContainer("stripe-cli", "stripe/stripe-cli:latest")
            .WithContainerRuntimeArgs("--add-host", $"{appHostname}:host-gateway")
            .WithArgs("listen", "--forward-to", $"{appBaseUrl}/api/account/subscriptions/stripe-webhook", "--skip-verify")
            .WithEnvironment("STRIPE_API_KEY", stripeApiKey)
            .WithLifetime(ContainerLifetime.Persistent);
    }
}

(bool Configured, IResourceBuilder<ParameterResource> ClientId, IResourceBuilder<ParameterResource> ClientSecret) ConfigureGoogleOAuthParameters()
{
    _ = builder.AddParameter("google-oauth-enabled")
        .WithDescription("""
                         **Google OAuth** -- Enables "Sign in with Google" for login and signup using OpenID Connect with PKCE.

                         **Important**: Set up OAuth credentials in the [Google Cloud Console](https://console.cloud.google.com/apis/credentials) and configure them according to the guide in README.md **before** enabling this.

                         - Enter `true` to enable Google OAuth, or `false` to skip. This can be changed later.
                         - After enabling, **restart Aspire** to be prompted for the Client ID and Client Secret.

                         See **README.md** for full setup instructions.
                         """, true
        );

    var configured = builder.Configuration["Parameters:google-oauth-enabled"] == "true";

    if (configured)
    {
        var clientId = builder.AddParameter("google-oauth-client-id", true)
            .WithDescription("""
                             Google OAuth Client ID from the [Google Cloud Console](https://console.cloud.google.com/apis/credentials). The format is `<id>.apps.googleusercontent.com`.

                             **After entering this and the Client Secret, restart Aspire** to apply the configuration.

                             See **README.md** for full setup instructions.
                             """, true
            );
        var clientSecret = builder.AddParameter("google-oauth-client-secret", true)
            .WithDescription("""
                             Google OAuth Client Secret from the [Google Cloud Console](https://console.cloud.google.com/apis/credentials).

                             **After entering this and the Client ID, restart Aspire** to apply the configuration.

                             See **README.md** for full setup instructions.
                             """, true
            );

        return (configured, clientId, clientSecret);
    }

    return (
        configured,
        builder.CreateResourceBuilder(new ParameterResource("google-oauth-client-id", _ => "not-configured", true)),
        builder.CreateResourceBuilder(new ParameterResource("google-oauth-client-secret", _ => "not-configured", true))
    );
}

(bool Configured, IResourceBuilder<ParameterResource> PublishableKey, IResourceBuilder<ParameterResource> ApiKey, IResourceBuilder<ParameterResource> WebhookSecret) ConfigureStripeParameters()
{
    _ = builder.AddParameter("stripe-enabled")
        .WithDescription("""
                         **Stripe Integration** -- Enables embedded checkout, prorated plan upgrades, automatic tax management, localized invoices, billing history with refunds, and more.

                         **Important**: Set up a [Stripe sandbox environment](https://dashboard.stripe.com) and configure it according to the guide in README.md **before** enabling this.

                         - Enter `true` to enable Stripe, or `false` to skip. This can be changed later.
                         - Setup requires **2 restarts** after enabling: first for API keys, then for the webhook secret from the stripe-cli container.

                         See **README.md** for full setup instructions.
                         """, true
        );

    var configured = builder.Configuration["Parameters:stripe-enabled"] == "true";

    if (configured)
    {
        var publishableKey = builder.AddParameter("stripe-publishable-key", true)
            .WithDescription("""
                             Stripe Publishable Key from the [Stripe Dashboard](https://dashboard.stripe.com/apikeys). Starts with `pk_test_` or `pk_live_`.

                             **After entering this and the Secret Key, restart Aspire.** The stripe-cli container will start and generate a webhook secret, which you will enter on the next restart.

                             See **README.md** for full setup instructions.
                             """, true
            );
        var apiKey = builder.AddParameter("stripe-api-key", true)
            .WithDescription("""
                             Stripe Secret Key from the [Stripe Dashboard](https://dashboard.stripe.com/apikeys). Starts with `sk_test_` or `sk_live_`.

                             **After entering this and the Publishable Key, restart Aspire.** The stripe-cli container will start and generate a webhook secret, which you will enter on the next restart.

                             See **README.md** for full setup instructions.
                             """, true
            );

        var apiKeyConfigured = builder.Configuration["Parameters:stripe-api-key"] is not null;
        var webhookSecret = apiKeyConfigured
            ? builder.AddParameter("stripe-webhook-secret", true)
                .WithDescription("Webhook signing secret. Find it in the [Stripe Dashboard Workbench](https://dashboard.stripe.com/test/workbench/webhooks) or in the stripe-cli container logs after the previous restart. Starts with `whsec_`.", true)
            : builder.CreateResourceBuilder(new ParameterResource("stripe-webhook-secret", _ => "not-configured", true));

        return (configured, publishableKey, apiKey, webhookSecret);
    }

    return (
        configured,
        builder.CreateResourceBuilder(new ParameterResource("stripe-publishable-key", _ => "not-configured", true)),
        builder.CreateResourceBuilder(new ParameterResource("stripe-api-key", _ => "not-configured", true)),
        builder.CreateResourceBuilder(new ParameterResource("stripe-webhook-secret", _ => "not-configured", true))
    );
}

void OverrideAspireDashboardEnvironmentVariables(PortAllocation portAllocation)
{
    // Must be set before DistributedApplication.CreateBuilder so Aspire picks them up.
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"https://localhost:{portAllocation.Aspire}");
    Environment.SetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL", $"https://localhost:{portAllocation.OtelEndpoint}");
    Environment.SetEnvironmentVariable("DOTNET_RESOURCE_SERVICE_ENDPOINT_URL", $"https://localhost:{portAllocation.ResourceService}");
}

void CheckPortAvailability(PortAllocation portAllocation)
{
    var portsToCheck = new[]
    {
        (portAllocation.ResourceService, "Resource Service"),
        (portAllocation.OtelEndpoint, "Dashboard"),
        (portAllocation.Aspire, "Aspire")
    };
    var blocked = portsToCheck.Where(p => !IsPortAvailable(p.Item1)).ToList();

    if (blocked.Count > 0)
    {
        Console.WriteLine($"⚠️  Port conflicts: {string.Join(", ", blocked.Select(b => $"{b.Item1} ({b.Item2})"))}");
        Console.WriteLine("   Services already running. Stop them first using 'run --stop'.");
        Environment.Exit(1);
    }

    bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
