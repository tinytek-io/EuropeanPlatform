using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using Scaleway.Sdk;
using Xunit;

namespace SharedKernel.Tests.Scaleway;

public sealed class ScalewayPricingCatalogClientTests
{
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
        handler.RequestedUris.Should().HaveCount(2).And.OnlyContain(uri => uri.Query.Contains("zone=fr-par-1"));
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
        handler.RequestedUris.Should().ContainSingle().Which.Query.Should().Contain("global=true");
    }

    private static string Product(string sku, string category, string product, long units, long nanos, string unit)
    {
        return $$"""{ "sku": "{{sku}}", "product_category": "{{category}}", "product": "{{product}}", "variant": "{{product}} - fr-par-1", "locality": { "zone": "fr-par-1" }, "price": { "retail_price": { "currency_code": "EUR", "units": {{units}}, "nanos": {{nanos}} } }, "unit_of_measure": { "unit": "{{unit}}", "size": 1 }, "status": "general_availability" }""";
    }

    private sealed class PagedCatalogHandler(string[] pages) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var page = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
            var body = page <= pages.Length ? pages[page - 1] : """{ "products": [] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
