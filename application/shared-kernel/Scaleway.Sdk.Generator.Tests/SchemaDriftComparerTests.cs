using FluentAssertions;
using Xunit;

namespace Scaleway.Sdk.Generator.Tests;

/// <summary>
///     Classifies what changed between two versions of a service's parsed schema, so a schema refresh can gate on
///     breaking changes. The comparer works over the parsed model (what the SDK generates), so drift in
///     un-generated corners is ignored and formatting/reordering noise never shows up.
/// </summary>
public sealed class SchemaDriftComparerTests
{
    [Fact]
    public void Compare_WhenSchemasAreIdentical_ReportsNoChanges()
    {
        var api = Build();

        var report = SchemaDriftComparer.Compare(api, api);

        report.Changes.Should().BeEmpty();
        report.HasBreaking.Should().BeFalse();
    }

    [Fact]
    public void Compare_WhenAnOperationIsRemoved_IsBreaking()
    {
        var previous = Build(operations: [Op("CreateContainer"), Op("DeleteContainer")]);
        var current = Build(operations: [Op("CreateContainer")]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Breaking, "operation DeleteContainer removed");
    }

    [Fact]
    public void Compare_WhenAnOperationIsAdded_IsAdditive()
    {
        var previous = Build(operations: [Op("CreateContainer")]);
        var current = Build(operations: [Op("CreateContainer"), Op("ListContainers")]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Additive, "operation ListContainers added");
    }

    [Fact]
    public void Compare_WhenAnOperationIsNewlyDeprecated_IsASignal()
    {
        var previous = Build(operations: [Op("CreateContainer")]);
        var current = Build(operations: [Op("CreateContainer", true)]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Signal, "operation CreateContainer deprecated");
    }

    [Fact]
    public void Compare_WhenAFieldIsRemoved_IsBreaking()
    {
        var previous = Build(records: [Record("CreateContainerRequest", Field("name"), Field("privacy"))]);
        var current = Build(records: [Record("CreateContainerRequest", Field("name"))]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Breaking, "field CreateContainerRequest.privacy removed");
    }

    [Fact]
    public void Compare_WhenAFieldIsAdded_IsAdditive()
    {
        var previous = Build(records: [Record("CreateContainerRequest", Field("name"))]);
        var current = Build(records: [Record("CreateContainerRequest", Field("name"), Field("tags"))]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Additive, "field CreateContainerRequest.tags added");
    }

    [Fact]
    public void Compare_WhenAFieldTypeChanges_IsBreaking()
    {
        var previous = Build(records: [Record("CreateContainerRequest", Field("memory_limit"))]);
        var current = Build(records: [Record("CreateContainerRequest", Field("memory_limit", "long?"))]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Breaking, "field CreateContainerRequest.memory_limit type changed string? → long?");
    }

    [Fact]
    public void Compare_WhenAQueryFieldIsNewlyDeprecated_IsASignal()
    {
        var previous = Build(queryRecords: [Record("ListApiKeysRequest", Field("access_key"))]);
        var current = Build(queryRecords: [Record("ListApiKeysRequest", Field("access_key", deprecated: true))]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Signal, "field ListApiKeysRequest.access_key deprecated");
    }

    [Fact]
    public void Compare_WhenARecordIsRemoved_IsBreaking()
    {
        var previous = Build(records: [Record("CreateContainerRequest", Field("name")), Record("Container", Field("id"))]);
        var current = Build(records: [Record("CreateContainerRequest", Field("name"))]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Breaking, "record Container removed");
    }

    [Fact]
    public void Compare_WhenAnEnumValueIsRemoved_IsBreaking()
    {
        var previous = Build(enums: [Enum("ContainerPrivacy", "public", "private")]);
        var current = Build(enums: [Enum("ContainerPrivacy", "public")]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Breaking, "enum ContainerPrivacy value 'private' removed");
    }

    [Fact]
    public void Compare_WhenAnEnumValueIsAdded_IsAdditive()
    {
        var previous = Build(enums: [Enum("ContainerPrivacy", "public")]);
        var current = Build(enums: [Enum("ContainerPrivacy", "public", "private")]);

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Additive, "enum ContainerPrivacy value 'private' added");
    }

    [Fact]
    public void Compare_WhenTheApiVersionChanges_IsBreaking()
    {
        var previous = Build("V1Beta1");
        var current = Build(); // defaults to V1

        var report = SchemaDriftComparer.Compare(previous, current);

        AssertOnly(report, SchemaDriftKind.Breaking, "API version changed V1Beta1 → V1");
    }

    [Fact]
    public void Compare_OrdersBreakingChangesFirst()
    {
        var previous = Build(operations: [Op("CreateContainer"), Op("DeleteContainer")]);
        var current = Build(operations: [Op("CreateContainer"), Op("ListContainers")]);

        var report = SchemaDriftComparer.Compare(previous, current);

        // A removed op (breaking) and an added op (additive): breaking sorts first so a reader sees severity top-down.
        report.Changes.Should().HaveCount(2);
        report.Changes[0].Kind.Should().Be(SchemaDriftKind.Breaking);
        report.Changes[1].Kind.Should().Be(SchemaDriftKind.Additive);
        report.HasBreaking.Should().BeTrue();
    }

    private static void AssertOnly(SchemaDriftReport report, SchemaDriftKind kind, string description)
    {
        report.Changes.Should().ContainSingle();
        report.Changes[0].Kind.Should().Be(kind);
        report.Changes[0].Description.Should().Be(description);
    }

    private static ParsedApi Build(
        string version = "V1",
        List<ParsedOperation>? operations = null,
        List<ParsedRecord>? records = null,
        List<ParsedClientEnum>? enums = null,
        List<ParsedRecord>? queryRecords = null)
    {
        var group = new ParsedResourceGroup("Container", "Containers", "containers/v1/containers", "containers/v1/containers", operations ?? []);
        return new ParsedApi("container", version, [group], records ?? [], enums ?? [], queryRecords ?? [], [], []);
    }

    private static ParsedOperation Op(string operationId, bool deprecated = false)
    {
        return new ParsedOperation(operationId, ApiOperationKind.Create, null, "Container", Deprecated: deprecated);
    }

    private static ParsedRecord Record(string name, params ParsedRecordField[] fields)
    {
        return new ParsedRecord(name, fields.ToList());
    }

    private static ParsedRecordField Field(string name, string csharpType = "string?", bool deprecated = false)
    {
        return new ParsedRecordField(name, csharpType, Deprecated: deprecated);
    }

    private static ParsedClientEnum Enum(string name, params string[] values)
    {
        return new ParsedClientEnum(name, values.ToList());
    }
}
