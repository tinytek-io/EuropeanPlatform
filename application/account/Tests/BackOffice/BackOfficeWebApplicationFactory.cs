using Account.Database;
using Account.Integrations.Stripe;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using SharedKernel.Authentication.MockEasyAuth;
using SharedKernel.ExecutionContext;
using SharedKernel.Integrations.Email;
using SharedKernel.SinglePageApp;
using SharedKernel.Telemetry;

namespace Account.Tests.BackOffice;

// Shared host for all back-office endpoint tests in a class. Constructed once via xUnit's
// IClassFixture, the host wires its DbContext, telemetry collector, and Stripe state to a
// per-test BackOfficeTestContext stored in an AsyncLocal — so the same host can serve every
// test in the class while each test still has isolated state.
public class BackOfficeWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string BackOfficeHost = "back-office.test.localhost";

    private readonly AsyncLocal<BackOfficeTestContext?> _currentContext = new();

    public BackOfficeWebApplicationFactory()
    {
        // The development-only CORS policy (AddApiInfrastructure) reads PUBLIC_URL; without it the
        // options validation fails at host startup even though the internal API tests never
        // exercise CORS themselves.
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey, "https://localhost");
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey, "https://localhost");
        Environment.SetEnvironmentVariable(
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost;LiveEndpoint=https://localhost"
        );
        Environment.SetEnvironmentVariable("Stripe__AllowMockProvider", "true");
        Environment.SetEnvironmentVariable("Stripe__PublishableKey", "pk_test_mock_publishable_key");
        Environment.SetEnvironmentVariable("BypassAntiforgeryValidation", "true");
    }

    private BackOfficeTestContext CurrentContext => _currentContext.Value
                                                    ?? throw new InvalidOperationException("BackOfficeTestContext is not set. Call BeginTest before resolving services.");

    // Sets the per-test context for the calling logical-call context. The returned scope clears the
    // context on Dispose so the AsyncLocal does not leak past the test instance lifetime.
    public IDisposable BeginTest(BackOfficeTestContext context)
    {
        _currentContext.Value = context;
        return new TestScope(this);
    }

    // TestServer.PreserveExecutionContext defaults to false, which means the calling test's
    // ExecutionContext (and therefore the AsyncLocal-stored BackOfficeTestContext) does not flow
    // into request handling. Enabling it preserves the flow so per-request DI resolutions see the
    // current test's context.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (host.Services.GetRequiredService<IServer>() is TestServer testServer)
        {
            testServer.PreserveExecutionContext = true;
        }

        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddFilter(_ => false));

        builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var backOfficeSettings = new Dictionary<string, string?>
                {
                    ["BackOffice:Host"] = BackOfficeHost,
                    // Match the AppHost wiring: mock admin identity carries this group id, so
                    // configuring it here lets BackOfficeAdminAuthorizationHandler and GetMe.IsAdmin
                    // resolve admin status the same way they do in dev.
                    ["BackOffice:AdminsGroupId"] = MockEasyAuthIdentities.MockAdminsGroupId,
                    // The user-facing SPA shell is scoped to Hostnames:App via UseHostScopedSinglePageAppFallback.
                    // Tests that target the user-facing host use app.test.localhost.
                    ["Hostnames:App"] = "app.test.localhost"
                };

                configuration.AddInMemoryCollection(backOfficeSettings);
            }
        );

        builder.ConfigureTestServices(services =>
            {
                services.Remove(services.Single(d => d.ServiceType == typeof(IDbContextOptionsConfiguration<AccountDbContext>)));
                services.AddDbContext<AccountDbContext>((_, options) => options.UseSqlite(CurrentContext.Connection).UseSnakeCaseNamingConvention());

                services.AddScoped<ITelemetryEventsCollector>(_ => CurrentContext.TelemetryCollector);

                // Replace the production singleton with a transient that resolves the per-test instance
                // from the current BackOfficeTestContext. MockStripeClient is keyed-scoped and captures
                // MockStripeState per scope, so this delegates state mutations to the active test.
                services.RemoveAll(typeof(MockStripeState));
                services.AddTransient<MockStripeState>(_ => CurrentContext.StripeState);

                services.Remove(services.Single(d => d.ServiceType == typeof(IEmailClient)));
                services.AddTransient<IEmailClient>(_ => Substitute.For<IEmailClient>());

                services.AddScoped<IExecutionContext, HttpExecutionContext>();

                ConfigureAdditionalTestServices(services);
            }
        );
    }

    protected virtual void ConfigureAdditionalTestServices(IServiceCollection services)
    {
    }

    private void EndTest()
    {
        _currentContext.Value = null;
    }

    private sealed class TestScope(BackOfficeWebApplicationFactory factory) : IDisposable
    {
        public void Dispose()
        {
            factory.EndTest();
        }
    }
}
