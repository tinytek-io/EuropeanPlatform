using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AppHost;

// The Authelia OIDC dev harness needs two private keys: its TLS serving key
// (authelia-tls.key/.pem, CN=authelia.dev.localhost) and the RS256 OIDC id_token signing key
// injected into the generated configuration. Dev-only material — but private keys are never
// committed regardless, so both are generated on first run into the AppHost's obj folder
// (gitignored build output) and reused until a clean removes them. Authelia is reached by name
// only (authelia.dev.localhost), and the signing key rotates freely between dev sessions: tokens
// are short-lived and clients fetch the current public key through Authelia's discovery/JWKS
// endpoint.
internal static class AutheliaTls
{
    private const string CertificateCommonName = "authelia.dev.localhost";

    private const string TlsKeyFileName = "authelia-tls.key";

    private const string TlsCertificateFileName = "authelia-tls.pem";

    private const string OidcSigningKeyFileName = "oidc-signing.key";

    internal static void EnsureDevSecrets(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);

        if (!File.Exists(Path.Combine(outputFolder, TlsKeyFileName)) || !File.Exists(Path.Combine(outputFolder, TlsCertificateFileName)))
        {
            WriteSelfSignedCertificate(outputFolder);
        }

        if (!File.Exists(Path.Combine(outputFolder, OidcSigningKeyFileName)))
        {
            using var signingKey = RSA.Create(2048);
            File.WriteAllText(Path.Combine(outputFolder, OidcSigningKeyFileName), signingKey.ExportPkcs8PrivateKeyPem());
            Console.WriteLine("Authelia harness: generated the RS256 OIDC signing key.");
        }
    }

    // The signing key as the template's `key: |` block content: every line indented to the
    // block's YAML indentation, exactly how the static key used to be spelled in the template.
    internal static string GetOidcSigningKeyYamlBlock(string outputFolder)
    {
        return File.ReadAllText(Path.Combine(outputFolder, OidcSigningKeyFileName)).Replace("\n", "\n          ");
    }

    private static void WriteSelfSignedCertificate(string outputFolder)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={CertificateCommonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectAlternativeNameExtension(BuildSubjectAlternativeNameDer(CertificateCommonName, "localhost")));

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllText(Path.Combine(outputFolder, TlsCertificateFileName), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(outputFolder, TlsKeyFileName), rsa.ExportPkcs8PrivateKeyPem());
        Console.WriteLine($"Authelia harness: generated the self-signed dev certificate for {CertificateCommonName}.");
    }

    // Minimal DER for a SAN extension: SEQUENCE of dNSName entries (UTF-8, primitive
    // context-specific tag 2). The framework's SAN helper only exposes raw-bytes constructors,
    // and the names are fixed ASCII short enough that the one-byte DER lengths always hold.
    private static byte[] BuildSubjectAlternativeNameDer(params string[] dnsNames)
    {
        var content = new List<byte>();
        foreach (var dnsName in dnsNames)
        {
            var nameBytes = Encoding.UTF8.GetBytes(dnsName);
            content.Add(0x82); // [2] primitive (dNSName)
            content.Add((byte)nameBytes.Length);
            content.AddRange(nameBytes);
        }

        var sequence = new List<byte> { 0x30, (byte)content.Count };
        sequence.AddRange(content);
        return [.. sequence];
    }
}
