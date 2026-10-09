using SharedKernel.Integrations.BlobStorage;
using Xunit;

namespace SharedKernel.Tests.Configuration;

public class S3BlobStorageOptionsTests
{
    [Fact]
    public void Parse_WithoutBucketPrefix_DefaultsToEmpty()
    {
        var options = S3BlobStorageOptions.Parse(
            "ServiceUrl=http://localhost:9000;AccessKey=key;SecretKey=secret;ForcePathStyle=true;Region=fr-par");

        Assert.Equal("", options.BucketPrefix);
        Assert.Equal("avatars", options.ResolveBucketName("avatars"));
    }

    [Fact]
    public void Parse_WithBucketPrefix_ResolvesPhysicalBucketNames()
    {
        var options = S3BlobStorageOptions.Parse(
            "ServiceUrl=https://s3.fr-par.scw.cloud;AccessKey=key;SecretKey=secret;ForcePathStyle=false;Region=fr-par;BucketPrefix=pp-stage-");

        Assert.Equal("pp-stage-", options.BucketPrefix);
        Assert.Equal("pp-stage-avatars", options.ResolveBucketName("avatars"));
        Assert.Equal("pp-stage-data-protection", options.ResolveBucketName("data-protection"));
    }

    [Fact]
    public void Parse_IgnoresUnknownEntries()
    {
        // Forward compatibility: a newer environment may append entries this version does not know.
        var options = S3BlobStorageOptions.Parse(
            "ServiceUrl=http://localhost:9000;AccessKey=key;SecretKey=secret;Region=fr-par;SomeFutureEntry=value");

        Assert.Equal("http://localhost:9000", options.ServiceUrl);
    }

    [Fact]
    public void Parse_WithoutRegionOnRegionlessHost_Throws()
    {
        // The region can be derived from hosts like s3.fr-par.scw.cloud, but not from localhost.
        Assert.Throws<InvalidOperationException>(
            () => S3BlobStorageOptions.Parse("ServiceUrl=http://localhost:9000;AccessKey=key;SecretKey=secret"));
    }

    [Fact]
    public void Parse_WithoutRegion_DerivesItFromServiceUrlHost()
    {
        var options = S3BlobStorageOptions.Parse(
            "ServiceUrl=https://s3.fr-par.scw.cloud;AccessKey=key;SecretKey=secret");

        Assert.Equal("fr-par", options.Region);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AccessKey=key;SecretKey=secret")]
    [InlineData("ServiceUrl=;AccessKey=key;SecretKey=secret")]
    [InlineData("ServiceUrl=http://localhost:9000;SecretKey=secret")]
    [InlineData("ServiceUrl=http://localhost:9000;AccessKey=key")]
    public void Parse_MissingRequiredEntries_Throws(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => S3BlobStorageOptions.Parse(connectionString));
    }
}
