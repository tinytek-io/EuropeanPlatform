using Aspire.Hosting.Kubernetes.Resources;
using YamlDotNet.Serialization;

namespace AppHost;

/// Aspire.Hosting.Kubernetes 13.4.6-preview ships no Job or CronJob type, so run-once and scheduled
/// workloads must be hand-modelled. Deriving from Workload (rather than BaseKubernetesResource) lets
/// the model be assigned to KubernetesResource.Workload, which replaces the generated Deployment
/// rather than emitting a batch workload alongside an idle one.
public sealed class Job() : Workload("batch/v1", "Job")
{
    [YamlMember(Alias = "spec")]
    public JobSpec Spec { get; } = new();

    public override PodTemplateSpecV1 PodTemplate => Spec.Template;
}

public sealed class CronJob() : Workload("batch/v1", "CronJob")
{
    [YamlMember(Alias = "spec")]
    public CronJobSpec Spec { get; } = new();

    public override PodTemplateSpecV1 PodTemplate => Spec.JobTemplate.Spec.Template;
}

public sealed class CronJobSpec
{
    [YamlMember(Alias = "schedule")]
    public string Schedule { get; set; } = "*/15 * * * *";

    [YamlMember(Alias = "concurrencyPolicy")]
    public string ConcurrencyPolicy { get; set; } = "Forbid";

    [YamlMember(Alias = "jobTemplate")]
    public JobTemplate JobTemplate { get; } = new();
}

public sealed class JobTemplate
{
    [YamlMember(Alias = "spec")]
    public JobSpec Spec { get; } = new();
}

public sealed class JobSpec
{
    [YamlMember(Alias = "template")]
    public PodTemplateSpecV1 Template { get; } = new();

    [YamlMember(Alias = "backoffLimit")]
    public int BackoffLimit { get; set; } = 3;

    [YamlMember(Alias = "ttlSecondsAfterFinished")]
    public int TtlSecondsAfterFinished { get; set; } = 300;
}
