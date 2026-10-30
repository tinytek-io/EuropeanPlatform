using BackOffice.Authentication;
using BackOffice.Authentication.MockEasyAuth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BackOffice.Tests.System;

/// <summary>
///     Base class for the back-office system tests: HTTP clients against the real host, either
///     anonymous or carrying one of the mock staff identities (injected as the EasyAuth-style
///     headers the mock selector's middleware would produce for its dev cookie).
/// </summary>
public abstract class BackOfficeEndpointBaseTest
{
    protected BackOfficeEndpointBaseTest(BackOfficeWebApplicationFactory factory)
    {
        Factory = factory;
        // A fresh recording surface per test: xUnit constructs a new test-class instance before
        // each test, so a previous test's forwarded requests can never leak into the next one.
        factory.AccountApi.Reset();
    }

    protected BackOfficeWebApplicationFactory Factory { get; }

    protected static MockEasyAuthIdentity AdminIdentity { get; } = MockEasyAuthIdentities.Default.Single(i => i.Id == "admin");

    protected static MockEasyAuthIdentity UserIdentity { get; } = MockEasyAuthIdentities.Default.Single(i => i.Id == "user");

    protected HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        return client;
    }

    protected HttpClient CreateStaffClient(MockEasyAuthIdentity identity)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        client.DefaultRequestHeaders.Add(BackOfficeIdentityDefaults.PrincipalNameHeader, identity.Name);
        client.DefaultRequestHeaders.Add(BackOfficeIdentityDefaults.PrincipalIdHeader, identity.ObjectId);
        client.DefaultRequestHeaders.Add(BackOfficeIdentityDefaults.PrincipalPayloadHeader, MockEasyAuthCookie.EncodePayload(identity));
        return client;
    }

    private HttpClient CreateClient()
    {
        return Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"https://{BackOfficeWebApplicationFactory.BackOfficeHost}"),
                AllowAutoRedirect = false
            }
        );
    }
}
