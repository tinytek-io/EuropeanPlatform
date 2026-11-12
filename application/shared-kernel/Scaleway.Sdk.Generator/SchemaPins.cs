namespace Scaleway.Sdk.Generator;

/// <summary>
///     Where one checked-in schema in <c>Scaleway.Sdk/Schemas</c> came from: the product doc slug Scaleway
///     publishes it under, the API version the copy on disk was downloaded from, and why that version rather than
///     another.
///     <para>
///         The rationale is a positional parameter rather than an optional one on purpose. Scaleway keeps
///         superseded schema documents serving HTTP 200 indefinitely, so a pin left behind by an upstream release
///         breaks nothing, fails nothing and prompts nobody. That has now happened twice: Serverless Containers
///         sat on <c>v1beta1</c> long after <c>v1</c> shipped (the migration included a MiB→bytes unit change that
///         would have provisioned undersized containers), and <c>serverless-jobs</c> sits on <c>v1alpha1</c>
///         today. A version alone cannot be reviewed — "deliberate" and "forgotten" look identical. A reason can
///         be, and omitting it does not compile.
///     </para>
/// </summary>
/// <param name="Slug">
///     The product doc path Scaleway publishes the schema under, e.g. <c>edge-services</c> or
///     <c>load-balancer/zoned</c> (a product with several APIs nests them). <see cref="FileName" /> is the flat name
///     the schema is written to on disk.
/// </param>
/// <param name="Version">The API version segment of the published document's URL, e.g. <c>v1beta1</c>.</param>
/// <param name="Rationale">
///     Why this version and not the newest one Scaleway serves. <see cref="SchemaVersionCheck" /> is what keeps
///     this honest: it probes upstream and reports a pin the rationale no longer describes.
/// </param>
public sealed record SchemaPin(string Slug, string Version, string Rationale)
{
    /// <summary>The schema's file name in <c>Scaleway.Sdk/Schemas</c>, without extension: the slug, flattened.</summary>
    public string FileName => Slug.Replace('/', '-');
}

/// <summary>
///     The checked-in schema set, as the pins it was downloaded from. Read by the developer CLI's
///     <c>scaleway-schemas</c> command (which compiles this file directly rather than referencing the generator,
///     the way it already compiles <c>PortAllocation</c>) and by the tests that hold the pins to the schemas.
/// </summary>
public static class SchemaPins
{
    /// <summary>
    ///     Every schema the SDK generates from, with the reason for its version. Verified against upstream on
    ///     2026-08-02 by probing each candidate version above the pin; the only pin below the newest published
    ///     document is <c>serverless-jobs</c>, and it says so.
    /// </summary>
    public static IReadOnlyList<SchemaPin> All { get; } =
    [
        new("serverless-containers", "v1", "Current published version. v1beta1 is still served and was what this was pinned to for a long time — the migration renamed fields and changed a memory limit from MiB to bytes."),
        new("managed-database-postgre-mysql", "v1", "Current published version; Scaleway publishes no newer document."),
        new("managed-database-redis", "v1", "Current published version; Scaleway publishes no newer document."),
        new("registry", "v1", "Current published version; Scaleway publishes no newer document."),
        new("vpc", "v2", "Current published version. Scaleway also publishes a second document for this version (routes-with-nexthop) which is not checked in; whether it adds operations is unconfirmed."),
        new("secret-manager", "v1beta1", "Current published version; Scaleway has published no v1."),
        new("iam", "v1alpha1", "Current published version. The alpha label does not track maturity at Scaleway — IAM is a generally available, billed product, and no v1 document exists."),
        new("transactional-email", "v1alpha1", "Current published version. Generally available and billed despite the alpha label, and no newer document exists."),
        new("serverless-functions", "v1beta1", "Current published version; Scaleway has published no v1."),
        new("serverless-jobs", "v1alpha2", "Current published version, bumped from v1alpha1. v1alpha1 is still served — Scaleway freezes superseded documents rather than retiring them, which is why the stale pin broke nothing and prompted nobody."),
        new("serverless-databases", "v1alpha1", "Current published version; Scaleway publishes no newer document."),
        new("kubernetes", "v1", "Current published version; Scaleway publishes no newer document."),
        new("load-balancer/zoned", "v1", "The zoned API, which the gateway Load Balancer, its frontends, backends and ACLs live under; v1 is the version Scaleway documents. Not yet probed for a newer version."),
        new("account/project", "v3", "Projects live in Account v3; v2 is retired. Not yet probed for a newer version."),
        new("edge-services", "v1beta1", "The version Scaleway documents. Not yet probed for a newer version."),
        new("product-catalog/public-catalog", "v2alpha1", "The only published version (v2beta1, v2 and v3alpha1 are not published); alpha, so the drift check is what flags a breaking change. The cost estimate prices Terraform plans from it.")
    ];

    /// <summary>
    ///     The published schema document for a pin. The one place the URL pattern is spelled out, so the
    ///     downloader and the version probe can never disagree about which document a pin names.
    /// </summary>
    /// <param name="slug">The product doc slug.</param>
    /// <param name="version">The API version segment.</param>
    public static string SchemaUrl(string slug, string version)
    {
        return $"https://www.scaleway.com/en/developers/api/{slug}/{version}/schema.yml";
    }

    /// <summary>
    ///     Pins whose rationale is present but says nothing — the hole the type system cannot close, since a
    ///     positional parameter can still be handed an empty string. Callers fail on a non-empty result.
    /// </summary>
    public static IReadOnlyList<SchemaPin> PinsWithoutRationale()
    {
        return All.Where(pin => string.IsNullOrWhiteSpace(pin.Rationale)).ToArray();
    }
}
