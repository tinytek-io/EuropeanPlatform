using SharedKernel.Cqrs;

namespace SharedKernel.Integrations.Email;

public interface IEmailClient
{
    /// <summary>
    ///     Sends the message. A failure the user can act on, such as the provider temporarily refusing the recipient's
    ///     domain, is returned as a failed <see cref="Result" /> for the handler to pass on; anything else throws.
    /// </summary>
    Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
