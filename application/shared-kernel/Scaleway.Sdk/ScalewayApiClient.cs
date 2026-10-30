using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>
///     Typed HTTP client for the Scaleway REST API.
///     Handles authentication, region routing, and JSON serialization.
/// </summary>
public sealed class ScalewayApiClient : IDisposable, IScalewayApiClient
{
    /// <summary>The largest page Scaleway list endpoints accept; asking for more is rejected.</summary>
    private const int MaxPageSize = 100;

    /// <summary>Runaway guard: 100 pages of 100 is far past any real deployment, so exceeding it means the API ignored paging.</summary>
    private const int MaxPages = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IScalewayDiagnosticsSink? _diagnostics;

    private readonly HttpClient _httpClient;

    /// <summary>
    ///     Whether <see cref="_httpClient" /> was built here. A caller-supplied client is not ours to dispose:
    ///     one from <c>IHttpClientFactory</c> is pooled and shared, so disposing it on our own teardown would
    ///     take down a handler other callers are still using.
    /// </summary>
    private readonly bool _ownsHttpClient;

    /// <summary>Builds the transport over an <see cref="HttpClient" /> it owns and disposes, configured from the credentials.</summary>
    /// <param name="credentials">Base address, secret key and the defaults a scope is built from.</param>
    /// <param name="productToken">
    ///     The caller's own <c>name/version</c> token, appended to the SDK's <c>User-Agent</c> so its traffic is
    ///     attributable to the application rather than only to this package. See <see cref="ScalewayUserAgent" />.
    /// </param>
    public ScalewayApiClient(ScalewayCredentialConfig credentials, ProductInfoHeaderValue? productToken = null)
    {
        _ownsHttpClient = true;
        _httpClient = new HttpClient { BaseAddress = new Uri(credentials.ApiUrl) };
        _httpClient.DefaultRequestHeaders.Add("X-Auth-Token", credentials.SecretKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ScalewayUserAgent.ApplyTo(_httpClient, productToken);

        // Opt-in: with SCW_DIAGNOSTICS_PATH set, uncategorized / bad-request errors are captured for generator
        // improvement. Unset (the default) means no sink and no file I/O.
        var diagnosticsPath = Environment.GetEnvironmentVariable("SCW_DIAGNOSTICS_PATH");
        _diagnostics = string.IsNullOrWhiteSpace(diagnosticsPath) ? null : new FileScalewayDiagnosticsSink(diagnosticsPath);
    }

    /// <summary>
    ///     Builds the transport over a caller-supplied <see cref="HttpClient" />, which must already carry the
    ///     base address and <c>X-Auth-Token</c> header. This is the seam for a long-running application: workloads
    ///     hand in a client from <c>IHttpClientFactory</c> so pooling and resilience policies apply, rather than
    ///     letting the SDK own a socket for the process lifetime.
    ///     <para>
    ///         The SDK's <c>User-Agent</c> product is added to the client unless it is already there, so traffic
    ///         through this seam identifies itself like traffic through the credential constructor does. Call
    ///         <see cref="ScalewayUserAgent.ApplyTo" /> with your own product token first to be named alongside it.
    ///     </para>
    /// </summary>
    public ScalewayApiClient(HttpClient httpClient) : this(httpClient, null)
    {
    }

    // The diagnostics sink stays internal: it exists to capture uncategorized errors and improve the generator,
    // not as consumer API.
    internal ScalewayApiClient(HttpClient httpClient, IScalewayDiagnosticsSink? diagnostics)
    {
        _httpClient = ScalewayUserAgent.ApplyTo(httpClient);
        _diagnostics = diagnostics;
    }

    /// <summary>Disposes the <see cref="HttpClient" /> only when this transport created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    ///     Lists resources of a given type, optionally filtered by tags, following pagination to the end.
    ///     Scaleway pages list results (20 per page by default, <see cref="MaxPageSize" /> at most), so a single
    ///     request returns a truncated view — and a find-or-create that only saw page 1 would miss an existing
    ///     resource and create a duplicate. A caller that sets <c>page</c> or <c>page_size</c> itself gets exactly
    ///     that page instead.
    /// </summary>
    public async Task<JsonElement[]> ListResourcesAsync(string apiPath, string region, IReadOnlyList<KeyValuePair<string, string>>? queryParams = null, CancellationToken cancellationToken = default)
    {
        if (queryParams is not null && queryParams.Any(parameter => parameter.Key is "page" or "page_size"))
        {
            var (requestedPage, _) = await GetPageAsync(apiPath, region, queryParams, cancellationToken);
            return requestedPage;
        }

        var collected = new List<JsonElement>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var pagedParams = new List<KeyValuePair<string, string>>(queryParams ?? [])
            {
                new("page", page.ToString(CultureInfo.InvariantCulture)),
                new("page_size", MaxPageSize.ToString(CultureInfo.InvariantCulture))
            };

            var (items, totalCount) = await GetPageAsync(apiPath, region, pagedParams, cancellationToken);
            collected.AddRange(items);

            // A short page is the last one; reaching total_count means everything has been collected.
            if (items.Length < MaxPageSize || collected.Count >= totalCount)
            {
                return collected.ToArray();
            }
        }

        throw new ScalewayApiException(
            $"Listing '{apiPath}' still returned full pages after {MaxPages} requests of {MaxPageSize} items. " +
            "The API is likely ignoring the page parameter; refusing to keep requesting rather than return a partial list."
        );
    }

    /// <summary>
    ///     Creates a resource via POST.
    /// </summary>
    public async Task<JsonElement> CreateResourceAsync(string apiPath, string region, object body, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(apiPath, region);
        var requestJson = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var response = await SendClassifyingConnectionFailuresAsync(() => _httpClient.PostAsync(url, content, cancellationToken));
        await EnsureSuccessAsync(response, requestJson, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     Updates a resource via PATCH.
    /// </summary>
    public async Task<JsonElement> UpdateResourceAsync(string apiPath, string region, string resourceId, object body, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl($"{apiPath}/{resourceId}", region);
        var requestJson = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
        var response = await SendClassifyingConnectionFailuresAsync(() => _httpClient.SendAsync(request, cancellationToken));
        await EnsureSuccessAsync(response, requestJson, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>PUTs to a collection path — Scaleway's idempotent set-style operations (e.g. RDB privileges).</summary>
    public async Task<JsonElement> PutResourceAsync(string apiPath, string region, object body, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(apiPath, region);
        var requestJson = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        var response = await SendClassifyingConnectionFailuresAsync(() => _httpClient.SendAsync(request, cancellationToken));
        await EnsureSuccessAsync(response, requestJson, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     Gets a single resource by ID.
    /// </summary>
    public async Task<JsonElement?> GetResourceAsync(string apiPath, string region, string resourceId, CancellationToken cancellationToken = default)
    {
        return await GetAsync($"{apiPath}/{resourceId}", region, cancellationToken);
    }

    /// <summary>
    ///     Generic GET against an arbitrary path. Used for endpoints that don't follow the
    ///     <c>{apiPath}/{id}</c> shape (e.g., Secret Manager's <c>/versions/latest_enabled/access</c>).
    ///     Returns <c>null</c> on 404; throws on other errors.
    /// </summary>
    public Task<JsonElement?> GetAsync(string apiPath, string region, CancellationToken cancellationToken = default)
    {
        return GetAsync(apiPath, region, null, cancellationToken);
    }

    /// <summary>
    ///     Generic GET carrying a query string — the action operations whose query parameters name the target
    ///     (Secret Manager's secrets-by-path access) or select an option (Kapsule's <c>redacted</c> kubeconfig).
    ///     Returns <c>null</c> on 404; throws on other errors.
    /// </summary>
    public async Task<JsonElement?> GetAsync(string apiPath, string region, IReadOnlyList<KeyValuePair<string, string>>? queryParams, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(apiPath, region, queryParams);
        var response = await SendClassifyingConnectionFailuresAsync(() => _httpClient.GetAsync(url, cancellationToken));

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, null, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     Deletes a resource by ID.
    /// </summary>
    public async Task DeleteResourceAsync(string apiPath, string region, string resourceId, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl($"{apiPath}/{resourceId}", region);
        var response = await SendClassifyingConnectionFailuresAsync(() => _httpClient.DeleteAsync(url, cancellationToken));
        await EnsureSuccessAsync(response, null, cancellationToken);
    }

    /// <summary>
    ///     A connection-level failure (socket reset, DNS blip, request timeout) never carried an HTTP status;
    ///     like a 5xx it says nothing about server-side effect, so it is classified transient and retried the
    ///     same way — the find-or-create re-check makes the retry safe either way. A genuine cancellation
    ///     (the caller's token) is never swallowed.
    /// </summary>
    private static async Task<HttpResponseMessage> SendClassifyingConnectionFailuresAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException exception)
        {
            throw new ScalewayTransientException($"Connection-level failure calling Scaleway: {exception.Message}");
        }
        catch (TaskCanceledException exception) when (exception.InnerException is TimeoutException)
        {
            throw new ScalewayTransientException("The Scaleway API request timed out.");
        }
    }

    /// <summary>Fetches one page, returning its items and the server's <c>total_count</c> (long.MaxValue if absent).</summary>
    private async Task<(JsonElement[] Items, long TotalCount)> GetPageAsync(string apiPath, string region, IReadOnlyList<KeyValuePair<string, string>>? queryParams, CancellationToken cancellationToken)
    {
        var url = BuildUrl(apiPath, region, queryParams);
        var response = await SendClassifyingConnectionFailuresAsync(() => _httpClient.GetAsync(url, cancellationToken));
        await EnsureSuccessAsync(response, null, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        // Scaleway list APIs return a single array property alongside total_count. Some responses carry more than
        // one array, so the first one wins — matching the shape the callers have always relied on.
        JsonElement[] items = [];
        var foundItems = false;
        var totalCount = long.MaxValue;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!foundItems && property.Value.ValueKind == JsonValueKind.Array)
            {
                items = property.Value.EnumerateArray().Select(e => e.Clone()).ToArray();
                foundItems = true;
            }
            else if (property.NameEquals("total_count") && property.Value.ValueKind == JsonValueKind.Number)
            {
                totalCount = property.Value.GetInt64();
            }
        }

        return (items, totalCount);
    }

    // Surfaces the Scaleway error body on failure — a 400/404 says WHY in its JSON, and swallowing it made every
    // failure a bare status code. Transient responses (5xx / request-timeout / too-many-requests) become a retryable
    // ScalewayTransientException; other failures throw a ScalewayApiException carrying the response body.
    private async Task EnsureSuccessAsync(HttpResponseMessage response, string? requestBodyJson, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var error = ScalewayError.Parse(body);
        // Prefer the parsed one-liner ("invalid_arguments: organization_id (required)") over the raw JSON blob.
        var message = $"Scaleway returned {status} ({response.StatusCode}): {error.Describe() ?? body}";

        if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new ScalewayTransientException(message);
        }

        // A 409 whose type is transient_state means the target resource is still provisioning ("resource is in
        // a transient state") — retry-later, not a duplicate. It must be classified before the already-exists
        // branch below, which also keys off 409.
        if (response.StatusCode == HttpStatusCode.Conflict && error.Type == "transient_state")
        {
            throw new ScalewayTransientException(message);
        }

        // A duplicate-name create is a 409 — the reliable, structured signal — or, for registry namespaces, a 400
        // whose message says "already exist(s)". Scaleway has no single error type for that latter case, so the
        // message match stays as a fallback: the parsed message where present, and the raw body as a safety net so
        // an unexpected shape can't regress the registry-collision adoption. Surfacing this as a distinct type lets
        // find-or-create adopt the existing resource — the lost-response race the retry can't see.
        var alreadyExists = error.Message?.Contains("already exist", StringComparison.OrdinalIgnoreCase) == true
                            || body.Contains("already exist", StringComparison.OrdinalIgnoreCase);
        if (response.StatusCode == HttpStatusCode.Conflict || alreadyExists)
        {
            throw new ScalewayResourceAlreadyExistsException(message, status, error.Type, error.Details);
        }

        // This is the actionable set: a request the API rejected outright. Capture it (if diagnostics are on) so a
        // developer or agent can see whether the generated client — hence the generator — needs correcting.
        CaptureDiagnostics(response, requestBodyJson, status, error);
        throw new ScalewayApiException(message, status, error.Type, error.Details);
    }

    private void CaptureDiagnostics(HttpResponseMessage response, string? requestBodyJson, int status, ScalewayError error)
    {
        if (_diagnostics is null)
        {
            return;
        }

        var details = error.Details
            .Select(detail => new ScalewayDiagnosticDetail(detail.ArgumentName, detail.Reason, ScalewayDiagnosticsRedactor.Redact(detail.HelpMessage)))
            .ToList();

        _diagnostics.Record(new ScalewayDiagnosticEvent(
                response.RequestMessage?.Method.Method ?? "",
                ScalewayDiagnosticsRedactor.Redact(response.RequestMessage?.RequestUri?.PathAndQuery) ?? "",
                status,
                status == 400 ? "bad_request" : "uncategorized",
                error.Type,
                details,
                RequestFieldNames(requestBodyJson)
            )
        );
    }

    // Only the field names, never the values — the names alone show what the request did or did not send (e.g.
    // organization_id absent when the API demanded it), with no risk of writing a secret.
    private static IReadOnlyList<string> RequestFieldNames(string? requestBodyJson)
    {
        if (string.IsNullOrWhiteSpace(requestBodyJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(requestBodyJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(property => property.Name).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string BuildUrl(string apiPath, string region, IReadOnlyList<KeyValuePair<string, string>>? queryParams = null)
    {
        var url = $"/{apiPath.TrimStart('/')}".Replace("{region}", region);

        if (queryParams is { Count: > 0 })
        {
            var query = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{query}";
        }

        return url;
    }
}
