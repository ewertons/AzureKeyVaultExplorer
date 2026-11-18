using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys;

namespace AzureKeyVaultStudio.Tests;

public class RequestValidationTests
{
    [Theory]
    [InlineData("my-secret-01", true)]
    [InlineData("", false)]
    [InlineData("has_underscore", false)]
    [InlineData("has space", false)]
    public void ItemNames_FollowKeyVaultRules(string name, bool valid)
    {
        Assert.Equal(valid, NewKeyRequest.IsValidName(name));
    }

    [Fact]
    public void ItemName_LongerThan127Chars_IsInvalid()
    {
        Assert.True(NewKeyRequest.IsValidName(new string('a', 127)));
        Assert.False(NewKeyRequest.IsValidName(new string('a', 128)));
    }

    [Fact]
    public void Key_RejectsBadSizeMissingOperationsAndReversedDates()
    {
        var request = new NewKeyRequest
        {
            Name = "k",
            KeySize = 1024,
            Operations = new HashSet<string>(),
            NotBefore = DateTimeOffset.UtcNow,
            ExpiresOn = DateTimeOffset.UtcNow.AddDays(-1),
        };

        Assert.Equal(["KeySizeInvalidMessage", "KeyOperationsRequiredMessage", "ExpiryBeforeActivationMessage"], request.Validate());
    }

    [Fact]
    public void EcHsmKey_MapsToEcOptions_KeepingOnlyEcOperations()
    {
        var request = new NewKeyRequest
        {
            Name = "k",
            Kind = NewKeyKind.EcHsm,
            Curve = "P-384",
            Operations = new HashSet<string>(["sign", "encrypt"]),
        };

        Assert.Empty(request.Validate());
        var options = Assert.IsType<CreateEcKeyOptions>(request.ToCreateOptions());
        Assert.True(options.HardwareProtected);
        Assert.Equal(KeyCurveName.P384, options.CurveName);
        Assert.Equal([KeyOperation.Sign], options.KeyOperations);
    }

    [Theory]
    [InlineData("example.com", "CN=example.com")]
    [InlineData("CN=app, O=Contoso", "CN=app, O=Contoso")]
    [InlineData("  ", "")]
    public void CertificateSubject_IsNormalized(string input, string expected)
    {
        Assert.Equal(expected, NewCertificateRequest.NormalizeSubject(input));
    }

    [Fact]
    public void CertificateDnsNames_SplitOnCommonSeparators_AndDeduplicate()
    {
        Assert.Equal(["a.com", "b.com", "c.com"], NewCertificateRequest.ParseDnsNames("a.com, b.com;c.com\nA.COM"));
    }

    [Fact]
    public void CertificateGenerate_RequiresSubjectOrDnsName()
    {
        var request = new NewCertificateRequest { Name = "c" };
        Assert.Equal(["CertificateSubjectRequiredMessage"], request.ValidateForGenerate());

        request.DnsNames = "app.contoso.com";
        Assert.Empty(request.ValidateForGenerate());
        var policy = request.ToPolicy();
        Assert.Equal(["app.contoso.com"], policy.SubjectAlternativeNames.DnsNames);
        Assert.Equal(CertificatePolicyAction.AutoRenew, Assert.Single(policy.LifetimeActions).Action);
    }

    [Fact]
    public void CertificateImport_DetectsPem()
    {
        var pem = System.Text.Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----");
        var request = new NewCertificateRequest { Name = "c", ImportData = pem };

        Assert.Empty(request.ValidateForImport());
        Assert.Equal(CertificateContentType.Pem, request.ToImportOptions().Policy.ContentType);
        Assert.False(NewCertificateRequest.LooksLikePem([0x30, 0x82, 0x01]));
    }

    [Theory]
    [InlineData("kv-prod-01", true)]
    [InlineData("ab", false)]
    [InlineData("1vault", false)]
    [InlineData("vault-", false)]
    [InlineData("my--vault", false)]
    [InlineData("abcdefghijklmnopqrstuvwxy", false)]
    public void VaultNames_FollowAzureRules(string name, bool valid)
    {
        Assert.Equal(valid, NewVaultRequest.IsValidName(name));
    }

    [Fact]
    public void Vault_RequiresLocationAndValidRetention()
    {
        var request = new NewVaultRequest { Name = "kv-prod", SoftDeleteRetentionInDays = 3 };

        Assert.Equal(["VaultLocationRequired", "VaultRetentionInvalid"], request.Validate());
    }

    [Fact]
    public void VaultWithAccessPolicies_GrantsCurrentUser_AndOmitsDisabledPurgeProtection()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var request = new NewVaultRequest { Name = "kv-prod", Location = "westus", UseRbacAuthorization = false };

        var properties = request.ToCreateContent(tenant, user).Properties;

        Assert.Null(properties.EnablePurgeProtection);
        var policy = Assert.Single(properties.AccessPolicies);
        Assert.Equal(user.ToString(), policy.ObjectId);
    }

    [Fact]
    public void VaultWithRbac_HasNoAccessPolicies()
    {
        var request = new NewVaultRequest { Name = "kv-prod", Location = "westus", EnablePurgeProtection = true };

        var properties = request.ToCreateContent(Guid.NewGuid(), Guid.NewGuid()).Properties;

        Assert.True(properties.EnableRbacAuthorization);
        Assert.True(properties.EnablePurgeProtection);
        Assert.Empty(properties.AccessPolicies);
    }
}
