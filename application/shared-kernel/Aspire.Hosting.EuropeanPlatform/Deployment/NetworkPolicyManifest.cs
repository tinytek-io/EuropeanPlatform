using System.Text.RegularExpressions;

namespace Aspire.Hosting.EuropeanPlatform.Deployment;

/// <summary>
///     The east-west network policies (cloud-infrastructure/scaleway/cluster-ops/network-policies.yaml), rendered for
///     one deployment. The file's placeholders are values Terraform owns, passed by the deploy command: the database
///     endpoint and the region. Full-line comments are dropped: they document the placeholders and would read wrongly
///     once those are resolved. The chart ships every policy rendered this way, and the migration-job admission step
///     applies the migrator policy from the same rendering, so both always agree.
/// </summary>
internal static class NetworkPolicyManifest
{
    public static string Render(string policiesPath, string databaseEndpointIp, string databaseEndpointPort, string region)
    {
        var placeholders = new Dictionary<string, string>
        {
            ["<rdb-ip>"] = databaseEndpointIp,
            ["<rdb-port>"] = databaseEndpointPort,
            ["<region>"] = region
        };

        var lines = File.ReadAllLines(policiesPath).Where(line => !line.TrimStart().StartsWith('#'));
        var policies = placeholders.Aggregate(string.Join("\n", lines), (yaml, placeholder) => yaml.Replace(placeholder.Key, placeholder.Value));

        // The chart's templates folder is rendered by Helm: template braces would be evaluated, and an unresolved
        // placeholder would apply a policy that matches nothing (or fails to parse).
        if (policies.Contains("{{"))
        {
            throw new InvalidOperationException($"'{policiesPath}' contains '{{{{', which Helm would evaluate as a template expression.");
        }

        var unresolvedPlaceholder = Regex.Match(policies, "<[a-z-]+>");
        if (unresolvedPlaceholder.Success)
        {
            throw new InvalidOperationException($"'{policiesPath}' has the placeholder '{unresolvedPlaceholder.Value}', which the network policy rendering does not resolve.");
        }

        return policies;
    }

    /// <summary>The one policy named <paramref name="policyName" /> from <see cref="Render" />'s output, or null.</summary>
    public static string? FindPolicy(string renderedPolicies, string policyName)
    {
        var namePattern = new Regex($"^  name: {Regex.Escape(policyName)}$", RegexOptions.Multiline);
        return Regex.Split(renderedPolicies, "^---$", RegexOptions.Multiline)
            .Select(document => document.Trim('\n'))
            .SingleOrDefault(namePattern.IsMatch);
    }
}
