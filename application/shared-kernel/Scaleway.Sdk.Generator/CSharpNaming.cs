using System.Text;
using System.Text.RegularExpressions;

namespace Scaleway.Sdk.Generator;

/// <summary>
///     The C# naming rules every emitted identifier follows, shared by the parser and the client emitter.
/// </summary>
public static class CSharpNaming
{
    /// <summary>
    ///     Normalises a Scaleway operationId to .NET casing by title-casing embedded acronym runs:
    ///     <c>ListAPIKeys</c> → <c>ListApiKeys</c>, <c>CreateSSHKey</c> → <c>CreateSshKey</c>. Splits an acronym
    ///     from a following word (<c>ACLRules</c> → <c>Acl Rules</c>) and from a preceding word, then lower-cases
    ///     all but the first letter of each acronym. A pluralized acronym (<c>ListVPCs</c>) keeps its trailing
    ///     <c>s</c> with the acronym (<c>ListVpcs</c>, not <c>ListVpCs</c>).
    /// </summary>
    public static string NormalizeOperationId(string operationId)
    {
        // "VPCs" → "VPCS": fold a pluralized acronym's trailing s into the run so it stays one word.
        var spaced = Regex.Replace(operationId, "([A-Z]{2,})s(?=$|[A-Z])", "$1S");

        // "APIKey" → "API Key" (acronym run before an initial-capped word), then "aB" → "a B".
        spaced = Regex.Replace(spaced, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        spaced = Regex.Replace(spaced, "([a-z0-9])([A-Z])", "$1 $2");

        var result = new StringBuilder();
        foreach (var word in spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Append(char.ToUpperInvariant(word[0]));
            result.Append(word[1..].ToLowerInvariant());
        }

        return result.ToString();
    }

    /// <summary>
    ///     A parameter name for a resource group's identifier, with a leading acronym lower-cased as a whole:
    ///     <c>DNSStage</c> → <c>dnsStageId</c>, <c>APIKey</c> → <c>apiKeyId</c>, rather than <c>dNSStageId</c>.
    /// </summary>
    public static string IdentifierParameter(string resourceGroupName)
    {
        var normalized = NormalizeOperationId(resourceGroupName);
        return char.ToLowerInvariant(normalized[0]) + normalized[1..] + "Id";
    }

    public static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var sb = new StringBuilder();
        var capitalizeNext = true;

        foreach (var c in input)
        {
            if (c is '_' or '-' or ' ')
            {
                capitalizeNext = true;
                continue;
            }

            if (capitalizeNext)
            {
                sb.Append(char.ToUpperInvariant(c));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
