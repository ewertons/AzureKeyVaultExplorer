using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace AzureKeyVaultStudio.Database;

/// <summary>
/// Offline copy of vault contents. Lives in the same SQLCipher-encrypted database as the rest of the
/// app data. Secret values are stored only for vaults that opted in, and are never added to the
/// search index.
/// </summary>
public partial class DbContext
{
    private static readonly SemaphoreSlim _offlineSchemaLock = new(1, 1);
    private static bool _offlineSchemaReady;

    private const string OfflineSchema = """
        CREATE TABLE IF NOT EXISTS OfflineVaults (
            VaultUri            TEXT PRIMARY KEY,
            Name                TEXT NOT NULL,
            TenantId            TEXT,
            SubscriptionId      TEXT,
            IncludeSecretValues INTEGER NOT NULL DEFAULT 0,
            LastSyncedOn        TEXT,
            LastSyncError       TEXT
        );
        CREATE TABLE IF NOT EXISTS OfflineItems (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            VaultUri    TEXT    NOT NULL,
            Type        INTEGER NOT NULL,
            Name        TEXT    NOT NULL COLLATE NOCASE,
            ItemId      TEXT    NOT NULL,
            Version     TEXT,
            ContentType TEXT,
            Enabled     INTEGER NOT NULL,
            CreatedOn   TEXT,
            UpdatedOn   TEXT,
            ExpiresOn   TEXT,
            Tags        TEXT,
            SecretValue TEXT,
            UNIQUE (VaultUri, Type, Name)
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS OfflineItemsSearch USING fts5(Name, Tags, ContentType, VaultName, tokenize = 'trigram');
        CREATE TRIGGER IF NOT EXISTS OfflineItems_ai AFTER INSERT ON OfflineItems BEGIN
            INSERT INTO OfflineItemsSearch (rowid, Name, Tags, ContentType, VaultName)
            VALUES (new.Id, new.Name, new.Tags, new.ContentType, (SELECT Name FROM OfflineVaults WHERE VaultUri = new.VaultUri));
        END;
        CREATE TRIGGER IF NOT EXISTS OfflineItems_ad AFTER DELETE ON OfflineItems BEGIN
            DELETE FROM OfflineItemsSearch WHERE rowid = old.Id;
        END;
        CREATE TRIGGER IF NOT EXISTS OfflineItems_au AFTER UPDATE OF Name, Tags, ContentType ON OfflineItems BEGIN
            DELETE FROM OfflineItemsSearch WHERE rowid = old.Id;
            INSERT INTO OfflineItemsSearch (rowid, Name, Tags, ContentType, VaultName)
            VALUES (new.Id, new.Name, new.Tags, new.ContentType, (SELECT Name FROM OfflineVaults WHERE VaultUri = new.VaultUri));
        END;
        """;

    private const string DropOfflineSchema = """
        DROP TRIGGER IF EXISTS OfflineItems_ai;
        DROP TRIGGER IF EXISTS OfflineItems_ad;
        DROP TRIGGER IF EXISTS OfflineItems_au;
        DROP TABLE IF EXISTS OfflineItemsSearch;
        DROP TABLE IF EXISTS OfflineItems;
        DROP TABLE IF EXISTS OfflineVaults;
        """;

    private static async Task<SqliteConnection> OpenOfflineConnectionAsync()
    {
        var connection = await TryCreateDatabaseAndOpenConnection();
        await connection.OpenAsync();

        if (!_offlineSchemaReady)
        {
            await _offlineSchemaLock.WaitAsync();
            try
            {
                if (!_offlineSchemaReady)
                {
                    await ExecuteAsync(connection, OfflineSchema);
                    _offlineSchemaReady = true;
                }
            }
            finally
            {
                _offlineSchemaLock.Release();
            }
        }

        return connection;
    }

    private static async Task DropOfflineTablesAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, DropOfflineSchema);
        _offlineSchemaReady = false;
    }

    public static async Task<List<OfflineVault>> GetOfflineVaultsAsync()
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VaultUri, Name, TenantId, SubscriptionId, IncludeSecretValues, LastSyncedOn, LastSyncError FROM OfflineVaults ORDER BY Name COLLATE NOCASE;";

        var vaults = new List<OfflineVault>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            vaults.Add(new OfflineVault(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                ReadDate(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return vaults;
    }

    public static async Task<OfflineVault?> GetOfflineVaultAsync(string vaultUri) =>
        (await GetOfflineVaultsAsync()).FirstOrDefault(v => string.Equals(v.VaultUri, vaultUri, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a vault to the offline store, or updates its identity fields if it is already there.</summary>
    public static async Task AddOfflineVaultAsync(OfflineVault vault)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OfflineVaults (VaultUri, Name, TenantId, SubscriptionId, IncludeSecretValues)
            VALUES (@VaultUri, @Name, @TenantId, @SubscriptionId, @IncludeSecretValues)
            ON CONFLICT (VaultUri) DO UPDATE SET Name = excluded.Name, TenantId = excluded.TenantId, SubscriptionId = excluded.SubscriptionId;
            """;
        command.Parameters.AddWithValue("@VaultUri", vault.VaultUri);
        command.Parameters.AddWithValue("@Name", vault.Name);
        command.Parameters.AddWithValue("@TenantId", (object?)vault.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubscriptionId", (object?)vault.SubscriptionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@IncludeSecretValues", vault.IncludeSecretValues ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Turns storing of secret values on or off for a vault. Turning it off deletes stored values immediately.</summary>
    public static async Task SetOfflineVaultIncludeSecretValuesAsync(string vaultUri, bool include)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@VaultUri", vaultUri);
        command.Parameters.AddWithValue("@Include", include ? 1 : 0);
        command.CommandText = include
            ? "UPDATE OfflineVaults SET IncludeSecretValues = @Include WHERE VaultUri = @VaultUri;"
            : "UPDATE OfflineVaults SET IncludeSecretValues = @Include WHERE VaultUri = @VaultUri; UPDATE OfflineItems SET SecretValue = NULL WHERE VaultUri = @VaultUri;";
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public static async Task RecordOfflineVaultSyncAsync(string vaultUri, DateTimeOffset? syncedOn, string? error)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE OfflineVaults SET LastSyncedOn = COALESCE(@SyncedOn, LastSyncedOn), LastSyncError = @Error WHERE VaultUri = @VaultUri;";
        command.Parameters.AddWithValue("@VaultUri", vaultUri);
        command.Parameters.AddWithValue("@SyncedOn", (object?)FormatDate(syncedOn) ?? DBNull.Value);
        command.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task RemoveOfflineVaultAsync(string vaultUri)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM OfflineItems WHERE VaultUri = @VaultUri; DELETE FROM OfflineVaults WHERE VaultUri = @VaultUri;";
        command.Parameters.AddWithValue("@VaultUri", vaultUri);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public static async Task<List<LocalItemState>> GetOfflineItemStatesAsync(string vaultUri)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Type, Name, Version, UpdatedOn, SecretValue IS NOT NULL FROM OfflineItems WHERE VaultUri = @VaultUri;";
        command.Parameters.AddWithValue("@VaultUri", vaultUri);

        var items = new List<LocalItemState>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new LocalItemState(
                (KeyVaultItemType)reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ReadDate(reader, 3),
                reader.GetInt64(4) != 0));
        }
        return items;
    }

    /// <summary>Applies a sync plan in a single transaction.</summary>
    /// <param name="secretValues">Fetched secret values, keyed by secret name.</param>
    public static async Task ApplyOfflineSyncAsync(string vaultUri, OfflineSyncPlan plan, IReadOnlyDictionary<string, string> secretValues)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            // Any change may mean a new version, which invalidates the stored value; it's re-set below if it was fetched.
            upsert.CommandText = """
                INSERT INTO OfflineItems (VaultUri, Type, Name, ItemId, Version, ContentType, Enabled, CreatedOn, UpdatedOn, ExpiresOn, Tags)
                VALUES (@VaultUri, @Type, @Name, @ItemId, @Version, @ContentType, @Enabled, @CreatedOn, @UpdatedOn, @ExpiresOn, @Tags)
                ON CONFLICT (VaultUri, Type, Name) DO UPDATE SET
                    Name = excluded.Name, ItemId = excluded.ItemId, ContentType = excluded.ContentType, Enabled = excluded.Enabled,
                    CreatedOn = excluded.CreatedOn, ExpiresOn = excluded.ExpiresOn, Tags = excluded.Tags,
                    SecretValue = CASE WHEN OfflineItems.Version IS excluded.Version AND OfflineItems.UpdatedOn IS excluded.UpdatedOn
                                       THEN OfflineItems.SecretValue ELSE NULL END,
                    Version = excluded.Version, UpdatedOn = excluded.UpdatedOn;
                """;
            var p = upsert.Parameters;
            p.AddWithValue("@VaultUri", vaultUri);
            var type = p.Add("@Type", SqliteType.Integer);
            var name = p.Add("@Name", SqliteType.Text);
            var itemId = p.Add("@ItemId", SqliteType.Text);
            var version = p.Add("@Version", SqliteType.Text);
            var contentType = p.Add("@ContentType", SqliteType.Text);
            var enabled = p.Add("@Enabled", SqliteType.Integer);
            var createdOn = p.Add("@CreatedOn", SqliteType.Text);
            var updatedOn = p.Add("@UpdatedOn", SqliteType.Text);
            var expiresOn = p.Add("@ExpiresOn", SqliteType.Text);
            var tags = p.Add("@Tags", SqliteType.Text);

            foreach (var item in plan.Upserts)
            {
                type.Value = (int)item.Type;
                name.Value = item.Name;
                itemId.Value = item.ItemId;
                version.Value = (object?)item.Version ?? DBNull.Value;
                contentType.Value = (object?)item.ContentType ?? DBNull.Value;
                enabled.Value = item.Enabled ? 1 : 0;
                createdOn.Value = (object?)FormatDate(item.CreatedOn) ?? DBNull.Value;
                updatedOn.Value = (object?)FormatDate(item.UpdatedOn) ?? DBNull.Value;
                expiresOn.Value = (object?)FormatDate(item.ExpiresOn) ?? DBNull.Value;
                tags.Value = (object?)SerializeTags(item.Tags) ?? DBNull.Value;
                await upsert.ExecuteNonQueryAsync();
            }
        }

        await using (var setValue = connection.CreateCommand())
        {
            setValue.Transaction = transaction;
            setValue.CommandText = "UPDATE OfflineItems SET SecretValue = @Value WHERE VaultUri = @VaultUri AND Type = @Type AND Name = @Name;";
            setValue.Parameters.AddWithValue("@VaultUri", vaultUri);
            setValue.Parameters.AddWithValue("@Type", (int)KeyVaultItemType.Secret);
            var name = setValue.Parameters.Add("@Name", SqliteType.Text);
            var value = setValue.Parameters.Add("@Value", SqliteType.Text);
            foreach (var (secretName, secretValue) in secretValues)
            {
                name.Value = secretName;
                value.Value = secretValue;
                await setValue.ExecuteNonQueryAsync();
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM OfflineItems WHERE VaultUri = @VaultUri AND Type = @Type AND Name = @Name;";
            delete.Parameters.AddWithValue("@VaultUri", vaultUri);
            var type = delete.Parameters.Add("@Type", SqliteType.Integer);
            var name = delete.Parameters.Add("@Name", SqliteType.Text);
            foreach (var (itemType, itemName) in plan.Deletes)
            {
                type.Value = (int)itemType;
                name.Value = itemName;
                await delete.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Case-insensitive substring search over item name, tags, content type and vault name. Every
    /// whitespace-separated term must match at least one of those fields.
    /// </summary>
    public static async Task<List<OfflineItem>> SearchOfflineItemsAsync(string? query, int limit = 500)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();

        var sql = new StringBuilder("""
            SELECT i.Id, i.VaultUri, v.Name, i.Type, i.Name, i.ItemId, i.Version, i.ContentType, i.Enabled,
                   i.CreatedOn, i.UpdatedOn, i.ExpiresOn, i.Tags, i.SecretValue IS NOT NULL
            FROM OfflineItems i JOIN OfflineVaults v ON v.VaultUri = i.VaultUri
            """);

        var terms = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 0)
        {
            sql.Append(" WHERE i.Id IN (SELECT rowid FROM OfflineItemsSearch WHERE ");
            for (var i = 0; i < terms.Length; i++)
            {
                if (i > 0)
                    sql.Append(" AND ");
                sql.Append($"(Name LIKE @t{i} OR Tags LIKE @t{i} OR ContentType LIKE @t{i} OR VaultName LIKE @t{i})");
                command.Parameters.AddWithValue($"@t{i}", $"%{terms[i]}%");
            }
            sql.Append(')');
        }

        sql.Append(" ORDER BY v.Name COLLATE NOCASE, i.Name COLLATE NOCASE, i.Type LIMIT @Limit;");
        command.Parameters.AddWithValue("@Limit", limit);
        command.CommandText = sql.ToString();

        var items = new List<OfflineItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new OfflineItem
            {
                Id = reader.GetInt64(0),
                VaultUri = reader.GetString(1),
                VaultName = reader.GetString(2),
                Type = (KeyVaultItemType)reader.GetInt32(3),
                Name = reader.GetString(4),
                ItemId = reader.GetString(5),
                Version = reader.IsDBNull(6) ? null : reader.GetString(6),
                ContentType = reader.IsDBNull(7) ? null : reader.GetString(7),
                Enabled = reader.GetInt64(8) != 0,
                CreatedOn = ReadDate(reader, 9),
                UpdatedOn = ReadDate(reader, 10),
                ExpiresOn = ReadDate(reader, 11),
                Tags = DeserializeTags(reader.IsDBNull(12) ? null : reader.GetString(12)),
                HasSecretValue = reader.GetInt64(13) != 0,
            });
        }
        return items;
    }

    public static async Task<string?> GetOfflineSecretValueAsync(long itemId)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SecretValue FROM OfflineItems WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", itemId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string? FormatDate(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static string? SerializeTags(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
            return null;

        var json = new JsonObject();
        foreach (var (key, value) in tags)
            json[key] = value;
        // Keep non-ASCII characters literal so they remain searchable.
        return json.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    internal static IReadOnlyDictionary<string, string> DeserializeTags(string? json)
    {
        var tags = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonObject obj)
            return tags;

        foreach (var (key, value) in obj)
            tags[key] = value?.GetValue<string>() ?? string.Empty;
        return tags;
    }
}
