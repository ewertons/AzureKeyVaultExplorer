using Azure.Core;
using Microsoft.Identity.Client;

namespace AzureKeyVaultStudio.Tests;

public class KeyVaultEndpointsTests
{
    [Theory]
    [InlineData("https://contoso.vault.azure.net/")]
    [InlineData("https://CONTOSO.VAULT.AZURE.NET")]
    [InlineData("https://contoso.vault.azure.cn/")]
    [InlineData("https://contoso.vault.usgovcloudapi.net/")]
    [InlineData("https://contoso.managedhsm.azure.net/")]
    public void IsKeyVaultUri_AcceptsVaultHosts(string uri)
    {
        Assert.True(KeyVaultEndpoints.IsKeyVaultUri(new Uri(uri)));
    }

    [Theory]
    [InlineData("http://contoso.vault.azure.net/")]
    [InlineData("https://contoso.vault.azure.net:8443/")]
    [InlineData("https://contoso.vault.azure.net.evil.com/")]
    [InlineData("https://evilvault.azure.net/")]
    [InlineData("https://vault.azure.net/")]
    [InlineData("https://user@contoso.vault.azure.net/")]
    [InlineData("https://management.azure.com/")]
    public void IsKeyVaultUri_RejectsOtherHosts(string uri)
    {
        Assert.False(KeyVaultEndpoints.IsKeyVaultUri(new Uri(uri)));
    }

    [Theory]
    [InlineData("https://vault.azure.net/.default")]
    [InlineData("https://vault.azure.net//.default")]
    [InlineData("https://vault.azure.net")]
    [InlineData("https://vault.usgovcloudapi.net/.default")]
    [InlineData("https://managedhsm.azure.net/.default")]
    public void IsKeyVaultScope_AcceptsKeyVaultResources(string scope)
    {
        Assert.True(KeyVaultEndpoints.IsKeyVaultScope(scope));
    }

    [Theory]
    [InlineData("https://management.azure.com/.default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("https://vault.azure.net.evil.com/.default")]
    [InlineData("https://contoso.vault.azure.net/.default")]
    [InlineData("https://vault.azure.net/other/.default")]
    [InlineData("http://vault.azure.net/.default")]
    [InlineData("")]
    public void IsKeyVaultScope_RejectsOtherAudiences(string scope)
    {
        Assert.False(KeyVaultEndpoints.IsKeyVaultScope(scope));
    }

    private static AuthenticationResult Token() =>
        new("token-value", false, null, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1), "tenant", null, null, ["https://vault.azure.net/.default"], Guid.NewGuid());

    [Fact]
    public async Task KeyVaultCredential_OnlyReleasesTokenForKeyVaultScopes()
    {
        var credential = new CustomTokenCredential(Token(), KeyVaultEndpoints.IsKeyVaultScope);

        Assert.Equal("token-value", credential.GetToken(new TokenRequestContext(["https://vault.azure.net/.default"]), default).Token);
        Assert.Throws<InvalidOperationException>(() => credential.GetToken(new TokenRequestContext(["https://management.azure.com/.default"]), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await credential.GetTokenAsync(new TokenRequestContext(["https://vault.azure.net/.default", "https://graph.microsoft.com/.default"]), default));
    }
}
