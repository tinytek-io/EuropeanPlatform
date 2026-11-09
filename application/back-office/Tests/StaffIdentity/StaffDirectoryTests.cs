using System.Runtime.CompilerServices;
using BackOffice.StaffIdentity;
using FluentAssertions;
using Xunit;

namespace BackOffice.Tests.StaffIdentity;

public sealed class StaffDirectoryTests
{
    private const string ValidFile = """
                                     staff:
                                       - id: jane.doe
                                         email: Jane.Doe@Example.com
                                         name: Jane Doe
                                         groups: [staff, admins]
                                       - id: john.roe
                                         email: john.roe@example.com
                                         name: John Roe
                                         groups: [staff]
                                         active: false
                                     clients:
                                       - clientId: forgejo
                                         displayName: Forgejo
                                         redirectUris: [https://git.example.com/user/oauth2/back-office/callback]
                                         allowedGroups: [staff]
                                     """;

    [Fact]
    public void Parse_WhenFileIsValid_ShouldExposeStaffAndClients()
    {
        // Act
        var directory = StaffDirectory.Parse(ValidFile);

        // Assert
        directory.Staff.Should().HaveCount(2);
        directory.FindActiveByEmail(" jane.doe@EXAMPLE.com ").Should().Be(directory.Staff[0]);
        directory.Staff[0].Email.Should().Be("jane.doe@example.com");
        directory.Staff[0].Active.Should().BeTrue();
        directory.FindClient("forgejo")!.RedirectUris.Should().Equal("https://git.example.com/user/oauth2/back-office/callback");
        directory.FindClient("forgejo")!.PostLogoutRedirectUris.Should().BeEmpty();
    }

    [Fact]
    public void FindActive_WhenStaffMemberIsDeactivated_ShouldNotFindThem()
    {
        // Arrange
        var directory = StaffDirectory.Parse(ValidFile);

        // Act & Assert
        directory.FindActiveByEmail("john.roe@example.com").Should().BeNull();
        directory.FindActiveById("john.roe").Should().BeNull();
        directory.FindActiveById("jane.doe").Should().NotBeNull();
    }

    [Fact]
    public void Parse_WhenFileHasUnknownKey_ShouldThrow()
    {
        // Arrange
        var yaml = ValidFile.Replace("active: false", "is_active: false");

        // Act
        var parse = () => StaffDirectory.Parse(yaml);

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage("*is_active*");
    }

    [Theory]
    [InlineData("id: jane.doe", "id: john.roe", "staff id 'john.roe' is used more than once")]
    [InlineData("email: Jane.Doe@Example.com", "email: john.roe@example.com", "staff email 'john.roe@example.com' is used more than once")]
    [InlineData("groups: [staff, admins]", "groups: [admins]", "must be in the 'staff' group")]
    [InlineData("groups: [staff, admins]", "groups: [staff, owners]", "unknown group 'owners'")]
    [InlineData("email: Jane.Doe@Example.com", "email: not-an-email", "needs a valid email")]
    [InlineData("id: jane.doe", "id: Jane Doe", "needs an id")]
    [InlineData("redirectUris: [https://git.example.com/user/oauth2/back-office/callback]", "redirectUris: [http://git.example.com/callback]", "must be absolute https")]
    [InlineData("allowedGroups: [staff]", "allowedGroups: []", "needs at least one allowedGroup")]
    public void Parse_WhenFileBreaksARule_ShouldThrowNamingTheProblem(string original, string replacement, string expectedProblem)
    {
        // Arrange
        var yaml = ReplaceFirst(ValidFile, original, replacement);

        // Act
        var parse = () => StaffDirectory.Parse(yaml);

        // Assert
        parse.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedProblem}*");
    }

    [Fact]
    public void Parse_WhenRedirectUriIsLocalhostHttp_ShouldAllowIt()
    {
        // Arrange
        var yaml = ValidFile.Replace("https://git.example.com/user/oauth2/back-office/callback", "http://localhost:3000/callback");

        // Act
        var directory = StaffDirectory.Parse(yaml);

        // Assert
        directory.Clients.Should().ContainSingle();
    }

    [Fact]
    public void Parse_WhenFileIsEmpty_ShouldHaveNobody()
    {
        // Act
        var directory = StaffDirectory.Parse("staff: []\nclients: []\n");

        // Assert
        directory.Staff.Should().BeEmpty();
        directory.Clients.Should().BeEmpty();
    }

    [Fact]
    public void Load_WhenReadingEveryStaffIdentityFileInTheRepository_ShouldAcceptThem()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(repositoryRoot, "cloud-infrastructure", "staff-identity"), "*.yaml")
            .Append(Path.Combine(repositoryRoot, "application", "AppHost", "StaffIdentity", "development.yaml"))
            .ToArray();

        // Act & Assert
        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            var load = () => StaffDirectory.Load(file);
            load.Should().NotThrow($"{Path.GetFileName(file)} must be a valid staff identity file");
        }
    }

    private static string ReplaceFirst(string text, string original, string replacement)
    {
        var index = text.IndexOf(original, StringComparison.Ordinal);
        return text[..index] + replacement + text[(index + original.Length)..];
    }

    private static string GetRepositoryRoot([CallerFilePath] string callerFilePath = "")
    {
        var directory = Path.GetDirectoryName(callerFilePath)!;
        // Path.Exists rather than Directory.Exists: in a git worktree ".git" is a file, not a directory.
        while (!string.IsNullOrEmpty(directory) && !Path.Exists(Path.Combine(directory, ".git")))
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        return directory;
    }
}
