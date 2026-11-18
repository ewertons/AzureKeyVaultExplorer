namespace AzureKeyVaultStudio.Models;

/// <summary>Pure rules for pushing queued changes: conflict detection, ordering and retry back-off.</summary>
public static class PendingOperationPolicy
{
    public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    /// <summary>Decides what to do with a queued change given the item's current state in Key Vault.</summary>
    /// <param name="remote">The item's current state, or null if it doesn't exist.</param>
    public static PendingPushDecision Evaluate(PendingOperation operation, RemoteItemState? remote)
    {
        if (operation.Kind == PendingOperationKind.Delete)
        {
            if (remote is null)
                return PendingPushDecision.AlreadyApplied;
            return operation.Force || Unchanged(operation, remote) ? PendingPushDecision.Apply : PendingPushDecision.Conflict;
        }

        if (operation.Force)
            return PendingPushDecision.Apply;

        // A new secret must not exist yet; an update must still be based on the current state.
        if (!operation.ExpectedExists)
            return remote is null ? PendingPushDecision.Apply : PendingPushDecision.Conflict;

        return remote is not null && Unchanged(operation, remote) ? PendingPushDecision.Apply : PendingPushDecision.Conflict;
    }

    /// <summary>
    /// Returns the operations that may run now, in queue order. Changes to the same item run strictly in
    /// order: once one is blocked (conflict, failure or waiting for a retry), later ones for that item wait too.
    /// </summary>
    public static IReadOnlyList<PendingOperation> SelectRunnable(IEnumerable<PendingOperation> operations, DateTimeOffset now, bool ignoreSchedule)
    {
        var blocked = new HashSet<(KeyVaultItemType, string)>(ItemKeyComparer.Instance);
        var runnable = new List<PendingOperation>();

        foreach (var operation in operations.OrderBy(o => o.Id))
        {
            var key = (operation.ItemType, operation.Name);
            if (blocked.Contains(key))
                continue;

            var due = ignoreSchedule || operation.NextAttemptOn is null || operation.NextAttemptOn <= now;
            if (operation.Status == PendingOperationStatus.Pending && due)
            {
                runnable.Add(operation);
            }
            else
            {
                blocked.Add(key);
            }
        }

        return runnable;
    }

    /// <summary>Exponential back-off: 30s, 1m, 2m, … capped at 30 minutes.</summary>
    public static TimeSpan RetryDelay(int attempts)
    {
        if (attempts <= 1)
            return InitialRetryDelay;

        var exponent = Math.Min(attempts - 1, 16);
        var delay = TimeSpan.FromTicks(InitialRetryDelay.Ticks * (1L << exponent));
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    // Key Vault timestamps have one-second resolution.
    private static bool Unchanged(PendingOperation operation, RemoteItemState remote) =>
        operation.ExpectedUpdatedOn is null
        || (remote.UpdatedOn is { } updatedOn && (updatedOn - operation.ExpectedUpdatedOn.Value).Duration() < TimeSpan.FromSeconds(1));

    private sealed class ItemKeyComparer : IEqualityComparer<(KeyVaultItemType Type, string Name)>
    {
        public static readonly ItemKeyComparer Instance = new();

        public bool Equals((KeyVaultItemType Type, string Name) x, (KeyVaultItemType Type, string Name) y) =>
            x.Type == y.Type && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((KeyVaultItemType Type, string Name) obj) =>
            HashCode.Combine(obj.Type, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
