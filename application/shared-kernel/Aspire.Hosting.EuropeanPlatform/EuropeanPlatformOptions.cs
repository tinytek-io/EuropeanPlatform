namespace Aspire.Hosting.EuropeanPlatform;

/// <summary>What the local development stack needs from the AppHost; deployed environments take none of it.</summary>
/// <param name="VolumeNamePrefix">Prefixes the Docker volumes, so two worktrees on one machine keep separate data.</param>
/// <param name="PostgresPassword">
///     Creates the local PostgreSQL password. Called only in run mode, so a deploy never creates a local secret.
/// </param>
/// <param name="PostgresPort">The host port local PostgreSQL listens on.</param>
/// <param name="ObjectStoragePort">The host port the local S3-compatible object store listens on.</param>
/// <param name="MailHttpPort">The host port of the local mail catcher's web UI.</param>
/// <param name="MailSmtpPort">The host port of the local mail catcher's SMTP listener.</param>
public sealed record EuropeanPlatformOptions(
    string VolumeNamePrefix,
    Func<IResourceBuilder<ParameterResource>> PostgresPassword,
    int PostgresPort,
    int ObjectStoragePort,
    int MailHttpPort,
    int MailSmtpPort
);
