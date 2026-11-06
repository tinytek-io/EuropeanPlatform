using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace SharedKernel.Authentication;

/// <summary>
///     Loads the back-office delegation key pair (ES256) from configuration — Scaleway Secret Manager
///     in deployed workloads, AppHost-generated development keys locally. Each side only ever loads
///     its own half, and only when it first signs or validates a delegation token, so systems that do
///     neither need no key at all.
/// </summary>
public static class BackOfficeDelegationKeys
{
    public const string Algorithm = SecurityAlgorithms.EcdsaSha256;

    public static ECDsaSecurityKey LoadSigningKey(IConfiguration configuration)
    {
        return Load(configuration, BackOfficeDelegationDefaults.SigningKeyConfigurationKey, "the back-office signs delegation tokens with it");
    }

    public static ECDsaSecurityKey LoadPublicKey(IConfiguration configuration)
    {
        return Load(configuration, BackOfficeDelegationDefaults.PublicKeyConfigurationKey, "the internal APIs validate delegation tokens with it");
    }

    private static ECDsaSecurityKey Load(IConfiguration configuration, string configurationKey, string purpose)
    {
        var pem = configuration[configurationKey];
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException($"The '{configurationKey}' configuration value is required: {purpose}.");
        }

        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return new ECDsaSecurityKey(key);
    }
}
