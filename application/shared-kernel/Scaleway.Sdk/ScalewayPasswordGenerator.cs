using System.Security.Cryptography;

namespace Scaleway.Sdk;

/// <summary>
///     Generates database passwords for provisioned resources (RDB, Redis) with a cryptographically secure RNG.
/// </summary>
internal static class ScalewayPasswordGenerator
{
    // Character classes per Scaleway's password policy (upper/lower/digit/special). Specials are restricted to
    // URI-unreserved characters so the password embeds safely in connection strings and URIs without escaping.
    private const string UpperCase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowerCase = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Specials = "-_.~";

    /// <summary>
    ///     32 characters (~190 bits), with at least one character from each class Scaleway's password policy
    ///     requires, shuffled so the guaranteed characters sit at random positions.
    /// </summary>
    internal static string Generate()
    {
        const int length = 32;
        const string allCharacters = UpperCase + LowerCase + Digits + Specials;

        var characters = new char[length];
        characters[0] = UpperCase[RandomNumberGenerator.GetInt32(UpperCase.Length)];
        characters[1] = LowerCase[RandomNumberGenerator.GetInt32(LowerCase.Length)];
        characters[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        characters[3] = Specials[RandomNumberGenerator.GetInt32(Specials.Length)];
        for (var index = 4; index < length; index++)
        {
            characters[index] = allCharacters[RandomNumberGenerator.GetInt32(allCharacters.Length)];
        }

        // Fisher–Yates so the guaranteed class characters aren't at predictable positions.
        for (var index = length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }

        return new string(characters);
    }
}
