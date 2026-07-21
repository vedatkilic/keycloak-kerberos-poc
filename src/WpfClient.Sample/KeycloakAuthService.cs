// Production client reference: layered, windowless token acquisition.
// Layer 1: headless Negotiate (same logic as ConsoleClient)
// Layer 2: invisible WebView2; a window is shown only if interaction is required.
using IdentityModel.OidcClient;

namespace WpfClient.Sample;

public class KeycloakAuthService
{
    private readonly OidcClient _oidc = new(new OidcClientOptions
    {
        Authority   = "https://keycloak.bank.local/realms/bank",
        ClientId    = "boa-desktop",
        Scope       = "openid profile offline_access",
        RedirectUri = "http://127.0.0.1/boa-callback",  // never visited; acts as a marker
        Browser     = new LayeredBrowser()
    });

    public async Task<HttpClient> SignInAndCreateApiClientAsync()
    {
        var result = await _oidc.LoginAsync();
        if (result.IsError)
            throw new InvalidOperationException($"Authentication failed: {result.Error}");

        // Token injection + silent refresh + 401 retry: the library's handler does it
        return new HttpClient(result.RefreshTokenHandler)
        {
            BaseAddress = new Uri("https://boa-api.bank.local")
        };
    }
}
