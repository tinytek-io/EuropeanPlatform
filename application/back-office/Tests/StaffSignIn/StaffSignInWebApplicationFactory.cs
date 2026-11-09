using System.Text.RegularExpressions;
using BackOffice.StaffIdentity;
using BackOffice.Tests.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using SharedKernel.Integrations.Email;
using SharedKernel.Telemetry;
using SharedKernel.Tests.Telemetry;

namespace BackOffice.Tests.StaffSignIn;

/// <summary>
///     The back-office host with the real staff sign-in turned on: a staff identity file of its own, a substitute email
///     client that captures the codes, and a telemetry spy.
/// </summary>
public sealed partial class StaffSignInWebApplicationFactory : BackOfficeWebApplicationFactory
{
    public const string AdminEmail = "jane.doe@example.com";
    public const string ThrottledEmail = "max.mustermann@example.com";
    public const string DeactivatedEmail = "eve.gone@example.com";

    // An OIDC client only admins may sign in to, as a tool like Forgejo is registered.
    public const string ClientId = "test-tool";
    public const string ClientSecret = "test-tool-client-secret";
    public const string ClientRedirectUri = "https://tool.test.localhost/oauth/callback";

    // One staff member per sign-in test, so no test runs into another's code-request limit.
    public static readonly string[] SignInTestEmails = Enumerable.Range(1, 8).Select(number => $"staff.member{number}@example.com").ToArray();

    private readonly string _staffIdentityFile = Path.Combine(Path.GetTempPath(), $"staff-identity-{Guid.NewGuid():N}.yaml");

    public StaffSignInWebApplicationFactory()
    {
        File.WriteAllText(_staffIdentityFile, $"""
                                               staff:
                                                 - id: jane.doe
                                                   email: {AdminEmail}
                                                   name: Jane Doe
                                                   groups: [staff, admins]
                                                 - id: max.mustermann
                                                   email: {ThrottledEmail}
                                                   name: Max Mustermann
                                                   groups: [staff]
                                                 - id: eve.gone
                                                   email: {DeactivatedEmail}
                                                   name: Eve Gone
                                                   groups: [staff]
                                                   active: false
                                               {string.Join('\n', SignInTestEmails.Select((email, index) => $"  - id: staff.member{index + 1}\n    email: {email}\n    name: Staff Member {index + 1}\n    groups: [staff]"))}
                                               clients:
                                                 - clientId: {ClientId}
                                                   displayName: Test tool
                                                   redirectUris: [{ClientRedirectUri}]
                                                   allowedGroups: [admins]
                                               """
        );
    }

    public IEmailClient EmailClient { get; } = Substitute.For<IEmailClient>();

    public TelemetryEventsCollectorSpy TelemetryEventsCollectorSpy { get; } = new(new TelemetryEventsCollector());

    protected override Dictionary<string, string?> AdditionalSettings => new()
    {
        ["StaffIdentity:File"] = _staffIdentityFile,
        [$"StaffIdentity:ClientSecrets:{ClientId}"] = ClientSecret
    };

    [GeneratedRegex("is: ([A-Z]{6})")]
    private static partial Regex OneTimePasswordPattern { get; }

    /// <summary>The code in the latest sign-in email sent to this address.</summary>
    public string LastCodeSentTo(string email)
    {
        var message = EmailClient.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<EmailMessage>()
            .Last(message => message.Recipient == email);
        return OneTimePasswordPattern.Match(message.PlainTextBody).Groups[1].Value;
    }

    protected override void ConfigureAdditionalTestServices(IServiceCollection services)
    {
        services.RemoveAll<IEmailClient>();
        services.AddSingleton(EmailClient);
        services.RemoveAll<ITelemetryEventsCollector>();
        services.AddScoped<ITelemetryEventsCollector>(_ => TelemetryEventsCollectorSpy);

        // The back-office workers sync the client list in a deployment; the test host does it right after the schema
        // exists (the schema creator is the first hosted service).
        services.AddTransient<StaffIdentityClientReconciler>();
        services.Insert(1, ServiceDescriptor.Singleton<IHostedService, StaffIdentityClientSync>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) File.Delete(_staffIdentityFile);
    }

    private sealed class StaffIdentityClientSync(IServiceProvider serviceProvider) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StaffIdentityClientReconciler>().ReconcileAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
