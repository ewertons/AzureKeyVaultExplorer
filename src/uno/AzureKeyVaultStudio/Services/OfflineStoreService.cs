using System.Collections.Concurrent;
using Azure.ResourceManager.KeyVault;
using AzureKeyVaultStudio.Database;
using AzureKeyVaultStudio.Messages;
using CommunityToolkit.Mvvm.Messaging;

namespace AzureKeyVaultStudio.Services;

public sealed record OfflineSyncResult(string VaultUri, int Upserted, int Deleted, int ValuesStored, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    /// <summary>Queued changes pushed to Key Vault during this sync.</summary>
    public int Pushed { get; init; }

    /// <summary>The pull was skipped because Key Vault couldn't be reached while pushing.</summary>
    public bool PullSkipped { get; init; }
}

/// <summary>
/// Keeps an encrypted local copy of selected vaults so they can be searched without network access.
/// </summary>
public sealed partial class OfflineStoreService
{
    private const int MaxParallelValueReads = 8;
    private const int MaxReportedErrors = 5;

    private readonly VaultService _vaultService;
    private readonly AuthService _authService;
    private readonly IStringLocalizer _localizer;
    private readonly object _syncLock = new();
    private readonly Dictionary<string, Task<OfflineSyncResult>> _runningSyncs = new(StringComparer.OrdinalIgnoreCase);

    public OfflineStoreService(VaultService vaultService, AuthService authService, IStringLocalizer localizer)
    {
        _vaultService = vaultService;
        _authService = authService;
        _localizer = localizer;
    }

    public static string NormalizeVaultUri(Uri vaultUri) => vaultUri.AbsoluteUri.TrimEnd('/') + "/";

    public bool IsSyncing(string vaultUri)
    {
        lock (_syncLock)
            return _runningSyncs.ContainsKey(vaultUri);
    }

    public async Task<OfflineVault> AddVaultAsync(KeyVaultResource vault, bool includeSecretValues)
    {
        var offlineVault = new OfflineVault(
            NormalizeVaultUri(vault.Data.Properties.VaultUri),
            vault.Data.Name,
            _authService.TenantId,
            vault.Id.SubscriptionId,
            includeSecretValues,
            null,
            null);

        await DbContext.AddOfflineVaultAsync(offlineVault);
        NotifyChanged(offlineVault.VaultUri);
        return offlineVault;
    }

    public async Task RemoveVaultAsync(string vaultUri)
    {
        await DbContext.RemoveOfflineVaultAsync(vaultUri);
        NotifyChanged(vaultUri);
    }

    public async Task SetIncludeSecretValuesAsync(string vaultUri, bool include)
    {
        await DbContext.SetOfflineVaultIncludeSecretValuesAsync(vaultUri, include);
        NotifyChanged(vaultUri);
    }

    /// <summary>Syncs one vault. Concurrent calls for the same vault share the running sync.</summary>
    public Task<OfflineSyncResult> SyncVaultAsync(string vaultUri, CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            if (_runningSyncs.TryGetValue(vaultUri, out var running))
                return running;

            var task = RunSyncAsync(vaultUri, cancellationToken);
            _runningSyncs[vaultUri] = task;
            return task;
        }
    }

    public async Task<IReadOnlyList<OfflineSyncResult>> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var vaults = await DbContext.GetOfflineVaultsAsync();
        var results = new List<OfflineSyncResult>();
        foreach (var vault in vaults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await SyncVaultAsync(vault.VaultUri, cancellationToken));
        }
        return results;
    }

    private async Task<OfflineSyncResult> RunSyncAsync(string vaultUri, CancellationToken cancellationToken)
    {
        // Return to the caller (still inside the lock in SyncVaultAsync) before doing any work.
        await Task.Yield();
        try
        {
            NotifyChanged(vaultUri);
            var result = await SyncCoreAsync(vaultUri, cancellationToken);
            await DbContext.RecordOfflineVaultSyncAsync(
                vaultUri,
                result.PullSkipped ? null : DateTimeOffset.UtcNow,
                result.Succeeded ? null : string.Join(Environment.NewLine, result.Errors.Take(MaxReportedErrors)));
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DbContext.RecordOfflineVaultSyncAsync(vaultUri, null, ex.Message);
            return new OfflineSyncResult(vaultUri, 0, 0, 0, [ex.Message]);
        }
        finally
        {
            lock (_syncLock)
                _runningSyncs.Remove(vaultUri);
            NotifyChanged(vaultUri);
        }
    }

    private async Task<OfflineSyncResult> SyncCoreAsync(string vaultUri, CancellationToken cancellationToken)
    {
        var vault = await DbContext.GetOfflineVaultAsync(vaultUri)
            ?? throw new InvalidOperationException($"Vault {vaultUri} is not in the offline store.");
        var uri = new Uri(vault.VaultUri);

        // Push first, so the pull reflects the changes just made.
        var push = await PushPendingAsync(vaultUri, uri, cancellationToken);
        if (push.Interrupted)
            return new OfflineSyncResult(vaultUri, 0, 0, 0, push.Errors) { Pushed = push.Pushed, PullSkipped = true };

        var remote = new List<RemoteItemSnapshot>();
        var completeTypes = new HashSet<KeyVaultItemType>();
        var errors = new List<string>(push.Errors);

        async Task CollectAsync<T>(KeyVaultItemType type, IAsyncEnumerable<T> source, Func<T, RemoteItemSnapshot> map)
        {
            var items = new List<RemoteItemSnapshot>();
            try
            {
                await foreach (var item in source.WithCancellation(cancellationToken))
                    items.Add(map(item));

                remote.AddRange(items);
                completeTypes.Add(type);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{type}: {ex.Message}");
            }
        }

        await CollectAsync(KeyVaultItemType.Secret, _vaultService.GetVaultAssociatedSecrets(uri, cancellationToken), s => new RemoteItemSnapshot(
            KeyVaultItemType.Secret, s.Name, s.Id.ToString(), s.Version, s.ContentType, s.Enabled ?? false, s.Managed,
            s.CreatedOn, s.UpdatedOn, s.ExpiresOn, CopyTags(s.Tags)));
        await CollectAsync(KeyVaultItemType.Key, _vaultService.GetVaultAssociatedKeys(uri, cancellationToken), k => new RemoteItemSnapshot(
            KeyVaultItemType.Key, k.Name, k.Id.ToString(), k.Version, null, k.Enabled ?? false, k.Managed,
            k.CreatedOn, k.UpdatedOn, k.ExpiresOn, CopyTags(k.Tags)));
        await CollectAsync(KeyVaultItemType.Certificate, _vaultService.GetVaultAssociatedCertificates(uri, cancellationToken), c => new RemoteItemSnapshot(
            KeyVaultItemType.Certificate, c.Name, c.Id.ToString(), c.Version, null, c.Enabled ?? false, false,
            c.CreatedOn, c.UpdatedOn, c.ExpiresOn, CopyTags(c.Tags)));

        if (completeTypes.Count == 0)
            return new OfflineSyncResult(vaultUri, 0, 0, 0, errors) { Pushed = push.Pushed };

        var local = await DbContext.GetOfflineItemStatesAsync(vaultUri);
        var plan = OfflineSyncPlanner.Plan(local, remote, completeTypes, vault.IncludeSecretValues);

        var values = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var valueErrors = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            plan.ValuesToFetch,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelValueReads, CancellationToken = cancellationToken },
            async (item, _) =>
            {
                try
                {
                    var secret = await _vaultService.GetSecret(uri, item.Name, item.Version!);
                    values[item.Name] = secret.Value;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    valueErrors.Add($"{item.Name}: {ex.Message}");
                }
            });
        errors.AddRange(valueErrors);

        await DbContext.ApplyOfflineSyncAsync(vaultUri, plan, values);
        return new OfflineSyncResult(vaultUri, plan.Upserts.Count, plan.Deletes.Count, values.Count, errors) { Pushed = push.Pushed };
    }

    private static IReadOnlyDictionary<string, string> CopyTags(IDictionary<string, string>? tags) =>
        tags is null ? new Dictionary<string, string>() : new Dictionary<string, string>(tags);

    private static void NotifyChanged(string? vaultUri) =>
        WeakReferenceMessenger.Default.Send(new OfflineStoreChangedMessage(vaultUri));
}
