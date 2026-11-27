using System.Text.RegularExpressions;
using Scaleway.Sdk;

namespace SharedKernel.Integrations.Email;

/// <summary>
///     Scaleway Transactional Email's refusal of a recipient domain during a provider incident, for example when it
///     stopped delivering to Microsoft-hosted domains (Outlook, Hotmail and custom domains on Microsoft 365).
/// </summary>
/// <remarks>
///     The HTTP API answers <c>400 invalid_arguments</c> with the detail
///     <c>{ argument_name: "to.email", reason: "constraint", help_message: "Recipient domain \"example.com\" is temporarily
///     not supported. For more information: https://status.scaleway.com/incidents/&lt;id&gt;" }</c> (SMTP answers
///     <c>550 5.7.1</c> with the same phrase). Detection keys on the argument, the reason and the stable phrase only:
///     Scaleway's own notice quotes the message without the domain, so the domain and the incident link are read when
///     present but never required. A <c>to.email</c> constraint without the phrase is a malformed address, not an
///     incident. If Scaleway rephrases the message, detection stops matching and the send fails as before.
/// </remarks>
public sealed partial record EmailRecipientDomainRefusal(string RecipientDomain, string? IncidentId, string StatusUrl)
{
    public const string ProblemReason = "email-recipient-domain-unavailable";

    private const string RecipientArgument = "to.email";
    private const string ConstraintReason = "constraint";
    private const string TemporarilyNotSupportedPhrase = "is temporarily not supported";
    private const string StatusPageUrl = "https://status.scaleway.com";

    public static EmailRecipientDomainRefusal? Detect(ScalewayApiException exception, string recipient)
    {
        var detail = exception.Details.FirstOrDefault(d =>
            d is { ArgumentName: RecipientArgument, Reason: ConstraintReason } && d.HelpMessage?.Contains(TemporarilyNotSupportedPhrase, StringComparison.OrdinalIgnoreCase) == true
        );
        if (detail is null) return null;

        var helpMessage = detail.HelpMessage!;
        var domainMatch = RecipientDomainPattern().Match(helpMessage);
        var recipientDomain = domainMatch.Success ? domainMatch.Groups["domain"].Value : recipient[(recipient.LastIndexOf('@') + 1)..];

        // Only a link to Scaleway's own status page is passed on to the user; any other link in the message is ignored.
        var incidentMatch = IncidentUrlPattern().Match(helpMessage);
        return incidentMatch.Success
            ? new EmailRecipientDomainRefusal(recipientDomain, incidentMatch.Groups["id"].Value, incidentMatch.Value)
            : new EmailRecipientDomainRefusal(recipientDomain, null, StatusPageUrl);
    }

    [GeneratedRegex("Recipient domain \"(?<domain>[^\"\\s]+)\"", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RecipientDomainPattern();

    [GeneratedRegex("""https://status\.scaleway\.com/incidents/(?<id>[A-Za-z0-9]+)""", RegexOptions.None, 1000)]
    private static partial Regex IncidentUrlPattern();
}
