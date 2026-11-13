using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

namespace Aspire.Hosting.EuropeanPlatform.Kubernetes;

public static class KubernetesResourceExtensions
{
    extension<TResource>(IResourceBuilder<TResource> builder) where TResource : IResource
    {
        /// <summary>
        ///     Sets the container's memory request and limit in the generated Kubernetes workload. The limit stops one
        ///     runaway pod from taking the node down, and .NET sizes its heap to it. The request is what the scheduler
        ///     reserves: kept low so a rollout's replacement pods (started before the old ones stop) still fit on a
        ///     small node. No CPU limit is set, since throttling a pod shows up as request latency.
        /// </summary>
        /// <param name="request">The memory the scheduler reserves for the pod (<c>96Mi</c>).</param>
        /// <param name="limit">The memory the container is killed above (<c>384Mi</c>).</param>
        public IResourceBuilder<TResource> WithKubernetesMemory(string request, string limit)
        {
            return builder.WithAnnotation(new KubernetesServiceCustomizationAnnotation(kubernetesResource =>
                    {
                        var container = kubernetesResource.Workload?.PodTemplate.Spec.Containers.SingleOrDefault();
                        if (container is null) return;

                        container.Resources = new ResourceRequirementsV1
                        {
                            Requests = { ["memory"] = request },
                            Limits = { ["memory"] = limit }
                        };
                    }
                )
            );
        }
    }
}
