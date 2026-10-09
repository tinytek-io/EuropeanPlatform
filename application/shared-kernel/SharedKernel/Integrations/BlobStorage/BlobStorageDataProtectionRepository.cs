using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

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
    private const string BucketName = "data-protection";
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
            elements.Add(XElement.Load(stream));
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
}
