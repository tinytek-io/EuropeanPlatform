using Aspire.Hosting.Pipelines;
using Scaleway.Sdk;
using ScwRegistry = Scaleway.Sdk.Registry.V1;

namespace AppHost.Deployment;

/// <summary>
///     Deletes stale deploy tags from the Container Registry after the rollout, keeping the newest few per image.
///     Every deploy pushes a full tag set; without pruning the registry fills its storage quota and pushes start
///     failing with HTTP 500 (found live: 24 deploys filled 18 GB before the quota broke every subsequent deploy).
///     Housekeeping only: a failure warns and never fails a deploy whose rollout succeeded.
/// </summary>
internal static class RegistryPruneStep
{
    // Three tags per image: the running one and two to roll back to.
    private const int RegistryTagsToKeep = 3;

    /// <param name="builder">The AppHost builder, in publish mode with the Kubernetes environment added.</param>
    /// <param name="registryNamespaceId">
    ///     The registry namespace, region-qualified as Terraform reports it (<c>fr-par/&lt;uuid&gt;</c>). Only the deploy
    ///     passes it, so it is required when the step runs: publishing and listing the steps work without it.
    /// </param>
    /// <param name="projectId">The project the namespace belongs to.</param>
    public static void AddRegistryPruneStep(this IDistributedApplicationBuilder builder, string? registryNamespaceId, string projectId)
    {
        // After the rollout, so a tag is never deleted while a pod may still be pulling it.
        builder.Pipeline.AddStep("prune-registry-tags", context => PruneAsync(context, registryNamespaceId, projectId), dependsOn: "helm-deploy-kubernetes", requiredBy: WellKnownPipelineSteps.Deploy);
    }

    private static async Task PruneAsync(PipelineStepContext context, string? registryNamespaceId, string projectId)
    {
        var cancellationToken = context.CancellationToken;
        var task = await context.ReportingStep.CreateTaskAsync($"Registry tags beyond the newest {RegistryTagsToKeep} per image", cancellationToken);

        if (string.IsNullOrEmpty(registryNamespaceId))
        {
            await task.WarnAsync("Skipped: Registry:NamespaceId is not set (the deploy passes the registry_namespace_id output)", cancellationToken);
            return;
        }

        // Registry namespace ids are region-qualified: the region selects the API, which takes the bare uuid.
        var region = registryNamespaceId.Split('/')[0];
        var namespaceId = registryNamespaceId.Split('/').Last();

        try
        {
            // The deploy command exports the operator key and organization for Terraform; this process inherits them.
            using var scaleway = new ScalewayClient(ScalewayCredentialConfig.FromEnvironment());
            var registry = scaleway.WithProject(ProjectId.Parse(projectId)).Registry;

            var kept = 0;
            var deleted = 0;
            var failed = 0;
            foreach (var image in await registry.ListImagesAsync(new ScwRegistry.ListImagesRequest { NamespaceId = namespaceId }, region, cancellationToken))
            {
                var tags = await registry.ListTagsAsync(image.Id!, new ScwRegistry.ListTagsRequest { OrderBy = ScwRegistry.ListTagsOrderBy.CreatedAtDesc }, region, cancellationToken);
                foreach (var tag in tags.Skip(RegistryTagsToKeep))
                {
                    try
                    {
                        await registry.DeleteTagAsync(tag.Id!, region, cancellationToken);
                        deleted++;
                    }
                    catch (ScalewayApiException)
                    {
                        // A tag that cannot be deleted now is retried by the next deploy's prune.
                        failed++;
                    }
                }

                kept += Math.Min(tags.Count, RegistryTagsToKeep);
            }

            var summary = $"Kept {kept} tags, deleted {deleted} stale tags";
            if (failed > 0)
            {
                await task.WarnAsync($"{summary}; {failed} could not be deleted and are retried next deploy", cancellationToken);
                return;
            }

            await task.SucceedAsync(summary, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await task.WarnAsync($"Prune failed (the rollout itself succeeded): {exception.Message}", cancellationToken);
        }
    }
}
