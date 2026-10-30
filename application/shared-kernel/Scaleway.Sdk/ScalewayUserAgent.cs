using System.Net.Http.Headers;

namespace Scaleway.Sdk;

/// <summary>
///     The <c>User-Agent</c> this package sends: <c>Scaleway.Sdk-dotnet/{version}</c>, the same shape Scaleway's
///     own libraries use (<c>scaleway-sdk-go/vX.Y.Z</c>). .NET sends no <c>User-Agent</c> at all unless one is
///     set, so without this every request from this package is indistinguishable from an unnamed script — which
///     is what a provider deciding where to apply a rate limit has to fall back on.
///     <para>
///         The token names the package, never the application it is embedded in: a consumer's traffic must not
///         advertise whoever built the binary. A consumer that wants to be identifiable supplies its own product
///         token, which is appended — <c>Scaleway.Sdk-dotnet/{version} MyApp/2.3</c> — which is also how the
///         repository's own tooling identifies its non-production traffic.
///     </para>
/// </summary>
public static class ScalewayUserAgent
{
    private const string ProductName = "Scaleway.Sdk-dotnet";

    private static readonly ProductInfoHeaderValue SdkProduct = ProductToken(ProductName, typeof(ScalewayUserAgent).Assembly);

    /// <summary>
    ///     This package's product token, for a caller assembling a <c>User-Agent</c> by hand rather than through
    ///     <see cref="ApplyTo" />.
    /// </summary>
    public static string Value => SdkProduct.ToString();

    /// <summary>
    ///     Adds this package's product token to <paramref name="httpClient" />, then the caller's if there is one.
    ///     Each product is added only when no token of that name is present, so building several clients over one
    ///     <see cref="HttpClient" /> cannot stack up repeated copies of the same identity.
    /// </summary>
    /// <param name="httpClient">The client to identify. Its default request headers are modified in place.</param>
    /// <param name="productToken">
    ///     The caller's own <c>name/version</c> token, appended after this package's. Scaleway sees both, so an
    ///     application can be attributed separately from the library it calls through.
    /// </param>
    /// <returns>The same instance, so this can be used in a field initializer that owns the client.</returns>
    public static HttpClient ApplyTo(HttpClient httpClient, ProductInfoHeaderValue? productToken = null)
    {
        AddIfAbsent(httpClient, SdkProduct);

        if (productToken is not null)
        {
            AddIfAbsent(httpClient, productToken);
        }

        return httpClient;
    }

    /// <summary>
    ///     Builds a caller's token for <see cref="ApplyTo" /> from an assembly's own version, so an application
    ///     names itself without keeping a version constant in sync with the one it ships.
    /// </summary>
    /// <param name="productName">The name the caller is known by, e.g. its application or tool name.</param>
    /// <param name="assembly">The assembly whose informational version the token carries.</param>
    public static ProductInfoHeaderValue ProductToken(string productName, Assembly assembly)
    {
        return new ProductInfoHeaderValue(productName, FormatVersion(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
    }

    private static void AddIfAbsent(HttpClient httpClient, ProductInfoHeaderValue product)
    {
        var userAgent = httpClient.DefaultRequestHeaders.UserAgent;

        if (userAgent.Any(existing => string.Equals(existing.Product?.Name, product.Product?.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        userAgent.Add(product);
    }

    // The informational version is what shipped, so it cannot drift from the package the traffic came from the way
    // a hand-maintained constant would. Source Link appends the commit hash as build metadata ("1.0.0+9f1c…");
    // that identifies our build machine rather than the released version, so it is cut.
    internal static string FormatVersion(string? informationalVersion)
    {
        var version = informationalVersion?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }
}
