using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

namespace AppHost;

public static class KubernetesProbeExtensions
{
    private static ProbeV1 NewProbe(string path, int initialDelaySeconds, int periodSeconds)
    {
        return new ProbeV1
        {
            HttpGet = new HttpGetActionV1
            {
                Path = path,
                Port = 8080,
                Scheme = "HTTP"
            },
            InitialDelaySeconds = initialDelaySeconds,
            PeriodSeconds = periodSeconds,
            TimeoutSeconds = 3,
            FailureThreshold = 3
        };
    }

    extension<TResource>(IResourceBuilder<TResource> builder) where TResource : IResource
    {
        /// <summary>
        ///     Adds readiness and liveness probes to the workload the Kubernetes publisher generates for
        ///     this resource. Only used in publish mode; the annotation is inert during local development.
        ///     The APIs expose the SharedKernel health endpoints; the YARP gateway has none, so it probes
        ///     its Scalar OpenAPI page instead.
        /// </summary>
        public IResourceBuilder<TResource> WithKubernetesProbes(string readinessPath, string? livenessPath)
        {
            return builder.WithAnnotation(new KubernetesServiceCustomizationAnnotation(kubernetesResource =>
                    {
                        var container = kubernetesResource.Workload?.PodTemplate.Spec.Containers.SingleOrDefault();
                        if (container is null) return;

                        container.ReadinessProbe = NewProbe(readinessPath, 5, 10);

                        if (livenessPath is not null)
                        {
                            container.LivenessProbe = NewProbe(livenessPath, 15, 30);
                        }
                    }
                )
            );
        }
    }
}
