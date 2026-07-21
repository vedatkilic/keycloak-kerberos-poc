// Headless PoC: obtaining a token from Keycloak with a Kerberos ticket (no browser)
// Runs on Mac/Linux after kinit; on Windows it is the same code with the domain session.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

var kc       = Environment.GetEnvironmentVariable("KC_BASE")
               ?? "http://keycloak.bank.local:8080/realms/bank/protocol/openid-connect";
var clientId = "boa-desktop";
var redirect = "http://127.0.0.1:43521/cb";
var apiBase  = Environment.GetEnvironmentVariable("API_BASE") ?? "http://localhost:5080";

// --- PKCE ---
static string B64Url(byte[] b) =>
    Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var verifier  = B64Url(RandomNumberGenerator.GetBytes(32));
var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,
    CookieContainer   = new CookieContainer(),
    // Pin the scheme to Negotiate: prevents the NTLM fallback and Basic attempts
    Credentials = new CredentialCache
    {
        { new Uri(kc), "Negotiate", CredentialCache.DefaultNetworkCredentials }
    }
};
using var http = new HttpClient(handler);

Console.WriteLine("1) /auth call (Kerberos handshake is automatic)...");
var authUrl = $"{kc}/auth?client_id={clientId}&response_type=code" +
              $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
              $"&scope=openid&code_challenge={challenge}&code_challenge_method=S256";
var resp = await http.GetAsync(authUrl);

if (resp.StatusCode != HttpStatusCode.Found)
{
    Console.WriteLine($"   Unexpected response: {(int)resp.StatusCode}");
    Console.WriteLine("   If 200 came back, the login form was returned -> was kinit run? is klist empty?");
    Console.WriteLine("   Diagnosis: KRB5_TRACE=/dev/stdout dotnet run");
    return;
}

var code = HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["code"];
if (code is null)
{
    Console.WriteLine($"   Got a 302 but no code. Location: {resp.Headers.Location}");
    return;
}
Console.WriteLine($"   code obtained: {code[..12]}... (no password was asked)");

Console.WriteLine("2) /token call (code -> token)...");
var tokenResp = await http.PostAsync($"{kc}/token", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"]    = "authorization_code",
    ["client_id"]     = clientId,
    ["code"]          = code,
    ["redirect_uri"]  = redirect,
    ["code_verifier"] = verifier
}));
tokenResp.EnsureSuccessStatusCode();
var json = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync()).RootElement;
var access  = json.GetProperty("access_token").GetString()!;
var refresh = json.GetProperty("refresh_token").GetString()!;
Console.WriteLine($"   access_token : {access[..40]}...");
Console.WriteLine($"   refresh_token: {refresh[..40]}...");

// Show the token contents (signature validation is the backend's job; here we just read it)
var payload = access.Split('.')[1];
payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
var claims = JsonDocument.Parse(Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'))).RootElement;
Console.WriteLine($"   user         : {claims.GetProperty("preferred_username")}");
Console.WriteLine($"   audience     : {claims.GetProperty("aud")}");

Console.WriteLine("3) Authorized call to the backend...");
using var api = new HttpClient { BaseAddress = new Uri(apiBase) };
api.DefaultRequestHeaders.Authorization = new("Bearer", access);
try
{
    var hello = await api.GetStringAsync("/api/hello");
    Console.WriteLine($"   API response: {hello}");
}
catch (Exception ex)
{
    Console.WriteLine($"   Could not reach the API ({ex.Message}) - is BackendApi running? (dotnet run)");
}

Console.WriteLine("\nDemo complete: no Windows login, no password, but we have a token.");
