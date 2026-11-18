namespace AzureKeyVaultStudio.Models;

public sealed record OfflineSyncPlan(
    IReadOnlyList<RemoteItemSnapshot> Upserts,
    IReadOnlyList<RemoteItemSnapshot> ValuesToFetch,
    IReadOnlyList<(KeyVaultItemType Type, string Name)> Deletes);

/// <summary>
/// Computes the incremental changes needed to bring the offline copy of a vault in line with Key Vault.
/// </summary>
public static class OfflineSyncPlanner
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <param name="local">Items currently in the offline store for the vault.</param>
    /// <param name="remote">Items listed from Key Vault.</param>
    /// <param name="completeTypes">
    /// Item types whose listing finished successfully. Local items of other types are kept, so a
    /// permission error on (say) keys doesn't wipe the offline copy of keys.
    /// </param>
    /// <param name="includeSecretValues">Whether the vault opted in to storing secret values offline.</param>
    public static OfflineSyncPlan Plan(
        IEnumerable<LocalItemState> local,
        IEnumerable<RemoteItemSnapshot> remote,
        IReadOnlySet<KeyVaultItemType> completeTypes,
        bool includeSecretValues)
    {
        var localByKey = new Dictionary<(KeyVaultItemType, string), LocalItemState>(KeyComparer.Instance);
        foreach (var item in local)
            localByKey[(item.Type, item.Name)] = item;

        var upserts = new List<RemoteItemSnapshot>();
        var valuesToFetch = new List<RemoteItemSnapshot>();
        var seen = new HashSet<(KeyVaultItemType, string)>(KeyComparer.Instance);

        foreach (var item in remote)
        {
            if (!seen.Add((item.Type, item.Name)))
                continue;

            localByKey.TryGetValue((item.Type, item.Name), out var existing);
            var versionChanged = existing is null || existing.Version != item.Version;

            if (versionChanged || existing!.UpdatedOn != item.UpdatedOn)
                upserts.Add(item);

            if (includeSecretValues && CanStoreValue(item) && (versionChanged || !existing!.HasSecretValue))
                valuesToFetch.Add(item);
        }

        var deletes = localByKey.Keys
            .Where(k => completeTypes.Contains(k.Item1) && !seen.Contains(k))
            .Select(k => (k.Item1, k.Item2))
            .ToList();

        return new OfflineSyncPlan(upserts, valuesToFetch, deletes);
    }

    /// <summary>
    /// Only enabled, user-managed secrets are stored. Certificate-backed secrets hold private keys and
    /// disabled secrets can't be read.
    /// </summary>
    public static bool CanStoreValue(RemoteItemSnapshot item) =>
        item.Type == KeyVaultItemType.Secret && item.Enabled && !item.Managed;

    private sealed class KeyComparer : IEqualityComparer<(KeyVaultItemType Type, string Name)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((KeyVaultItemType Type, string Name) x, (KeyVaultItemType Type, string Name) y) =>
            x.Type == y.Type && NameComparer.Equals(x.Name, y.Name);

        public int GetHashCode((KeyVaultItemType Type, string Name) obj) =>
            HashCode.Combine(obj.Type, NameComparer.GetHashCode(obj.Name));
    }
}
