namespace SharedKernel.Integrations.BlobStorage;

public interface IBlobStorageClient
{
    Task UploadAsync(string bucketName, string blobName, string contentType, Stream stream, CancellationToken cancellationToken);

    Task<string[]> ListBlobNamesAsync(string bucketName, string prefix, CancellationToken cancellationToken);

    Task<(Stream Stream, string ContentType)?> DownloadAsync(string bucketName, string blobName, CancellationToken cancellationToken);

    string GetBlobUrl(string bucketName, string blobName);

    Task<bool> DeleteIfExistsAsync(string bucketName, string blobName, CancellationToken cancellationToken);

    Uri GetPresignedUri(string bucketName, string blobName, TimeSpan expiresIn);

    Task CreateBucketIfNotExistsAsync(string bucketName, bool publicRead, CancellationToken cancellationToken);
}
