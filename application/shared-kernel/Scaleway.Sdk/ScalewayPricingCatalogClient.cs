using System.Collections.Concurrent;
using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>
///     Reads the Scaleway Product Catalog, a public API that needs no authentication. Products are listed per
///     locality: a region holds regional products (managed databases), a zone holds zonal ones (Instances and
///     therefore Kubernetes nodes, Load Balancers, Public Gateways), and global products (Edge Services) belong to
///     neither. Responses are cached on disk in <c>~/.platformplatform/scaleway-pricing-cache-{locality}.json</c>
///     for 24 hours so repeated callers avoid the network round-trip; <c>SCW_PRICING_CACHE_DISABLED=1</c> turns
///     the disk cache off.
///     <para>
///         The catalog states a price per unit (<c>hour</c>, <c>node</c>, <c>ip</c>, <c>plan</c>, <c>gigabyte</c>,
///         ...) but not the period it is billed over: an Instance's price is per hour, an Edge Services plan's per
///         month. Turning a product into a monthly estimate is therefore the caller's job, product by product.
///     </para>
/// </summary>
public sealed class ScalewayPricingCatalogClient(HttpClient httpClient, string? cacheDirectory = null, bool? cacheDisabled = null) : IDisposable
{
    private const string DefaultCatalogBaseUrl = "https://api.scaleway.com";
    private const int PageSize = 100;

    // The global catalog alone spans more than a thousand products; a fetch that still sees full pages after this
    // many is reading an API that ignores the page parameter, and stops rather than loop.
    private const int MaxPages = 50;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
    private readonly bool _cacheDisabled = cacheDisabled ?? Environment.GetEnvironmentVariable("SCW_PRICING_CACHE_DISABLED") == "1";

    // Identified here rather than at each call site so a caller-supplied client is named too. The catalog needs no
    // credentials, which makes it the one Scaleway call that would otherwise be anonymous in every sense.
    private readonly HttpClient _httpClient = ScalewayUserAgent.ApplyTo(httpClient);

    // Process-level cache. The disk cache is the cross-process backing store; this layer earns its keep when
    // SCW_PRICING_CACHE_DISABLED=1 (CI) and the disk path is off.
    private readonly ConcurrentDictionary<string, IReadOnlyList<CatalogProduct>> _inMemoryCache = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Disposes the <see cref="HttpClient" /> only when this client created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    ///     Every product the catalog lists for a locality, retired ones included (a running resource may still be on
    ///     one). Served from memory, then from the 24-hour disk cache, then from Scaleway, paging to the end.
    /// </summary>
    /// <param name="locality">The region, zone, or the global catalog.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP requests.</param>
    /// <exception cref="HttpRequestException">The catalog could not be read and no cached copy exists.</exception>
    public async Task<IReadOnlyList<CatalogProduct>> GetProductsAsync(CatalogLocality locality, CancellationToken cancellationToken = default)
    {
        if (_inMemoryCache.TryGetValue(locality.Key, out var cached))
        {
            return cached;
        }

        var products = (_cacheDisabled ? null : TryReadDiskCache(locality.Key)) ?? await FetchAsync(locality, cancellationToken);
        _inMemoryCache[locality.Key] = products;
        return products;
    }

    private async Task<IReadOnlyList<CatalogProduct>> FetchAsync(CatalogLocality locality, CancellationToken cancellationToken)
    {
        var products = new List<CatalogProduct>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = $"/product-catalog/v2alpha1/public-catalog/products?{locality.QueryParameter}&page_size={PageSize}&page={page}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var pageDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var pageProducts = pageDocument.RootElement.TryGetProperty("products", out var productArray) && productArray.ValueKind == JsonValueKind.Array
                ? productArray.EnumerateArray().Select(ToCatalogProduct).ToArray()
                : [];
            products.AddRange(pageProducts);

            if (pageProducts.Length < PageSize)
            {
                if (!_cacheDisabled) TryWriteDiskCache(locality.Key, products);
                return products;
            }
        }

        throw new HttpRequestException($"The product catalog for '{locality.Key}' still returned full pages after {MaxPages} requests; refusing to return a partial list.");
    }

    private static CatalogProduct ToCatalogProduct(JsonElement product)
    {
        var locality = Property(product, "locality");
        var retailPrice = Property(Property(product, "price"), "retail_price");
        var unitOfMeasure = Property(product, "unit_of_measure");
        var price = Number(retailPrice, "units") + Number(retailPrice, "nanos") / 1_000_000_000m;
        var localityName = Text(locality, "zone") ?? Text(locality, "region") ?? (Property(locality, "global").ValueKind == JsonValueKind.True ? "global" : "");

        return new CatalogProduct(
            Text(product, "sku") ?? "", Text(product, "product_category") ?? "", Text(product, "product") ?? "", Text(product, "variant") ?? "",
            localityName, price, Text(unitOfMeasure, "unit") ?? "", unitOfMeasure.ValueKind == JsonValueKind.Object ? (long)Number(unitOfMeasure, "size") : 1, Text(product, "status") ?? ""
        );
    }

    // A product without a price, unit or locality omits the object entirely, so every lookup tolerates its absence.
    private static JsonElement Property(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    }

    private static string? Text(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static decimal Number(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : 0m;
    }

    private IReadOnlyList<CatalogProduct>? TryReadDiskCache(string localityKey)
    {
        var path = CacheFilePath(localityKey);
        if (!File.Exists(path)) return null;

        try
        {
            var cache = JsonSerializer.Deserialize<DiskCache>(File.ReadAllText(path), CacheJsonOptions);
            return cache is not null && DateTimeOffset.UtcNow - cache.UpdatedAt <= CacheTtl ? cache.Products : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache or a permission problem: pretend it is not there, and the fetch refreshes it.
            return null;
        }
    }

    private void TryWriteDiskCache(string localityKey, IReadOnlyList<CatalogProduct> products)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = CacheFilePath(localityKey);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new DiskCache(DateTimeOffset.UtcNow, products), CacheJsonOptions));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache writes are best-effort: never fail a caller because of disk problems.
        }
    }

    private string CacheFilePath(string localityKey)
    {
        return Path.Combine(_cacheDirectory, $"scaleway-pricing-cache-{localityKey}.json");
    }

    private static string ResolveBaseUrl()
    {
        return Environment.GetEnvironmentVariable("SCW_API_URL") ?? DefaultCatalogBaseUrl;
    }

    private static string DefaultCacheDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformplatform");
    }

    private sealed record DiskCache(DateTimeOffset UpdatedAt, IReadOnlyList<CatalogProduct> Products);
}

/// <summary>Where catalog products are listed: one region, one zone, or the global catalog.</summary>
/// <param name="Key">
///     Identifies the locality in cache file names (<c>region-fr-par</c>, <c>zone-fr-par-1</c>,
///     <c>global</c>).
/// </param>
/// <param name="QueryParameter">The catalog's filter for the locality.</param>
public sealed record CatalogLocality(string Key, string QueryParameter)
{
    /// <summary>Products with no region or zone, such as Edge Services.</summary>
    public static CatalogLocality Global { get; } = new("global", "global=true");

    /// <summary>Products that exist once per region, such as managed databases.</summary>
    public static CatalogLocality ForRegion(string region)
    {
        return new CatalogLocality($"region-{region}", $"region={Uri.EscapeDataString(region)}");
    }

    /// <summary>Products that exist per zone, such as Instances, Kubernetes nodes and Load Balancers.</summary>
    public static CatalogLocality ForZone(string zone)
    {
        return new CatalogLocality($"zone-{zone}", $"zone={Uri.EscapeDataString(zone)}");
    }
}

/// <summary>One priced line of Scaleway's public catalog.</summary>
/// <param name="Sku">Scaleway's identifier for the line (<c>/instance/server/play2_nano/fr-par-1</c>).</param>
/// <param name="Category">The product category (<c>Instance</c>, <c>Loadbalancer</c>, <c>Edge Services</c>).</param>
/// <param name="Product">The product within the category (<c>PLAY2-NANO</c>, <c>LB-S</c>, <c>Edge Services - Plan</c>).</param>
/// <param name="Variant">The catalog's verbose name, which embeds the locality (<c>PLAY2-NANO - fr-par-1</c>).</param>
/// <param name="Locality">The zone or region the line is priced in, or <c>global</c>.</param>
/// <param name="Price">Euros per <paramref name="UnitSize" /> <paramref name="Unit" />, before any commitment discount.</param>
/// <param name="Unit">What the price is per (<c>hour</c>, <c>node</c>, <c>ip</c>, <c>plan</c>, <c>gigabyte</c>, ...).</param>
/// <param name="UnitSize">How many units the price covers.</param>
/// <param name="Status">The line's lifecycle (<c>general_availability</c>, <c>retired</c>, ...).</param>
public sealed record CatalogProduct(
    string Sku,
    string Category,
    string Product,
    string Variant,
    string Locality,
    decimal Price,
    string Unit,
    long UnitSize,
    string Status
);
