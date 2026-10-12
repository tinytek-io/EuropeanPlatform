using BackOffice.Integrations.Account;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using SharedKernel.SinglePageApp;

namespace BackOffice.Tests.System;

// Shared host for the back-office system tests. The host runs the real pipeline — staff identity
// scheme, SPA-facing policies, delegation handler, account internal API client — with only the
// internal API's transport swapped for a recording stub, so every test exercises the wiring the
// staging deployment actually uses. The back-office owns no database, so there is no per-test
// state to isolate.
public class BackOfficeWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string BackOfficeHost = "back-office.test.localhost";

    public BackOfficeWebApplicationFactory()
    {
        // The development-only CORS policy (AddApiInfrastructure) and the SPA shell options
        // validate these at host startup, mirroring account's test factory.
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey, "https://localhost");
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey, "https://localhost");
        Environment.SetEnvironmentVariable(
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost;LiveEndpoint=https://localhost"
        );
        Environment.SetEnvironmentVariable("BypassAntiforgeryValidation", "true");
    }

    public RecordingAccountApiHandler AccountApi { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddFilter(_ => false));

        builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BackOffice:Host"] = BackOfficeHost,
                        // Match the AppHost wiring: the mock admin identity carries this group id,
                        // so the BackOfficeAdmin policy and MeResponse.IsAdmin resolve admin status
                        // the same way they do in development.
                        ["BackOffice:AdminsGroupId"] = BackOffice.Authentication.MockEasyAuth.MockEasyAuthIdentities.MockAdminsGroupId
                    }
                );
            }
        );

        builder.ConfigureTestServices(services =>
            {
                // Swap only the transport of the typed internal API client: the base address, the
                // delegation handler, and the serialization contract stay exactly as production
                // wires them — the stub sees the delegated request after the whole pipeline.
                services.Configure<HttpClientFactoryOptions>(typeof(AccountInternalApiClient).Name, options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder => handlerBuilder.PrimaryHandler = AccountApi)
                );
            }
        );
    }
}
