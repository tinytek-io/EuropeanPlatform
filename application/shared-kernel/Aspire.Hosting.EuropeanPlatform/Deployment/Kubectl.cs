namespace Aspire.Hosting.EuropeanPlatform.Deployment;

/// <summary>
///     Runs kubectl against the cluster the deploy targets: the deploy command hands the AppHost that cluster's
///     kubeconfig through <c>KUBECONFIG</c>, which kubectl reads itself. Arguments are passed as a list, never as
///     one command string, so JSON patches and names reach kubectl exactly as written.
/// </summary>
internal static class Kubectl
{
    /// <summary>Runs kubectl and returns its result, failed or not.</summary>
    /// <param name="arguments">kubectl's arguments, one per element.</param>
    /// <param name="standardInput">Written to kubectl's standard input (for <c>apply -f -</c>), or null.</param>
    /// <param name="cancellationToken">
    ///     Kills kubectl when the pipeline is cancelled. Cancelling only the wait would leave a cluster-mutating
    ///     command running after its step has ended, so the process is terminated and reaped before this returns.
    /// </param>
    public static async Task<KubectlResult> RunAsync(IEnumerable<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("kubectl")
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new KubectlResult(process.ExitCode, await standardOutput, await standardError);
        }
        catch (OperationCanceledException)
        {
            KillIfRunning(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Runs kubectl and returns its output, failing the pipeline step when kubectl fails.</summary>
    /// <param name="operation">What the call does, for the failure message ("Reading cilium-config").</param>
    /// <param name="arguments">kubectl's arguments, one per element.</param>
    /// <param name="cancellationToken">Stops kubectl when the pipeline is cancelled.</param>
    /// <param name="standardInput">Written to kubectl's standard input (for <c>apply -f -</c>), or null.</param>
    public static async Task<string> RequireAsync(string operation, IEnumerable<string> arguments, CancellationToken cancellationToken, string? standardInput = null)
    {
        var result = await RunAsync(arguments, standardInput, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{operation} failed (kubectl exited with {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill, so it is no longer running either way.
        }
    }

    /// <summary>Whether a <c>kubectl apply</c> created or changed anything, from its per-resource output lines.</summary>
    /// <param name="applyOutput">kubectl apply's standard output.</param>
    /// <param name="resourcePrefix">Count only resources whose output line starts with this (a kind), or every resource.</param>
    public static bool AppliedChanges(string applyOutput, string resourcePrefix = "")
    {
        return applyOutput.Split('\n')
            .Where(line => line.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .Any(line => line.EndsWith(" created") || line.EndsWith(" configured"));
    }
}

internal sealed record KubectlResult(int ExitCode, string StandardOutput, string StandardError);
