using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedKernel.Configuration;

/// <summary>
///     .NET configuration provider that loads secrets from Scaleway Secret Manager. Each workload
///     runs with its own IAM key whose policy is path-conditioned, so it can only list and read
///     secrets under its own path (e.g. /account-api/) — the per-service least-privilege model.
///     The secret NAME becomes the configuration key (e.g. ConnectionStrings__account-database).
/// </summary>
public sealed class ScalewaySecretManagerConfigurationProvider : ConfigurationProvider, IDisposable
{
    private const string ApiUrl = "https://api.scaleway.com/secret-manager/v1beta1/regions/{0}";
    private const int PageSize = 100;

    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _projectId;
    private readonly string _path;
    private readonly TimeSpan? _reloadInterval;
    private readonly ILogger _logger;
    private Timer? _refreshTimer;
    private int _refreshInProgress;

    public ScalewaySecretManagerConfigurationProvider(string secretKey, string projectId, string region, string path, TimeSpan? reloadInterval, ILogger? logger = null)
    {
        _projectId = projectId;
        _apiUrl = string.Format(CultureInfo.InvariantCulture, ApiUrl, region);
        _path = path;
        _reloadInterval = reloadInterval;
        _logger = logger ?? NullLogger.Instance;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _httpClient.Dispose();
    }

    public override void Load()
    {
        LoadAsync().GetAwaiter().GetResult();

        if (_reloadInterval is { } interval)
        {
            _refreshTimer = new Timer(state => _ = RefreshAsync(), null, interval, interval);
        }
    }

    // The timer fires on its interval regardless of how long the previous refresh took, and an
    // exception on a timer thread is an unobserved task failure. Overlapping refreshes are skipped
    // and a failed refresh keeps the previous snapshot.
    private async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) == 1)
        {
            return;
        }

        try
        {
            await LoadAsync();
        }
        catch (Exception exception)
        {
            Log($"refresh failed with {exception.Message} — keeping the previously loaded configuration");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }

    private async Task LoadAsync()
    {
        var secrets = await ListSecretsAsync();
        if (secrets is null)
        {
            return;
        }

        var previousData = Data;
        var data = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var secret in secrets)
        {
            // Secret names follow the environment-variable convention (ConnectionStrings__account-database).
            // Configuration keys are ':'-separated like the environment-variables provider produces,
            // so '__' must be translated or the values would coexist with (not override) the
            // appsettings placeholders.
            var configurationKey = secret.Name.Replace("__", ":");
            var (value, failed) = await AccessSecretValueAsync(secret.Id, secret.Name);
            if (value is not null)
            {
                data[configurationKey] = value;
            }
            else if (failed && previousData.TryGetValue(configurationKey, out var previousValue))
            {
                // A failed access must not drop a value the process already holds. A secret whose
                // enabled version was deliberately removed answers with no value and no failure,
                // and does get dropped.
                data[configurationKey] = previousValue;
            }
        }

        Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }

    // Both calls below degrade rather than throw: configuration loads at startup, so a permissions
    // problem or a transient API failure must not take the process down — it logs and starts with
    // whatever it could read.
    private async Task<List<SecretSummary>?> ListSecretsAsync()
    {
        var secrets = new List<SecretSummary>();
        try
        {
            for (var page = 1; ; page++)
            {
                var response = await HttpGetJson($"{_apiUrl}/secrets?project_id={_projectId}&path={Uri.EscapeDataString(_path)}&page_size={PageSize}&page={page}");
                if (response is null)
                {
                    return null;
                }

                var items = response.RootElement.GetProperty("secrets");
                foreach (var item in items.EnumerateArray())
                {
                    secrets.Add(new SecretSummary(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!));
                }

                if (items.GetArrayLength() < PageSize)
                {
                    return secrets;
                }
            }
        }
        catch (Exception exception)
        {
            Log($"listing secrets failed with {exception.Message} — keeping the previously loaded configuration values (none at startup)");
            return null;
        }
    }

    private async Task<(string? Value, bool Failed)> AccessSecretValueAsync(string secretId, string secretName)
    {
        try
        {
            var response = await HttpGetJson($"{_apiUrl}/secrets/{secretId}/versions/{Uri.EscapeDataString("latest_enabled")}/access");
            if (response is null || !response.RootElement.TryGetProperty("data", out var data) || data.GetString() is not { } base64)
            {
                Log($"secret '{secretName}' has no accessible enabled version — its configuration value will be missing");
                return (null, false);
            }

            return (Encoding.UTF8.GetString(Convert.FromBase64String(base64)), false);
        }
        catch (FormatException)
        {
            Log($"secret '{secretName}' holds a value that is not valid base64 — its configuration value will be missing");
            return (null, true);
        }
        catch (Exception exception)
        {
            Log($"accessing secret '{secretName}' failed with {exception.Message} — its configuration value will be missing (403 means the key lacks the SecretManagerReadOnly permission set or its path condition)");
            return (null, true);
        }
    }

    private async Task<JsonDocument?> HttpGetJson(string requestUri)
    {
        using var response = await _httpClient.GetAsync(requestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {requestUri}");
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private void Log(string message)
    {
        // Configuration providers run before the DI logger exists, so failures are also written
        // to stderr; otherwise a startup fetch failure is invisible (the process silently runs
        // on its placeholder values until something downstream breaks).
        Console.Error.WriteLine($"Scaleway Secret Manager: {message}");
        _logger.LogError("Scaleway Secret Manager: {Message}", message);
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
