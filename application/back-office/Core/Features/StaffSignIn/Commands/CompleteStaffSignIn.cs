using BackOffice.Authentication;
using BackOffice.Features.StaffSignIn.Domain;
using BackOffice.StaffIdentity;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using SharedKernel.Authentication;
using SharedKernel.Cqrs;
using SharedKernel.Telemetry;

namespace BackOffice.Features.StaffSignIn.Commands;

[PublicAPI]
public sealed record CompleteStaffSignInCommand(string OneTimePassword) : ICommand, IRequest<Result>
{
    [JsonIgnore] // Removes this property from the API contract
    public StaffSignInId Id { get; init; } = null!;
}

public sealed class CompleteStaffSignInHandler(
    IStaffSignInRepository staffSignInRepository,
    StaffDirectory staffDirectory,
    OneTimePasswordHelper oneTimePasswordHelper,
    IHttpContextAccessor httpContextAccessor,
    ITelemetryEventsCollector events,
    TimeProvider timeProvider
) : IRequestHandler<CompleteStaffSignInCommand, Result>
{
    // One answer for every unusable code, so a guess learns nothing about which ids or emails exist.
    private const string InvalidCodeMessage = "The code is wrong or no longer valid.";

    public async Task<Result> Handle(CompleteStaffSignInCommand command, CancellationToken cancellationToken)
    {
        var staffSignIn = await staffSignInRepository.GetByIdAsync(command.Id, cancellationToken);
        if (staffSignIn is null || staffSignIn.Completed)
        {
            return Result.BadRequest(InvalidCodeMessage);
        }

        if (staffSignIn.RetryCount >= Domain.StaffSignIn.MaxAttempts)
        {
            staffSignIn.RegisterInvalidPasswordAttempt();
            staffSignInRepository.Update(staffSignIn);
            events.CollectEvent(new StaffSignInCodeBlocked(staffSignIn.Id, staffSignIn.RetryCount));
            return Result.Forbidden("Too many attempts, please request a new code.", true);
        }

        // Validate returns true when the code does NOT match.
        if (oneTimePasswordHelper.Validate(staffSignIn.OneTimePasswordHash, command.OneTimePassword))
        {
            staffSignIn.RegisterInvalidPasswordAttempt();
            staffSignInRepository.Update(staffSignIn);
            events.CollectEvent(new StaffSignInCodeFailed(staffSignIn.Id, staffSignIn.RetryCount));
            return Result.BadRequest(InvalidCodeMessage, true);
        }

        var timeInSeconds = (int)(timeProvider.GetUtcNow() - staffSignIn.CreatedAt).TotalSeconds;
        if (staffSignIn.IsExpired(timeProvider.GetUtcNow()))
        {
            events.CollectEvent(new StaffSignInCodeExpired(staffSignIn.Id, timeInSeconds));
            return Result.BadRequest("The code is no longer valid, please request a new code.", true);
        }

        // The staff identity file is asked again: someone deactivated (or whose email changed) since the code was sent
        // does not get a session.
        var member = staffDirectory.FindActiveById(staffSignIn.StaffMemberId);
        if (member is null || member.Email != staffSignIn.Email)
        {
            return Result.BadRequest(InvalidCodeMessage);
        }

        staffSignIn.MarkAsCompleted(timeProvider.GetUtcNow());
        staffSignInRepository.Update(staffSignIn);

        await StaffSessionExtensions.SignInAsync(httpContextAccessor.HttpContext!, member);

        events.CollectEvent(new StaffSignInCompleted(member.Id, timeInSeconds));

        return Result.Success();
    }
}
