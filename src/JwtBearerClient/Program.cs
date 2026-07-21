// PoC: RFC 7523 JWT Bearer grant (Keycloak 26.5+ "JWT Authorization Grant").
// This console app plays the role of the trusted backend (in production: the WCF
// service). It signs its OWN JWT assertion with a private key and exchanges it at
// Keycloak's token endpoint for a real access token — no user password, no browser.
//   assertion (signed by us) --> Keycloak /token (grant_type=jwt-bearer) --> access_token
// Keycloak validates the signature against the wcf-issuer identity provider, resolves
// the asserted user via the federated-identity link, and issues the token.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// KC_BASE points at the realm's OIDC base; the assertion audience is the realm issuer URL.
var kcBase   = Environment.GetEnvironmentVariable("KC_BASE")
               ?? "http://keycloak.bank.local:8080/realms/bank/protocol/openid-connect";
var realmIssuer = kcBase.Replace("/protocol/openid-connect", "");
var apiBase  = Environment.GetEnvironmentVariable("API_BASE") ?? "http://localhost:5080";

var issuer   = Environment.GetEnvironmentVariable("WCF_ISSUER") ?? "https://token-issuer.bank.local";
var subject  = Environment.GetEnvironmentVariable("WCF_SUBJECT") ?? "wcf-vedat-001"; // external user id
var clientId = "boa-wcf";
var secret   = Environment.GetEnvironmentVariable("CLIENT_SECRET") ?? "wcf-demo-secret";
var keyId    = "wcf-demo-key";

static string B64Url(byte[] b) =>
    Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

// --- Locate and load the demo signing key (pki/wcf-issuer.key), walking up from cwd ---
static string FindKey()
{
    var explicitPath = Environment.GetEnvironmentVariable("WCF_KEY");
    if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "pki", "wcf-issuer.key");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    throw new FileNotFoundException("pki/wcf-issuer.key not found (set WCF_KEY to override).");
}

using var rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(FindKey()));

// --- Build and sign the assertion JWT (RS256) ---
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var header  = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = keyId };
var payload = new Dictionary<string, object>
{
    ["iss"] = issuer,
    ["sub"] = subject,
    ["aud"] = realmIssuer,
    ["iat"] = now,
    ["exp"] = now + 300,
    ["jti"] = Guid.NewGuid().ToString("N"),
    ["preferred_username"] = subject,
};
var signingInput = B64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                   B64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput),
                             HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var assertion = signingInput + "." + B64Url(signature);

Console.WriteLine("1) Assertion imzalandi (WCF backend'in private key'i ile).");
Console.WriteLine($"   iss={issuer}  sub={subject}  aud={realmIssuer}");

// --- Exchange the assertion for a Keycloak access token (RFC 7523) ---
Console.WriteLine("2) /token cagrisi (grant_type=jwt-bearer)...");
using var http = new HttpClient();
var tokenResp = await http.PostAsync($"{kcBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"]    = "urn:ietf:params:oauth:grant-type:jwt-bearer",
    ["assertion"]     = assertion,
    ["client_id"]     = clientId,
    ["client_secret"] = secret,
    ["scope"]         = "openid",
}));

var body = await tokenResp.Content.ReadAsStringAsync();
if (!tokenResp.IsSuccessStatusCode)
{
    Console.WriteLine($"   Token alinamadi ({(int)tokenResp.StatusCode}): {body}");
    Console.WriteLine("   Ipucu: setup-realm.sh calisti mi? Kullanici wcf-issuer'a linkli mi?");
    return;
}
var json = JsonDocument.Parse(body).RootElement;
var access = json.GetProperty("access_token").GetString()!;
Console.WriteLine($"   access_token : {access[..40]}... (parola/tarayici yok)");

// Peek at the resolved identity (signature verification is the backend's job).
var claimsPart = access.Split('.')[1];
claimsPart = claimsPart.PadRight(claimsPart.Length + (4 - claimsPart.Length % 4) % 4, '=');
var claims = JsonDocument.Parse(
    Convert.FromBase64String(claimsPart.Replace('-', '+').Replace('_', '/'))).RootElement;
Console.WriteLine($"   kullanici    : {claims.GetProperty("preferred_username")}");
Console.WriteLine($"   audience     : {claims.GetProperty("aud")}");

// --- Call the backend with the Keycloak-issued token ---
Console.WriteLine("3) Backend'e yetkili cagri...");
using var api = new HttpClient { BaseAddress = new Uri(apiBase) };
api.DefaultRequestHeaders.Authorization = new("Bearer", access);
try
{
    var hello = await api.GetStringAsync("/api/hello");
    Console.WriteLine($"   API cevabi: {hello}");
}
catch (Exception ex)
{
    Console.WriteLine($"   API'ye ulasilamadi ({ex.Message}) - BackendApi calisiyor mu?");
}

Console.WriteLine("\nDemo tamam: WCF backend kendi JWT'sini uretti, Keycloak gercek token'a cevirdi.");
