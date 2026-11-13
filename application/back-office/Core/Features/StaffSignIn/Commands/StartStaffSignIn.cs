using System.Net;
using BackOffice.Features.StaffSignIn.Domain;
using BackOffice.StaffIdentity;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SharedKernel.Authentication;
using SharedKernel.Cqrs;
using SharedKernel.Integrations.Email;
using SharedKernel.Telemetry;
using SharedKernel.Validation;

namespace BackOffice.Features.StaffSignIn.Commands;

[PublicAPI]
public sealed record StartStaffSignInCommand(string Email) : ICommand, IRequest<Result<StartStaffSignInResponse>>
{
    public string Email { get; init; } = Email.Trim().ToLower();
}

[PublicAPI]
public sealed record StartStaffSignInResponse(StaffSignInId StaffSignInId, int ValidForSeconds);

public sealed class StartStaffSignInValidator : AbstractValidator<StartStaffSignInCommand>
{
    public StartStaffSignInValidator()
    {
        RuleFor(x => x.Email).SetValidator(new SharedValidations.Email());
    }
}

public sealed class StartStaffSignInHandler(
    IStaffSignInRepository staffSignInRepository,
    StaffDirectory staffDirectory,
    IEmailClient emailClient,
    IPasswordHasher<object> passwordHasher,
    IOptions<BackOfficeHostOptions> backOfficeHostOptions,
    ITelemetryEventsCollector events,
    TimeProvider timeProvider
) : IRequestHandler<StartStaffSignInCommand, Result<StartStaffSignInResponse>>
{
    private const int MaxSignInsPerWindow = 3;
    private static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(15);

    public async Task<Result<StartStaffSignInResponse>> Handle(StartStaffSignInCommand command, CancellationToken cancellationToken)
    {
        // Someone who is not active staff gets the same answer and no email: the response must not reveal who is staff.
        var member = staffDirectory.FindActiveByEmail(command.Email);
        if (member is null)
        {
            return new StartStaffSignInResponse(StaffSignInId.NewId(), Domain.StaffSignIn.ValidForSeconds);
        }

        var recentSignIns = await staffSignInRepository.GetStartedSinceAsync(member.Email, timeProvider.GetUtcNow().Subtract(SignInWindow), cancellationToken);
        if (recentSignIns.Length >= MaxSignInsPerWindow)
        {
            return Result<StartStaffSignInResponse>.TooManyRequests("Too many sign-in codes requested. Please try again later.");
        }

        var oneTimePassword = OneTimePasswordHelper.GenerateOneTimePassword(6);
        var staffSignIn = Domain.StaffSignIn.Create(member.Id, member.Email, passwordHasher.HashPassword(this, oneTimePassword));
        await staffSignInRepository.AddAsync(staffSignIn, cancellationToken);

        var host = backOfficeHostOptions.Value.Host;
        var expiryMinutes = Domain.StaffSignIn.ValidForSeconds / 60;
        await emailClient.SendAsync(
            new EmailMessage(
                member.Email,
                "Your back-office sign-in code",
                $"<p>Your code for signing in to {WebUtility.HtmlEncode(host)} is:</p><p style=\"font-size:24px;font-weight:bold;letter-spacing:4px\">{oneTimePassword}</p><p>It is valid for {expiryMinutes} minutes. If you did not try to sign in, ignore this email.</p>",
                $"Your code for signing in to {host} is: {oneTimePassword}\n\nIt is valid for {expiryMinutes} minutes. If you did not try to sign in, ignore this email."
            ),
            cancellationToken
        );

        events.CollectEvent(new StaffSignInStarted(member.Id));

        return new StartStaffSignInResponse(staffSignIn.Id, Domain.StaffSignIn.ValidForSeconds);
    }
}
