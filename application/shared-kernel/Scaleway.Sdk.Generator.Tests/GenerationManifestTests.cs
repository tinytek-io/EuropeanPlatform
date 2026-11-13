using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     The generation manifest is the answer to defect (d) — a CRUD operation the parser cannot match is skipped
///     rather than fatal, so an operation Scaleway renames upstream stops being generated with no signal anywhere.
///     These assert that the checked-in file is re-derivable from the schemas (so it cannot go stale unnoticed) and
///     that a lost operation actually surfaces as a diff.
/// </summary>
public sealed class GenerationManifestTests
{
    [Fact]
    public void CheckedInManifest_MatchesAFreshParseOfTheSchemas()
    {
        // The manifest is only worth having if it is a fact about the current schemas rather than a snapshot
        // somebody forgot to regenerate. Regenerating is `dotnet run --project developer-cli -- scaleway-sdk generate`.
        var expected = GenerationManifest.Render(SchemaFiles.AllSchemas());

        var checkedIn = File.ReadAllText(Path.Combine(SchemaFiles.GeneratedDirectory(), GenerationManifest.FileName));

        checkedIn.Should().Be(expected, "the manifest is generated output — regenerate rather than hand-editing it");
    }

    [Fact]
    public void Manifest_ShowsAnOperationTheSchemaStoppedDeclaring_RatherThanDroppingItSilently()
    {
        // Defect (d), reproduced against the real secret-manager schema: renaming GetSecret upstream makes the
        // parser skip it and emit the group anyway, one method short. Generation stays green either way — the
        // manifest is the only place that difference becomes visible.
        var json = SchemaSource.YamlToJson(File.ReadAllText(SchemaFiles.SchemaPath("secret-manager")));
        var renamedUpstream = json.Replace("\"GetSecret\"", "\"FetchSecret\"", StringComparison.Ordinal);
        renamedUpstream.Should().NotBe(json, "the test is meaningless if the operationId it rewrites is not there");

        var before = GenerationManifest.Render([("secret-manager", Parse(json))]);
        var after = GenerationManifest.Render([("secret-manager", Parse(renamedUpstream))]);

        before.Should().Contain("\"GetSecret\"");
        after.Should().NotContain("\"GetSecret\"", "the operation is gone from the generated client, so it is gone from the manifest");
        after.Should().NotBe(before, "a silently skipped operation must show up as a manifest diff");
    }

    [Fact]
    public void EveryGeneratedResourceGroup_DeclaresATier()
    {
        // A tier is a claim about what has been verified. The manifest is the README's source, so a group with no
        // declared tier would render as coverage with no stated basis — the shape of the "38 services" claim.
        var manifest = GenerationManifest.Render(SchemaFiles.AllSchemas());

        manifest.Should().NotContain($"\"tier\": \"{nameof(ValidationTier.Excluded)}\"", "an excluded group is not generated, so it cannot appear");
        manifest.Should().NotContain("\"tier\": \"\"");
    }

    [Fact]
    public void EveryGeneratedService_DeclaresAnEntryPoint()
    {
        // R3: a generated *Api nobody can reach from ScalewayClient or ScalewayProjectClient is breadth producing
        // clients no caller has. EntryPointFor throws during generation, so this pins the rendered result.
        var manifest = GenerationManifest.Render(SchemaFiles.AllSchemas());

        manifest.Should().Contain("\"entryPoint\": \"ScalewayProjectClient.Databases\"");
        manifest.Should().Contain("\"entryPoint\": \"ScalewayClient.Iam\"", "IAM identities are organization-scoped, not project-scoped");
    }

    [Fact]
    public void AServiceWithNoDeclaredEntryPoint_FailsGeneration()
    {
        // Adding a resource group without wiring its *Api onto an entry point is the failure R3 names. It fails
        // here rather than shipping a client nothing can call.
        //
        // The subject is a name no schema declares, on purpose. This used to name a real service that had no entry
        // point yet ('jobs'), which made the test go red the moment that service was wired up — a test that stops
        // holding as soon as the queue advances past it. Every real service either has an entry point today or gets
        // one when it is admitted, so the only durable subject is one that never will.
        var act = () => SchemaSource.EntryPointFor("a-service-nobody-wired-up");

        act.Should().Throw<InvalidOperationException>().WithMessage("*a-service-nobody-wired-up*no entry-point property*ScalewayProjectClient*");
    }

    private static ParsedApi Parse(string json)
    {
        return OpenApiClientParser.Parse("secret", json, [("Secret", "Secrets"), ("SecretVersion", "SecretVersions")], SchemaSource.ClientActionOperations("secret"));
    }
}
