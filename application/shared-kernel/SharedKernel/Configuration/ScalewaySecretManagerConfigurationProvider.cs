using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

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
    private readonly string _apiUrl;
    private readonly HttpClient _httpClient;
    private readonly string _path;
    private readonly string _projectId;
    private readonly TimeSpan? _reloadInterval;
    private int _refreshInProgress;
    private Timer? _refreshTimer;

    public ScalewaySecretManagerConfigurationProvider(string secretKey, string projectId, string region, string path, TimeSpan? reloadInterval)
    {
        _projectId = projectId;
        _apiUrl = string.Format(CultureInfo.InvariantCulture, ApiUrl, region);
        _path = path;
        _reloadInterval = reloadInterval;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-Auth-Token", secretKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

    private async Task<List<SecretSummary>> ListSecretsAsync()
    {
        var secrets = new List<SecretSummary>();
        for (var page = 1;; page++)
        {
            using var response = await HttpGetJson($"{_apiUrl}/secrets?project_id={_projectId}&path={Uri.EscapeDataString(_path)}&page_size={PageSize}&page={page}");
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

    private async Task<string?> AccessSecretValueAsync(SecretSummary secret)
    {
        try
        {
            using var response = await HttpGetJson($"{_apiUrl}/secrets/{secret.Id}/versions/{Uri.EscapeDataString("latest_enabled")}/access");
            return response.RootElement.TryGetProperty("data", out var data) && data.GetString() is { } base64
                ? Encoding.UTF8.GetString(Convert.FromBase64String(base64))
                : null;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"Accessing secret '{secret.Name}' failed with {exception.Message} (403 means the key lacks the SecretManagerReadOnly permission set or its path condition).", exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"Secret '{secret.Name}' holds a value that is not valid base64.", exception);
        }
    }

    private async Task<JsonDocument> HttpGetJson(string requestUri)
    {
        using var response = await _httpClient.GetAsync(requestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {requestUri}", null, response.StatusCode);
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
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
