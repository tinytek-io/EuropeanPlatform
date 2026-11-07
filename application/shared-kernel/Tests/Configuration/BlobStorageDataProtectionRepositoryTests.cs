using System.Text;
using FluentAssertions;
using NSubstitute;
using SharedKernel.Configuration;
using SharedKernel.Integrations.BlobStorage;
using Xunit;

namespace SharedKernel.Tests.Configuration;

public sealed class BlobStorageDataProtectionRepositoryTests
{
    private const string BlobName = "keys/key-1.xml";

    [Fact]
    public void GetAllElements_WhenKeyIsEncryptedByThePlatform_ShouldLoadIt()
    {
        // Arrange
        var repository = CreateRepository(KeyXml($"""<encryptedSecret decryptorType="{typeof(AesGcmXmlDecryptor).AssemblyQualifiedName}"><encryptedKey /></encryptedSecret>"""));

        // Act
        var elements = repository.GetAllElements();

        // Assert
        elements.Should().ContainSingle();
    }

    [Fact]
    public void GetAllElements_WhenKeyIsUnencrypted_ShouldRefuseToLoadTheRing()
    {
        // Arrange: a plaintext master key planted by someone with bucket write access.
        var repository = CreateRepository(KeyXml("""<masterKey><value>AAAA</value></masterKey>"""));

        // Act
        var act = repository.GetAllElements;

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{BlobName}'*");
    }

    [Fact]
    public void GetAllElements_WhenKeyNamesAnotherDecryptor_ShouldRefuseToLoadTheRing()
    {
        // Arrange
        var repository = CreateRepository(KeyXml("""<encryptedSecret decryptorType="Attacker.PassThroughDecryptor, Attacker"><masterKey /></encryptedSecret>"""));

        // Act
        var act = repository.GetAllElements;

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetAllElements_WhenElementIsARevocation_ShouldLoadIt()
    {
        // Arrange
        var repository = CreateRepository("""<revocation version="1"><revocationDate>2026-01-01T00:00:00Z</revocationDate><key id="00000000-0000-0000-0000-000000000001" /><reason>rotated</reason></revocation>""");

        // Act
        var elements = repository.GetAllElements();

        // Assert
        elements.Should().ContainSingle();
    }

    private static string KeyXml(string secret)
    {
        return $"""<key id="00000000-0000-0000-0000-000000000001" version="1"><descriptor deserializerType="Descriptor"><descriptor>{secret}</descriptor></descriptor></key>""";
    }

    private static BlobStorageDataProtectionRepository CreateRepository(string blobXml)
    {
        var blobStorageClient = Substitute.For<IBlobStorageClient>();
        blobStorageClient.ListBlobNamesAsync(BlobStorageDataProtectionRepository.BucketName, "keys/", Arg.Any<CancellationToken>()).Returns([BlobName]);
        blobStorageClient.DownloadAsync(BlobStorageDataProtectionRepository.BucketName, BlobName, Arg.Any<CancellationToken>())
            .Returns(((Stream, string)?)(new MemoryStream(Encoding.UTF8.GetBytes(blobXml)), "application/xml"));
        return new BlobStorageDataProtectionRepository(blobStorageClient);
    }
}
