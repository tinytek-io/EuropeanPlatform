using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

public sealed class ScalewayConfigFileTests : IDisposable
{
    // The shape the scw CLI writes: a commented default profile at the top level, then the named profiles.
    private const string ScwWrittenFile = """
                                          # Scaleway configuration file
                                          # An access key is a secret key identifier.
                                          access_key: SCWDEFAULTDEFAULTDEFA
                                          secret_key: 00000000-0000-0000-0000-000000000000
                                          default_organization_id: 11111111-1111-1111-1111-111111111111
                                          default_region: fr-par
                                          # send_telemetry: false

                                          profiles:
                                            ep-stage:
                                              access_key: SCWSTAGESTAGESTAGEST
                                              secret_key: 22222222-2222-2222-2222-222222222222
                                              default_project_id: 33333333-3333-3333-3333-333333333333
                                              # api_url: https://api.scaleway.com
                                            other:
                                              access_key: SCWOTHEROTHEROTHEROT
                                              secret_key: 44444444-4444-4444-4444-444444444444
                                          """;

    private readonly string _folder = Directory.CreateTempSubdirectory("scw-config-tests-").FullName;

    private string ConfigPath => Path.Combine(_folder, "config.yaml");

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void ReadProfile_WhenNamedProfileOmitsAValue_ShouldInheritItFromTheDefaultProfile()
    {
        // Arrange
        File.WriteAllText(ConfigPath, ScwWrittenFile);

        // Act
        var profile = ScalewayConfigFile.ReadProfile("ep-stage", ConfigPath);

        // Assert
        profile.Should().Be(new ScalewayProfile(
                "SCWSTAGESTAGESTAGEST",
                "22222222-2222-2222-2222-222222222222",
                "11111111-1111-1111-1111-111111111111",
                "33333333-3333-3333-3333-333333333333",
                "fr-par"
            )
        );
    }

    [Fact]
    public void ReadProfile_WhenDefaultProfileIsRequested_ShouldReadTheTopLevelSettings()
    {
        // Arrange
        File.WriteAllText(ConfigPath, ScwWrittenFile);

        // Act
        var profile = ScalewayConfigFile.ReadProfile(ScalewayConfigFile.DefaultProfileName, ConfigPath);

        // Assert
        profile!.AccessKey.Should().Be("SCWDEFAULTDEFAULTDEFA");
        profile.DefaultProjectId.Should().BeNull();
    }

    [Fact]
    public void ReadProfile_WhenProfileDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        File.WriteAllText(ConfigPath, ScwWrittenFile);

        // Act
        var profile = ScalewayConfigFile.ReadProfile("ep-prod", ConfigPath);

        // Assert
        profile.Should().BeNull();
    }

    [Fact]
    public void ReadProfile_WhenFileDoesNotExist_ShouldReturnNull()
    {
        // Act
        var profile = ScalewayConfigFile.ReadProfile("ep-stage", ConfigPath);

        // Assert
        profile.Should().BeNull();
    }

    [Fact]
    public void WriteProfile_WhenProfileExists_ShouldUpdateItAndKeepEverythingElse()
    {
        // Arrange
        File.WriteAllText(ConfigPath, ScwWrittenFile);

        // Act
        ScalewayConfigFile.WriteProfile("ep-stage", new ScalewayProfile("SCWROTATEDROTATEDROT", "55555555-5555-5555-5555-555555555555", DefaultZone: "fr-par-1"), ConfigPath);

        // Assert
        var expected = ScwWrittenFile
            .Replace("SCWSTAGESTAGESTAGEST", "SCWROTATEDROTATEDROT")
            .Replace("22222222-2222-2222-2222-222222222222", "55555555-5555-5555-5555-555555555555")
            .Replace(
                "    default_project_id: 33333333-3333-3333-3333-333333333333\n",
                "    default_project_id: 33333333-3333-3333-3333-333333333333\n    default_zone: fr-par-1\n"
            );
        File.ReadAllText(ConfigPath).Should().Be(expected + "\n");
    }

    [Fact]
    public void WriteProfile_WhenProfileIsNew_ShouldAddItAfterTheExistingProfiles()
    {
        // Arrange
        File.WriteAllText(ConfigPath, ScwWrittenFile);

        // Act
        ScalewayConfigFile.WriteProfile("ep-prod", new ScalewayProfile("SCWPRODPRODPRODPRODP", "66666666-6666-6666-6666-666666666666"), ConfigPath);

        // Assert
        File.ReadAllText(ConfigPath).Should().Be(ScwWrittenFile + "\n  ep-prod:\n    access_key: SCWPRODPRODPRODPRODP\n    secret_key: 66666666-6666-6666-6666-666666666666\n");
        ScalewayConfigFile.ReadProfile("other", ConfigPath)!.AccessKey.Should().Be("SCWOTHEROTHEROTHEROT");
    }

    [Fact]
    public void WriteProfile_WhenFileHasNoProfilesSection_ShouldAddOne()
    {
        // Arrange
        File.WriteAllText(ConfigPath, "access_key: SCWDEFAULTDEFAULTDEFA\nsecret_key: 00000000-0000-0000-0000-000000000000\n");

        // Act
        ScalewayConfigFile.WriteProfile("ep-stage", new ScalewayProfile("SCWSTAGESTAGESTAGEST", "22222222-2222-2222-2222-222222222222"), ConfigPath);

        // Assert
        ScalewayConfigFile.ReadProfile("ep-stage", ConfigPath)!.AccessKey.Should().Be("SCWSTAGESTAGESTAGEST");
        ScalewayConfigFile.ReadProfile(ScalewayConfigFile.DefaultProfileName, ConfigPath)!.AccessKey.Should().Be("SCWDEFAULTDEFAULTDEFA");
    }

    [Fact]
    public void WriteProfile_WhenFileDoesNotExist_ShouldCreateItReadableByItsOwnerOnly()
    {
        // Arrange
        var path = Path.Combine(_folder, "scw", "config.yaml");

        // Act
        ScalewayConfigFile.WriteProfile("ep-stage", new ScalewayProfile("SCWSTAGESTAGESTAGEST", "22222222-2222-2222-2222-222222222222"), path);

        // Assert
        ScalewayConfigFile.ReadProfile("ep-stage", path)!.SecretKey.Should().Be("22222222-2222-2222-2222-222222222222");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("has space")]
    public void WriteProfile_WhenProfileNameIsInvalid_ShouldThrow(string profileName)
    {
        // Act
        var write = () => ScalewayConfigFile.WriteProfile(profileName, new ScalewayProfile("SCWSTAGESTAGESTAGEST", "secret"), ConfigPath);

        // Assert
        write.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WriteProfile_WhenValueContainsWhitespace_ShouldThrow()
    {
        // Act
        var write = () => ScalewayConfigFile.WriteProfile("ep-stage", new ScalewayProfile("SCW STAGE", "secret"), ConfigPath);

        // Assert
        write.Should().Throw<ArgumentException>();
    }
}
