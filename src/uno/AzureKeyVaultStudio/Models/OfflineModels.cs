namespace AzureKeyVaultStudio.Models;

/// <summary>A vault the user has made available offline.</summary>
public sealed record OfflineVault(
    string VaultUri,
    string Name,
    string? TenantId,
    string? SubscriptionId,
    bool IncludeSecretValues,
    DateTimeOffset? LastSyncedOn,
    string? LastSyncError);

/// <summary>Metadata of a vault item as stored offline. Secret values are loaded on demand only.</summary>
public sealed class OfflineItem
{
    public long Id { get; init; }
    public string VaultUri { get; init; } = string.Empty;
    public string VaultName { get; init; } = string.Empty;
    public KeyVaultItemType Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? ContentType { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset? CreatedOn { get; init; }
    public DateTimeOffset? UpdatedOn { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public bool HasSecretValue { get; init; }

    /// <summary>A queued change for this item hasn't been pushed to Key Vault yet.</summary>
    public bool HasPendingChange { get; init; }

    public bool IsSecret => Type == KeyVaultItemType.Secret;

    public string TypeText => Type.ToString();
    public string UpdatedOnText => UpdatedOn?.ToLocalTime().ToString("g") ?? string.Empty;
    public string TagsText => string.Join(", ", Tags.Select(t => $"{t.Key}={t.Value}"));
}

/// <summary>Item properties as listed from Key Vault during a sync.</summary>
public sealed record RemoteItemSnapshot(
    KeyVaultItemType Type,
    string Name,
    string ItemId,
    string? Version,
    string? ContentType,
    bool Enabled,
    bool Managed,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? UpdatedOn,
    DateTimeOffset? ExpiresOn,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>What the offline store currently holds for an item, used to plan an incremental sync.</summary>
public sealed record LocalItemState(KeyVaultItemType Type, string Name, string? Version, DateTimeOffset? UpdatedOn, bool HasSecretValue);
