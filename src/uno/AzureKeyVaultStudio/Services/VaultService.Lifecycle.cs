using System.Runtime.CompilerServices;
using Azure;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;
using AzureKeyVaultStudio.Exceptions;

namespace AzureKeyVaultStudio.Services;

/// <summary>
/// Delete / recover / purge operations for secrets, keys and certificates.
/// </summary>
public partial class VaultService
{
    public async Task DeleteItemAsync(KeyVaultItemType type, Uri vaultUri, string name, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        await ExecuteDataPlaneAsync(async () =>
        {
            switch (type)
            {
                case KeyVaultItemType.Secret:
                    var secretOp = await new SecretClient(vaultUri, token).StartDeleteSecretAsync(name, cancellationToken);
                    await secretOp.WaitForCompletionAsync(cancellationToken);
                    break;
                case KeyVaultItemType.Key:
                    var keyOp = await new KeyClient(vaultUri, token).StartDeleteKeyAsync(name, cancellationToken);
                    await keyOp.WaitForCompletionAsync(cancellationToken);
                    break;
                case KeyVaultItemType.Certificate:
                    var certOp = await new CertificateClient(vaultUri, token).StartDeleteCertificateAsync(name, cancellationToken);
                    await certOp.WaitForCompletionAsync(cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        });
    }

    public async Task RecoverDeletedItemAsync(KeyVaultItemType type, Uri vaultUri, string name, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        await ExecuteDataPlaneAsync(async () =>
        {
            switch (type)
            {
                case KeyVaultItemType.Secret:
                    var secretOp = await new SecretClient(vaultUri, token).StartRecoverDeletedSecretAsync(name, cancellationToken);
                    await secretOp.WaitForCompletionAsync(cancellationToken);
                    break;
                case KeyVaultItemType.Key:
                    var keyOp = await new KeyClient(vaultUri, token).StartRecoverDeletedKeyAsync(name, cancellationToken);
                    await keyOp.WaitForCompletionAsync(cancellationToken);
                    break;
                case KeyVaultItemType.Certificate:
                    var certOp = await new CertificateClient(vaultUri, token).StartRecoverDeletedCertificateAsync(name, cancellationToken);
                    await certOp.WaitForCompletionAsync(cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        });
    }

    public async Task PurgeDeletedItemAsync(KeyVaultItemType type, Uri vaultUri, string name, CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        await ExecuteDataPlaneAsync(async () =>
        {
            switch (type)
            {
                case KeyVaultItemType.Secret:
                    await new SecretClient(vaultUri, token).PurgeDeletedSecretAsync(name, cancellationToken);
                    break;
                case KeyVaultItemType.Key:
                    await new KeyClient(vaultUri, token).PurgeDeletedKeyAsync(name, cancellationToken);
                    break;
                case KeyVaultItemType.Certificate:
                    await new CertificateClient(vaultUri, token).PurgeDeletedCertificateAsync(name, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        });
    }

    /// <summary>
    /// Lists soft-deleted items of the given type. <see cref="KeyVaultItemType.All"/> returns every type.
    /// Types the caller has no permission to list are skipped when <paramref name="type"/> is All.
    /// </summary>
    public async IAsyncEnumerable<KeyVaultItemProperties> GetDeletedItemsAsync(Uri vaultUri, KeyVaultItemType type, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var token = await GetKeyVaultCredentialAsync();
        var types = type == KeyVaultItemType.All
            ? new[] { KeyVaultItemType.Secret, KeyVaultItemType.Key, KeyVaultItemType.Certificate }
            : new[] { type };

        foreach (var itemType in types)
        {
            IAsyncEnumerable<KeyVaultItemProperties> source = itemType switch
            {
                KeyVaultItemType.Secret => Map(new SecretClient(vaultUri, token).GetDeletedSecretsAsync(cancellationToken), KeyVaultItemProperties.FromDeletedSecret),
                KeyVaultItemType.Key => Map(new KeyClient(vaultUri, token).GetDeletedKeysAsync(cancellationToken), KeyVaultItemProperties.FromDeletedKey),
                KeyVaultItemType.Certificate => Map(new CertificateClient(vaultUri, token).GetDeletedCertificatesAsync(cancellationToken: cancellationToken), KeyVaultItemProperties.FromDeletedCertificate),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

            await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (RequestFailedException ex) when (ex.Status == 403 && type == KeyVaultItemType.All)
                {
                    break;
                }
                catch (RequestFailedException ex)
                {
                    throw MapRequestFailed(ex);
                }

                if (!hasNext)
                    break;
                yield return enumerator.Current;
            }
        }
    }

    private static async IAsyncEnumerable<KeyVaultItemProperties> Map<T>(AsyncPageable<T> pageable, Func<T, KeyVaultItemProperties> selector) where T : notnull
    {
        await foreach (var item in pageable)
            yield return selector(item);
    }

    private static async Task ExecuteDataPlaneAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RequestFailedException ex)
        {
            throw MapRequestFailed(ex);
        }
    }

    internal static Exception MapRequestFailed(RequestFailedException ex) => ex.Status switch
    {
        404 => new KeyVaultItemNotFoundException(ex.Message, ex),
        403 => new KeyVaultInsufficientPrivilegesException(ex.Message, ex),
        409 => new KeyVaultItemConflictException(ex.Message, ex),
        _ => ex
    };
}
