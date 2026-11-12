using System.Collections.Concurrent;
using System.Text.Json;
using Scaleway.Sdk.ProductCatalog.V2Alpha1;

namespace Scaleway.Sdk;

/// <summary>
///     Reads the Scaleway Product Catalog, a public API that needs no authentication, through the generated
///     <see cref="ProductCatalogApi" />. Products are listed per locality: a region holds regional products (managed
///     databases), a zone holds zonal ones (Instances and therefore Kubernetes nodes, Load Balancers, Public
///     Gateways), and global products (Edge Services) belong to neither. Responses are cached on disk in
///     <c>~/.platformplatform/scaleway-pricing-cache-{locality}.json</c> for 24 hours so repeated callers avoid the
///     network round-trip; <c>SCW_PRICING_CACHE_DISABLED=1</c> turns the disk cache off.
///     <para>
///         The catalog states a price per unit (<c>hour</c>, <c>node</c>, <c>ip</c>, <c>plan</c>, <c>gigabyte</c>,
///         ...) but not the period it is billed over: an Instance's price is per hour, an Edge Services plan's per
///         month. Turning a product into a monthly estimate is therefore the caller's job, product by product.
///     </para>
/// </summary>
public sealed class ScalewayPricingCatalogClient(HttpClient httpClient, string? cacheDirectory = null, bool? cacheDisabled = null) : IDisposable
{
    private const decimal NanosPerUnit = 1_000_000_000m;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly string _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
    private readonly bool _cacheDisabled = cacheDisabled ?? Environment.GetEnvironmentVariable("SCW_PRICING_CACHE_DISABLED") == "1";

    // Process-level cache. The disk cache is the cross-process backing store; this layer earns its keep when
    // SCW_PRICING_CACHE_DISABLED=1 (CI) and the disk path is off.
    private readonly ConcurrentDictionary<string, IReadOnlyList<CatalogProduct>> _inMemoryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Only a client built here is disposed here. A caller-supplied one may be pooled and shared — disposing
    ///     it on our teardown would take down a handler other callers still hold.
    /// </summary>
    private readonly bool _ownsHttpClient;

    // The transport pages to the end of the catalog and names the SDK in the User-Agent, a caller-supplied client
    // included. It sends no X-Auth-Token unless the caller's client already carries one: the catalog needs none.
    private readonly ProductCatalogApi _productCatalog = new(new ScalewayApiClient(httpClient));

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
            httpClient.Dispose();
        }
    }

    /// <summary>
    ///     Every product the catalog lists for a locality, retired ones included (a running resource may still be on
    ///     one). Served from memory, then from the 24-hour disk cache, then from Scaleway, paging to the end.
    /// </summary>
    /// <param name="locality">The region, zone, or the global catalog.</param>
    /// <param name="cancellationToken">Passed to the underlying HTTP requests.</param>
    /// <exception cref="HttpRequestException">
    ///     The catalog could not be read and no cached copy exists. The Scaleway error behind it is the inner
    ///     exception.
    /// </exception>
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
        IReadOnlyList<PublicCatalogProduct> catalogProducts;
        try
        {
            // No status filter: Scaleway then returns every lifecycle stage up to and including retired.
            catalogProducts = await _productCatalog.ListPublicCatalogProductsAsync(locality.Filter, cancellationToken);
        }
        catch (Exception exception) when (exception is ScalewayApiException or ScalewayTransientException)
        {
            // An unreadable catalog is an unknown price to the callers, who catch it as an HTTP failure.
            throw new HttpRequestException($"The product catalog for '{locality.Key}' could not be read: {exception.Message}", exception);
        }

        var products = catalogProducts.Select(ToCatalogProduct).ToArray();
        if (!_cacheDisabled) TryWriteDiskCache(locality.Key, products);
        return products;
    }

    // A product without a price, unit or locality omits the object or sends null, so every lookup tolerates its
    // absence: no price reads as zero, and no unit of measure as a single unit.
    private static CatalogProduct ToCatalogProduct(PublicCatalogProduct product)
    {
        var retailPrice = product.Price?.RetailPrice;
        var price = (retailPrice?.Units ?? 0) + (retailPrice?.Nanos ?? 0) / NanosPerUnit;
        var locality = product.Locality?.Zone ?? product.Locality?.Region ?? (product.Locality?.Global == true ? "global" : "");
        var unitSize = product.UnitOfMeasure is null ? 1 : product.UnitOfMeasure.Size ?? 0;

        return new CatalogProduct(
            product.Sku ?? "", product.ProductCategory ?? "", product.Product ?? "", product.Variant ?? "", locality, price,
            product.UnitOfMeasure?.Unit?.Value ?? "", unitSize, product.Status?.Value ?? ""
        );
    }

    private IReadOnlyList<CatalogProduct>? TryReadDiskCache(string localityKey)
    {
        var path = CacheFilePath(localityKey);
        if (!File.Exists(path)) return null;

        try
        {
            var cache = JsonSerializer.Deserialize(File.ReadAllText(path), ScalewaySdkJsonContext.Default.DiskCache);
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
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new DiskCache(DateTimeOffset.UtcNow, products), ScalewaySdkJsonContext.Default.DiskCache));
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
        return Environment.GetEnvironmentVariable("SCW_API_URL") ?? ScalewayApiClient.DefaultApiUrl;
    }

    private static string DefaultCacheDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformplatform");
    }

    internal sealed record DiskCache(DateTimeOffset UpdatedAt, IReadOnlyList<CatalogProduct> Products);
}

/// <summary>Where catalog products are listed: one region, one zone, or the global catalog.</summary>
public sealed record CatalogLocality
{
    private CatalogLocality(string key, ListPublicCatalogProductsRequest filter)
    {
        Key = key;
        Filter = filter;
    }

    /// <summary>
    ///     Identifies the locality in cache file names (<c>region-fr-par</c>, <c>zone-fr-par-1</c>,
    ///     <c>global</c>).
    /// </summary>
    public string Key { get; }

    /// <summary>The catalog request for the locality, which sets exactly one of global, region and zone.</summary>
    internal ListPublicCatalogProductsRequest Filter { get; }

    /// <summary>Products with no region or zone, such as Edge Services.</summary>
    public static CatalogLocality Global { get; } = new("global", new ListPublicCatalogProductsRequest { Global = true });

    /// <summary>Products that exist once per region, such as managed databases.</summary>
    public static CatalogLocality ForRegion(string region)
    {
        return new CatalogLocality($"region-{region}", new ListPublicCatalogProductsRequest { Region = region });
    }

    /// <summary>Products that exist per zone, such as Instances, Kubernetes nodes and Load Balancers.</summary>
    public static CatalogLocality ForZone(string zone)
    {
        return new CatalogLocality($"zone-{zone}", new ListPublicCatalogProductsRequest { Zone = zone });
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
