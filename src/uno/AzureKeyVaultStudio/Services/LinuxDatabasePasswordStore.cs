using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AzureKeyVaultStudio.Services;

/// <summary>
/// Stores the database password in the Linux Secret Service keyring (libsecret), falling back to a
/// user-only (0600) file when no keyring is available.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxDatabasePasswordStore
{
    private static string FallbackFilePath => Path.Combine(Constants.LocalAppDataFolder, Constants.LinuxDatabasePasswordFallbackFileName);

    private static Storage CreateKeyringStorage()
    {
        var properties = new StorageCreationPropertiesBuilder(Constants.LinuxDatabaseKeyRingMarkerFileName, Constants.LocalAppDataFolder)
            .WithLinuxKeyring(
                Constants.LinuxDatabaseKeyRingSchema,
                Constants.LinuxKeyRingCollection,
                Constants.LinuxDatabaseKeyRingLabel,
                Constants.LinuxKeyRingAttr1,
                Constants.LinuxKeyRingAttr2)
            .Build();
        return Storage.Create(properties);
    }

    public static string? Read()
    {
        try
        {
            var data = CreateKeyringStorage().ReadData();
            if (data.Length > 0)
                return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Linux keyring unavailable for reading the database password: {ex.Message}");
        }

        return File.Exists(FallbackFilePath) ? File.ReadAllText(FallbackFilePath) : null;
    }

    public static void Write(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var storage = CreateKeyringStorage();
            storage.WriteData(bytes);
            if (storage.ReadData().AsSpan().SequenceEqual(bytes))
            {
                DeleteFallbackFile();
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Linux keyring unavailable for storing the database password, using a protected file instead: {ex.Message}");
        }

        WriteFallbackFile(password);
    }

    public static void Clear()
    {
        try
        {
            CreateKeyringStorage().Clear(ignoreExceptions: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not clear the database password from the Linux keyring: {ex.Message}");
        }

        DeleteFallbackFile();
    }

    private static void WriteFallbackFile(string password)
    {
        Directory.CreateDirectory(Constants.LocalAppDataFolder);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var stream = new FileStream(FallbackFilePath, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(password);
        }

        // UnixCreateMode only applies to new files; tighten permissions on a pre-existing one too.
        File.SetUnixFileMode(FallbackFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void DeleteFallbackFile()
    {
        if (File.Exists(FallbackFilePath))
            File.Delete(FallbackFilePath);
    }
}
