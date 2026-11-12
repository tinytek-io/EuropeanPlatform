using Aspire.Hosting.EuropeanPlatform.Profiles;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.EuropeanPlatform.Deployment;

/// <summary>
///     The values a deployment takes from Terraform, which the deploy command passes as configuration. They are read
///     once, in publish mode, and a missing one fails the publish naming who should have passed it.
/// </summary>
public sealed record ScalewayDeploymentSettings(
    EnvironmentType EnvironmentType,
    string ProjectId,
    string Region,
    string RegistryEndpoint,
    string RegistryRepository,
    string EmailSender,
    string AcmeEmail,
    string StorageEndpoint,
    string StorageBucketPrefix
)
{
    internal static ScalewayDeploymentSettings Read(IConfiguration configuration)
    {
        return new ScalewayDeploymentSettings(
            EnvironmentProfiles.ParseEnvironmentType(Require(configuration, "Environment:Type", "the deploy passes the environment module's environment_type")),
            Require(configuration, "Scaleway:ProjectId", "the deploy passes the bootstrap module's environment_project_id"),
            Require(configuration, "Scaleway:Region", "the deploy passes the environment module's region"),
            Require(configuration, "Registry:Endpoint", "the deploy passes the bootstrap module's registry_endpoint host"),
            Require(configuration, "Registry:Repository", "the deploy passes the bootstrap module's registry namespace"),
            Require(configuration, "Email:Sender", "the From address, in AppHost configuration"),
            Require(configuration, "Acme:Email", "the ACME account contact, in AppHost configuration"),
            Require(configuration, "Storage:Endpoint", "the deploy passes the environment module's object_storage_endpoint"),
            Require(configuration, "Storage:BucketPrefix", "the deploy passes the environment module's bucket_prefix")
        );
    }

    private static string Require(IConfiguration configuration, string key, string source)
    {
        return configuration[key] ?? throw new InvalidOperationException($"Publish mode requires {key} ({source}).");
    }
}
