using Scaleway.Sdk;
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
    private readonly string _projectId = Environment.GetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID")
                                         ?? throw new InvalidOperationException("SCW_DEFAULT_PROJECT_ID environment variable is not configured.");

    private readonly string _region = Environment.GetEnvironmentVariable("SCW_DEFAULT_REGION") ?? "fr-par";

    private readonly string _senderEmail = Environment.GetEnvironmentVariable("SENDER_EMAIL_ADDRESS")
                                           ?? throw new InvalidOperationException("SENDER_EMAIL_ADDRESS environment variable is not configured.");

    private readonly ScwTem.TemApi _temApi = new(new ScalewayApiClient(httpClient));

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var request = new ScwTem.CreateEmailRequest
        {
            From = new ScwTem.EmailFrom { Email = _senderEmail },
            To = [new ScwTem.Address { Email = message.Recipient }],
            Subject = message.Subject,
            Text = message.PlainTextBody,
            Html = message.HtmlBody,
            ProjectId = _projectId,
            AdditionalHeaders = message.Headers?.Select(header => new ScwTem.Header { Key = header.Key, Value = header.Value }).ToList()
        };

        // The recipient address is PII and logs are exported to Cockpit, so only its domain is logged.
        logger.LogInformation("Sending email to a recipient at {RecipientDomain} with subject {Subject}", message.Recipient[(message.Recipient.LastIndexOf('@') + 1)..], message.Subject);

        // The SDK raises ScalewayApiException carrying Scaleway's own reason (unverified domain, quota
        // exhausted, blocklisted recipient), which is the only actionable part of a failed send.
        await _temApi.CreateEmailAsync(request, _region, cancellationToken);
    }
}
