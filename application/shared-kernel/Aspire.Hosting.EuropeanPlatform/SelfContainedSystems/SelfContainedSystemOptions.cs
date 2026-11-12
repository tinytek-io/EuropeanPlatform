using Aspire.Hosting.EuropeanPlatform.Profiles;

namespace Aspire.Hosting.EuropeanPlatform.SelfContainedSystems;

/// <summary>The development ports a self-contained system's hosts listen on locally.</summary>
public sealed record SelfContainedSystemPorts(int Api, int Workers);

/// <summary>How a self-contained system differs from the platform's defaults.</summary>
public sealed record SelfContainedSystemOptions
{
    /// <summary>Whether the system stores blobs; its API and workers then reference the platform's object storage.</summary>
    public bool UsesBlobStorage { get; init; }

    /// <summary>
    ///     Whether this system's API generates and rotates the shared Data Protection keyring. Exactly one system owns it;
    ///     every other API reads the ring.
    /// </summary>
    public bool OwnsDataProtectionKeyRing { get; init; }

    /// <summary>
    ///     How the API behaves relative to an API's norm. Each environment type's profile sizes the class
    ///     (cloud-infrastructure/scaleway/environment-profiles.yaml), so a system states a need, never a quantity.
    /// </summary>
    public WorkloadClass ApiClass { get; init; } = WorkloadClass.Standard;

    /// <summary>
    ///     How the workers behave relative to a workers host's norm, sized per environment type like
    ///     <see cref="ApiClass" />.
    /// </summary>
    public WorkloadClass WorkersClass { get; init; } = WorkloadClass.Standard;
}
