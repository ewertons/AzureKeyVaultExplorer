using Azure;
using Azure.Security.KeyVault.Certificates;

namespace AzureKeyVaultStudio.Services;

/// <summary>
/// Certificate creation and import.
/// </summary>
public partial class VaultService
{
    /// <summary>
    /// Creates a certificate and waits for issuance (self-signed certificates complete within seconds).
    /// </summary>
    public async Task<KeyVaultCertificateWithPolicy> CreateCertificateAsync(Uri vaultUri, string name, CertificatePolicy policy, bool enabled, IDictionary<string, string>? tags, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        var client = new CertificateClient(vaultUri, token);
        try
        {
            var operation = await client.StartCreateCertificateAsync(name, policy, enabled, tags, cancellationToken);
            return await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }

    public async Task<KeyVaultCertificateWithPolicy> ImportCertificateAsync(Uri vaultUri, ImportCertificateOptions options, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        var client = new CertificateClient(vaultUri, token);
        try
        {
            return await client.ImportCertificateAsync(options, cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }
}
