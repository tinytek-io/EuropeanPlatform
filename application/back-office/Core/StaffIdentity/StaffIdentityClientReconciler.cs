using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace BackOffice.StaffIdentity;

/// <summary>
///     Converges the OIDC provider's applications to the committed client list: each listed client is created or
///     updated (its secret from configuration), and an application no longer listed is deleted, so removing a client
///     from git revokes it with the next deploy. The back-office workers run it on every start, right after the
///     migrations — one replica, so there is a single writer. OpenIddict stores only a hash of each secret, so the
///     workers are the only workload that holds them. A listed client without a secret fails the start.
/// </summary>
public sealed class StaffIdentityClientReconciler(
    StaffDirectory staffDirectory,
    IOpenIddictApplicationManager applicationManager,
    IConfiguration configuration,
    ILogger<StaffIdentityClientReconciler> logger
)
{
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        // Without a staff identity file nobody signs in here, so there is nothing to provide.
        if (ReferenceEquals(staffDirectory, StaffDirectory.Empty)) return;

        foreach (var client in staffDirectory.Clients)
        {
            var secret = configuration[$"{StaffIdentityProvider.ClientSecretsSection}:{client.ClientId}"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException($"The OIDC client '{client.ClientId}' has no secret: set {StaffIdentityProvider.ClientSecretsSection}:{client.ClientId} (Terraform generates one per listed client).");
            }

            var descriptor = CreateDescriptor(client, secret);
            var application = await applicationManager.FindByClientIdAsync(client.ClientId, cancellationToken);
            if (application is null)
            {
                await applicationManager.CreateAsync(descriptor, cancellationToken);
                logger.LogInformation("Registered the OIDC client {ClientId}", client.ClientId);
            }
            else
            {
                await applicationManager.UpdateAsync(application, descriptor, cancellationToken);
            }
        }

        var listed = staffDirectory.Clients.Select(client => client.ClientId).ToHashSet();
        var unlisted = new List<object>();
        await foreach (var application in applicationManager.ListAsync(cancellationToken: cancellationToken))
        {
            if (!listed.Contains(await applicationManager.GetClientIdAsync(application, cancellationToken) ?? "")) unlisted.Add(application);
        }

        foreach (var application in unlisted)
        {
            logger.LogInformation("Removing the OIDC client {ClientId}, which is no longer listed", await applicationManager.GetClientIdAsync(application, cancellationToken));
            await applicationManager.DeleteAsync(application, cancellationToken);
        }
    }

    private static OpenIddictApplicationDescriptor CreateDescriptor(StaffIdentityClient client, string secret)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            ApplicationType = ApplicationTypes.Web,
            // The committed client list is the consent: a signed-in staff member is never asked.
            ConsentType = ConsentTypes.Implicit,
            DisplayName = client.DisplayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + StaffIdentityProvider.GroupsScope
            }
        };

        foreach (var uri in client.RedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        foreach (var uri in client.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        }

        return descriptor;
    }
}
