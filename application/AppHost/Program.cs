using System.Net;
using System.Net.Sockets;
using AppHost;
using Aspire.Hosting.EuropeanPlatform;
using Aspire.Hosting.EuropeanPlatform.Profiles;
using Aspire.Hosting.EuropeanPlatform.SelfContainedSystems;
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
// certificates, emulator bootstrapping) must not run. CheckPortAvailability in particular calls
// Environment.Exit(1) when Aspire is already running locally, which would abort every publish.
var isPublishMode = builder.ExecutionContext.IsPublishMode;

if (!isPublishMode)
{
    CheckPortAvailability(ports);
}

// The platform the systems run on: PostgreSQL, object storage and a mail catcher locally; the Scaleway Kapsule
// environment, registry, managed services and per-workload Secret Manager identities when deployed. Values Terraform
// owns are passed by the deploy command from the Terraform outputs; per-tier choices live in
// appsettings.<environment>.json; secrets stay deploy-time inputs in the chart's values.yaml.
var platform = builder.AddEuropeanPlatform(new EuropeanPlatformOptions(
        $"{dockerVolumePrefix}{ports.VolumeNameInfix}",
        () => builder.CreateStablePassword("postgres-password"),
        ports.Postgres,
        ports.Blob,
        ports.MailpitHttp,
        ports.MailpitSmtp
    )
);

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
// Locally the back-office bypasses the app gateway and hits account-api directly on a dedicated Kestrel port.
var backOfficeBaseUrl = $"https://{backOfficeHostname}:{ports.BackOfficeApi}";
// In publish mode the scheme+hostname is the public URL seen by browsers; internally the gateway
// forwards plain HTTP to the workloads.
var appPublicUrl = $"https://{appPublicHostname}";

// Development-machine material only: the dev HTTPS certificate (consumed by the build-only frontend
// container, which never reaches the manifest) and the user-secrets token signing key. Deployed
// workloads read their signing key and TLS material from Secret Manager.
var certificatePassword = isPublishMode ? string.Empty : await builder.CreateSslCertificateIfNotExists();

if (!isPublishMode)
{
    SecretManagerHelper.GenerateAuthenticationTokenSigningKey("authentication-token-signing-key");
}

var (googleOAuthConfigured, googleOAuthClientId, googleOAuthClientSecret) = ConfigureGoogleOAuthParameters();

var (stripeConfigured, stripePublishableKey, stripeApiKey, stripeWebhookSecret) = ConfigureStripeParameters();
var stripeFullyConfigured = stripeConfigured && builder.Configuration["Parameters:stripe-webhook-secret"] is not null and not "not-configured";

// The billing UI (account's subscription pages and the back-office billing/revenue views). Forced on
// in local development so newcomers see it without configuring Stripe; deployed environments show
// it only when Stripe is fully configured, so a tier without Stripe never presents billing it
// cannot serve.
var subscriptionUiEnabled = !isPublishMode || stripeFullyConfigured ? "true" : "false";

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

// account's API needs more memory than the others: under Server GC it used about 200Mi on staging, the other APIs
// about 150Mi.
var account = platform.AddSelfContainedSystem<Account_Api, Account_Workers, Account_Migrator>(
    "account",
    new SelfContainedSystemPorts(ports.AccountApi, ports.AccountWorkers),
    new SelfContainedSystemOptions { UsesBlobStorage = true, ApiClass = WorkloadClass.MemoryIntensive }
);

// The BillingDriftWorker resolves StripeClientFactory which reads the Stripe settings. Without them the
// worker process sees UnconfiguredStripeClient even when Stripe is configured at the API level, and
// every stale subscription logs a warn + fail line through ProcessPendingStripeEvents on every start.
WithStripeConfiguration(account.Workers);

account.Api
    .WithUrlConfiguration(appHostname, isPublishMode ? null : ports.AppGateway, "/account")
    // Google OAuth's redirect_uri whitelist requires literal 'localhost' in development, not
    // subdomains like 'app.dev.localhost'; the callback then 301's via LocalhostRedirectMiddleware
    // back to the canonical dev hostname. In publish mode the public URL is the real one.
    .WithEnvironment("OAUTH_PUBLIC_URL", isPublishMode ? appPublicUrl : "https://localhost:" + ports.AppGateway)
    .WithEnvironment("Hostnames__App", appPublicHostname)
    .WithEnvironment("BackOffice__Host", backOfficePublicHostname)
    .WithEnvironment("BackOffice__AdminsGroupId", MockEasyAuthIdentities.MockAdminsGroupId)
    .WithEnvironment("OAuth__Google__ClientId", googleOAuthClientId)
    .WithEnvironment("OAuth__Google__ClientSecret", googleOAuthClientSecret)
    // The mock OAuth provider is a developer-machine convenience, so publish mode pins it off
    // explicitly (WithStripeConfiguration does the same for the Stripe mock).
    .WithEnvironment("OAuth__AllowMockProvider", isPublishMode ? "false" : "true")
    .WithEnvironment("PUBLIC_GOOGLE_OAUTH_ENABLED", googleOAuthConfigured ? "true" : "false")
    .WithEnvironment("PUBLIC_SUBSCRIPTION_ENABLED", subscriptionUiEnabled);

WithStripeConfiguration(account.Api);

if (!isPublishMode)
{
    // The back-office is served by account-api on a second, local-only Kestrel listener: deployed environments do
    // not route the back-office hostname, because nothing signs staff in there.
    account.Api
        .WithEnvironment("BACK_OFFICE_KESTREL_PORT", ports.BackOfficeApi.ToString())
        // BackOfficeDevStaticProxy forwards /static/* and HMR traffic on the back-office listener to the rsbuild dev
        // server.
        .WithEnvironment("BACK_OFFICE_STATIC_PORT", ports.BackOfficeStatic.ToString())
        // Back-office bundle URLs target the dedicated Kestrel port directly (no AppGateway).
        .WithEnvironment("BACK_OFFICE_PUBLIC_URL", backOfficeBaseUrl)
        .WithEnvironment("BACK_OFFICE_CDN_URL", backOfficeBaseUrl);
}

// main's API owns the Data Protection keyring: it mints the antiforgery tokens in the module-federated shell that
// account's API validates.
var main = platform.AddSelfContainedSystem<Main_Api, Main_Workers, Main_Migrator>(
    "main",
    new SelfContainedSystemPorts(ports.MainApi, ports.MainWorkers),
    new SelfContainedSystemOptions { UsesBlobStorage = true, OwnsDataProtectionKeyRing = true }
);

main.Api
    .WithUrlConfiguration(appHostname, isPublishMode ? null : ports.AppGateway, "")
    .WithEnvironment("PUBLIC_GOOGLE_OAUTH_ENABLED", googleOAuthConfigured ? "true" : "false")
    .WithEnvironment("PUBLIC_SUBSCRIPTION_ENABLED", stripeFullyConfigured ? "true" : "false");

// The app hostname flows through the app gateway; the back-office hostname is not routed when deployed.
platform.AddAppGateway<AppGateway>(ports.AppGateway, [account, main], [appPublicHostname])
    .WithReference(frontendBuild)
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

AddStripeCliContainer();

await builder.Build().RunAsync();

// The Stripe settings every Stripe-consuming workload needs. The mock provider is a
// developer-machine convenience and throws when enabled in deployed environments.
void WithStripeConfiguration(IResourceBuilder<ProjectResource> workload)
{
    workload
        .WithEnvironment("Stripe__SubscriptionEnabled", stripeFullyConfigured ? "true" : "false")
        .WithEnvironment("Stripe__ApiKey", stripeApiKey)
        .WithEnvironment("Stripe__WebhookSecret", stripeWebhookSecret)
        .WithEnvironment("Stripe__PublishableKey", stripePublishableKey)
        .WithEnvironment("Stripe__AllowMockProvider", isPublishMode ? "false" : "true");
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
