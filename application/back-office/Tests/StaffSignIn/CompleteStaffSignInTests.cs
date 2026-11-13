using System.Net;
using System.Net.Http.Json;
using BackOffice.Authentication;
using BackOffice.Features.Me;
using BackOffice.Features.StaffSignIn.Commands;
using BackOffice.Features.StaffSignIn.Domain;
using BackOffice.Tests.System;
using FluentAssertions;
using Meziantou.Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using SharedKernel.Tests;
using SharedKernel.Tests.Persistence;
using Xunit;
using StaffSignInAggregate = BackOffice.Features.StaffSignIn.Domain.StaffSignIn;

namespace BackOffice.Tests.StaffSignIn;

[DisableParallelization]
public sealed class CompleteStaffSignInTests(StaffSignInWebApplicationFactory factory) : IClassFixture<StaffSignInWebApplicationFactory>
{
    [Fact]
    public async Task CompleteStaffSignIn_WhenCodeIsCorrect_ShouldStartASessionForTheStaffMember()
    {
        // Arrange
        var client = CreateClient();
        var staffSignInId = await StartSignIn(client, StaffSignInWebApplicationFactory.AdminEmail);
        var code = factory.LastCodeSentTo(StaffSignInWebApplicationFactory.AdminEmail);
        factory.TelemetryEventsCollectorSpy.Reset();

        // Act
        var response = await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(code));

        // Assert
        await response.ShouldBeSuccessfulPostRequest(hasLocation: false);
        var sessionCookie = response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith($"{StaffSessionDefaults.CookieName}="));
        factory.Connection.ExecuteScalar<long>("SELECT completed FROM staff_sign_ins WHERE id = @id", [new { id = staffSignInId.ToString() }]).Should().Be(1);
        factory.TelemetryEventsCollectorSpy.CollectedEvents.Should().ContainSingle().Which.GetType().Name.Should().Be("StaffSignInCompleted");

        var me = await GetMe(sessionCookie);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await me.DeserializeResponse<MeResponse>();
        profile!.Email.Should().Be(StaffSignInWebApplicationFactory.AdminEmail);
    }

    [Fact]
    public async Task CompleteStaffSignIn_WhenCodeIsWrong_ShouldReturnBadRequestAndCountTheAttempt()
    {
        // Arrange
        var client = CreateClient();
        var staffSignInId = await StartSignIn(client, StaffSignInWebApplicationFactory.SignInTestEmails[0]);
        factory.TelemetryEventsCollectorSpy.Reset();

        // Act
        var response = await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand("WRONGG"));

        // Assert
        await response.ShouldHaveErrorStatusCode(HttpStatusCode.BadRequest, "The code is wrong or no longer valid.");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        factory.Connection.ExecuteScalar<long>("SELECT retry_count FROM staff_sign_ins WHERE id = @id", [new { id = staffSignInId.ToString() }]).Should().Be(1);
        factory.TelemetryEventsCollectorSpy.CollectedEvents.Should().ContainSingle().Which.GetType().Name.Should().Be("StaffSignInCodeFailed");
    }

    [Fact]
    public async Task CompleteStaffSignIn_WhenAttemptsAreUsedUp_ShouldBeForbiddenEvenWithTheRightCode()
    {
        // Arrange
        var client = CreateClient();
        var staffSignInId = await StartSignIn(client, StaffSignInWebApplicationFactory.SignInTestEmails[1]);
        var code = factory.LastCodeSentTo(StaffSignInWebApplicationFactory.SignInTestEmails[1]);
        for (var i = 0; i < StaffSignInAggregate.MaxAttempts; i++)
        {
            await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand("WRONGG"));
        }

        // Act
        var response = await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(code));

        // Assert
        await response.ShouldHaveErrorStatusCode(HttpStatusCode.Forbidden, "Too many attempts, please request a new code.");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task CompleteStaffSignIn_WhenCodeWasAlreadyUsed_ShouldReturnBadRequest()
    {
        // Arrange
        var client = CreateClient();
        var staffSignInId = await StartSignIn(client, StaffSignInWebApplicationFactory.SignInTestEmails[2]);
        var code = factory.LastCodeSentTo(StaffSignInWebApplicationFactory.SignInTestEmails[2]);
        (await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(code))).EnsureSuccessStatusCode();

        // Act
        var response = await CreateClient().PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(code));

        // Assert
        await response.ShouldHaveErrorStatusCode(HttpStatusCode.BadRequest, "The code is wrong or no longer valid.");
    }

    [Fact]
    public async Task CompleteStaffSignIn_WhenCodeHasExpired_ShouldReturnBadRequest()
    {
        // Arrange
        var client = CreateClient();
        var staffSignInId = await StartSignIn(client, StaffSignInWebApplicationFactory.SignInTestEmails[3]);
        var code = factory.LastCodeSentTo(StaffSignInWebApplicationFactory.SignInTestEmails[3]);
        factory.Connection.Update("staff_sign_ins", "id", staffSignInId.ToString(), [("created_at", TimeProvider.System.GetUtcNow().AddSeconds(-StaffSignInAggregate.ValidForSeconds - 1))]);

        // Act
        var response = await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(code));

        // Assert
        await response.ShouldHaveErrorStatusCode(HttpStatusCode.BadRequest, "The code is no longer valid, please request a new code.");
    }

    [Fact]
    public async Task CompleteStaffSignIn_WhenSignInIdIsUnknown_ShouldReturnBadRequest()
    {
        // Act
        var response = await CreateClient().PostAsJsonAsync($"/api/back-office/staff-sign-in/{StaffSignInId.NewId()}/complete", new CompleteStaffSignInCommand("ABCDEF"));

        // Assert
        await response.ShouldHaveErrorStatusCode(HttpStatusCode.BadRequest, "The code is wrong or no longer valid.");
    }

    [Fact]
    public async Task CompleteStaffSignIn_WhenStaffMemberIsNoLongerActive_ShouldNotStartASession()
    {
        // Arrange
        var client = CreateClient();
        var staffSignInId = await StartSignIn(client, StaffSignInWebApplicationFactory.SignInTestEmails[4]);
        var code = factory.LastCodeSentTo(StaffSignInWebApplicationFactory.SignInTestEmails[4]);
        factory.Connection.Update("staff_sign_ins", "id", staffSignInId.ToString(), [("staff_member_id", "eve.gone"), ("email", StaffSignInWebApplicationFactory.DeactivatedEmail)]);

        // Act
        var response = await client.PostAsJsonAsync($"/api/back-office/staff-sign-in/{staffSignInId}/complete", new CompleteStaffSignInCommand(code));

        // Assert
        await response.ShouldHaveErrorStatusCode(HttpStatusCode.BadRequest, "The code is wrong or no longer valid.");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    private async Task<StaffSignInId> StartSignIn(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/back-office/staff-sign-in/start", new StartStaffSignInCommand(email));
        return (await response.DeserializeResponse<StartStaffSignInResponse>())!.StaffSignInId;
    }

    private async Task<HttpResponseMessage> GetMe(string sessionCookie)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", sessionCookie.Split(';')[0]);
        return await client.GetAsync("/api/back-office/me");
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{BackOfficeWebApplicationFactory.BackOfficeHost}"), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        return client;
    }
}
