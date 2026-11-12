using System.Net;
using System.Net.Http.Json;
using BackOffice.Features.StaffSignIn.Commands;
using BackOffice.Tests.System;
using FluentAssertions;
using Meziantou.Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using SharedKernel.Integrations.Email;
using SharedKernel.Tests;
using SharedKernel.Tests.Persistence;
using Xunit;

namespace BackOffice.Tests.StaffSignIn;

[DisableParallelization]
public sealed class StartStaffSignInTests(StaffSignInWebApplicationFactory factory) : IClassFixture<StaffSignInWebApplicationFactory>
{
    private const string StartPath = "/api/back-office/staff-sign-in/start";

    [Fact]
    public async Task StartStaffSignIn_WhenActiveStaff_ShouldEmailCodeAndStoreSignIn()
    {
        // Arrange
        factory.EmailClient.ClearReceivedCalls();
        factory.TelemetryEventsCollectorSpy.Reset();

        // Act
        var response = await CreateClient().PostAsJsonAsync(StartPath, new StartStaffSignInCommand(StaffSignInWebApplicationFactory.AdminEmail));

        // Assert
        var body = await response.DeserializeResponse<StartStaffSignInResponse>();
        body!.ValidForSeconds.Should().Be(300);
        factory.Connection.RowExists("staff_sign_ins", body.StaffSignInId.ToString()).Should().BeTrue();
        factory.LastCodeSentTo(StaffSignInWebApplicationFactory.AdminEmail).Should().MatchRegex("^[A-Z]{6}$");
        factory.TelemetryEventsCollectorSpy.CollectedEvents.Should().ContainSingle().Which.GetType().Name.Should().Be("StaffSignInStarted");
    }

    [Theory]
    [InlineData("nobody@example.com")]
    [InlineData(StaffSignInWebApplicationFactory.DeactivatedEmail)]
    public async Task StartStaffSignIn_WhenNotActiveStaff_ShouldAnswerTheSameWithoutEmail(string email)
    {
        // Arrange
        factory.EmailClient.ClearReceivedCalls();
        factory.TelemetryEventsCollectorSpy.Reset();

        // Act
        var response = await CreateClient().PostAsJsonAsync(StartPath, new StartStaffSignInCommand(email));

        // Assert
        var body = await response.DeserializeResponse<StartStaffSignInResponse>();
        body!.ValidForSeconds.Should().Be(300);
        factory.Connection.RowExists("staff_sign_ins", body.StaffSignInId.ToString()).Should().BeFalse();
        await factory.EmailClient.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        factory.TelemetryEventsCollectorSpy.CollectedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task StartStaffSignIn_WhenTooManyCodesRequested_ShouldReturnTooManyRequests()
    {
        // Arrange
        var client = CreateClient();
        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync(StartPath, new StartStaffSignInCommand(StaffSignInWebApplicationFactory.ThrottledEmail))).EnsureSuccessStatusCode();
        }

        // Act
        var response = await client.PostAsJsonAsync(StartPath, new StartStaffSignInCommand(StaffSignInWebApplicationFactory.ThrottledEmail));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task StartStaffSignIn_WhenEmailIsInvalid_ShouldReturnBadRequest()
    {
        // Act
        var response = await CreateClient().PostAsJsonAsync(StartPath, new StartStaffSignInCommand("not-an-email"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{BackOfficeWebApplicationFactory.BackOfficeHost}"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Host = BackOfficeWebApplicationFactory.BackOfficeHost;
        return client;
    }
}
