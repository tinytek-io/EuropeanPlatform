using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace SharedKernel.Configuration;

/// <summary>
///     Creates the shared Data Protection key ring at startup on its owner, so the read-only consumers — which
///     run with automatic key generation disabled — never meet an empty ring on a user's first request. One
///     <c>Protect</c> is enough: the framework's key manager creates the first key on demand.
/// </summary>
/// <remarks>
///     Retried because the object store can still be cold when the owner starts. Never fatal: if every attempt
///     fails the owner still creates the key on its first real <c>Protect</c> (it keeps automatic generation),
///     so this only accelerates and de-races the ring's creation.
/// </remarks>
public sealed class DataProtectionKeyRingWarmup(IDataProtectionProvider dataProtectionProvider, ILogger<DataProtectionKeyRingWarmup> logger) : IHostedService
{
    private const int MaxAttempts = 10;

    internal static TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                dataProtectionProvider.CreateProtector("data-protection-key-ring-warmup").Protect([0]);
                logger.LogInformation("Data protection key ring warmed on attempt {Attempt}", attempt);
                return;
            }
            catch (Exception exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Data protection key ring warmup attempt {Attempt} failed; retrying in {Delay}s", attempt, RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not warm the data protection key ring after {Attempts} attempts; it will be created on first use", attempt);
                return;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
