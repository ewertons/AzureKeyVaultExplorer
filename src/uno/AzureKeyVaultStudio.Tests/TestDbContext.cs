using Microsoft.Data.Sqlite;

namespace AzureKeyVaultStudio.Database;

/// <summary>
/// Test replacement for the app's connection factory (which resolves a per-user password and path):
/// an encrypted SQLCipher database at a path each test sets. Like the app, the connection is returned unopened.
/// </summary>
public partial class DbContext
{
    private static string _testDatabasePath = string.Empty;

    /// <summary>Points the offline store at a new database file; the schema is created on next use.</summary>
    public static string TestDatabasePath
    {
        get => _testDatabasePath;
        set
        {
            _testDatabasePath = value;
            _offlineSchemaReady = false;
        }
    }

    private static Task<SqliteConnection> TryCreateDatabaseAndOpenConnection() =>
        Task.FromResult(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = TestDatabasePath,
            Password = "test-password",
        }.ToString()));
}
