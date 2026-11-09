using System.Text.RegularExpressions;
using BackOffice.Tests.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
                                               clients: []
                                               """
        );
    }

    public IEmailClient EmailClient { get; } = Substitute.For<IEmailClient>();

    public TelemetryEventsCollectorSpy TelemetryEventsCollectorSpy { get; } = new(new TelemetryEventsCollector());

    protected override Dictionary<string, string?> AdditionalSettings => new() { ["StaffIdentity:File"] = _staffIdentityFile };

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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) File.Delete(_staffIdentityFile);
    }
}
