using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Account.Integrations.Stripe;
using Bogus;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel.Authentication.BackOfficeIdentity;
using SharedKernel.Authentication.MockEasyAuth;
using SharedKernel.Authentication.TokenSigning;
using SharedKernel.Telemetry;
using SharedKernel.Tests.Telemetry;

namespace Account.Tests.BackOffice;

// Base class for the internal back-office API tests. Each derived class declares
// IClassFixture<BackOfficeWebApplicationFactory> (or a subclass) to share a single host across
// its tests; per-test isolation is preserved by the BackOfficeTestContext routed through the
// fixture's AsyncLocal slot.
//
// The routes under test are the INTERNAL API the back-office BFF consumes: every request
// authenticates with a short-lived delegation token minted from a staff principal (the same
// mechanism the BFF's delegation handler uses), not with the EasyAuth-style headers of the
// former staff-facing routes.
public abstract class BackOfficeEndpointBaseTest : IDisposable
{
    protected const string BackOfficeHost = BackOfficeWebApplicationFactory.BackOfficeHost;

    protected readonly Faker Faker = new();
    private readonly BackOfficeWebApplicationFactory _factory;
    private readonly IDisposable _testScope;

    protected BackOfficeEndpointBaseTest(BackOfficeWebApplicationFactory factory)
    {
        _factory = factory;

        Connection = new SqliteConnection($"Data Source=TestDb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        Connection.Open();

        TelemetryEventsCollectorSpy = new TelemetryEventsCollectorSpy(new TelemetryEventsCollector());
        StripeState = new MockStripeState();

        // BeginTest must run before any service resolution so the host's startup hosted services
        // (PlatformCurrencyStartupResolver) see the per-test state.
        _testScope = factory.BeginTest(new BackOfficeTestContext
            {
                Connection = Connection,
                TelemetryCollector = TelemetryEventsCollectorSpy,
                StripeState = StripeState
            }
        );

        // Fill this test's database from the seeded template with a fast binary copy instead of
        // recreating the schema and reseeding per test. The shared seeder's entity references match the
        // rows copied into this connection.
        DatabaseSeeder = SeededDatabaseTemplate.EnsureSeeded();
        SeededDatabaseTemplate.RestoreInto(Connection);
    }

    protected SqliteConnection Connection { get; }

    protected DatabaseSeeder DatabaseSeeder { get; }

    protected TelemetryEventsCollectorSpy TelemetryEventsCollectorSpy { get; }

    protected MockStripeState StripeState { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // The internal API authenticates the back-office BFF's delegation token. Tests mint the
    // same token from an EasyAuth-style staff principal (identical to what the BFF sends): the
    // claims are rebuilt exactly like the BackOfficeIdentityHandler does for the header-based
    // staff identity.
    protected HttpClient CreateBackOfficeClient(string? clientPrincipalName = null, string? clientPrincipalId = null, string? clientPrincipalPayload = null)
    {
        var client = CreateClientWithoutIdentity();
        if (clientPrincipalName is null) return client;

        AddDelegationToken(client, BuildStaffPrincipal(clientPrincipalName, clientPrincipalId, clientPrincipalPayload));
        return client;
    }

    protected HttpClient CreateBackOfficeClientForIdentity(MockEasyAuthIdentity identity)
    {
        var client = CreateClientWithoutIdentity();
        AddDelegationToken(client, BuildStaffPrincipal(identity.Name, identity.ObjectId, MockEasyAuthCookie.EncodePayload(identity)));
        return client;
    }

    protected HttpClient CreateClientForHost(string host)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"https://{host}"),
                AllowAutoRedirect = false
            }
        );
        client.DefaultRequestHeaders.Host = host;
        return client;
    }

    private HttpClient CreateClientWithoutIdentity()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"https://{BackOfficeHost}"),
                AllowAutoRedirect = false
            }
        );
        client.DefaultRequestHeaders.Host = BackOfficeHost;
        return client;
    }

    private void AddDelegationToken(HttpClient client, ClaimsPrincipal principal)
    {
        var tokenGenerator = new BackOfficeDelegationTokenGenerator(
            _factory.Services.GetRequiredService<ITokenSigningClient>(),
            _factory.Services.GetRequiredService<TimeProvider>()
        );
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenGenerator.Generate(principal));
    }

    // Rebuilds the staff principal from the EasyAuth-style identity parts, mirroring
    // BackOfficeIdentityHandler.BuildClaims: the payload's claims carry the .NET claim types the
    // cookie encoder wrote (email, name identifier, groups), and the display name prefers the
    // payload's friendly 'name' claim with the principal name header as the fallback.
    private static ClaimsPrincipal BuildStaffPrincipal(string principalName, string? principalId, string? clientPrincipalPayload)
    {
        var claims = new List<Claim>();
        if (clientPrincipalPayload is not null)
        {
            var principal = JsonSerializer.Deserialize<BackOfficeClientPrincipal>(Convert.FromBase64String(clientPrincipalPayload), JsonSerializerOptions.Web);
            foreach (var claim in principal?.Claims ?? [])
            {
                claims.Add(new Claim(claim.Type, claim.Value));
            }
        }

        var displayName = claims.FirstOrDefault(claim => claim.Type == BackOfficeIdentityDefaults.NameClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(displayName)) displayName = principalName;
        claims.Add(new Claim(ClaimTypes.Name, displayName));

        if (!string.IsNullOrWhiteSpace(principalId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, principalId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestStaffIdentity"));
    }

    [UsedImplicitly]
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        Connection.Close();
        _testScope.Dispose();
    }
}
