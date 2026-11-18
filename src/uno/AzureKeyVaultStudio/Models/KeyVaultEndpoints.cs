namespace AzureKeyVaultStudio.Models;

/// <summary>Known Key Vault and Managed HSM endpoints across the Azure clouds.</summary>
public static class KeyVaultEndpoints
{
    private static readonly string[] ServiceHosts =
    [
        "vault.azure.net",
        "vault.azure.cn",
        "vault.usgovcloudapi.net",
        "vault.microsoftazure.de",
        "managedhsm.azure.net",
        "managedhsm.azure.cn",
        "managedhsm.usgovcloudapi.net",
    ];

    /// <summary>True for an https URI whose host is a vault or managed HSM (e.g. https://contoso.vault.azure.net/).</summary>
    public static bool IsKeyVaultUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && ServiceHosts.Any(host => uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when an OAuth scope is for the Key Vault service itself (e.g. https://vault.azure.net/.default).
    /// The Key Vault SDK derives the scope from the server's authentication challenge, so a host
    /// pretending to be a vault could otherwise ask for a token for any audience.
    /// </summary>
    public static bool IsKeyVaultScope(string? scope)
    {
        if (string.IsNullOrEmpty(scope))
            return false;

        var resource = scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase) ? scope[..^"/.default".Length] : scope;
        return Uri.TryCreate(resource, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.Trim('/').Length == 0
            && string.IsNullOrEmpty(uri.Query)
            && ServiceHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
