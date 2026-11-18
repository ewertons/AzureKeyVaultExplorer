using Azure;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;

namespace AzureKeyVaultStudio.Services;

/// <summary>Data-plane calls used to push queued offline changes.</summary>
public partial class VaultService
{
    /// <summary>
    /// Returns the current state of an item, or null if it doesn't exist (or is soft-deleted). UpdatedOn
    /// matches what the list APIs report, so it can be compared with the offline copy.
    /// </summary>
    public async Task<RemoteItemState?> GetCurrentStateAsync(KeyVaultItemType type, Uri vaultUri, string name, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        try
        {
            try
            {
                return type switch
                {
                    KeyVaultItemType.Secret => new((await new SecretClient(vaultUri, token).GetSecretAsync(name, cancellationToken: cancellationToken)).Value.Properties.UpdatedOn),
                    KeyVaultItemType.Key => new((await new KeyClient(vaultUri, token).GetKeyAsync(name, cancellationToken: cancellationToken)).Value.Properties.UpdatedOn),
                    KeyVaultItemType.Certificate => new((await new CertificateClient(vaultUri, token).GetCertificateAsync(name, cancellationToken)).Value.Properties.UpdatedOn),
                    _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 403)
            {
                // Disabled items can't be read, but their versions can still be listed.
                var latest = type switch
                {
                    KeyVaultItemType.Secret => await LatestAsync(new SecretClient(vaultUri, token).GetPropertiesOfSecretVersionsAsync(name, cancellationToken), p => (p.CreatedOn, p.UpdatedOn)),
                    KeyVaultItemType.Key => await LatestAsync(new KeyClient(vaultUri, token).GetPropertiesOfKeyVersionsAsync(name, cancellationToken), p => (p.CreatedOn, p.UpdatedOn)),
                    KeyVaultItemType.Certificate => await LatestAsync(new CertificateClient(vaultUri, token).GetPropertiesOfCertificateVersionsAsync(name, cancellationToken), p => (p.CreatedOn, p.UpdatedOn)),
                    _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
                };
                return latest;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }

    /// <summary>Creates a secret, or adds a new version to an existing one, keeping its current tags.</summary>
    public async Task<SecretProperties> SetSecretValueAsync(Uri vaultUri, string name, string value, string? contentType, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        var client = new SecretClient(vaultUri, token);
        try
        {
            var secret = new KeyVaultSecret(name, value);
            secret.Properties.ContentType = contentType;

            // A new version starts without tags, so carry the current ones over.
            try
            {
                var current = await client.GetSecretAsync(name, cancellationToken: cancellationToken);
                foreach (var (key, tag) in current.Value.Properties.Tags)
                    secret.Properties.Tags[key] = tag;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 403)
            {
            }

            return (await client.SetSecretAsync(secret, cancellationToken)).Value.Properties;
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }

    private static async Task<RemoteItemState?> LatestAsync<T>(AsyncPageable<T> versions, Func<T, (DateTimeOffset? CreatedOn, DateTimeOffset? UpdatedOn)> dates) where T : notnull
    {
        (DateTimeOffset? CreatedOn, DateTimeOffset? UpdatedOn)? latest = null;
        await foreach (var version in versions)
        {
            var current = dates(version);
            if (latest is null || current.CreatedOn > latest.Value.CreatedOn)
                latest = current;
        }
        return latest is null ? null : new RemoteItemState(latest.Value.UpdatedOn);
    }
}
