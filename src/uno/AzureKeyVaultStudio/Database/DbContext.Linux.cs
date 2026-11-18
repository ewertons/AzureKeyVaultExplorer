using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using AzureKeyVaultStudio.Services;
using Microsoft.Data.Sqlite;

namespace AzureKeyVaultStudio.Database;

public partial class DbContext
{
    private static readonly byte[] PlaintextSqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// Resolves the database password on Linux, creating one on first run. Databases written by earlier
    /// versions (which used no password on Linux) are encrypted in place; databases that can't be opened
    /// are moved aside so the app can start with a fresh one.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static async Task<string> InitializeLinuxDatabasePasswordAsync()
    {
        var password = await DatabaseEncryptedPasswordManager.GetSecret();
        if (string.IsNullOrEmpty(password))
        {
            password = DatabaseEncryptedPasswordManager.GeneratePassword();
            DatabaseEncryptedPasswordManager.SetSecret(password);
        }

        if (!File.Exists(Constants.DatabaseFilePath))
            return password;

        if (IsPlaintextDatabase(Constants.DatabaseFilePath))
        {
            try
            {
                await EncryptPlaintextDatabaseAsync(Constants.DatabaseFilePath, password);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not encrypt the existing database: {ex.Message}");
                MoveDatabaseAside();
            }
        }
        else if (!await CanOpenAsync(Constants.DatabaseFilePath, password))
        {
            MoveDatabaseAside();
        }

        return password;
    }

    private static bool IsPlaintextDatabase(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.SequenceEqual(PlaintextSqliteHeader);
    }

    private static async Task EncryptPlaintextDatabaseAsync(string path, string password)
    {
        var encryptedPath = path + ".encrypting";
        if (File.Exists(encryptedPath))
            File.Delete(encryptedPath);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // ATTACH ... KEY doesn't accept parameters, so the literals are quote-escaped.
            command.CommandText = $"""
                ATTACH DATABASE '{encryptedPath.Replace("'", "''")}' AS encrypted KEY '{password.Replace("'", "''")}';
                SELECT sqlcipher_export('encrypted');
                DETACH DATABASE encrypted;
                """;
            await command.ExecuteNonQueryAsync();
        }

        File.Move(encryptedPath, path, overwrite: true);
    }

    private static async Task<bool> CanOpenAsync(string path, string password)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = password, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync();
            return true;
        }
        catch (SqliteException ex)
        {
            Debug.WriteLine($"Could not open the existing database with the stored password: {ex.Message}");
            return false;
        }
    }

    private static void MoveDatabaseAside()
    {
        var backupPath = $"{Constants.DatabaseFilePath}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(Constants.DatabaseFilePath, backupPath, overwrite: true);
        Debug.WriteLine($"Moved the unreadable database to {backupPath}; a new one will be created.");
    }
}
