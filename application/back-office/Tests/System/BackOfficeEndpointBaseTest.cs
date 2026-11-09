using BackOffice.Authentication.MockSignIn;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BackOffice.Tests.System;

/// <summary>
///     Base class for the back-office system tests: HTTP clients against the real host, either
///     anonymous or carrying one of the mock staff identities through the mock selector's dev
///     cookie, exactly as a browser signed in through the picker does.
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

    protected static MockStaffIdentity AdminIdentity { get; } = MockStaffIdentities.Default.Single(i => i.Id == "admin");

    protected static MockStaffIdentity UserIdentity { get; } = MockStaffIdentities.Default.Single(i => i.Id == "user");

    protected HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        return client;
    }

    protected HttpClient CreateStaffClient(MockStaffIdentity identity)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        client.DefaultRequestHeaders.Add("Cookie", $"{MockStaffIdentityCookie.CookieName}={identity.Id}");
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
