using System.Security.Cryptography;

namespace SharedKernel.Tests.Authentication;

/// <summary>
///     One back-office delegation key pair for the whole test run: the back-office test host signs
///     with the private half, the account test host validates with the public half.
/// </summary>
public static class TestBackOfficeDelegationKeys
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string SigningKeyPem { get; } = Key.ExportPkcs8PrivateKeyPem();

    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();
}
