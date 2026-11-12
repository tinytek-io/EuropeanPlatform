using Aspire.Hosting.EuropeanPlatform.Profiles;
using FluentAssertions;
using Xunit;

namespace Aspire.Hosting.EuropeanPlatform.Tests;

public sealed class EnvironmentProfilesTests
{
    private static readonly string[] EnvironmentTypes = ["review", "test", "staging", "production"];

    [Fact]
    public void Load_WhenReadingTheRepositorysMatrix_ShouldSizeEveryRoleInEveryEnvironmentType()
    {
        // Arrange
        var path = RepositoryFiles.PathOf("cloud-infrastructure", "scaleway", "environment-profiles.yaml");

        // Act
        var profiles = EnvironmentProfiles.Load(path);

        // Assert
        foreach (var role in Enum.GetValues<WorkloadRole>())
        {
            foreach (var environmentType in Enum.GetValues<EnvironmentType>())
            {
                profiles.For(role, WorkloadClass.Standard, environmentType).MemoryRequest.Should().NotBeNullOrEmpty();
            }
        }

        profiles.For(WorkloadRole.Api, WorkloadClass.MemoryIntensive, EnvironmentType.Staging).MemoryLimit.Should().Be("512Mi");
    }

    [Fact]
    public void For_WhenTheClassIsKebabCasedInTheFile_ShouldResolveTheEnumMember()
    {
        // Arrange
        var yaml = Matrix(extraApiClass: "memory-intensive");

        // Act
        var sizing = EnvironmentProfiles.Parse(yaml, "test.yaml").For(WorkloadRole.Api, WorkloadClass.MemoryIntensive, EnvironmentType.Production);

        // Assert
        sizing.Should().Be(new WorkloadSizing(1, "96Mi", "384Mi"));
    }

    [Fact]
    public void Parse_WhenAnEnvironmentTypeIsMissingFromAClass_ShouldNameTheGap()
    {
        // Arrange
        var yaml = Matrix("production");

        // Act
        var parse = () => EnvironmentProfiles.Parse(yaml, "test.yaml");

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage("*workloads.api.standard.production is missing*");
    }

    [Fact]
    public void Parse_WhenASettingIsMisspelt_ShouldRejectTheFile()
    {
        // Arrange
        var yaml = Matrix().Replace("memoryLimit: 256Mi", "memoryLimt: 256Mi");

        // Act
        var parse = () => EnvironmentProfiles.Parse(yaml, "test.yaml");

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage("test.yaml is not a valid profile matrix*");
    }

    [Fact]
    public void Parse_WhenTheRequestIsAboveTheLimitAndAMigratorSetsReplicas_ShouldListBothProblems()
    {
        // Arrange
        var yaml = Matrix(apiProductionMemoryRequest: "512Mi", migratorReplicas: true);

        // Act
        var parse = () => EnvironmentProfiles.Parse(yaml, "test.yaml");

        // Assert
        parse.Should().Throw<InvalidOperationException>()
            .Where(exception => exception.Message.Contains("workloads.api.standard.production.memoryRequest (512Mi) is above its memoryLimit (384Mi)")
                                && exception.Message.Contains("workloads.migrator.standard.review.replicas does not apply")
            );
    }

    [Fact]
    public void Parse_WhenAQuantityHasNoUnit_ShouldRejectIt()
    {
        // Arrange
        var yaml = Matrix().Replace("memoryRequest: 64Mi", "memoryRequest: 64");

        // Act
        var parse = () => EnvironmentProfiles.Parse(yaml, "test.yaml");

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage("*memoryRequest must be a quantity in Mi or Gi*");
    }

    [Fact]
    public void Parse_WhenAClassIsUnknown_ShouldNameTheKnownClasses()
    {
        // Arrange
        var yaml = Matrix(extraApiClass: "large");

        // Act
        var parse = () => EnvironmentProfiles.Parse(yaml, "test.yaml");

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage("*workloads.api.large is not a workload class (standard, memory-intensive)*");
    }

    [Fact]
    public void ParseEnvironmentType_WhenTheTypeIsUnknown_ShouldNameTheKnownTypes()
    {
        // Act
        var parse = () => EnvironmentProfiles.ParseEnvironmentType("qa");

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage("'qa' is not an environment type (review, test, staging, production).");
    }

    // A complete, valid matrix: every role's standard class in every environment type, with optional defects.
    private static string Matrix(string? omitApiEnvironment = null, string? extraApiClass = null, string apiProductionMemoryRequest = "96Mi", bool migratorReplicas = false)
    {
        var lines = new List<string> { "infrastructure:" };
        lines.AddRange(EnvironmentTypes.Select(environmentType => $"  {environmentType}: {{}}"));
        lines.Add("workloads:");
        foreach (var role in new[] { "api", "workers", "gateway", "migrator" })
        {
            lines.Add($"  {role}:");
            var classes = role == "api" && extraApiClass is not null ? new[] { "standard", extraApiClass } : ["standard"];
            foreach (var workloadClass in classes)
            {
                lines.Add($"    {workloadClass}:");
                foreach (var environmentType in EnvironmentTypes.Where(environmentType => role != "api" || environmentType != omitApiEnvironment))
                {
                    lines.Add(role switch
                        {
                            "migrator" => $"      {environmentType}: {{ {(migratorReplicas ? "replicas: 1, " : "")}memoryRequest: 64Mi, memoryLimit: 256Mi }}",
                            "api" => $"      {environmentType}: {{ replicas: 1, memoryRequest: {(environmentType == "production" ? apiProductionMemoryRequest : "96Mi")}, memoryLimit: 384Mi }}",
                            _ => $"      {environmentType}: {{ replicas: 1, memoryRequest: 64Mi, memoryLimit: 256Mi }}"
                        }
                    );
                }
            }
        }

        return string.Join('\n', lines);
    }
}
