using System.Text.RegularExpressions;
using Azure.Core;
using Azure.ResourceManager.KeyVault.Models;

namespace AzureKeyVaultStudio.Models;

/// <summary>
/// Everything needed to create a new key vault through ARM.
/// </summary>
public sealed partial class NewVaultRequest
{
    public const int MinSoftDeleteRetentionDays = 7;
    public const int MaxSoftDeleteRetentionDays = 90;

    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public bool PremiumSku { get; set; }
    public bool UseRbacAuthorization { get; set; } = true;
    public int SoftDeleteRetentionInDays { get; set; } = MaxSoftDeleteRetentionDays;
    public bool EnablePurgeProtection { get; set; }
    public bool GrantCurrentUserAccess { get; set; } = true;
    public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

    // 3-24 chars, starts with a letter, ends with a letter or digit, no consecutive hyphens.
    [GeneratedRegex("^(?=.{3,24}$)(?!.*--)[a-zA-Z][a-zA-Z0-9-]*[a-zA-Z0-9]$")]
    private static partial Regex VaultNameRegex();

    public static bool IsValidName(string? name) => !string.IsNullOrEmpty(name) && VaultNameRegex().IsMatch(name);

    /// <summary>
    /// Returns resource keys describing each validation error.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsValidName(Name))
            errors.Add("VaultNameInvalid");
        if (string.IsNullOrWhiteSpace(Location))
            errors.Add("VaultLocationRequired");
        if (SoftDeleteRetentionInDays is < MinSoftDeleteRetentionDays or > MaxSoftDeleteRetentionDays)
            errors.Add("VaultRetentionInvalid");
        return errors;
    }

    public KeyVaultCreateOrUpdateContent ToCreateContent(Guid tenantId, Guid? currentUserObjectId)
    {
        var sku = new KeyVaultSku(KeyVaultSkuFamily.A, PremiumSku ? KeyVaultSkuName.Premium : KeyVaultSkuName.Standard);
        var properties = new KeyVaultProperties(tenantId, sku)
        {
            EnableRbacAuthorization = UseRbacAuthorization,
            EnableSoftDelete = true,
            SoftDeleteRetentionInDays = SoftDeleteRetentionInDays,
            // The service rejects an explicit "false"; omitting it leaves purge protection off.
            EnablePurgeProtection = EnablePurgeProtection ? true : null,
        };

        if (!UseRbacAuthorization && GrantCurrentUserAccess && currentUserObjectId is Guid objectId)
        {
            var permissions = new IdentityAccessPermissions();
            permissions.Keys.Add(IdentityAccessKeyPermission.All);
            permissions.Secrets.Add(IdentityAccessSecretPermission.All);
            permissions.Certificates.Add(IdentityAccessCertificatePermission.All);
            properties.AccessPolicies.Add(new KeyVaultAccessPolicy(tenantId, objectId.ToString(), permissions));
        }

        var content = new KeyVaultCreateOrUpdateContent(new AzureLocation(Location!), properties);
        foreach (var tag in Tags)
            content.Tags[tag.Key] = tag.Value;
        return content;
    }
}
