using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SharedKernel.Configuration;

/// <summary>
///     Encrypts Data Protection keyring elements with AES-256-GCM before they are persisted to Object Storage.
///     Without this the master keys serialize as plaintext XML, so anyone with read access to the bucket
///     holds the keys that antiforgery and OAuth state stand on. The key comes from Scaleway Secret Manager,
///     so bucket access alone no longer yields usable key material.
/// </summary>
public sealed class AesGcmXmlEncryptor(DataProtectionEncryptionKey encryptionKey) : IXmlEncryptor
{
    private const int NonceSizeInBytes = 12;
    internal const int TagSizeInBytes = 16;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeInBytes];

        using var aesGcm = new AesGcm(encryptionKey.KeyBytes, TagSizeInBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var encryptedElement = new XElement("encryptedKey",
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("value", Convert.ToBase64String(ciphertext))
        );

        return new EncryptedXmlInfo(encryptedElement, typeof(AesGcmXmlDecryptor));
    }
}

/// <summary>
///     Decrypts keyring elements produced by <see cref="AesGcmXmlEncryptor" />. Data Protection instantiates
///     this type by the name stored in the encrypted element, passing the service provider — the constructor
///     shape the framework's own decryptors use — so the key is resolved from DI rather than captured.
/// </summary>
public sealed class AesGcmXmlDecryptor(IServiceProvider serviceProvider) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var encryptionKey = serviceProvider.GetRequiredService<DataProtectionEncryptionKey>();
        var nonce = Convert.FromBase64String(encryptedElement.Element("nonce")!.Value);
        var tag = Convert.FromBase64String(encryptedElement.Element("tag")!.Value);
        var ciphertext = Convert.FromBase64String(encryptedElement.Element("value")!.Value);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(encryptionKey.KeyBytes, AesGcmXmlEncryptor.TagSizeInBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return XElement.Parse(Encoding.UTF8.GetString(plaintext));
    }
}

/// <summary>
///     The AES-256 key protecting the persisted keyring, derived from the <c>data-protection-encryption-key</c>
///     secret provisioned in Scaleway Secret Manager (surfaced to workloads through the Secret Manager
///     configuration provider under the secret's own name).
/// </summary>
public sealed class DataProtectionEncryptionKey
{
    public const string SecretName = "data-protection-encryption-key";

    private DataProtectionEncryptionKey(byte[] keyBytes)
    {
        KeyBytes = keyBytes;
    }

    internal byte[] KeyBytes { get; }

    /// <summary>
    ///     Reads the secret from configuration and derives the AES-256 key with SHA-256, so the provisioned
    ///     secret's length is not load-bearing. A missing secret stops startup: continuing without it would
    ///     store the keyring in plaintext, which is the exposure this key exists to close.
    /// </summary>
    public static DataProtectionEncryptionKey FromConfiguration(IConfiguration configuration)
    {
        var secretBase64 = configuration[SecretName];
        if (string.IsNullOrEmpty(secretBase64))
        {
            throw new InvalidOperationException(
                $"The '{SecretName}' secret is missing from configuration. It is provisioned by Terraform and surfaced through the Secret Manager configuration provider; without it the Data Protection keyring would be stored unencrypted."
            );
        }

        return new DataProtectionEncryptionKey(SHA256.HashData(Convert.FromBase64String(secretBase64)));
    }
}
