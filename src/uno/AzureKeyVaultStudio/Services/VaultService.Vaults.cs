using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using AzureKeyVaultStudio.Exceptions;

namespace AzureKeyVaultStudio.Services;

/// <summary>
/// Result of creating a vault. <see cref="AccessWarning"/> is set when the vault was created
/// but granting the signed-in user access to it failed.
/// </summary>
public sealed record VaultCreationResult(KeyVaultResource Vault, string? AccessWarning);

/// <summary>
/// Vault-level (ARM control plane) create / delete / recover / purge operations.
/// </summary>
public partial class VaultService
{
    // Built-in "Key Vault Administrator" role.
    internal const string KeyVaultAdministratorRoleId = "00482a5a-887f-4fb3-b363-3b7fe8e74483";
    private const string ArmEndpoint = "https://management.azure.com";
    private static readonly HttpClient s_armHttpClient = new();

    public async IAsyncEnumerable<LocationExpanded> GetVaultLocationsAsync(SubscriptionResource subscription, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var armClient = await GetOrCreateArmClient();
        var sub = armClient.GetSubscriptionResource(subscription.Id);
        await foreach (var location in sub.GetLocationsAsync(cancellationToken: cancellationToken))
        {
            if (location.Metadata?.RegionType == RegionType.Physical)
                yield return location;
        }
    }

    public async IAsyncEnumerable<ResourceGroupResource> GetResourceGroupsAsync(SubscriptionResource subscription, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var armClient = await GetOrCreateArmClient();
        var sub = armClient.GetSubscriptionResource(subscription.Id);
        await foreach (var rg in sub.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken))
            yield return rg;
    }

    public async Task<KeyVaultNameAvailabilityResult> CheckVaultNameAvailabilityAsync(SubscriptionResource subscription, string name, CancellationToken cancellationToken = default)
    {
        var armClient = await GetOrCreateArmClient();
        var sub = armClient.GetSubscriptionResource(subscription.Id);
        return await ExecuteArmAsync(async () =>
            (await sub.CheckKeyVaultNameAvailabilityAsync(new KeyVaultNameAvailabilityContent(name), cancellationToken)).Value);
    }

    public async Task<VaultCreationResult> CreateVaultAsync(ResourceGroupResource resourceGroup, NewVaultRequest request, CancellationToken cancellationToken = default)
    {
        var token = await _authService.GetAzureArmTokenSilent()
            ?? throw new AuthenticationRequiredException("No accounts logged in. Please sign in to continue.");
        var tenantId = Guid.Parse(token.TenantId);
        Guid? objectId = Guid.TryParse(token.UniqueId, out var oid) ? oid : null;

        var armClient = await GetOrCreateArmClient();
        var rg = armClient.GetResourceGroupResource(resourceGroup.Id);

        var vault = await ExecuteArmAsync(async () =>
            (await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, request.Name, request.ToCreateContent(tenantId, objectId), cancellationToken)).Value);

        string? warning = null;
        if (request.UseRbacAuthorization && request.GrantCurrentUserAccess)
        {
            if (objectId is null)
            {
                warning = "Could not determine the signed-in user's object id.";
            }
            else
            {
                try
                {
                    await AssignRoleAsync(token.AccessToken, vault.Id, KeyVaultAdministratorRoleId, objectId.Value, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warning = ex.Message;
                }
            }
        }

        return new VaultCreationResult(vault, warning);
    }

    public async Task DeleteVaultAsync(KeyVaultResource vault, CancellationToken cancellationToken = default)
    {
        var armClient = await GetOrCreateArmClient();
        await ExecuteArmAsync(async () =>
        {
            await armClient.GetKeyVaultResource(vault.Id).DeleteAsync(WaitUntil.Completed, cancellationToken);
            return true;
        });
    }

    public async IAsyncEnumerable<DeletedKeyVaultResource> GetDeletedVaultsAsync(SubscriptionResource subscription, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var armClient = await GetOrCreateArmClient();
        var sub = armClient.GetSubscriptionResource(subscription.Id);
        var pages = sub.GetDeletedKeyVaultsAsync(cancellationToken).AsPages().GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                Page<DeletedKeyVaultResource> page;
                try
                {
                    if (!await pages.MoveNextAsync())
                        yield break;
                    page = pages.Current;
                }
                catch (RequestFailedException ex)
                {
                    throw MapRequestFailed(ex);
                }

                foreach (var item in page.Values)
                    yield return item;
            }
        }
        finally
        {
            await pages.DisposeAsync();
        }
    }

    /// <summary>
    /// Recovers a soft-deleted vault into the resource group it was deleted from.
    /// </summary>
    public async Task<KeyVaultResource> RecoverDeletedVaultAsync(DeletedKeyVaultResource deletedVault, CancellationToken cancellationToken = default)
    {
        var originalId = deletedVault.Data.Properties?.VaultId
            ?? throw new InvalidOperationException("The deleted vault does not report its original resource id.");
        var location = deletedVault.Data.Properties.Location
            ?? throw new InvalidOperationException("The deleted vault does not report its location.");

        var token = await _authService.GetAzureArmTokenSilent()
            ?? throw new AuthenticationRequiredException("No accounts logged in. Please sign in to continue.");

        var armClient = await GetOrCreateArmClient();
        var rg = armClient.GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(originalId.SubscriptionId, originalId.ResourceGroupName));
        var properties = new KeyVaultProperties(Guid.Parse(token.TenantId), new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard))
        {
            CreateMode = KeyVaultCreateMode.Recover,
        };

        return await ExecuteArmAsync(async () =>
            (await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, originalId.Name, new KeyVaultCreateOrUpdateContent(location, properties), cancellationToken)).Value);
    }

    /// <summary>
    /// Starts purging a soft-deleted vault. Purging continues in the background and can take several minutes.
    /// </summary>
    public async Task PurgeDeletedVaultAsync(DeletedKeyVaultResource deletedVault, CancellationToken cancellationToken = default)
    {
        var armClient = await GetOrCreateArmClient();
        await ExecuteArmAsync(async () =>
        {
            await armClient.GetDeletedKeyVaultResource(deletedVault.Id).PurgeDeletedAsync(WaitUntil.Started, cancellationToken);
            return true;
        });
    }

    private static async Task AssignRoleAsync(string armAccessToken, ResourceIdentifier scope, string roleDefinitionGuid, Guid principalId, CancellationToken cancellationToken)
    {
        var uri = $"{ArmEndpoint}{scope}/providers/Microsoft.Authorization/roleAssignments/{Guid.NewGuid()}?api-version=2022-04-01";
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleDefinitionId"] = $"/subscriptions/{scope.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{roleDefinitionGuid}",
                ["principalId"] = principalId.ToString(),
                ["principalType"] = "User",
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);
        using var response = await s_armHttpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Role assignment failed ({(int)response.StatusCode}): {detail}", null, response.StatusCode);
        }
    }

    private static async Task<T> ExecuteArmAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }
}
