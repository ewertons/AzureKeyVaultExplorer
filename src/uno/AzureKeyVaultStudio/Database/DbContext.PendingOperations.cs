namespace AzureKeyVaultStudio.Database;

/// <summary>
/// Queue of changes waiting to be pushed to Key Vault. Stored in the encrypted database, including the
/// new secret values, until they are pushed or discarded.
/// </summary>
public partial class DbContext
{
    public static async Task<long> AddPendingOperationAsync(PendingOperation operation)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PendingOperations (VaultUri, Kind, ItemType, Name, Value, ContentType, ExpectedExists, ExpectedUpdatedOn, Force, CreatedOn)
            VALUES (@VaultUri, @Kind, @ItemType, @Name, @Value, @ContentType, @ExpectedExists, @ExpectedUpdatedOn, @Force, @CreatedOn);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@VaultUri", operation.VaultUri);
        command.Parameters.AddWithValue("@Kind", (int)operation.Kind);
        command.Parameters.AddWithValue("@ItemType", (int)operation.ItemType);
        command.Parameters.AddWithValue("@Name", operation.Name);
        command.Parameters.AddWithValue("@Value", (object?)operation.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("@ContentType", (object?)operation.ContentType ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExpectedExists", operation.ExpectedExists ? 1 : 0);
        command.Parameters.AddWithValue("@ExpectedUpdatedOn", (object?)FormatDate(operation.ExpectedUpdatedOn) ?? DBNull.Value);
        command.Parameters.AddWithValue("@Force", operation.Force ? 1 : 0);
        command.Parameters.AddWithValue("@CreatedOn", FormatDate(operation.CreatedOn == default ? DateTimeOffset.UtcNow : operation.CreatedOn));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Returns queued changes in queue order, for one vault or for all vaults.</summary>
    public static async Task<List<PendingOperation>> GetPendingOperationsAsync(string? vaultUri = null)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.VaultUri, COALESCE(v.Name, p.VaultUri), p.Kind, p.ItemType, p.Name, p.Value, p.ContentType,
                   p.ExpectedExists, p.ExpectedUpdatedOn, p.Force, p.CreatedOn, p.Attempts, p.NextAttemptOn, p.Status, p.LastError
            FROM PendingOperations p LEFT JOIN OfflineVaults v ON v.VaultUri = p.VaultUri
            WHERE @VaultUri IS NULL OR p.VaultUri = @VaultUri
            ORDER BY p.Id;
            """;
        command.Parameters.AddWithValue("@VaultUri", (object?)vaultUri ?? DBNull.Value);

        var operations = new List<PendingOperation>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            operations.Add(new PendingOperation
            {
                Id = reader.GetInt64(0),
                VaultUri = reader.GetString(1),
                VaultName = reader.GetString(2),
                Kind = (PendingOperationKind)reader.GetInt32(3),
                ItemType = (KeyVaultItemType)reader.GetInt32(4),
                Name = reader.GetString(5),
                Value = reader.IsDBNull(6) ? null : reader.GetString(6),
                ContentType = reader.IsDBNull(7) ? null : reader.GetString(7),
                ExpectedExists = reader.GetInt64(8) != 0,
                ExpectedUpdatedOn = ReadDate(reader, 9),
                Force = reader.GetInt64(10) != 0,
                CreatedOn = ReadDate(reader, 11) ?? DateTimeOffset.MinValue,
                Attempts = reader.GetInt32(12),
                NextAttemptOn = ReadDate(reader, 13),
                Status = (PendingOperationStatus)reader.GetInt32(14),
                LastError = reader.IsDBNull(15) ? null : reader.GetString(15),
            });
        }
        return operations;
    }

    /// <summary>Records the outcome of a push attempt.</summary>
    public static async Task UpdatePendingOperationAsync(long id, PendingOperationStatus status, int attempts, DateTimeOffset? nextAttemptOn, string? lastError)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PendingOperations SET Status = @Status, Attempts = @Attempts, NextAttemptOn = @NextAttemptOn, LastError = @LastError WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@Attempts", attempts);
        command.Parameters.AddWithValue("@NextAttemptOn", (object?)FormatDate(nextAttemptOn) ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastError", (object?)lastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Puts a conflicted or failed change back in the queue, optionally forcing it over remote changes.</summary>
    public static async Task ResetPendingOperationAsync(long id, bool force)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PendingOperations
            SET Status = @Status, Attempts = 0, NextAttemptOn = NULL, LastError = NULL, Force = CASE WHEN @Force = 1 THEN 1 ELSE Force END
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Status", (int)PendingOperationStatus.Pending);
        command.Parameters.AddWithValue("@Force", force ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Changes the state a queued change expects to find in Key Vault before it is pushed.</summary>
    public static async Task RebasePendingOperationAsync(long id, bool expectedExists, DateTimeOffset? expectedUpdatedOn)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PendingOperations SET ExpectedExists = @ExpectedExists, ExpectedUpdatedOn = @ExpectedUpdatedOn WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@ExpectedExists", expectedExists ? 1 : 0);
        command.Parameters.AddWithValue("@ExpectedUpdatedOn", (object?)FormatDate(expectedUpdatedOn) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Replaces the value of a queued, not yet pushed, secret change.</summary>
    public static async Task UpdatePendingSetSecretAsync(long id, string value, string? contentType)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PendingOperations SET Value = @Value, ContentType = @ContentType WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Value", value);
        command.Parameters.AddWithValue("@ContentType", (object?)contentType ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DeletePendingOperationAsync(long id)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingOperations WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Returns the vaults that have queued changes due to run at <paramref name="now"/>.</summary>
    public static async Task<List<string>> GetVaultsWithDuePendingOperationsAsync(DateTimeOffset now)
    {
        await using var connection = await OpenOfflineConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT VaultUri FROM PendingOperations
            WHERE Status = @Status AND (NextAttemptOn IS NULL OR NextAttemptOn <= @Now);
            """;
        command.Parameters.AddWithValue("@Status", (int)PendingOperationStatus.Pending);
        command.Parameters.AddWithValue("@Now", FormatDate(now));

        var vaults = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            vaults.Add(reader.GetString(0));
        return vaults;
    }
}
