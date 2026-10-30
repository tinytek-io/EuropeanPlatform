using System.Text;
using ScwSecret = Scaleway.Sdk.Secret.V1Beta1;

namespace Scaleway.Sdk;

/// <summary>
///     Get-or-create wrapper over Scaleway Secret Manager. Used by the deploy step to persist
///     credentials (RDB password, host, etc.) before the resource that needs them is created,
///     so a partial failure can never leave a credential orphaned with no recovery path.
/// </summary>
public sealed class ScalewaySecretClient(IScalewayApiClient apiClient)
{
    private readonly ScwSecret.SecretApi _secretApi = new(apiClient);

    /// <summary>
    ///     Returns the value of the secret with <paramref name="secretName" />, creating it
    ///     with <paramref name="valueFactory" />'s output if absent. Idempotent — repeat calls
    ///     return the original value. The secret is created under <paramref name="path" /> (Secret Manager's
    ///     directory structure, the unit IAM path conditions scope to), and an existing secret found on a
    ///     different path is moved there without touching its value.
    /// </summary>
    /// <param name="projectId">Project the secret belongs to.</param>
    /// <param name="region">Region the secret lives in.</param>
    /// <param name="secretName">Name of the secret.</param>
    /// <param name="valueFactory">Produces the value stored when the secret has none yet; not invoked otherwise.</param>
    /// <param name="tags">Tags set on the secret when it is created.</param>
    /// <param name="path">Directory path the secret converges to; <c>/</c> (the root) when omitted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<string> GetOrCreateAsync(
        string projectId,
        string region,
        string secretName,
        Func<string> valueFactory,
        string[] tags,
        string path = "/",
        CancellationToken cancellationToken = default)
    {
        var existing = await FindByNameAsync(projectId, region, secretName, cancellationToken);
        if (existing is not null)
        {
            await ConvergePathAsync(region, existing, path, cancellationToken);
            var existingValue = await AccessLatestAsync(region, existing.Id!, cancellationToken);
            if (existingValue is not null)
            {
                return existingValue;
            }
        }

        var value = valueFactory();
        var secretId = existing?.Id ?? await CreateSecretAsync(projectId, region, secretName, tags, path, cancellationToken);
        await CreateVersionAsync(region, secretId, value, cancellationToken);
        return value;
    }

    /// <summary>Whether <paramref name="secretName" /> exists — a name lookup only, no version access.</summary>
    public async Task<bool> ExistsAsync(string projectId, string region, string secretName, CancellationToken cancellationToken = default)
    {
        return await FindByNameAsync(projectId, region, secretName, cancellationToken) is not null;
    }

    /// <summary>
    ///     Returns the current value of <paramref name="secretName" />, or <c>null</c> when the
    ///     secret doesn't exist or its latest version is empty. Read-only — never creates.
    /// </summary>
    public async Task<string?> TryReadAsync(string projectId, string region, string secretName, CancellationToken cancellationToken = default)
    {
        var existing = await FindByNameAsync(projectId, region, secretName, cancellationToken);
        if (existing is null) return null;
        return await AccessLatestAsync(region, existing.Id!, cancellationToken);
    }

    /// <summary>
    ///     Writes <paramref name="value" /> as the latest version of <paramref name="secretName" />,
    ///     creating the secret if absent. Used for values the deploy step computes after a resource
    ///     is created (e.g., the RDB endpoint hostname). The secret converges to <paramref name="path" />
    ///     the same way <see cref="GetOrCreateAsync" /> does.
    /// </summary>
    /// <param name="projectId">Project the secret belongs to.</param>
    /// <param name="region">Region the secret lives in.</param>
    /// <param name="secretName">Name of the secret.</param>
    /// <param name="value">Value written as the secret's latest version.</param>
    /// <param name="tags">Tags set on the secret when it is created.</param>
    /// <param name="path">Directory path the secret converges to; <c>/</c> (the root) when omitted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task SetAsync(
        string projectId,
        string region,
        string secretName,
        string value,
        string[] tags,
        string path = "/",
        CancellationToken cancellationToken = default)
    {
        var existing = await FindByNameAsync(projectId, region, secretName, cancellationToken);
        if (existing is not null)
        {
            await ConvergePathAsync(region, existing, path, cancellationToken);
        }

        var secretId = existing?.Id ?? await CreateSecretAsync(projectId, region, secretName, tags, path, cancellationToken);
        await CreateVersionAsync(region, secretId, value, cancellationToken);
    }

    /// <summary>
    ///     The name lookup every entry point starts with. Scaleway's <c>AccessSecretVersionByPath</c> reads a value
    ///     by name in one call and is now generatable, but it does not replace this: it returns the version, not the
    ///     secret's <c>Id</c>, and <see cref="SetAsync" /> and <see cref="GetOrCreateAsync" /> need that id to write
    ///     the next version. Swapping it in would trade this lookup for a second call on the write paths, so the
    ///     find-then-access shape is kept deliberately — "one round trip" was never actually on offer.
    /// </summary>
    private async Task<ScwSecret.Secret?> FindByNameAsync(string projectId, string region, string name, CancellationToken cancellationToken)
    {
        var secrets = await _secretApi.ListSecretsAsync(new ScwSecret.ListSecretsRequest { ProjectId = projectId, Name = name }, region, cancellationToken);
        return secrets.FirstOrDefault(secret => secret.Name == name);
    }

    private async Task ConvergePathAsync(string region, ScwSecret.Secret existing, string path, CancellationToken cancellationToken)
    {
        // Scaleway may render "/runtime/" as "/runtime", so paths are compared normalized — a redeploy against
        // a converged secret must not issue a write.
        if (NormalizePath(existing.Path) == NormalizePath(path))
        {
            return;
        }

        await _secretApi.UpdateSecretAsync(existing.Id!, new ScwSecret.UpdateSecretRequest { Path = path }, region, cancellationToken);
    }

    private static string NormalizePath(string? path)
    {
        var trimmed = (path ?? "/").TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private async Task<string> CreateSecretAsync(string projectId, string region, string name, string[] tags, string path, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _secretApi.CreateSecretAsync(
                new ScwSecret.CreateSecretRequest { ProjectId = projectId, Name = name, Tags = tags, Path = path }, region, cancellationToken
            );
            return created.Id!;
        }
        catch (ScalewayResourceAlreadyExistsException)
        {
            // A prior create persisted server-side but its response was lost (and the list may only now have
            // caught up). Adopt the existing secret instead of failing — the caller writes a fresh version anyway.
            var adopted = await FindByNameAsync(projectId, region, name, cancellationToken);
            return adopted?.Id
                   ?? throw new ScalewayApiException($"Scaleway reports secret '{name}' already exists, but it isn't visible to our list query for project '{projectId}' in '{region}'.");
        }
    }

    private async Task CreateVersionAsync(string region, string secretId, string value, CancellationToken cancellationToken)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        await _secretApi.CreateSecretVersionAsync(secretId, new ScwSecret.CreateSecretVersionRequest { Data = base64 }, region, cancellationToken);
    }

    private async Task<string?> AccessLatestAsync(string region, string secretId, CancellationToken cancellationToken)
    {
        // "latest_enabled" is Scaleway's magic revision for the current value.
        var version = await _secretApi.AccessSecretVersionAsync(secretId, "latest_enabled", region, cancellationToken);
        return version?.Data is { } base64 ? Encoding.UTF8.GetString(Convert.FromBase64String(base64)) : null;
    }
}
