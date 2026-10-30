using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.IdentityModel.Tokens;

namespace SharedKernel.Authentication.TokenSigning;

/// <summary>
///     Signs and validates authentication tokens with a symmetric HMAC key. The key resolves from
///     configuration (Authentication:TokenSigning:Key — populated by Scaleway Secret Manager in
///     deployed workloads), falls back to the AUTHENTICATION_TOKEN_SIGNING_KEY environment
///     variable, and finally to the user-secrets key the AppHost generates for local development.
///     Issuer and audience default to Localhost and can be overridden the same way.
/// </summary>
public class HmacTokenSigningClient(IConfiguration? configuration = null) : ITokenSigningClient
{
    private const string KeyConfigurationSection = "Authentication:TokenSigning:Key";
    private const string IssuerConfigurationSection = "Authentication:TokenSigning:Issuer";
    private const string AudienceConfigurationSection = "Authentication:TokenSigning:Audience";
    private const string SigningKeyEnvironmentVariable = "AUTHENTICATION_TOKEN_SIGNING_KEY";
    private const string IssuerEnvironmentVariable = "AUTHENTICATION_TOKEN_ISSUER";
    private const string AudienceEnvironmentVariable = "AUTHENTICATION_TOKEN_AUDIENCE";
    private const string UserSecretsKey = "authentication-token-signing-key";

    private byte[]? _key;

    private static Assembly EntryAssembly => Assembly.GetExecutingAssembly();

    private static string UserSecretsId => EntryAssembly.GetCustomAttribute<UserSecretsIdAttribute>()!.UserSecretsId;

    public string Issuer =>
        configuration?[IssuerConfigurationSection]
        ?? Environment.GetEnvironmentVariable(IssuerEnvironmentVariable)
        ?? "Localhost";

    public string Audience =>
        configuration?[AudienceConfigurationSection]
        ?? Environment.GetEnvironmentVariable(AudienceEnvironmentVariable)
        ?? "Localhost";

    public SigningCredentials GetSigningCredentials()
    {
        var key = new SymmetricSecurityKey(GetKey());
        return new SigningCredentials(key, SecurityAlgorithms.HmacSha512);
    }

    public TokenValidationParameters GetTokenValidationParameters(TimeSpan clockSkew, bool validateLifetime)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(GetKey()),
            ClockSkew = clockSkew,
            ValidateLifetime = validateLifetime
        };
    }

    private byte[] GetKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        var base64Key = configuration?[KeyConfigurationSection]
                        ?? Environment.GetEnvironmentVariable(SigningKeyEnvironmentVariable)
                        ?? ReadFromUserSecrets();
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException(
                $"The signing key is not configured. Set {KeyConfigurationSection} or the {SigningKeyEnvironmentVariable} environment variable."
            );
        }

        return _key = Convert.FromBase64String(base64Key);
    }

    private static string? ReadFromUserSecrets()
    {
        var config = new ConfigurationBuilder().AddUserSecrets(UserSecretsId).Build();
        return config[UserSecretsKey];
    }
}
