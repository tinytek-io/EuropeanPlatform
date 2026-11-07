using BackOffice.Features.SecurityChecks;
using Microsoft.Extensions.Options;

namespace BackOffice.Workers;

/// <summary>
///     Runs the security checks on start and then on every interval: reads the project's public entry points from
///     the Scaleway API and logs one pass or fail line per check. A failure is an error log naming the offending
///     resource, which is what alerting keys on (check names are in <see cref="SecurityCheckEvaluator" />).
/// </summary>
public sealed class SecurityCheckWorker(IServiceProvider serviceProvider, IOptions<SecurityChecksOptions> options, ILogger<SecurityCheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            await RunChecksAsync(cancellationToken);
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RunChecksAsync(CancellationToken cancellationToken)
    {
        SecurityInventory inventory;
        try
        {
            using var scope = serviceProvider.CreateScope();
            inventory = await scope.ServiceProvider.GetRequiredService<ScalewaySecurityInventoryClient>().ReadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreadable inventory is not a pass: it is logged as an error like a failed check, and the next
            // interval tries again instead of the worker crash-looping on a Scaleway API outage.
            logger.LogError(exception, "Security checks could not read the Scaleway inventory");
            return;
        }

        foreach (var result in SecurityCheckEvaluator.Evaluate(inventory))
        {
            if (result.Passed)
            {
                logger.LogInformation("Security check {SecurityCheck} passed", result.Name);
            }
            else
            {
                logger.LogError("Security check {SecurityCheck} failed: {SecurityCheckFindings}", result.Name, string.Join(" ", result.Findings));
            }
        }
    }
}
