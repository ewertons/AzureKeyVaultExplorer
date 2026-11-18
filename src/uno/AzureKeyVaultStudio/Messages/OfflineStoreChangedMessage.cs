namespace AzureKeyVaultStudio.Messages;

/// <summary>Sent after the offline store changed (vault added or removed, sync finished, settings changed).</summary>
public sealed record OfflineStoreChangedMessage(string? VaultUri);
