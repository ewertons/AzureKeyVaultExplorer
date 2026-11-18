namespace AzureKeyVaultStudio.Tests;

public class OfflineSyncPlannerTests
{
    private const string Vault = "https://kv1.vault.azure.net/";
    private static readonly DateTimeOffset T1 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<KeyVaultItemType> AllTypes = [KeyVaultItemType.Secret, KeyVaultItemType.Key, KeyVaultItemType.Certificate];

    // List APIs return item ids without a version, so synced items have a null Version.
    private static RemoteItemSnapshot Remote(string name, KeyVaultItemType type = KeyVaultItemType.Secret, DateTimeOffset? updatedOn = null, bool enabled = true, bool managed = false) =>
        new(type, name, $"{Vault}secrets/{name}", null, null, enabled, managed, T1, updatedOn ?? T1, null, new Dictionary<string, string>());

    private static LocalItemState Local(string name, KeyVaultItemType type = KeyVaultItemType.Secret, DateTimeOffset? updatedOn = null, bool hasValue = false) =>
        new(type, name, null, updatedOn ?? T1, hasValue);

    [Fact]
    public void FirstSync_UpsertsEverything_AndFetchesValuesOnlyForEnabledUnmanagedSecrets()
    {
        var remote = new[]
        {
            Remote("db-password"),
            Remote("disabled", enabled: false),
            Remote("cert-backed", managed: true),
            Remote("signing", KeyVaultItemType.Key),
        };

        var plan = OfflineSyncPlanner.Plan([], remote, AllTypes, includeSecretValues: true);

        Assert.Equal(4, plan.Upserts.Count);
        Assert.Equal("db-password", Assert.Single(plan.ValuesToFetch).Name);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void UnchangedVault_IsNoOp()
    {
        var plan = OfflineSyncPlanner.Plan([Local("a", hasValue: true)], [Remote("a")], AllTypes, includeSecretValues: true);

        Assert.Empty(plan.Upserts);
        Assert.Empty(plan.ValuesToFetch);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void NewerUpdatedOn_WithoutVersion_IsDetectedAsChange_AndRefetchesValue()
    {
        var plan = OfflineSyncPlanner.Plan([Local("a", hasValue: true)], [Remote("A", updatedOn: T1.AddMinutes(1))], AllTypes, includeSecretValues: true);

        Assert.Single(plan.Upserts);
        Assert.Single(plan.ValuesToFetch);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void MissingValue_IsFetchedEvenWhenItemUnchanged()
    {
        var plan = OfflineSyncPlanner.Plan([Local("a", hasValue: false)], [Remote("a")], AllTypes, includeSecretValues: true);

        Assert.Empty(plan.Upserts);
        Assert.Single(plan.ValuesToFetch);
    }

    [Fact]
    public void OptedOutVault_NeverFetchesValues()
    {
        var plan = OfflineSyncPlanner.Plan([], [Remote("a")], AllTypes, includeSecretValues: false);

        Assert.Single(plan.Upserts);
        Assert.Empty(plan.ValuesToFetch);
    }

    [Fact]
    public void Deletes_OnlyItemsOfTypesWhoseListingCompleted()
    {
        var local = new[] { Local("gone"), Local("signing", KeyVaultItemType.Key) };

        var plan = OfflineSyncPlanner.Plan(local, [], new HashSet<KeyVaultItemType> { KeyVaultItemType.Secret }, includeSecretValues: false);

        var delete = Assert.Single(plan.Deletes);
        Assert.Equal((KeyVaultItemType.Secret, "gone"), (delete.Type, delete.Name));
    }

    [Fact]
    public void SameNameOfDifferentTypes_AreDistinct_AndDuplicatesAreIgnored()
    {
        var remote = new[] { Remote("app"), Remote("APP"), Remote("app", KeyVaultItemType.Certificate) };

        var plan = OfflineSyncPlanner.Plan([], remote, AllTypes, includeSecretValues: false);

        Assert.Equal(2, plan.Upserts.Count);
    }
}
