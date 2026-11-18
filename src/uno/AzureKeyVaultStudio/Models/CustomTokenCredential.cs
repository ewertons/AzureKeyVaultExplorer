using Azure.Core;
using Microsoft.Identity.Client;

namespace AzureKeyVaultStudio.Models;

/// <summary>
/// Wraps an access token acquired through MSAL. When <c>isAllowedScope</c> is given, the token is only handed
/// out for requests whose scopes all pass it, so it can't be released to a different audience.
/// </summary>
public class CustomTokenCredential : TokenCredential
{
    private readonly string _token;
    private readonly DateTimeOffset _expiresOn;
    private readonly Func<string, bool>? _isAllowedScope;

    public CustomTokenCredential(AuthenticationResult accessResult, Func<string, bool>? isAllowedScope = null)
    {
        _expiresOn = accessResult.ExpiresOn;
        _token = accessResult.AccessToken;
        _isAllowedScope = isAllowedScope;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        EnsureAllowed(requestContext);
        return new AccessToken(_token, _expiresOn);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        EnsureAllowed(requestContext);
        return ValueTask.FromResult(new AccessToken(_token, _expiresOn));
    }

    private void EnsureAllowed(TokenRequestContext requestContext)
    {
        if (_isAllowedScope is null)
            return;

        var scopes = requestContext.Scopes ?? [];
        if (scopes.Length == 0 || !scopes.All(_isAllowedScope))
            throw new InvalidOperationException($"Refusing to provide an access token for the requested scope '{string.Join(" ", scopes)}'.");
    }
}
