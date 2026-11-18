using Azure.Security.KeyVault.Certificates;

namespace AzureKeyVaultStudio.Models;

public enum NewCertificateKeyKind
{
    Rsa,
    RsaHsm,
    Ec,
    EcHsm
}

/// <summary>
/// User input for generating a self-signed certificate or importing an existing one.
/// </summary>
public sealed class NewCertificateRequest
{
    public static readonly int[] RsaKeySizes = [2048, 3072, 4096];
    public static readonly string[] EcCurves = ["P-256", "P-384", "P-521", "P-256K"];

    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

    // Generate
    public string Subject { get; set; } = string.Empty;
    public string DnsNames { get; set; } = string.Empty;
    public int ValidityInMonths { get; set; } = 12;
    public NewCertificateKeyKind KeyKind { get; set; } = NewCertificateKeyKind.Rsa;
    public int KeySize { get; set; } = 2048;
    public string Curve { get; set; } = "P-256";
    public bool Pem { get; set; }
    public bool Exportable { get; set; } = true;
    public bool AutoRenew { get; set; } = true;

    // Import
    public byte[]? ImportData { get; set; }
    public string? ImportPassword { get; set; }

    public bool IsRsa => KeyKind is NewCertificateKeyKind.Rsa or NewCertificateKeyKind.RsaHsm;

    /// <summary>Normalizes a plain host/name to "CN=..."; full distinguished names are kept as-is.</summary>
    public static string NormalizeSubject(string? subject)
    {
        var s = subject?.Trim() ?? string.Empty;
        if (s.Length == 0)
            return s;
        return s.Contains('=') ? s : $"CN={s}";
    }

    public static IReadOnlyList<string> ParseDnsNames(string? dnsNames) =>
        (dnsNames ?? string.Empty)
            .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Returns true when the bytes look like an ASCII PEM file (otherwise PKCS#12 is assumed).
    /// </summary>
    public static bool LooksLikePem(byte[] data)
    {
        var length = Math.Min(data.Length, 4096);
        var head = System.Text.Encoding.ASCII.GetString(data, 0, length);
        return head.Contains("-----BEGIN ", StringComparison.Ordinal);
    }

    public IReadOnlyList<string> ValidateForGenerate()
    {
        var errors = new List<string>();
        if (!NewKeyRequest.IsValidName(Name))
            errors.Add("ItemNameInvalidMessage");
        if (string.IsNullOrWhiteSpace(Subject) && ParseDnsNames(DnsNames).Count == 0)
            errors.Add("CertificateSubjectRequiredMessage");
        if (ValidityInMonths is < 1 or > 1200)
            errors.Add("CertificateValidityInvalidMessage");
        if (IsRsa && !RsaKeySizes.Contains(KeySize))
            errors.Add("KeySizeInvalidMessage");
        if (!IsRsa && !EcCurves.Contains(Curve))
            errors.Add("KeyCurveInvalidMessage");
        return errors;
    }

    public IReadOnlyList<string> ValidateForImport()
    {
        var errors = new List<string>();
        if (!NewKeyRequest.IsValidName(Name))
            errors.Add("ItemNameInvalidMessage");
        if (ImportData is null || ImportData.Length == 0)
            errors.Add("CertificateFileRequiredMessage");
        return errors;
    }

    public CertificatePolicy ToPolicy()
    {
        var subject = NormalizeSubject(Subject);
        var dns = ParseDnsNames(DnsNames);

        CertificatePolicy policy;
        if (dns.Count > 0)
        {
            var sans = new SubjectAlternativeNames();
            foreach (var name in dns)
                sans.DnsNames.Add(name);
            policy = subject.Length > 0
                ? new CertificatePolicy(WellKnownIssuerNames.Self, subject, sans)
                : new CertificatePolicy(WellKnownIssuerNames.Self, sans);
        }
        else
        {
            policy = new CertificatePolicy(WellKnownIssuerNames.Self, subject);
        }

        policy.ValidityInMonths = ValidityInMonths;
        policy.ContentType = Pem ? CertificateContentType.Pem : CertificateContentType.Pkcs12;
        policy.Exportable = Exportable;
        policy.KeyType = KeyKind switch
        {
            NewCertificateKeyKind.Rsa => CertificateKeyType.Rsa,
            NewCertificateKeyKind.RsaHsm => CertificateKeyType.RsaHsm,
            NewCertificateKeyKind.Ec => CertificateKeyType.Ec,
            _ => CertificateKeyType.EcHsm
        };
        if (IsRsa)
            policy.KeySize = KeySize;
        else
            policy.KeyCurveName = new CertificateKeyCurveName(Curve);

        policy.LifetimeActions.Add(new LifetimeAction(AutoRenew ? CertificatePolicyAction.AutoRenew : CertificatePolicyAction.EmailContacts)
        {
            LifetimePercentage = 80
        });

        return policy;
    }

    public ImportCertificateOptions ToImportOptions()
    {
        ArgumentNullException.ThrowIfNull(ImportData);
        var options = new ImportCertificateOptions(Name, ImportData)
        {
            Enabled = Enabled,
            Password = string.IsNullOrEmpty(ImportPassword) ? null : ImportPassword,
        };
        if (LooksLikePem(ImportData))
            options.Policy = new CertificatePolicy { ContentType = CertificateContentType.Pem };

        foreach (var tag in Tags)
            options.Tags[tag.Key] = tag.Value;
        return options;
    }
}
