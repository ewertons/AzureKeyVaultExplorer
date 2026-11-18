using System.Text.RegularExpressions;
using Azure.Security.KeyVault.Keys;

namespace AzureKeyVaultStudio.Models;

public enum NewKeyKind
{
    Rsa,
    RsaHsm,
    Ec,
    EcHsm
}

/// <summary>
/// User input for creating a key, translated into the SDK's create options.
/// </summary>
public sealed partial class NewKeyRequest
{
    public static readonly int[] RsaKeySizes = [2048, 3072, 4096];
    public static readonly string[] EcCurves = ["P-256", "P-384", "P-521", "P-256K"];
    public static readonly string[] RsaOperations = ["encrypt", "decrypt", "sign", "verify", "wrapKey", "unwrapKey"];
    public static readonly string[] EcOperations = ["sign", "verify"];

    public string Name { get; set; } = string.Empty;
    public NewKeyKind Kind { get; set; } = NewKeyKind.Rsa;
    public int KeySize { get; set; } = 2048;
    public string Curve { get; set; } = "P-256";
    public ISet<string> Operations { get; set; } = new HashSet<string>(RsaOperations, StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? ExpiresOn { get; set; }
    public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

    public bool IsRsa => Kind is NewKeyKind.Rsa or NewKeyKind.RsaHsm;
    public bool IsHardwareProtected => Kind is NewKeyKind.RsaHsm or NewKeyKind.EcHsm;

    [GeneratedRegex("^[0-9a-zA-Z-]{1,127}$")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    /// <summary>
    /// Returns validation problems as resource keys; empty when the request is valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsValidName(Name))
            errors.Add("ItemNameInvalidMessage");
        if (IsRsa && !RsaKeySizes.Contains(KeySize))
            errors.Add("KeySizeInvalidMessage");
        if (!IsRsa && !EcCurves.Contains(Curve))
            errors.Add("KeyCurveInvalidMessage");
        if (!AllowedOperations().Any(Operations.Contains))
            errors.Add("KeyOperationsRequiredMessage");
        if (NotBefore.HasValue && ExpiresOn.HasValue && ExpiresOn <= NotBefore)
            errors.Add("ExpiryBeforeActivationMessage");
        return errors;
    }

    public IEnumerable<string> AllowedOperations() => IsRsa ? RsaOperations : EcOperations;

    public CreateKeyOptions ToCreateOptions()
    {
        CreateKeyOptions options;
        if (IsRsa)
        {
            options = new CreateRsaKeyOptions(Name, IsHardwareProtected) { KeySize = KeySize };
        }
        else
        {
            options = new CreateEcKeyOptions(Name, IsHardwareProtected) { CurveName = new KeyCurveName(Curve) };
        }

        options.Enabled = Enabled;
        options.NotBefore = NotBefore;
        options.ExpiresOn = ExpiresOn;

        foreach (var op in AllowedOperations().Where(Operations.Contains))
            options.KeyOperations.Add(new KeyOperation(op));

        foreach (var tag in Tags)
            options.Tags[tag.Key] = tag.Value;

        return options;
    }
}
