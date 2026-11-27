using System.Net.Mail;
using System.Net.Mime;
using SharedKernel.Configuration;
using SharedKernel.Cqrs;

namespace SharedKernel.Integrations.Email;

public sealed class DevelopmentEmailClient(PortAllocation ports) : IEmailClient
{
    private const string Sender = "no-reply@localhost";

    private readonly SmtpClient _emailSender = new("localhost", ports.MailpitSmtp);

    public async Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mailMessage = new MailMessage(Sender, message.Recipient) { Subject = message.Subject };
        mailMessage.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.PlainTextBody, null, MediaTypeNames.Text.Plain));
        mailMessage.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, null, MediaTypeNames.Text.Html));

        if (message.Headers is not null)
        {
            foreach (var header in message.Headers)
            {
                mailMessage.Headers.Add(header.Key, header.Value);
            }
        }

        await _emailSender.SendMailAsync(mailMessage, cancellationToken);
        return Result.Success();
    }
}
