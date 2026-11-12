using FluentAssertions;
using Scaleway.Sdk;
using Scaleway.Sdk.Rdb.V1;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

/// <summary>
///     <c>CreateInstanceRequest</c> carries one of Scaleway's mutually exclusive <c>ProjectIdentifier</c> pairs, applied
///     in the order the generated client applies it: project first, then organization.
/// </summary>
public sealed class ScalewayRequestScopeTests
{
    private const string OrganizationId = "11111111-1111-1111-1111-111111111111";
    private const string ProjectId = "22222222-2222-2222-2222-222222222222";
    private const string CallerOrganizationId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void ApplyScope_WhenRequestSetsNeitherIdentifier_ShouldFillOnlyTheProject()
    {
        // Arrange
        var scope = new ScalewayRequestScope(OrganizationId, ProjectId);

        // Act
        var request = scope.ApplyOrganization(scope.ApplyProject(new CreateInstanceRequest()));

        // Assert
        request.ProjectId.Should().Be(ProjectId);
        request.OrganizationId.Should().BeNull();
    }

    [Fact]
    public void ApplyScope_WhenRequestSetsOrganization_ShouldKeepItAndNotInjectTheProject()
    {
        // Arrange
        var scope = new ScalewayRequestScope(OrganizationId, ProjectId);

        // Act
        var request = scope.ApplyOrganization(scope.ApplyProject(new CreateInstanceRequest { OrganizationId = CallerOrganizationId }));

        // Assert
        request.OrganizationId.Should().Be(CallerOrganizationId);
        request.ProjectId.Should().BeNull("the pair is mutually exclusive, so injecting the project would make the request invalid");
    }

    [Fact]
    public void ApplyScope_WhenScopeHasNoProject_ShouldFillTheOrganization()
    {
        // Arrange
        var scope = new ScalewayRequestScope(OrganizationId);

        // Act
        var request = scope.ApplyOrganization(scope.ApplyProject(new CreateInstanceRequest()));

        // Assert
        request.OrganizationId.Should().Be(OrganizationId);
        request.ProjectId.Should().BeNull();
    }
}
