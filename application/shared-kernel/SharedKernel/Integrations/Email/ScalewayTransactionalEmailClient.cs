using Scaleway.Sdk;
using SharedKernel.Cqrs;
using ScwTem = Scaleway.Sdk.Tem.V1Alpha1;

namespace SharedKernel.Integrations.Email;

/// <summary>
///     Sends email through Scaleway Transactional Email's HTTP API rather than SMTP. Scaleway blocks the SMTP
///     ports across its compute products, and Serverless Containers have no security group to open them in
///     (found live: every send timed out establishing the connection). The API needs no SMTP egress and
///     authenticates with the workload's own IAM key — the same credentials the container already carries.
/// </summary>
/// <remarks>
///     The sender domain must be registered and DNS-validated in Transactional Email, which the deploy
///     provisions (<c>PublishAsScalewayTem</c>); until it validates, Scaleway rejects sends for that domain.
/// </remarks>
public sealed class ScalewayTransactionalEmailClient(HttpClient httpClient, ILogger<ScalewayTransactionalEmailClient> logger) : IEmailClient
{
    // Provider incidents last hours rather than seconds, so a sooner retry would only be refused again.
    private static readonly TimeSpan RecipientDomainRetryAfter = TimeSpan.FromMinutes(15);

    private readonly string _projectId = Environment.GetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID")
                                         ?? throw new InvalidOperationException("SCW_DEFAULT_PROJECT_ID environment variable is not configured.");

    private readonly string _region = Environment.GetEnvironmentVariable("SCW_DEFAULT_REGION") ?? "fr-par";

    private readonly string _senderEmail = Environment.GetEnvironmentVariable("SENDER_EMAIL_ADDRESS")
                                           ?? throw new InvalidOperationException("SENDER_EMAIL_ADDRESS environment variable is not configured.");

    private readonly ScwTem.TemApi _temApi = new(new ScalewayApiClient(httpClient));

    public async Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var request = new ScwTem.CreateEmailRequest
        {
            From = new ScwTem.EmailFrom { Email = _senderEmail },
            To = [new ScwTem.CreateEmailRequestAddress { Email = message.Recipient }],
            Subject = message.Subject,
            Text = message.PlainTextBody,
            Html = message.HtmlBody,
            ProjectId = _projectId,
            AdditionalHeaders = message.Headers?.Select(header => new ScwTem.CreateEmailRequestHeader { Key = header.Key, Value = header.Value }).ToList()
        };

        // The recipient address is PII and logs are exported to Cockpit, so only its domain is logged.
        logger.LogInformation("Sending email to a recipient at {RecipientDomain} with subject {Subject}", message.Recipient[(message.Recipient.LastIndexOf('@') + 1)..], message.Subject);

        try
        {
            await _temApi.CreateEmailAsync(request, _region, cancellationToken);
            return Result.Success();
        }
        // Any other failure keeps propagating: the SDK's ScalewayApiException carries Scaleway's own reason
        // (unverified domain, quota exhausted, blocklisted recipient), which is the only actionable part of it.
        catch (ScalewayApiException exception) when (EmailRecipientDomainRefusal.Detect(exception, message.Recipient) is { } refusal)
        {
            return RecipientDomainUnavailable(refusal);
        }
    }

    private Result RecipientDomainUnavailable(EmailRecipientDomainRefusal refusal)
    {
        logger.LogWarning(
            "Transactional Email refused the recipient domain {RecipientDomain} during provider incident {IncidentId}", refusal.RecipientDomain, refusal.IncidentId ?? "unknown"
        );

        return Result.ServiceUnavailable(
            $"Email to '{refusal.RecipientDomain}' cannot be delivered right now because of an issue between our email provider and the recipient's. Please try again later.",
            RecipientDomainRetryAfter,
            new Dictionary<string, object?>
            {
                ["reason"] = EmailRecipientDomainRefusal.ProblemReason,
                ["recipientDomain"] = refusal.RecipientDomain,
                ["statusUrl"] = refusal.StatusUrl
            }
        );
    }
}
