using System.Diagnostics;
using Azure;
using AzureKeyVaultStudio.Database;
using AzureKeyVaultStudio.Exceptions;
using Microsoft.Identity.Client;

namespace AzureKeyVaultStudio.Services;

public sealed record PendingPushOutcome(int Pushed, IReadOnlyList<string> Errors, bool Interrupted);

/// <summary>
/// Queue of changes made against the offline copy. Changes are pushed before each pull, and in the
/// background while the app is running, with exponential back-off when Key Vault can't be reached.
/// </summary>
public sealed partial class OfflineStoreService
{
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromMinutes(1);

    // Serializes queue edits with the push of a single operation, so an edit never races an in-flight push.
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private int _backgroundStarted;

    /// <summary>Starts pushing queued changes periodically. Safe to call more than once.</summary>
    public void StartBackgroundSync()
    {
        if (Interlocked.Exchange(ref _backgroundStarted, 1) == 1)
            return;

        _ = Task.Run(RunBackgroundSyncAsync);
    }

    private async Task RunBackgroundSyncAsync()
    {
        using var timer = new PeriodicTimer(BackgroundInterval);
        while (await timer.WaitForNextTickAsync())
        {
            if (!_authService.IsAuthenticated)
                continue;

            try
            {
                foreach (var vaultUri in await DbContext.GetVaultsWithDuePendingOperationsAsync(DateTimeOffset.UtcNow))
                    await SyncVaultAsync(vaultUri);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Background sync failed: {ex}");
            }
        }
    }

    public Task<List<PendingOperation>> GetPendingOperationsAsync() => DbContext.GetPendingOperationsAsync();

    /// <summary>Queues creating a secret, or setting a new value on an existing one, and starts pushing it.</summary>
    public async Task QueueSetSecretAsync(string vaultUri, string name, string value, string? contentType)
    {
        await _queueLock.WaitAsync();
        try
        {
            var queued = await GetQueuedForItemAsync(vaultUri, KeyVaultItemType.Secret, name);
            var last = queued.LastOrDefault();

            if (last is { Kind: PendingOperationKind.SetSecret, Status: PendingOperationStatus.Pending })
            {
                // Not pushed yet: the newer value simply replaces it.
                await DbContext.UpdatePendingSetSecretAsync(last.Id, value, contentType);
            }
            else
            {
                bool expectedExists;
                DateTimeOffset? expectedUpdatedOn = null;
                if (last is not null)
                {
                    // Follows another queued change, so it is based on that change's outcome.
                    expectedExists = last.Kind == PendingOperationKind.SetSecret;
                }
                else
                {
                    var local = (await DbContext.GetOfflineItemStatesAsync(vaultUri))
                        .FirstOrDefault(i => i.Type == KeyVaultItemType.Secret && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                    expectedExists = local is not null;
                    expectedUpdatedOn = local?.UpdatedOn;
                }

                await DbContext.AddPendingOperationAsync(new PendingOperation
                {
                    VaultUri = vaultUri,
                    Kind = PendingOperationKind.SetSecret,
                    ItemType = KeyVaultItemType.Secret,
                    Name = name,
                    Value = value,
                    ContentType = contentType,
                    ExpectedExists = expectedExists,
                    ExpectedUpdatedOn = expectedUpdatedOn,
                    CreatedOn = DateTimeOffset.UtcNow,
                });
            }
        }
        finally
        {
            _queueLock.Release();
        }

        NotifyChanged(vaultUri);
        _ = SyncVaultAsync(vaultUri);
    }

    /// <summary>Queues deleting an item of the offline copy and starts pushing it.</summary>
    public async Task QueueDeleteAsync(OfflineItem item)
    {
        await _queueLock.WaitAsync();
        try
        {
            var queued = await GetQueuedForItemAsync(item.VaultUri, item.Type, item.Name);
            var last = queued.LastOrDefault();
            if (last?.Kind == PendingOperationKind.Delete)
                return;

            var expectedUpdatedOn = item.UpdatedOn;
            if (last is { Kind: PendingOperationKind.SetSecret, Status: PendingOperationStatus.Pending })
            {
                // Drop the unpushed value; the delete is then based on whatever that change was based on.
                await DbContext.DeletePendingOperationAsync(last.Id);
                expectedUpdatedOn = last.ExpectedUpdatedOn;
                if (!last.ExpectedExists && queued.Count == 1)
                    return;
            }
            else if (last is not null)
            {
                expectedUpdatedOn = null;
            }

            await DbContext.AddPendingOperationAsync(new PendingOperation
            {
                VaultUri = item.VaultUri,
                Kind = PendingOperationKind.Delete,
                ItemType = item.Type,
                Name = item.Name,
                ExpectedExists = true,
                ExpectedUpdatedOn = expectedUpdatedOn,
                CreatedOn = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            _queueLock.Release();
            NotifyChanged(item.VaultUri);
        }

        _ = SyncVaultAsync(item.VaultUri);
    }

    /// <summary>Puts a failed or conflicted change back in the queue. <paramref name="overwrite"/> ignores remote changes.</summary>
    public async Task RetryPendingAsync(PendingOperation operation, bool overwrite)
    {
        await _queueLock.WaitAsync();
        try
        {
            await DbContext.ResetPendingOperationAsync(operation.Id, overwrite);
        }
        finally
        {
            _queueLock.Release();
        }

        NotifyChanged(operation.VaultUri);
        _ = SyncVaultAsync(operation.VaultUri);
    }

    public async Task DiscardPendingAsync(PendingOperation operation)
    {
        await _queueLock.WaitAsync();
        try
        {
            await DbContext.DeletePendingOperationAsync(operation.Id);
            // The next change for this item was based on the discarded one; base it on what that one expected,
            // so it can't overwrite remote changes unnoticed.
            await RebaseNextAsync(operation, operation.ExpectedExists, operation.ExpectedUpdatedOn);
        }
        finally
        {
            _queueLock.Release();
        }

        NotifyChanged(operation.VaultUri);
    }

    private static async Task RebaseNextAsync(PendingOperation previous, bool expectedExists, DateTimeOffset? expectedUpdatedOn)
    {
        var next = PendingOperationPolicy.NextForSameItem(await DbContext.GetPendingOperationsAsync(previous.VaultUri), previous);
        if (next is not null)
            await DbContext.RebasePendingOperationAsync(next.Id, expectedExists, expectedUpdatedOn);
    }

    private static async Task<List<PendingOperation>> GetQueuedForItemAsync(string vaultUri, KeyVaultItemType type, string name) =>
        (await DbContext.GetPendingOperationsAsync(vaultUri))
            .Where(o => o.ItemType == type && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Pushes the vault's queued changes in order. Stops at the first transient failure (Key Vault
    /// unreachable, sign-in needed), which is retried later with back-off.
    /// </summary>
    private async Task<PendingPushOutcome> PushPendingAsync(string vaultUri, Uri uri, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var pushed = 0;
        var blocked = new HashSet<(KeyVaultItemType, string)>();
        var candidates = PendingOperationPolicy.SelectRunnable(
            await DbContext.GetPendingOperationsAsync(vaultUri), DateTimeOffset.UtcNow, ignoreSchedule: true);

        foreach (var candidate in candidates)
        {
            var key = (candidate.ItemType, candidate.Name.ToLowerInvariant());
            if (blocked.Contains(key))
                continue;

            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                // Re-read under the lock: the change may have been edited or discarded meanwhile.
                var operation = (await DbContext.GetPendingOperationsAsync(vaultUri)).FirstOrDefault(o => o.Id == candidate.Id);
                if (operation is null || operation.Status != PendingOperationStatus.Pending)
                {
                    blocked.Add(key);
                    continue;
                }

                try
                {
                    var remote = await _vaultService.GetCurrentStateAsync(operation.ItemType, uri, operation.Name, cancellationToken);
                    switch (PendingOperationPolicy.Evaluate(operation, remote))
                    {
                        case PendingPushDecision.Conflict:
                            await DbContext.UpdatePendingOperationAsync(operation.Id, PendingOperationStatus.Conflict, operation.Attempts, null, null);
                            errors.Add(string.Format(_localizer["PendingConflictError"], operation.Name));
                            blocked.Add(key);
                            break;

                        case PendingPushDecision.Apply when operation.Kind == PendingOperationKind.SetSecret:
                            var written = await _vaultService.SetSecretValueAsync(uri, operation.Name, operation.Value ?? string.Empty, operation.ContentType, cancellationToken);
                            await DbContext.DeletePendingOperationAsync(operation.Id);
                            await RebaseNextAsync(operation, expectedExists: true, written.UpdatedOn);
                            pushed++;
                            break;

                        case PendingPushDecision.Apply:
                            await _vaultService.DeleteItemAsync(operation.ItemType, uri, operation.Name, cancellationToken);
                            await DbContext.DeletePendingOperationAsync(operation.Id);
                            await RebaseNextAsync(operation, expectedExists: false, null);
                            pushed++;
                            break;

                        case PendingPushDecision.AlreadyApplied:
                            await DbContext.DeletePendingOperationAsync(operation.Id);
                            await RebaseNextAsync(operation, expectedExists: false, null);
                            pushed++;
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    var attempts = operation.Attempts + 1;
                    await DbContext.UpdatePendingOperationAsync(
                        operation.Id, PendingOperationStatus.Pending, attempts, DateTimeOffset.UtcNow + PendingOperationPolicy.RetryDelay(attempts), ex.Message);
                    errors.Add($"{operation.Name}: {ex.Message}");
                    return new PendingPushOutcome(pushed, errors, Interrupted: true);
                }
                catch (Exception ex)
                {
                    await DbContext.UpdatePendingOperationAsync(operation.Id, PendingOperationStatus.Failed, operation.Attempts + 1, null, ex.Message);
                    errors.Add($"{operation.Name}: {ex.Message}");
                    blocked.Add(key);
                }
            }
            finally
            {
                _queueLock.Release();
                NotifyChanged(vaultUri);
            }
        }

        return new PendingPushOutcome(pushed, errors, Interrupted: false);
    }

    /// <summary>Errors worth retrying later: no network, throttling, server errors, or sign-in needed.</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        AuthenticationRequiredException => true,
        HttpRequestException or TimeoutException or TaskCanceledException => true,
        RequestFailedException failed => failed.Status is 0 or 408 or 429 or >= 500,
        MsalServiceException msal => msal.StatusCode is 0 or 408 or 429 or >= 500,
        MsalClientException => true,
        _ => ex.InnerException is not null && IsTransient(ex.InnerException),
    };
}
