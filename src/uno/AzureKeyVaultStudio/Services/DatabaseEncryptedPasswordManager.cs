using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AzureKeyVaultStudio.Extensions;
using DeviceId;

namespace AzureKeyVaultStudio.Services;

public static class DatabaseEncryptedPasswordManager
{
    public static async Task<string?> GetSecret()
    {
        if (OperatingSystem.IsWindows())
        {
            byte[] unprotectedKey = GetDataKey();

            string encryptedSecretPath = Path.Combine(Constants.LocalAppDataFolder, Constants.EncryptedSecretFileName);
            if (!File.Exists(encryptedSecretPath))
                return null;

            byte[] combinedData = File.ReadAllBytes(encryptedSecretPath); // Read bytes directly

            using var aes = Aes.Create();
            aes.Key = unprotectedKey;
            aes.Padding = PaddingMode.PKCS7;

            // Extract the IV and the encrypted data
            byte[] iv = new byte[aes.IV.Length];
            Array.Copy(combinedData, 0, iv, 0, iv.Length);
            aes.IV = iv;

            byte[] encryptedSecret = new byte[combinedData.Length - iv.Length];
            Array.Copy(combinedData, iv.Length, encryptedSecret, 0, encryptedSecret.Length);

            using var decryptor = aes.CreateDecryptor();
            byte[] decryptedSecretBytes = decryptor.TransformFinalBlock(encryptedSecret, 0, encryptedSecret.Length);

            return Encoding.UTF8.GetString(decryptedSecretBytes); // Decode bytes consistently
        }
        if (OperatingSystem.IsMacOS())
        {
            string password = await MacOSKeyChainService.GetPasswordAsync(Constants.KeychainSecretName, Constants.KeychainServiceName);
            return password;
        }

        // if linux then use an empty string as the password
        return "";
    }

    public static void SetSecret(string secret)
    {
        if (OperatingSystem.IsMacOS())
        {
            // HACK: is to have a file there rather than checking the keychain on mac.
            string protectedKeyPath = Path.Combine(Constants.LocalAppDataFolder, Constants.ProtectedKeyFileName.Replace("bin", "txt"));
            var dbPassExists = File.Exists(protectedKeyPath);
            if (!dbPassExists)
            {
                File.WriteAllBytes(protectedKeyPath, new byte[1]);
                MacOSKeyChainService.SetPasswordAsync(Constants.KeychainSecretName, Constants.KeychainServiceName, secret);
            }
        }

        //#if WINDOWS
        //#endif
        //#if MACOS
        //#endif

        if (OperatingSystem.IsWindows())
        {
            byte[] unprotectedKey = GetDataKey();

            using var aes = Aes.Create();
            aes.Key = unprotectedKey;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();

            byte[] secretBytes = Encoding.UTF8.GetBytes(secret); // Encode secret consistently
            byte[] encryptedSecret = encryptor.TransformFinalBlock(secretBytes, 0, secretBytes.Length);

            // Store the IV along with the encrypted data
            byte[] iv = aes.IV;
            byte[] combinedData = new byte[iv.Length + encryptedSecret.Length];
            Array.Copy(iv, 0, combinedData, 0, iv.Length);
            Array.Copy(encryptedSecret, 0, combinedData, iv.Length, encryptedSecret.Length);

            string encryptedSecretPath = Path.Combine(Constants.LocalAppDataFolder, Constants.EncryptedSecretFileName);
            File.WriteAllBytes(encryptedSecretPath, combinedData); // Write bytes directly
        }
    }

    /// <summary>
    /// Returns the AES key that encrypts the database password, creating it on first use. The key is protected
    /// with DPAPI in the current user's scope, so other accounts on the same machine can't unprotect it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[] GetDataKey()
    {
        string protectedKeyPath = Path.Combine(Constants.LocalAppDataFolder, Constants.ProtectedKeyFileName);
        byte[] entropy = GetUserEntropy();

        if (File.Exists(protectedKeyPath))
        {
            byte[] protectedKey = File.ReadAllBytes(protectedKeyPath);
            try
            {
                return ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Earlier versions protected the key in the machine scope; move it to the user scope.
                byte[] key = ProtectedData.Unprotect(protectedKey, GetLegacyMachineEntropy(), DataProtectionScope.LocalMachine);
                WriteProtectedKey(protectedKeyPath, key, entropy);
                return key;
            }
        }

        byte[] newKey = RandomNumberGenerator.GetBytes(32);
        WriteProtectedKey(protectedKeyPath, newKey, entropy);
        return newKey;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteProtectedKey(string protectedKeyPath, byte[] key, byte[] entropy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(protectedKeyPath)!);
        byte[] protectedKey = ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser);
        string tempPath = protectedKeyPath + ".tmp";
        File.WriteAllBytes(tempPath, protectedKey);
        File.Move(tempPath, protectedKeyPath, overwrite: true);
    }

    private static string DeviceFileTokenPath => Path.Combine(Constants.LocalAppDataFolder, Constants.DeviceFileTokenName);

    // A random per-install token. It doesn't include the OS version, so Windows updates don't lock the key.
    private static byte[] GetUserEntropy() =>
        new DeviceIdBuilder().AddFileToken(DeviceFileTokenPath).ToString().ToByteArray();

    private static byte[] GetLegacyMachineEntropy() =>
        new DeviceIdBuilder().AddMachineName().AddOsVersion().AddFileToken(DeviceFileTokenPath).ToString().ToByteArray();

    public static void PurgePasswords()
    {
        string dbPath = Path.Combine(Constants.LocalAppDataFolder, Constants.ProtectedKeyFileName);
        if (OperatingSystem.IsMacOS())
        {
            string macosPath = dbPath.Replace("bin", "txt");
            if (File.Exists(macosPath))
            {
                File.Delete(macosPath);
            }
            MacOSKeyChainService.DeleteFromKeychain(Constants.KeychainSecretName, Constants.KeychainServiceName);
        }
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            string encryptedSecretPath = Path.Combine(Constants.LocalAppDataFolder, Constants.EncryptedSecretFileName);
            if (File.Exists(encryptedSecretPath))
                File.Delete(encryptedSecretPath);

            if (File.Exists(Constants.DatabasePasswordFilePath))
                File.Delete(Constants.DatabasePasswordFilePath);

            if (File.Exists(DeviceFileTokenPath))
                File.Delete(DeviceFileTokenPath);
        }
    }
}
