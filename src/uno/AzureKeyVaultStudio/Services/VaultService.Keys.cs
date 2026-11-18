using Azure;
using Azure.Security.KeyVault.Keys;

namespace AzureKeyVaultStudio.Services;

/// <summary>
/// Key creation and rotation.
/// </summary>
public partial class VaultService
{
    public async Task<KeyVaultKey> CreateKeyAsync(Uri vaultUri, CreateKeyOptions options, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        var client = new KeyClient(vaultUri, token);
        try
        {
            return options switch
            {
                CreateRsaKeyOptions rsa => await client.CreateRsaKeyAsync(rsa, cancellationToken),
                CreateEcKeyOptions ec => await client.CreateEcKeyAsync(ec, cancellationToken),
                _ => throw new ArgumentException($"Unsupported key options type '{options.GetType().Name}'.", nameof(options))
            };
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }

    public async Task<KeyVaultKey> RotateKeyAsync(Uri vaultUri, string name, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        var client = new KeyClient(vaultUri, token);
        try
        {
            return await client.RotateKeyAsync(name, cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }
}
