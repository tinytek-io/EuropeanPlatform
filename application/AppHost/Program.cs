using System.Net;
using System.Net.Sockets;
using AppHost;
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

CheckPortAvailability(ports);

var appHostname = builder.Configuration["Hostnames:App"] ?? "app.dev.localhost";
var backOfficeHostname = builder.Configuration["Hostnames:BackOffice"] ?? "back-office.dev.localhost";

var appBaseUrl = $"https://{appHostname}:{ports.AppGateway}";
// Localhost mirrors the Azure post-split topology: back-office traffic bypasses AppGateway and
// hits the consolidated account-api process directly on a dedicated Kestrel port.
var backOfficeBaseUrl = $"https://{backOfficeHostname}:{ports.BackOfficeApi}";

var certificatePassword = await builder.CreateSslCertificateIfNotExists();

SecretManagerHelper.GenerateAuthenticationTokenSigningKey("authentication-token-signing-key");

var (googleOAuthConfigured, googleOAuthClientId, googleOAuthClientSecret) = ConfigureGoogleOAuthParameters();

var (stripeConfigured, stripePublishableKey, stripeApiKey, stripeWebhookSecret) = ConfigureStripeParameters();
var stripeFullyConfigured = stripeConfigured && builder.Configuration["Parameters:stripe-webhook-secret"] is not null and not "not-configured";

var postgresPassword = builder.CreateStablePassword("postgres-password");
var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: ports.Postgres)
    .WithDataVolume($"{dockerVolumePrefix}{ports.VolumeNameInfix}-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithArgs("-c", "wal_level=logical");

// S3-compatible object storage, the local stand-in for Scaleway Object Storage. Fixed credentials, safe for a
// local-only emulator: SeaweedFS's default IAM accepts any, so they only keep the connection string's shape identical
// to the deployed one. Buckets are created lazily by the S3 client on first use, and the S3 gateway runs on the
// container's 8333 so the default `weed server` ports stay free.
builder
    .AddContainer("seaweedfs", "chrislusf/seaweedfs:latest")
    .WithHttpEndpoint(ports.Blob, 8333)
    .WithArgs("server", "-dir=/data", "-s3", "-s3.port=8333")
    .WithVolume($"{dockerVolumePrefix}{ports.VolumeNameInfix}-seaweed-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithUrlForEndpoint("http", url => url.DisplayText = "Object storage (SeaweedFS)");

// AddConnectionString reads its value from ConnectionStrings:{name} (its optional second argument is the environment
// variable name), so set this worktree's object storage port in configuration first.
builder.Configuration["ConnectionStrings:blob-storage"] = $"ServiceUrl=http://localhost:{ports.Blob};AccessKey=seaweed-admin;SecretKey=seaweed-admin;ForcePathStyle=true;Region=fr-par";
var blobStorage = builder.AddConnectionString("blob-storage");

builder
    .AddContainer("mail-server", "axllent/mailpit")
    .WithHttpEndpoint(ports.MailpitHttp, 8025)
    .WithEndpoint(ports.MailpitSmtp, 1025)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithUrlForEndpoint("http", u => u.DisplayText = "Read mail here");

var frontendBuild = builder
    .AddJavaScriptApp("frontend-build", "../")
    .WithEnvironment("CERTIFICATE_PASSWORD", certificatePassword)
    .WithEnvironment("MAIN_STATIC_PORT", ports.MainStatic.ToString())
    .WithEnvironment("ACCOUNT_STATIC_PORT", ports.AccountStatic.ToString())
    .WithEnvironment("BACK_OFFICE_STATIC_PORT", ports.BackOfficeStatic.ToString());

var accountDatabase = postgres
    .AddDatabase("account-database", "account");

var accountWorkers = builder
    .AddProject<Account_Workers>("account-workers")
    .WithEnvironment("KESTREL_PORT", ports.AccountWorkers.ToString())
    .WithReference(accountDatabase)
    .WithReference(blobStorage)
    // The BillingDriftWorker resolves StripeClientFactory which reads these. Without them the worker
    // process sees UnconfiguredStripeClient even when Stripe is configured at the API level, and every
    // stale subscription logs a warn + fail line through ProcessPendingStripeEvents on every worker start.
    .WithEnvironment("Stripe__SubscriptionEnabled", stripeFullyConfigured ? "true" : "false")
    .WithEnvironment("Stripe__ApiKey", stripeApiKey)
    .WithEnvironment("Stripe__WebhookSecret", stripeWebhookSecret)
    .WithEnvironment("Stripe__PublishableKey", stripePublishableKey)
    .WithEnvironment("Stripe__AllowMockProvider", "true")
    .WaitFor(accountDatabase);

var accountApi = builder
    .AddProject<Account_Api>("account-api")
    .WithEnvironment("KESTREL_PORT", ports.AccountApi.ToString())
    // Second Kestrel port for back-office.dev.localhost so localhost mirrors the Azure post-split
    // topology where back-office has its own external ingress and AppGateway is not in the path.
    .WithEnvironment("BACK_OFFICE_KESTREL_PORT", ports.BackOfficeApi.ToString())
    // BackOfficeDevStaticProxy forwards /static/* and HMR traffic on the back-office Kestrel listener
    // to the rsbuild dev server. Dev-only; production builds serve a baked bundle from disk.
    .WithEnvironment("BACK_OFFICE_STATIC_PORT", ports.BackOfficeStatic.ToString())
    // Back-office bundle URLs target the dedicated Kestrel port directly (no AppGateway).
    .WithEnvironment("BACK_OFFICE_PUBLIC_URL", backOfficeBaseUrl)
    .WithEnvironment("BACK_OFFICE_CDN_URL", backOfficeBaseUrl)
    .WithUrlConfiguration(appHostname, ports.AppGateway, "/account")
    // Google OAuth's redirect_uri whitelist requires literal 'localhost', not subdomains like
    // 'app.dev.localhost'. The callback then 301's via LocalhostRedirectMiddleware back to the
    // canonical 'app.dev.localhost' so OAuth-state session cookies flow with the redirected request.
    .WithEnvironment("OAUTH_PUBLIC_URL", "https://localhost:" + ports.AppGateway)
    .WithEnvironment("Hostnames__App", appHostname)
    .WithEnvironment("BackOffice__Host", backOfficeHostname)
    .WithEnvironment("BackOffice__AdminsGroupId", MockEasyAuthIdentities.MockAdminsGroupId)
    .WithReference(accountDatabase)
    .WithReference(blobStorage)
    .WithEnvironment("OAuth__Google__ClientId", googleOAuthClientId)
    .WithEnvironment("OAuth__Google__ClientSecret", googleOAuthClientSecret)
    .WithEnvironment("OAuth__AllowMockProvider", "true")
    .WithEnvironment("Stripe__SubscriptionEnabled", stripeFullyConfigured ? "true" : "false")
    .WithEnvironment("Stripe__ApiKey", stripeApiKey)
    .WithEnvironment("Stripe__WebhookSecret", stripeWebhookSecret)
    .WithEnvironment("Stripe__PublishableKey", stripePublishableKey)
    .WithEnvironment("Stripe__AllowMockProvider", "true")
    .WithEnvironment("PUBLIC_GOOGLE_OAUTH_ENABLED", googleOAuthConfigured ? "true" : "false")
    // Force-on so newcomers see the back-office billing UI without Stripe configured. Set to "false" (or
    // change back to `stripeFullyConfigured ? "true" : "false"`) to hide all billing/revenue/Stripe data.
    .WithEnvironment("PUBLIC_SUBSCRIPTION_ENABLED", "true")
    .WaitFor(accountWorkers);

var mainDatabase = postgres
    .AddDatabase("main-database", "main");

var mainWorkers = builder
    .AddProject<Main_Workers>("main-workers")
    .WithEnvironment("KESTREL_PORT", ports.MainWorkers.ToString())
    .WithReference(mainDatabase)
    .WithReference(blobStorage)
    .WaitFor(mainDatabase);

var mainApi = builder
    .AddProject<Main_Api>("main-api")
    .WithEnvironment("KESTREL_PORT", ports.MainApi.ToString())
    .WithUrlConfiguration(appHostname, ports.AppGateway, "")
    .WithReference(mainDatabase)
    .WithReference(blobStorage)
    .WithEnvironment("PUBLIC_GOOGLE_OAUTH_ENABLED", googleOAuthConfigured ? "true" : "false")
    .WithEnvironment("PUBLIC_SUBSCRIPTION_ENABLED", stripeFullyConfigured ? "true" : "false")
    .WaitFor(mainWorkers);

builder
    .AddProject<AppGateway>("app-gateway")
    .WithReference(frontendBuild)
    .WithReference(accountApi)
    .WithReference(mainApi)
    .WaitFor(accountApi)
    .WaitFor(frontendBuild)
    .WithEnvironment("ASPNETCORE_URLS", "https://localhost:" + ports.AppGateway)
    .WithEnvironment("Hostnames__App", appHostname)
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

AddStripeCliContainer();

await builder.Build().RunAsync();

return;

void AddStripeCliContainer()
{
    if (stripeConfigured)
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
