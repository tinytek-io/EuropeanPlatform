using System.Text;
using Microsoft.Extensions.Configuration;
using Scaleway.Sdk;
using ScwSecret = Scaleway.Sdk.Secret.V1Beta1;

namespace SharedKernel.Configuration;

/// <summary>
///     .NET configuration provider that loads secrets from Scaleway Secret Manager. Each workload
///     runs with its own IAM key whose policy is path-conditioned, so it can only list and read
///     secrets under its own path (e.g. /account-api/) — the per-service least-privilege model.
///     The secret NAME becomes the configuration key (e.g. ConnectionStrings__account-database).
/// </summary>
public sealed class ScalewaySecretManagerConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _path;
    private readonly string _projectId;
    private readonly string _region;
    private readonly TimeSpan? _reloadInterval;
    private readonly ScwSecret.SecretApi _secretApi;
    private int _refreshInProgress;
    private Timer? _refreshTimer;

    public ScalewaySecretManagerConfigurationProvider(string secretKey, string projectId, string region, string path, TimeSpan? reloadInterval)
    {
        _projectId = projectId;
        _region = region;
        _path = path;
        _reloadInterval = reloadInterval;
        // The SDK owns pagination and error classification; the provider only maps secrets to
        // configuration keys. A workload holds just its secret key (no access key or organization),
        // so the SDK runs over a client that carries the key, like the Transactional Email client.
        _httpClient = new HttpClient { BaseAddress = new Uri("https://api.scaleway.com/") };
        _httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);
        _secretApi = new ScwSecret.SecretApi(new ScalewayApiClient(_httpClient));
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _httpClient.Dispose();
    }

    // The startup load fails loud: a workload that cannot read its secrets (a wrong key, a missing
    // SecretManagerReadOnly permission or path condition, an API outage) must crash-loop visibly
    // rather than start on placeholder values and fail per request later.
    public override void Load()
    {
        Data = ReadSecretsAsync().GetAwaiter().GetResult();

        if (_reloadInterval is { } interval)
        {
            _refreshTimer = new Timer(state => _ = RefreshAsync(), null, interval, interval);
        }
    }

    // The timer fires on its interval regardless of how long the previous refresh took, and an
    // exception on a timer thread is an unobserved task failure. Overlapping refreshes are skipped,
    // a failed refresh keeps the previous snapshot, and listeners are only notified of real changes.
    private async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) == 1)
        {
            return;
        }

        try
        {
            var data = await ReadSecretsAsync();
            if (data.Count == Data.Count && data.All(entry => Data.TryGetValue(entry.Key, out var value) && value == entry.Value))
            {
                return;
            }

            Data = data;
            OnReload();
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Scaleway Secret Manager: refresh failed with {exception.Message} — keeping the previously loaded configuration");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }

    private async Task<Dictionary<string, string?>> ReadSecretsAsync()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in await ListSecretsAsync())
        {
            // Secret names follow the environment-variable convention (ConnectionStrings__account-database).
            // Configuration keys are ':'-separated like the environment-variables provider produces,
            // so '__' must be translated or the values would coexist with (not override) the
            // appsettings placeholders. A secret without an enabled version (deliberately disabled)
            // is left out.
            var value = await AccessSecretValueAsync(secret);
            if (value is not null)
            {
                data[secret.Name.Replace("__", ":")] = value;
            }
        }

        return data;
    }

    private async Task<IEnumerable<SecretSummary>> ListSecretsAsync()
    {
        var secrets = await _secretApi.ListSecretsAsync(new ScwSecret.ListSecretsRequest { ProjectId = _projectId, Path = _path }, _region);
        return secrets.Select(secret => new SecretSummary(secret.Id!, secret.Name!));
    }

    // A secret without an enabled version (deliberately disabled) answers 404, which the SDK
    // returns as null: the value is left out. Any other failure propagates.
    private async Task<string?> AccessSecretValueAsync(SecretSummary secret)
    {
        var response = await _secretApi.AccessSecretVersionAsync(secret.Id, "latest_enabled", _region);
        if (response?.Data is not { } base64) return null;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"Secret '{secret.Name}' holds a value that is not valid base64.", exception);
        }
    }

    private sealed record SecretSummary(string Id, string Name);
}

public sealed class ScalewaySecretManagerOptions
{
    public const string PathConfigurationKey = "SecretManager:Path";
    public const string ProjectIdConfigurationKey = "SecretManager:ProjectId";
    public const string RegionConfigurationKey = "SecretManager:Region";

    /// <summary>Scaleway secret key of the workload's IAM API key (X-Auth-Token).</summary>
    public const string SecretKeyEnvironmentVariable = "SCW_SECRET_KEY";
}

public static class ScalewaySecretManagerConfigurationExtensions
{
    /// <summary>
    ///     Adds a Scaleway Secret Manager configuration provider when the process is configured for
    ///     it (SecretManager:Path + SCW_SECRET_KEY); a no-op otherwise, so local development is
    ///     unaffected. Added last so secrets override chart-supplied placeholders.
    /// </summary>
    public static IConfigurationBuilder AddScalewaySecretManager(this IConfigurationBuilder configurationBuilder, TimeSpan? reloadInterval = null)
    {
        if (configurationBuilder.Sources.OfType<ScalewaySecretManagerSource>().Any())
        {
            return configurationBuilder;
        }

        configurationBuilder.Sources.Add(new ScalewaySecretManagerSource(reloadInterval));
        return configurationBuilder;
    }
}

public sealed class ScalewaySecretManagerSource(TimeSpan? reloadInterval) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        // The provider needs configuration values (path, project, region) that are only known once
        // the preceding providers have loaded, so they are resolved here.
        var configurationBuilder = new ConfigurationBuilder();
        foreach (var source in builder.Sources.Where(source => source is not ScalewaySecretManagerSource))
        {
            configurationBuilder.Add(source);
        }

        var configuration = configurationBuilder.Build();

        var path = configuration[ScalewaySecretManagerOptions.PathConfigurationKey];
        var secretKey = Environment.GetEnvironmentVariable(ScalewaySecretManagerOptions.SecretKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(secretKey))
        {
            // Not configured (local development, tests): an empty provider keeps the pipeline intact.
            return new EmptyConfigurationProvider();
        }

        var projectId = configuration[ScalewaySecretManagerOptions.ProjectIdConfigurationKey]
                        ?? Environment.GetEnvironmentVariable("SCW_DEFAULT_PROJECT_ID")
                        ?? throw new InvalidOperationException("Scaleway Secret Manager requires the project ID (SecretManager:ProjectId or SCW_DEFAULT_PROJECT_ID).");
        var region = configuration[ScalewaySecretManagerOptions.RegionConfigurationKey]
                     ?? Environment.GetEnvironmentVariable("SCW_DEFAULT_REGION")
                     ?? "fr-par";

        return new ScalewaySecretManagerConfigurationProvider(secretKey, projectId, region, path, reloadInterval);
    }

    private sealed class EmptyConfigurationProvider : ConfigurationProvider;
}
