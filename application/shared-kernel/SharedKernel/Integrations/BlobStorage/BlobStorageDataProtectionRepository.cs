using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using SharedKernel.Configuration;

namespace SharedKernel.Integrations.BlobStorage;

/// <summary>
///     Persists the Data Protection keyring in Object Storage so every API and every instance shares one
///     keyring. Required in cloud: antiforgery tokens are minted by the service that serves the SPA page
///     (main-api, via the module-federated shell) and validated by the service the API call targets
///     (found live: signup 400ed because each container held its own ephemeral keys). One immutable blob per
///     key mirrors the framework's file-per-key layout, so concurrent rotation by two instances writes two
///     different blobs instead of racing on one document.
/// </summary>
public sealed class BlobStorageDataProtectionRepository(IBlobStorageClient blobStorageClient) : IXmlRepository
{
    public const string BucketName = "data-protection";
    private const string BlobPrefix = "keys/";

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        // The Data Protection key manager is a synchronous API; it loads the keyring once at startup and
        // after rotations, so sync-over-async here is rare and off the request path.
        var blobNames = blobStorageClient.ListBlobNamesAsync(BucketName, BlobPrefix, CancellationToken.None).GetAwaiter().GetResult();

        var elements = new List<XElement>();
        foreach (var blobName in blobNames)
        {
            var download = blobStorageClient.DownloadAsync(BucketName, blobName, CancellationToken.None).GetAwaiter().GetResult();
            if (download is null) continue;

            using var stream = download.Value.Stream;
            var element = XElement.Load(stream);
            EnsureEncryptedByPlatform(element, blobName);
            elements.Add(element);
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var stream = new MemoryStream();
        element.Save(stream);
        stream.Position = 0;
        blobStorageClient.UploadAsync(BucketName, $"{BlobPrefix}{friendlyName}.xml", "application/xml", stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    // Only keys the platform encrypted are trusted. The storage identity is shared across workloads,
    // and the framework accepts an unencrypted key or one naming any decryptor type, so a key planted
    // by anyone with bucket write access would otherwise become a key the whole platform trusts.
    // Refusing to load the ring fails loud instead. Revocation elements carry no secret.
    private static void EnsureEncryptedByPlatform(XElement element, string blobName)
    {
        if (element.Name.LocalName != "key") return;

        var platformDecryptorPrefix = $"{typeof(AesGcmXmlDecryptor).FullName},";
        // Matched by local name: the framework writes <encryptedSecret> in its own XML namespace
        // (http://schemas.asp.net/2015/03/dataProtection), so a plain XName finds none.
        var encryptedSecrets = element.Descendants().Where(descendant => descendant.Name.LocalName == "encryptedSecret").ToArray();
        var encryptedByPlatform = encryptedSecrets.Length > 0 &&
                                  encryptedSecrets.All(secret => secret.Attribute("decryptorType")?.Value.StartsWith(platformDecryptorPrefix, StringComparison.Ordinal) == true) &&
                                  element.Descendants().All(descendant => descendant.Name.LocalName != "masterKey");
        if (!encryptedByPlatform)
        {
            throw new InvalidOperationException($"The Data Protection key '{blobName}' is not encrypted with the platform keyring key, so the keyring is not loaded.");
        }
    }
}
