using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

public sealed class ScalewayPricingCatalogClientTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"scaleway-pricing-cache-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, true);
        }
    }

    [Fact]
    public async Task GetProductsAsync_WhenTheCatalogSpansPages_ShouldReadEveryPageForTheLocality()
    {
        // Arrange
        var fullPage = string.Join(",", Enumerable.Range(1, 100).Select(number => Product($"/instance/server/type{number}/fr-par-1", "Instance", $"TYPE{number}", 0, 1_000_000, "hour")));
        var lastPage = Product("/network/loadbalancer/loadbalancer-s/par1", "Loadbalancer", "LB-S", 0, 23_000_000, "node") + ", " + """{ "sku": "/unpriced", "product_category": "Instance", "product": "UNPRICED", "variant": "UNPRICED - fr-par-1", "locality": { "zone": "fr-par-1" }, "status": "general_availability" }""";
        var handler = new PagedCatalogHandler([$$"""{ "products": [{{fullPage}}] }""", $$"""{ "products": [{{lastPage}}] }"""]);
        using var client = new ScalewayPricingCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") }, cacheDisabled: true);

        // Act
        var products = await client.GetProductsAsync(CatalogLocality.ForZone("fr-par-1"));

        // Assert
        products.Should().HaveCount(102);
        products.Should().ContainEquivalentOf(new CatalogProduct("/network/loadbalancer/loadbalancer-s/par1", "Loadbalancer", "LB-S", "LB-S - fr-par-1", "fr-par-1", 0.023m, "node", 1, "general_availability"));
        products.Single(product => product.Product == "UNPRICED").Price.Should().Be(0m);
        handler.RequestedUris.Should().HaveCount(2).And.OnlyContain(uri => uri.AbsolutePath == "/product-catalog/v2alpha1/public-catalog/products");
        handler.RequestedQueries.Should().OnlyContain(query => query["zone"] == "fr-par-1" && query["page_size"] == "100" && query["region"] == null && query["global"] == null);
        handler.RequestedQueries.Select(query => query["page"]).Should().Equal("1", "2");
    }

    [Fact]
    public async Task GetProductsAsync_WhenTheLastPageIsFull_ShouldStopAtTheTotalCount()
    {
        // Arrange
        var fullPage = string.Join(",", Enumerable.Range(1, 100).Select(number => Product($"/sku/{number}", "Instance", $"TYPE{number}", 0, 1, "hour")));
        var handler = new PagedCatalogHandler([$$"""{ "products": [{{fullPage}}], "total_count": 100 }"""]);
        using var client = new ScalewayPricingCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") }, cacheDisabled: true);

        // Act
        var products = await client.GetProductsAsync(CatalogLocality.ForRegion("fr-par"));

        // Assert
        products.Should().HaveCount(100);
        handler.RequestedQueries.Should().ContainSingle().Which["region"].Should().Be("fr-par");
    }

    [Fact]
    public async Task GetProductsAsync_WhenTheCatalogIsEmpty_ShouldReturnNoProducts()
    {
        // Arrange
        var handler = new PagedCatalogHandler(["""{ "total_count": 0 }"""]);
        using var client = new ScalewayPricingCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") }, cacheDisabled: true);

        // Act
        var products = await client.GetProductsAsync(CatalogLocality.ForZone("nl-ams-1"));

        // Assert
        products.Should().BeEmpty();
        handler.RequestedUris.Should().ContainSingle();
    }

    [Fact]
    public async Task GetProductsAsync_WhenProductsAreRetiredOrHaveNoUnitOfMeasure_ShouldMapThemAndSendNoStatusFilter()
    {
        // Arrange
        const string retired = """{ "sku": "/storage/hdd", "product_category": "Block Storage", "product": "Scalable HDD", "variant": "Scalable HDD - global", "locality": { "global": true }, "price": { "retail_price": { "currency_code": "EUR", "units": 0, "nanos": 10000000 } }, "unit_of_measure": { "unit": "gigabyte", "size": 25 }, "status": "retired" }""";
        const string withoutUnit = """{ "sku": "/free/tier", "product_category": "Generative APIs", "product": "Free Tier", "variant": "Free Tier - global", "locality": { "global": true }, "unit_of_measure": null, "status": "general_availability" }""";
        var handler = new PagedCatalogHandler([$$"""{ "products": [{{retired}}, {{withoutUnit}}], "total_count": 2 }"""]);
        using var client = new ScalewayPricingCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") }, cacheDisabled: true);

        // Act
        var products = await client.GetProductsAsync(CatalogLocality.Global);

        // Assert
        products.Should().BeEquivalentTo([
                new CatalogProduct("/storage/hdd", "Block Storage", "Scalable HDD", "Scalable HDD - global", "global", 0.01m, "gigabyte", 25, "retired"),
                new CatalogProduct("/free/tier", "Generative APIs", "Free Tier", "Free Tier - global", "global", 0m, "", 1, "general_availability")
            ]
        );
        handler.RequestedQueries.Should().ContainSingle().Which["status"].Should().BeNull();
    }

    [Fact]
    public async Task GetProductsAsync_WhenAskedTwice_ShouldServeTheSecondFromMemory()
    {
        // Arrange
        var handler = new PagedCatalogHandler([$$"""{ "products": [{{Product("/edge/plan", "Edge Services", "Edge Services - Plan", 12, 990_000_000, "plan")}}] }"""]);
        using var client = new ScalewayPricingCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") }, cacheDisabled: true);

        // Act
        await client.GetProductsAsync(CatalogLocality.Global);
        var products = await client.GetProductsAsync(CatalogLocality.Global);

        // Assert
        products.Should().ContainSingle().Which.Price.Should().Be(12.99m);
        handler.RequestedQueries.Should().ContainSingle().Which["global"].Should().Be("true");
    }

    [Fact]
    public async Task GetProductsAsync_WhenTheDiskCacheIsFresh_ShouldServeAnotherClientFromDisk()
    {
        // Arrange
        var page = $$"""{ "products": [{{Product("/edge/plan", "Edge Services", "Edge Services - Plan", 12, 990_000_000, "plan")}}] }""";
        var firstHandler = new PagedCatalogHandler([page]);
        var secondHandler = new PagedCatalogHandler([page]);
        using var firstClient = new ScalewayPricingCatalogClient(new HttpClient(firstHandler) { BaseAddress = new Uri("https://api.scaleway.com") }, _cacheDirectory, false);
        using var secondClient = new ScalewayPricingCatalogClient(new HttpClient(secondHandler) { BaseAddress = new Uri("https://api.scaleway.com") }, _cacheDirectory, false);

        // Act
        await firstClient.GetProductsAsync(CatalogLocality.Global);
        var products = await secondClient.GetProductsAsync(CatalogLocality.Global);

        // Assert
        File.Exists(Path.Combine(_cacheDirectory, "scaleway-pricing-cache-global.json")).Should().BeTrue();
        products.Should().ContainSingle().Which.Price.Should().Be(12.99m);
        firstHandler.RequestedUris.Should().ContainSingle();
        secondHandler.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProductsAsync_WhenTheDiskCacheIsDisabled_ShouldNeitherWriteNorReadIt()
    {
        // Arrange
        var page = $$"""{ "products": [{{Product("/edge/plan", "Edge Services", "Edge Services - Plan", 12, 990_000_000, "plan")}}] }""";
        var firstHandler = new PagedCatalogHandler([page]);
        var secondHandler = new PagedCatalogHandler([page]);
        using var firstClient = new ScalewayPricingCatalogClient(new HttpClient(firstHandler) { BaseAddress = new Uri("https://api.scaleway.com") }, _cacheDirectory, true);
        using var secondClient = new ScalewayPricingCatalogClient(new HttpClient(secondHandler) { BaseAddress = new Uri("https://api.scaleway.com") }, _cacheDirectory, true);

        // Act
        await firstClient.GetProductsAsync(CatalogLocality.Global);
        await secondClient.GetProductsAsync(CatalogLocality.Global);

        // Assert
        Directory.Exists(_cacheDirectory).Should().BeFalse();
        firstHandler.RequestedUris.Should().ContainSingle();
        secondHandler.RequestedUris.Should().ContainSingle();
    }

    [Fact]
    public async Task GetProductsAsync_WhenTheCatalogCannotBeRead_ShouldThrowHttpRequestException()
    {
        // Arrange
        var handler = new PagedCatalogHandler([], HttpStatusCode.ServiceUnavailable);
        using var client = new ScalewayPricingCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.com") }, cacheDisabled: true);

        // Act
        HttpRequestException? exception = null;
        try
        {
            await client.GetProductsAsync(CatalogLocality.Global);
        }
        catch (HttpRequestException caught)
        {
            exception = caught;
        }

        // Assert
        exception.Should().NotBeNull("callers catch an unreadable catalog as an HTTP failure");
        exception.InnerException.Should().BeOfType<ScalewayTransientException>();
    }

    private static string Product(string sku, string category, string product, long units, long nanos, string unit)
    {
        return $$"""{ "sku": "{{sku}}", "product_category": "{{category}}", "product": "{{product}}", "variant": "{{product}} - fr-par-1", "locality": { "zone": "fr-par-1" }, "price": { "retail_price": { "currency_code": "EUR", "units": {{units}}, "nanos": {{nanos}} } }, "unit_of_measure": { "unit": "{{unit}}", "size": 1 }, "status": "general_availability" }""";
    }

    private sealed class PagedCatalogHandler(string[] pages, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        public IEnumerable<NameValueCollection> RequestedQueries => RequestedUris.Select(uri => HttpUtility.ParseQueryString(uri.Query));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var page = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
            var body = page <= pages.Length ? pages[page - 1] : """{ "products": [] }""";
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
