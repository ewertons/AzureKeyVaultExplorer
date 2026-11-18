namespace AzureKeyVaultStudio.Models;

public enum PendingOperationKind
{
    SetSecret = 0,
    Delete = 1,
}

public enum PendingOperationStatus
{
    /// <summary>Waiting to be pushed, possibly after a retry delay.</summary>
    Pending = 0,

    /// <summary>The item changed in Key Vault since the change was queued. Needs the user to overwrite or discard.</summary>
    Conflict = 1,

    /// <summary>Key Vault rejected the change (e.g. missing permission). Needs the user to retry or discard.</summary>
    Failed = 2,
}

/// <summary>A change made while offline (or queued for background execution), waiting to be pushed to Key Vault.</summary>
public sealed class PendingOperation
{
    public long Id { get; init; }
    public string VaultUri { get; init; } = string.Empty;
    public string VaultName { get; init; } = string.Empty;
    public PendingOperationKind Kind { get; init; }
    public KeyVaultItemType ItemType { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The new secret value for <see cref="PendingOperationKind.SetSecret"/>.</summary>
    public string? Value { get; init; }

    public string? ContentType { get; init; }

    /// <summary>Whether the item existed when the change was queued (false for a new secret).</summary>
    public bool ExpectedExists { get; init; }

    /// <summary>
    /// The item's UpdatedOn when the change was queued. List APIs don't return versions, so this is the
    /// concurrency token used to detect remote changes.
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedOn { get; init; }

    /// <summary>Apply even if the item changed remotely.</summary>
    public bool Force { get; init; }

    public DateTimeOffset CreatedOn { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptOn { get; init; }
    public PendingOperationStatus Status { get; init; }
    public string? LastError { get; init; }
}

public enum PendingPushDecision
{
    /// <summary>Send the change to Key Vault.</summary>
    Apply,

    /// <summary>Key Vault is already in the desired state (e.g. the item to delete is gone).</summary>
    AlreadyApplied,

    /// <summary>The item changed remotely since the change was queued.</summary>
    Conflict,
}

/// <summary>The current state of an item in Key Vault, as needed for conflict detection.</summary>
public sealed record RemoteItemState(DateTimeOffset? UpdatedOn);
