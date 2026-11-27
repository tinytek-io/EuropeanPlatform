using NSubstitute;
using SharedKernel.Cqrs;
using SharedKernel.Integrations.Email;

namespace SharedKernel.Tests.Integrations.Email;

/// <summary>
///     An <see cref="IEmailClient" /> substitute whose sends succeed. A bare substitute returns a null
///     <see cref="Result" />, which the handlers would dereference.
/// </summary>
public static class EmailClientSubstitute
{
    public static IEmailClient Create()
    {
        var emailClient = Substitute.For<IEmailClient>();
        emailClient.SendAsync(null!, CancellationToken.None).ReturnsForAnyArgs(Result.Success());
        return emailClient;
    }
}
