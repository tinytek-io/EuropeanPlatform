using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>
///     Reads the Scaleway Product Catalog — a public API that needs no authentication — and resolves an hourly
///     price for a node type. Catalog responses are cached on disk in
///     <c>~/.platformplatform/scaleway-pricing-cache-{region}.json</c> with a 24h TTL so repeated callers avoid
///     the network round-trip. The cache can be disabled by setting <c>SCW_PRICING_CACHE_DISABLED=1</c>.
///     <para>
///         Cost figures are a product concern rather than a provisioning one, which is why the catalog lives in
///         the SDK: turning a catalog price into a per-resource estimate is the caller's job.
///     </para>
/// </summary>
public sealed class ScalewayPricingCatalogClient(HttpClient httpClient, string? cacheDirectory = null, bool? cacheDisabled = null) : IDisposable
{
    private const string DefaultCatalogBaseUrl = "https://api.scaleway.com";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly string _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
    private readonly bool _cacheDisabled = cacheDisabled ?? Environment.GetEnvironmentVariable("SCW_PRICING_CACHE_DISABLED") == "1";

    // Identified here rather than at each call site so a caller-supplied client is named too. The catalog needs no
    // credentials, which makes it the one Scaleway call that would otherwise be anonymous in every sense.
    private readonly HttpClient _httpClient = ScalewayUserAgent.ApplyTo(httpClient);

    // Process-level cache for the catalog. The disk cache is the cross-process backing store; this
    // layer earns its keep when SCW_PRICING_CACHE_DISABLED=1 (CI / E2E tests) and the disk path is off.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, CatalogProduct>> _inMemoryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Only a client built here is disposed here. A caller-supplied one may be pooled and shared — disposing
    ///     it on our teardown would take down a handler other callers still hold.
    /// </summary>
    private readonly bool _ownsHttpClient;

    /// <summary>
    ///     Builds a client owning its own <see cref="HttpClient" />, which it disposes. Use the overload taking one to
    ///     share a pooled handler.
    /// </summary>
    public ScalewayPricingCatalogClient() : this(new HttpClient { BaseAddress = new Uri(ResolveBaseUrl()) })
    {
        _ownsHttpClient = true;
    }

    /// <summary>
    ///     Set when a catalog fetch came back empty (API unreachable and no cache): estimates against it are a
    ///     meaningless €0, and a budget gate must be able to tell "free" from "unpriceable".
    /// </summary>
    public bool CatalogUnavailable { get; private set; }

    /// <summary>Disposes the <see cref="HttpClient" /> only when this client created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    ///     The region's price list, keyed by catalog product name. Served from memory, then from the 24-hour disk
    ///     cache, then from Scaleway. An unreachable API with no cache yields an empty catalog and sets
    ///     <see cref="CatalogUnavailable" /> rather than throwing.
    /// </summary>
    /// <param name="region">Prices differ per region, so the catalog is fetched and cached per region.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP request.</param>
    public async Task<IReadOnlyDictionary<string, CatalogProduct>> GetCatalogAsync(ScalewayRegion region, CancellationToken cancellationToken = default)
    {
        var regionKey = region.ToApiString();

        if (_inMemoryCache.TryGetValue(regionKey, out var cached))
        {
            return cached;
        }

        var fromDisk = _cacheDisabled ? null : TryReadDiskCache(regionKey);
        if (fromDisk is not null)
        {
            _inMemoryCache[regionKey] = fromDisk;
            return fromDisk;
        }

        var fetched = await FetchCatalogAsync(regionKey, cancellationToken);
        _inMemoryCache[regionKey] = fetched;

        // The real catalog has hundreds of products, so an empty result means the fetch failed outright (and no
        // cache existed). Remember that: an estimate computed against it is a meaningless €0, and the budget gate
        // must know the difference between "free" and "unpriceable".
        if (fetched.Count == 0)
        {
            CatalogUnavailable = true;
        }

        if (!_cacheDisabled && fetched.Count > 0)
        {
            TryWriteDiskCache(regionKey, fetched);
        }

        return fetched;
    }

    /// <summary>The standalone hourly price for a node type, or <c>0</c> when the catalog does not list it.</summary>
    /// <param name="catalog">A catalog from <see cref="GetCatalogAsync" />.</param>
    /// <param name="nodeType">Scaleway's node type (<c>DB-DEV-S</c>), matched as a substring of the verbose catalog name.</param>
    public static decimal FindPrice(IReadOnlyDictionary<string, CatalogProduct> catalog, string nodeType)
    {
        // The real catalog names RDB/Redis variants verbosely ("Database Management DB-DEV-S - fr-par"), so the node
        // type is a substring, not the key. Multi-AZ variants are skipped here — this is the standalone price.
        var match = catalog.Values.FirstOrDefault(product =>
            product.Variant.Contains(nodeType, StringComparison.OrdinalIgnoreCase) &&
            !product.Variant.Contains("Multi-AZ", StringComparison.OrdinalIgnoreCase)
        );
        return match?.HourlyPrice ?? 0m;
    }

    // High-availability RDB is a dedicated Multi-AZ catalog variant ("Database management Multi-AZ DB-GP-S - fr-par")
    // priced in its own right — not simply double the standalone (e.g. DB-DEV-S is €0.002/hr standalone, €0.0075/hr
    // Multi-AZ). Use that price directly; fall back to 2x the standalone only when the catalog has no Multi-AZ entry.
    /// <summary>
    ///     The Multi-AZ hourly price for a node type, falling back to twice the standalone when the catalog lists no
    ///     Multi-AZ variant.
    /// </summary>
    /// <param name="catalog">A catalog from <see cref="GetCatalogAsync" />.</param>
    /// <param name="nodeType">Scaleway's node type (<c>DB-GP-S</c>), matched as a substring of the verbose catalog name.</param>
    public static decimal FindMultiAzPrice(IReadOnlyDictionary<string, CatalogProduct> catalog, string nodeType)
    {
        var multiAz = catalog.Values.FirstOrDefault(product =>
            product.Variant.Contains(nodeType, StringComparison.OrdinalIgnoreCase) &&
            product.Variant.Contains("Multi-AZ", StringComparison.OrdinalIgnoreCase)
        );
        return multiAz?.HourlyPrice ?? FindPrice(catalog, nodeType) * 2m;
    }

    private async Task<IReadOnlyDictionary<string, CatalogProduct>> FetchCatalogAsync(string regionKey, CancellationToken cancellationToken)
    {
        // The catalog is paged (~400 products today) and RDB/Redis variants live past the first page, so walk every
        // page until a short (last) page. Without this the RDB/Redis price lookups always missed and estimated €0.
        const int pageSize = 100;
        const int maxPages = 20;
        var result = new Dictionary<string, CatalogProduct>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"/product-catalog/v2alpha1/public-catalog/products?region={regionKey}&page_size={pageSize}&page={page}";
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Fail-soft: keep whatever pages we already have (empty on page 1) so the caller still proceeds.
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageCount = 0;
            foreach (var product in products.EnumerateArray())
            {
                pageCount++;
                var variant = product.TryGetProperty("variant", out var v) ? v.GetString() : null;
                if (variant is null) continue;

                var hourlyPrice = 0m;
                if (product.TryGetProperty("price", out var price) &&
                    price.TryGetProperty("retail_price", out var retailPrice))
                {
                    var units = retailPrice.TryGetProperty("units", out var u) ? u.GetInt64() : 0;
                    var nanos = retailPrice.TryGetProperty("nanos", out var n) ? n.GetInt64() : 0;
                    hourlyPrice = units + nanos / 1_000_000_000m;
                }

                result[variant] = new CatalogProduct(variant, hourlyPrice);
            }

            if (pageCount < pageSize)
            {
                break;
            }
        }

        return result;
    }

    private IReadOnlyDictionary<string, CatalogProduct>? TryReadDiskCache(string regionKey)
    {
        var path = CacheFilePath(regionKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("updated_at", out var updatedAtProp) ||
                !DateTimeOffset.TryParse(updatedAtProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - updatedAt > CacheTtl)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("products", out var products))
            {
                return null;
            }

            var result = new Dictionary<string, CatalogProduct>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in products.EnumerateObject())
            {
                if (decimal.TryParse(product.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                {
                    result[product.Name] = new CatalogProduct(product.Name, price);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt cache or permission problem: pretend it isn't there. The fetch path will refresh.
            return null;
        }
    }

    private void TryWriteDiskCache(string regionKey, IReadOnlyDictionary<string, CatalogProduct> catalog)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            var products = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (variant, product) in catalog)
            {
                products[variant] = product.HourlyPrice.ToString(CultureInfo.InvariantCulture);
            }

            var payload = new
            {
                updated_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                products
            };

            var path = CacheFilePath(regionKey);
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(payload));
            File.Move(tempPath, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache writes are best-effort — never fail a caller because of disk problems.
        }
    }

    private string CacheFilePath(string regionKey)
    {
        return Path.Combine(_cacheDirectory, $"scaleway-pricing-cache-{regionKey}.json");
    }

    private static string ResolveBaseUrl()
    {
        return Environment.GetEnvironmentVariable("SCW_API_URL") ?? DefaultCatalogBaseUrl;
    }

    private static string DefaultCacheDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformplatform");
    }
}

/// <summary>One priced line of Scaleway's public catalog.</summary>
/// <param name="Variant">
///     The catalog's own verbose product name ("Database Management DB-DEV-S - fr-par"), which embeds
///     the node type and region.
/// </param>
/// <param name="HourlyPrice">Price per hour in euros, before any commitment discount.</param>
public sealed record CatalogProduct(string Variant, decimal HourlyPrice);
