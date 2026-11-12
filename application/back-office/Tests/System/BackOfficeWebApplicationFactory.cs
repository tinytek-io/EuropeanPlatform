using BackOffice.Authentication.MockSignIn;
using BackOffice.Database;
using BackOffice.Integrations.Account;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using SharedKernel.Authentication;
using SharedKernel.SinglePageApp;
using SharedKernel.Tests.Authentication;

namespace BackOffice.Tests.System;

// Shared host for the back-office system tests. The host runs the real pipeline — staff identity
// scheme, SPA-facing policies, delegation handler, account internal API client — with only the
// internal API's transport swapped for a recording stub, so every test exercises the wiring the
// staging deployment actually uses. The back-office database (the staff identity state) is an
// in-memory SQLite database per factory, created when the host starts.
public class BackOfficeWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string BackOfficeHost = "back-office.test.localhost";

    public BackOfficeWebApplicationFactory()
    {
        // The development-only CORS policy (AddApiInfrastructure) and the SPA shell options
        // validate these at host startup, mirroring account's test factory.
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.PublicUrlKey, "https://localhost");
        Environment.SetEnvironmentVariable(SinglePageAppConfiguration.CdnUrlKey, "https://localhost");
        Environment.SetEnvironmentVariable("BypassAntiforgeryValidation", "true");
    }

    public RecordingAccountApiHandler AccountApi { get; } = new();

    public SqliteConnection Connection { get; } = new($"Data Source=BackOfficeTestDb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    /// <summary>Configuration a derived factory adds on top of the shared settings.</summary>
    protected virtual Dictionary<string, string?> AdditionalSettings => [];

    /// <summary>Test services a derived factory swaps in after the shared ones.</summary>
    protected virtual void ConfigureAdditionalTestServices(IServiceCollection services)
    {
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Connection.Open();
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Connection.Dispose();
    }

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
                        ["BackOffice:AdminsGroupId"] = MockStaffIdentities.MockAdminsGroupId,
                        // The delegation handler signs every forwarded request with the back-office key.
                        [BackOfficeDelegationDefaults.SigningKeyConfigurationKey] = TestBackOfficeDelegationKeys.SigningKeyPem
                    }
                );
                configuration.AddInMemoryCollection(AdditionalSettings);
            }
        );

        builder.ConfigureTestServices(services =>
            {
                // Swap only the transport of the typed internal API client: the base address, the
                // delegation handler, and the serialization contract stay exactly as production
                // wires them — the stub sees the delegated request after the whole pipeline.
                services.Configure<HttpClientFactoryOptions>(nameof(AccountInternalApiClient), options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder => handlerBuilder.PrimaryHandler = AccountApi)
                );

                services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IDbContextOptionsConfiguration<BackOfficeDbContext>)));
                services.AddDbContext<BackOfficeDbContext>((_, options) => options.UseSqlite(Connection).UseSnakeCaseNamingConvention());

                // The schema must exist before the host's own hosted services start (the OIDC client sync reads it).
                services.Insert(0, ServiceDescriptor.Singleton<IHostedService, DatabaseSchemaCreator>());

                ConfigureAdditionalTestServices(services);
            }
        );
    }
}

// Creates the test database's schema as the very first hosted service.
internal sealed class DatabaseSchemaCreator(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackOfficeDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
