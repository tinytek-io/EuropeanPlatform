using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using SharedKernel.Integrations.BlobStorage;
using Xunit;

namespace SharedKernel.Tests.Integrations;

public sealed class S3BlobStorageClientTests
{
    private static readonly S3BlobStorageOptions Options = new("http://localhost:9000", "access-key", "secret-key", "fr-par", true);

    [Fact]
    public async Task UploadAsync_ShouldLeaveTheCallersStreamOpen()
    {
        // Arrange
        var s3Client = Substitute.For<IAmazonS3>();
        PutObjectRequest? sentRequest = null;
        s3Client.PutObjectAsync(Arg.Do<PutObjectRequest>(request => sentRequest = request), Arg.Any<CancellationToken>()).Returns(new PutObjectResponse());
        var blobStorageClient = new S3BlobStorageClient(s3Client, Options);
        using var stream = new MemoryStream([1, 2, 3]);

        // Act
        await blobStorageClient.UploadAsync("avatars", "avatar.png", "image/png", stream, CancellationToken.None);

        // Assert
        sentRequest.Should().NotBeNull();
        sentRequest.InputStream.Should().BeSameAs(stream);
        sentRequest.AutoCloseStream.Should().BeFalse();
    }

    [Fact]
    public async Task ListBlobNamesAsync_WhenListingSpansSeveralPages_ShouldReturnEveryPage()
    {
        // Arrange
        const string continuationToken = "page-2";
        var s3Client = Substitute.For<IAmazonS3>();
        s3Client.ListObjectsV2Async(Arg.Is<ListObjectsV2Request>(r => r.ContinuationToken == null), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = [new S3Object { Key = "keys/key-1.xml" }], IsTruncated = true, NextContinuationToken = continuationToken });
        s3Client.ListObjectsV2Async(Arg.Is<ListObjectsV2Request>(r => r.ContinuationToken == continuationToken), Arg.Any<CancellationToken>())
            .Returns(new ListObjectsV2Response { S3Objects = [new S3Object { Key = "keys/key-2.xml" }], IsTruncated = false });
        var blobStorageClient = new S3BlobStorageClient(s3Client, Options);

        // Act
        var blobNames = await blobStorageClient.ListBlobNamesAsync("data-protection", "keys/", CancellationToken.None);

        // Assert
        blobNames.Should().Equal("keys/key-1.xml", "keys/key-2.xml");
    }
}
