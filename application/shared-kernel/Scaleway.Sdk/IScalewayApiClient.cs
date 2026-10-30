using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>
///     The Scaleway HTTP transport seam. <see cref="ScalewayApiClient" /> is the real (HTTP) implementation; a
///     deterministic in-memory implementation can stand in for it in deterministic simulation tests (#45 slice 5),
///     letting the whole typed deploy path run offline against a simulated Scaleway with injected faults. The typed
///     generated clients and the deployment step depend on this interface, not the concrete client.
/// </summary>
public interface IScalewayApiClient
{
    /// <summary>
    ///     GETs a collection, following Scaleway's paging to the end and returning the flattened items. The page cap
    ///     is the transport's, not Scaleway's — see <c>ScalewayApiClient.MaxPages</c>.
    /// </summary>
    /// <param name="apiPath">Collection path, relative to the API base address.</param>
    /// <param name="region">Region the call is billed and routed to; also what a retry policy keys on.</param>
    /// <param name="queryParams">
    ///     Filters and paging. A list of pairs rather than a dictionary so an array parameter can
    ///     repeat.
    /// </param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task<JsonElement[]> ListResourcesAsync(string apiPath, string region, IReadOnlyList<KeyValuePair<string, string>>? queryParams, CancellationToken cancellationToken);

    /// <summary>POSTs to a collection path and returns the created resource as Scaleway echoed it back.</summary>
    /// <param name="apiPath">Collection path, relative to the API base address.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="body">Serialized with snake_case names; null properties are omitted rather than sent as null.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    /// <exception cref="ScalewayResourceAlreadyExistsException">Scaleway rejected the create because the name is taken.</exception>
    Task<JsonElement> CreateResourceAsync(string apiPath, string region, object body, CancellationToken cancellationToken);

    /// <summary>PATCHes one resource. Scaleway treats an omitted field as unchanged, so a partial body is a partial update.</summary>
    /// <param name="apiPath">Item path prefix, which this completes with the resource id.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="resourceId">Scaleway ID of the resource to update.</param>
    /// <param name="body">Serialized with snake_case names; null properties are omitted rather than sent as null.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task<JsonElement> UpdateResourceAsync(string apiPath, string region, string resourceId, object body, CancellationToken cancellationToken);

    /// <summary>PUTs to a collection path — Scaleway's idempotent set-style operations (e.g. RDB privileges).</summary>
    /// <param name="apiPath">Collection path, relative to the API base address.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="body">The whole desired state; unlike a PATCH, what is omitted is not kept.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task<JsonElement> PutResourceAsync(string apiPath, string region, object body, CancellationToken cancellationToken);

    /// <summary>GETs one resource, returning null on 404 so a caller can ask whether it exists without catching.</summary>
    /// <param name="apiPath">Item path prefix, which this completes with the resource id.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="resourceId">Scaleway ID of the resource to fetch.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task<JsonElement?> GetResourceAsync(string apiPath, string region, string resourceId, CancellationToken cancellationToken);

    /// <summary>GETs a fixed path that is not a collection and takes no id — an action operation. Null on 404.</summary>
    /// <param name="apiPath">The full path to address, relative to the API base address.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task<JsonElement?> GetAsync(string apiPath, string region, CancellationToken cancellationToken);

    /// <summary>
    ///     A GET whose query string carries part of the request — Scaleway's action operations that identify their
    ///     target through query parameters rather than the path (Secret Manager's secrets-by-path access), and the
    ///     ones that take an option (Kapsule's <c>redacted</c> kubeconfig). A list of pairs rather than a dictionary,
    ///     so an array parameter repeats the way OpenAPI's <c>style=form, explode=true</c> default requires.
    /// </summary>
    /// <param name="apiPath">The full path to address, relative to the API base address.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="queryParams">The query parameters carrying part of the request.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task<JsonElement?> GetAsync(string apiPath, string region, IReadOnlyList<KeyValuePair<string, string>>? queryParams, CancellationToken cancellationToken);

    /// <summary>
    ///     DELETEs one resource. Note the asymmetry with <see cref="GetResourceAsync" />: a 404 here throws like any
    ///     other error status rather than being treated as "already gone", so a caller deleting something that may
    ///     not exist has to check first or catch.
    /// </summary>
    /// <param name="apiPath">Item path prefix, which this completes with the resource id.</param>
    /// <param name="region">Region the call is billed and routed to.</param>
    /// <param name="resourceId">Scaleway ID of the resource to delete.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    Task DeleteResourceAsync(string apiPath, string region, string resourceId, CancellationToken cancellationToken);
}
