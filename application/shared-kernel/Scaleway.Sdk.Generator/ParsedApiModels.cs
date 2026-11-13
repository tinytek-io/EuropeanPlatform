namespace Scaleway.Sdk.Generator;

/// <summary>
///     The model the typed-client emitter consumes: HTTP <see cref="ParsedOperation">operations</see> and the
///     request/response <see cref="ParsedRecord">records</see> resolved from <c>components.schemas</c>.
/// </summary>
/// <summary>
///     <c>ApiVersion</c> is the PascalCase form of the version segment in the service's URL path
///     (<c>containers/v1/…</c> → <c>V1Beta1</c>). Scaleway versions each service independently and ships
///     several at alpha/beta, so the version is part of the generated namespace rather than an implicit detail.
/// </summary>
public sealed record ParsedApi(
    string ServiceName,
    string ApiVersion,
    List<ParsedResourceGroup> ResourceGroups,
    List<ParsedRecord> Records,
    List<ParsedClientEnum> Enums,
    List<ParsedRecord> QueryRecords,
    List<ParsedDroppedField> DroppedFields,
    List<ParsedActionOperation> Actions
);

/// <summary>
///     A non-CRUD operation on a static action segment (<c>…/versions/{revision}/access</c>) — declared per
///     service in <see cref="SchemaSource.ClientActionOperations" /> because the path shape carries no marker to
///     find them by. Supported: body-less GETs, empty-body POST triggers (CheckDomain), and POSTs or PATCHes carrying
///     a body (StartJobDefinition, UpdateOfferSubscription), whose body becomes a typed request record exactly as a
///     CRUD create's does. The parser fails loudly on anything else.
/// </summary>
/// <param name="QueryRecordName">
///     The typed query record the action's <c>in: query</c> parameters resolve to, or <c>null</c> when it declares
///     none. Same record shape a List gets, so a scoped client fills <c>project_id</c> here too.
/// </param>
/// <param name="QueryRequired">
///     Whether the schema marks any of those query parameters <c>required</c>. A required set is emitted without a
///     default, so a caller cannot omit the parameters that name the target — the shape of
///     <c>AccessSecretVersionByPath</c>.
/// </param>
/// <param name="HttpMethod"><c>GET</c>, <c>POST</c> or <c>PATCH</c>, upper-case.</param>
/// <param name="RequestRecordName">
///     The typed record a POST or PATCH body resolves to, or <c>null</c> for a body-less GET or an empty-body POST.
/// </param>
public sealed record ParsedActionOperation(
    string OperationId,
    string PathTemplate,
    string ResponseTypeName,
    string? QueryRecordName = null,
    bool QueryRequired = false,
    string? Summary = null,
    bool Deprecated = false,
    string HttpMethod = "GET",
    string? RequestRecordName = null
);

/// <summary>
///     A property the schema declares but the generator could not type, so the generated record omits it — e.g. an
///     untyped <c>object</c> (Scaleway's <c>tcp</c> health-check probe). Surfacing these turns a silent gap into a
///     tracked fact: the field exists in the API but not in the SDK.
/// </summary>
public sealed record ParsedDroppedField(string RecordName, string FieldName, string Reason);

/// <summary>
///     A resource (e.g. Container, Namespace) and the CRUD operations available on it. <c>Plural</c> is the
///     operation/method plural (e.g. Policies, APIKeys) — Scaleway's plurals are irregular, so they're passed in.
/// </summary>
/// <param name="BasePath">The collection path List and Create address.</param>
/// <param name="ItemPath">
///     The prefix Get/Update/Delete address, which the transport completes with <c>/{id}</c>. Usually identical to
///     <paramref name="BasePath" />, but Scaleway sometimes nests a collection under its parent while addressing the
///     item flat — Kapsule lists pools at <c>/clusters/{cluster_id}/pools</c> and fetches one at
///     <c>/pools/{pool_id}</c>. Derived from the item operations' own paths rather than assumed.
/// </param>
public sealed record ParsedResourceGroup(string Name, string Plural, string BasePath, string ItemPath, List<ParsedOperation> Operations);

public sealed record ParsedOperation(
    string OperationId,
    ApiOperationKind Kind,
    string? RequestRecordName,
    string ResponseTypeName,
    string? QueryRecordName = null,
    string? Summary = null,
    bool Deprecated = false
);

public enum ApiOperationKind
{
    List,
    Create,
    Get,
    Update,
    Delete
}

/// <summary>A generated request/response record: a C# <c>record</c> with one <see cref="ParsedRecordField" /> per field.</summary>
/// <param name="Description">
///     The component schema's own <c>description</c>, emitted as the record's <c>&lt;summary&gt;</c>. Often
///     <c>null</c> — Scaleway documents properties far more consistently than the components that hold them
///     (measured 2026-08-01: 94% of emitted properties carry one, against 37% of record types).
/// </param>
/// <param name="SchemaComponent">
///     The fully-qualified component this record was resolved from (<c>scaleway.registry.v1.Image</c>), or
///     <c>null</c> for a record synthesized from a request body or an inline object. It is what a reader follows to
///     find Scaleway's own documentation, so it is emitted even when the component carries no description of its own.
/// </param>
public sealed record ParsedRecord(string Name, List<ParsedRecordField> Fields, string? Description = null, string? SchemaComponent = null);

/// <summary>
///     An enum-valued field's value set, emitted as an extensible (string-backed) enum rather than a C# <c>enum</c>
///     so values Scaleway adds later round-trip instead of failing deserialization.
/// </summary>
public sealed record ParsedClientEnum(string Name, List<string> Values);

/// <summary>
///     <c>CSharpType</c> is the fully-resolved emitted type (e.g. <c>string?</c>, <c>long?</c>,
///     <c>List&lt;SecretHashedValue&gt;?</c>). <c>OneOfGroup</c> is Scaleway's <c>x-one-of</c> marker: fields
///     sharing a group name within a record are mutually exclusive, and setting more than one is a 400.
///     <c>Description</c> is the schema's own text for the field, emitted as the property's
///     <c>&lt;summary&gt;</c> — it is documentation, not shape, so it takes no part in deciding whether two
///     inline objects are the same record (see <c>RecordResolver.HasSameShape</c>).
/// </summary>
public sealed record ParsedRecordField(string Name, string CSharpType, string? OneOfGroup = null, bool Deprecated = false, string? Description = null);
