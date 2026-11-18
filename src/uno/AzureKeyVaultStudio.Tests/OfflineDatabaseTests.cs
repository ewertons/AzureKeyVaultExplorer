using AzureKeyVaultStudio.Database;

namespace AzureKeyVaultStudio.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseCollection
{
    public const string Name = "Database";
}

/// <summary>Runs the offline store against a real SQLCipher database in a fresh temp file per test.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class OfflineDatabaseTests : IDisposable
{
    private const string Vault = "https://kv1.vault.azure.net/";
    private static readonly DateTimeOffset T1 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<KeyVaultItemType> Secrets = [KeyVaultItemType.Secret];
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"akvs-test-{Guid.NewGuid():N}.db");

    public OfflineDatabaseTests()
    {
        SQLitePCL.Batteries_V2.Init();
        DbContext.TestDatabasePath = _path;
    }

    public void Dispose()
    {
        // Pooled connections keep the file open (and pooling avoids re-running SQLCipher's key derivation on every open).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static RemoteItemSnapshot Remote(string name, DateTimeOffset? updatedOn = null, Dictionary<string, string>? tags = null, KeyVaultItemType type = KeyVaultItemType.Secret) =>
        new(type, name, $"{Vault}secrets/{name}", null, "text/plain", true, false, T1, updatedOn ?? T1, null, tags ?? []);

    private static async Task SyncAsync(IEnumerable<RemoteItemSnapshot> remote, Dictionary<string, string>? values = null, IReadOnlySet<KeyVaultItemType>? complete = null)
    {
        var plan = OfflineSyncPlanner.Plan(await DbContext.GetOfflineItemStatesAsync(Vault), remote, complete ?? Secrets, includeSecretValues: true);
        await DbContext.ApplyOfflineSyncAsync(Vault, plan, values ?? []);
    }

    private static Task AddVaultAsync(bool includeValues = true) =>
        DbContext.AddOfflineVaultAsync(new OfflineVault(Vault, "kv-prod", "tenant", "sub", includeValues, null, null));

    [Fact]
    public async Task Search_MatchesNameTagsAndVault_ButNotSecretValues()
    {
        await AddVaultAsync();
        await SyncAsync([Remote("db-password", tags: new() { ["env"] = "prod", ["owner"] = "équipe" }), Remote("api-key")],
            new() { ["db-password"] = "s3cret", ["api-key"] = "k" });

        var hit = Assert.Single(await DbContext.SearchOfflineItemsAsync("PASS"));
        Assert.Equal("kv-prod", hit.VaultName);
        Assert.Equal("équipe", hit.Tags["owner"]);
        Assert.True(hit.HasSecretValue);
        Assert.Equal("s3cret", await DbContext.GetOfflineSecretValueAsync(hit.Id));

        Assert.Single(await DbContext.SearchOfflineItemsAsync("env prod"));
        Assert.Single(await DbContext.SearchOfflineItemsAsync("équipe"));
        Assert.Equal(2, (await DbContext.SearchOfflineItemsAsync("kv-prod")).Count);
        Assert.Empty(await DbContext.SearchOfflineItemsAsync("s3cret"));
    }

    [Fact]
    public async Task NewVersion_ReplacesStoredValue_OrClearsItWhenFetchFails()
    {
        await AddVaultAsync();
        await SyncAsync([Remote("db")], new() { ["db"] = "old" });

        await SyncAsync([Remote("db", updatedOn: T1.AddMinutes(1))], new() { ["db"] = "new" });
        var item = Assert.Single(await DbContext.SearchOfflineItemsAsync("db"));
        Assert.Equal("new", await DbContext.GetOfflineSecretValueAsync(item.Id));

        await SyncAsync([Remote("db", updatedOn: T1.AddMinutes(2))]);
        Assert.False(Assert.Single(await DbContext.SearchOfflineItemsAsync("db")).HasSecretValue);
    }

    [Fact]
    public async Task IncompleteListing_KeepsItemsOfThatType()
    {
        await AddVaultAsync();
        await SyncAsync([Remote("api-key"), Remote("signing", type: KeyVaultItemType.Key)], complete: new HashSet<KeyVaultItemType> { KeyVaultItemType.Secret, KeyVaultItemType.Key });

        await SyncAsync([]);

        Assert.Equal("signing", Assert.Single(await DbContext.SearchOfflineItemsAsync(null)).Name);
    }

    [Fact]
    public async Task OptingOutOfValues_ClearsStoredValues()
    {
        await AddVaultAsync();
        await SyncAsync([Remote("db")], new() { ["db"] = "v" });

        await DbContext.SetOfflineVaultIncludeSecretValuesAsync(Vault, false);

        Assert.False(Assert.Single(await DbContext.SearchOfflineItemsAsync("db")).HasSecretValue);
        Assert.False(Assert.Single(await DbContext.GetOfflineVaultsAsync()).IncludeSecretValues);
    }

    [Fact]
    public async Task PendingOperations_RoundTripAndFlagItems()
    {
        await AddVaultAsync();
        await SyncAsync([Remote("db")]);

        var updateId = await DbContext.AddPendingOperationAsync(new PendingOperation
        {
            VaultUri = Vault, Kind = PendingOperationKind.SetSecret, ItemType = KeyVaultItemType.Secret,
            Name = "DB", Value = "x", ExpectedExists = true, ExpectedUpdatedOn = T1, CreatedOn = DateTimeOffset.UtcNow,
        });
        var createId = await DbContext.AddPendingOperationAsync(new PendingOperation
        {
            VaultUri = Vault, Kind = PendingOperationKind.SetSecret, ItemType = KeyVaultItemType.Secret,
            Name = "new", Value = "y", CreatedOn = DateTimeOffset.UtcNow,
        });

        var ops = await DbContext.GetPendingOperationsAsync();
        Assert.Equal([updateId, createId], ops.Select(o => o.Id));
        Assert.Equal("kv-prod", ops[0].VaultName);
        Assert.Equal(T1, ops[0].ExpectedUpdatedOn);
        Assert.True(ops[0].ExpectedExists);
        Assert.False(ops[1].ExpectedExists);
        Assert.True(Assert.Single(await DbContext.SearchOfflineItemsAsync("db")).HasPendingChange);

        await DbContext.UpdatePendingSetSecretAsync(updateId, "x2", "text/plain");
        var updated = (await DbContext.GetPendingOperationsAsync(Vault))[0];
        Assert.Equal(("x2", "text/plain"), (updated.Value, updated.ContentType));
    }

    [Fact]
    public async Task PendingOperations_DueQuery_RespectsRetrySchedule()
    {
        await AddVaultAsync();
        var id = await DbContext.AddPendingOperationAsync(new PendingOperation
        {
            VaultUri = Vault, Kind = PendingOperationKind.Delete, ItemType = KeyVaultItemType.Secret, Name = "db", ExpectedExists = true, CreatedOn = T1,
        });
        var now = DateTimeOffset.UtcNow;

        await DbContext.UpdatePendingOperationAsync(id, PendingOperationStatus.Pending, 1, now.AddMinutes(5), "offline");

        Assert.Empty(await DbContext.GetVaultsWithDuePendingOperationsAsync(now));
        Assert.Equal(Vault, Assert.Single(await DbContext.GetVaultsWithDuePendingOperationsAsync(now.AddMinutes(6))));
    }

    [Fact]
    public async Task PendingOperations_OverwriteResets_AndRemovingVaultDropsQueue()
    {
        await AddVaultAsync();
        var id = await DbContext.AddPendingOperationAsync(new PendingOperation
        {
            VaultUri = Vault, Kind = PendingOperationKind.Delete, ItemType = KeyVaultItemType.Secret, Name = "db", ExpectedExists = true, CreatedOn = T1,
        });
        await DbContext.UpdatePendingOperationAsync(id, PendingOperationStatus.Conflict, 3, null, "changed");

        await DbContext.ResetPendingOperationAsync(id, force: true);

        var op = Assert.Single(await DbContext.GetPendingOperationsAsync());
        Assert.Equal(PendingOperationStatus.Pending, op.Status);
        Assert.True(op.Force);
        Assert.Equal(0, op.Attempts);
        Assert.Null(op.LastError);

        await DbContext.RemoveOfflineVaultAsync(Vault);
        Assert.Empty(await DbContext.GetPendingOperationsAsync());
        Assert.Empty(await DbContext.GetOfflineVaultsAsync());
    }

    [Fact]
    public async Task PendingOperations_Rebase_SetsTheExpectedState()
    {
        await AddVaultAsync();
        var id = await DbContext.AddPendingOperationAsync(new PendingOperation
        {
            VaultUri = Vault, Kind = PendingOperationKind.SetSecret, ItemType = KeyVaultItemType.Secret, Name = "db", Value = "v",
            ExpectedExists = false, ExpectedUpdatedOn = null, CreatedOn = T1,
        });

        await DbContext.RebasePendingOperationAsync(id, expectedExists: true, T1);
        var op = Assert.Single(await DbContext.GetPendingOperationsAsync());
        Assert.True(op.ExpectedExists);
        Assert.Equal(T1, op.ExpectedUpdatedOn);

        await DbContext.RebasePendingOperationAsync(id, expectedExists: false, null);
        op = Assert.Single(await DbContext.GetPendingOperationsAsync());
        Assert.False(op.ExpectedExists);
        Assert.Null(op.ExpectedUpdatedOn);
    }
}
