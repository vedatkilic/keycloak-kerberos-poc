**English** · [Türkçe](README.tr.md)

# Keycloak Kerberos SSO — PoC (macOS / Linux / Windows)

A demo that shows **passwordless (Kerberos SSO) Keycloak integration** end to end. The client code
(ConsoleClient, WpfClient) and the whole flow run on **macOS, Linux, and Windows** alike — Windows is
in fact the production target. The `(macOS / Linux / Windows)` note only means the Docker demo harness
has been exercised on all three; nothing here is macOS/Linux-only.

The demo can be run without a Windows domain environment: what "the user logging into the domain in the
morning" is on Windows, `kinit` is here; the rest of the flow uses the same protocol as production. On
a real domain-joined Windows PC you skip `kinit` entirely — see
[Testing from a domain-joined Windows PC](#testing-from-a-domain-joined-windows-pc-already-logged-in).

    Mac (client: kinit + .NET console / Chrome)
        |-- :88  Kerberos ------> KDC (Docker, MIT Kerberos)
        |-- :8080 HTTP ---------> Keycloak (Docker, SPNEGO via keytab)
        |-- :5080 HTTP ---------> BackendApi (dotnet, JWT bearer)

Two passwordless methods are demonstrated against the same backend:
1. **Kerberos SSO** — the *user* is authenticated (a desktop app, public client + PKCE).
2. **JWT Bearer grant (RFC 7523)** — a trusted *backend service* authenticates itself by
   signing its own JWT assertion (see [Method 2](#method-2--jwt-bearer-grant-rfc-7523)).

## Requirements
- Docker Desktop
- .NET 9 SDK
- On macOS the Kerberos tools are already available (kinit/klist); on Linux install `krb5-user`.
- Keycloak 26.7 (pinned in docker-compose.yml). The JWT Bearer grant needs 26.5+ and the
  `jwt-authorization-grant` feature, already enabled in the compose command.

## Setup (once)

    docker compose up -d --build          # KDC + Keycloak
    ./keycloak/setup-realm.sh             # realm, Kerberos federation, boa-desktop + JWT Bearer (wcf-issuer, boa-wcf)
    ./client-mac/setup-mac.sh             # /etc/hosts + krb5 config + Chrome permission

## Demo flow

    export KRB5_CONFIG=$PWD/client-mac/krb5-client.conf

    # 1) We have not "logged in" yet -> the flow falls back to the login form (proof of fallback)
    kdestroy 2>/dev/null
    cd src/ConsoleClient && dotnet run     # returns 200, prints a description of the form

    # 2) "We logged into Windows"
    kinit vedat                            # password: test123
    klist                                  # the TGT should appear

    # 3) Start the backend in a separate terminal
    (cd src/BackendApi && dotnet run)

    # 4) Passwordless token + authorized API call
    cd src/ConsoleClient && dotnet run

Expected output: a code is obtained (without being asked for a password), the access/refresh
tokens are printed, and `/api/hello` responds with the username.

Same SSO with Chrome: go to `http://keycloak.bank.local:8080/realms/bank/account`
— you should get in without a password (setup-mac.sh granted the Chrome permission;
restart Chrome).

## Method 2 — JWT Bearer grant (RFC 7523)

A different actor, still passwordless: a trusted **backend service** (in production a WCF
service) authenticates *itself*. It signs its own JWT "assertion" with a private key and
exchanges it at Keycloak's token endpoint for a real access token — no user, no browser,
no Kerberos:

    assertion (signed by the backend) --> Keycloak /token (grant_type=jwt-bearer) --> access_token

Keycloak validates the signature against the `wcf-issuer` identity provider and resolves the
asserted user through a federated-identity link. `setup-realm.sh` already registered the demo
public key (`pki/wcf-issuer.pub`), created the confidential `boa-wcf` client, and linked user
`wcf-user` to the external subject `wcf-user-001`. (A separate user from the Kerberos demo's
`vedat`, so the two flows do not collide over one username.)

    # Backend running (validates the issued token)
    (cd src/BackendApi && dotnet run)

    # The "WCF backend": signs an assertion, gets a token, calls the API
    cd src/JwtBearerClient && dotnet run

Expected output: the assertion is signed, a Keycloak access token comes back (`aud=boa-api`),
and `/api/hello` responds with `wcf-user`. This flow uses `keycloak.bank.local` as well, so the
`/etc/hosts` entry from `setup-mac.sh` is required (the issued token's issuer must match the
backend's configured authority). Override `KC_BASE`/`API_BASE` to point elsewhere.

> The signing key in `pki/` is demo-only. In production the private key stays in the WCF
> service (or an HSM), the public key is published via a JWKS URL, and the key is rotated.

## Troubleshooting (quick)
| Symptom | Check |
|---|---|
| 200 / login form in the console | Is `klist` empty? Was `kinit vedat` run? Was `KRB5_CONFIG` exported? |
| `Server not found in Kerberos database` | The /etc/hosts entry and access via `keycloak.bank.local` (not the IP) |
| Keycloak log: keytab error | `docker compose down -v && up --build` (refreshes the keytab volume) |
| Keycloak log: `Cannot locate KDC` | Keycloak's Java reads `/etc/krb5.conf` (mounted in docker-compose.yml), not `KRB5_CONFIG` |
| Kerberos: `302 but no code` (`execution=VERIFY_PROFILE`) | The imported user has no profile; `setup-realm.sh` disables the VERIFY_PROFILE required action — re-run it |
| .NET not producing Negotiate | Get the GSS trace with `KRB5_TRACE=/dev/stdout dotnet run` |
| Chrome showing the form | `defaults read com.google.Chrome AuthServerAllowlist` + restart Chrome |
| JWT Bearer: `invalid_grant` / `Invalid signature` | Did `setup-realm.sh` run? Does `pki/wcf-issuer.pub` match the key that signed the assertion? |
| JWT Bearer: `invalid_grant` (user) | The asserted `sub` must be linked to `wcf-issuer` (setup-realm links `wcf-user` ↔ `wcf-user-001`) |

## Mapping to production
| PoC (this repo) | Production (bank) |
|---|---|
| MIT KDC container | Active Directory (KDC) |
| `kadmin addprinc + ktadd` | `New-ADUser` + `setspn` + `ktpass` (AES256) |
| Standalone Kerberos federation | LDAP federation + Kerberos integration |
| `kinit vedat` | Windows domain login (automatic) |
| `defaults write ... AuthServerAllowlist` | GPO: AuthServerAllowlist + DisableAuthNegotiateCnameLookup |
| ConsoleClient (headless) | Same code + `src/WpfClient.Sample` (invisible WebView2 layer) |
| `JwtBearerClient` + `pki/wcf-issuer.key` (committed) | WCF service; private key in the service/HSM, published via JWKS, rotated |
| `wcf-issuer` IdP with a static public key | `wcf-issuer` IdP with `useJwksUrl=true` + the issuer's JWKS URL |
| http / dev mode | https, real certificate, prod mode |

`src/WpfClient.Sample` only builds on Windows; it is the reference code for the production
client (layered, windowless token acquisition). It is not needed for the Mac demo.

> This repo is a proof of concept; settings such as passwords and `RequireHttpsMetadata=false`
> are for the demo only and are not used in production.

---

# Architecture, components, and moving to production

> This section is written for the team/customer: which app represents what, which
> libraries are used, how it ran in the PoC, how it will run on a customer's Windows
> machine, and what has to change in production.

## 1. Components — what each app represents

| Component (repo) | What it represents | Role in the PoC | Production equivalent |
|---|---|---|---|
| **KDC** (`kdc/`, Docker, MIT Kerberos) | The authenticating ticket authority (KDC) | Issues a TGT via `kinit`, mints the keytab | **Active Directory** (the enterprise Domain Controller — that *is* the KDC) |
| **Keycloak** (`keycloak/`, Docker 26.7) | Enterprise IdP / OIDC token server | Validates SPNEGO with the keytab, does `authorization_code`→token and `jwt-bearer`→token | Enterprise Keycloak cluster (bound to AD via LDAP + Kerberos federation) |
| **ConsoleClient** (`src/ConsoleClient/`) | The desktop app's **core token-acquisition logic** (headless) | Drives `authorization_code` + PKCE over Negotiate, no browser | Same logic, running inside the WPF/WinForms app |
| **WpfClient.Sample** (`src/WpfClient.Sample/`) | The real **Windows desktop client** (reference code) | Builds on Windows only; layered/invisible token acquisition | The actual app shipped to the customer |
| **JwtBearerClient** (`src/JwtBearerClient/`) | The trusted **WCF backend service** | Signs an assertion with its own private key, gets a token via `jwt-bearer` | The real WCF service (private key inside the service/HSM) |
| **BackendApi** (`src/BackendApi/`) | The protected **resource API** (Resource Server) | Validates the bearer JWT signature locally against the JWKS; knows nothing about Kerberos | The enterprise microservices/APIs (same JWT validation) |
| **PKI** (`pki/`) | The WCF issuer's signing key pair | Demo public/private key (committed to the repo) | Private key in WCF/HSM, public key on a JWKS URL, rotated |

Key design point: **BackendApi contains not a single line of Kerberos code.** The backend only
asks "did a valid, signed JWT arrive?". *How* the user was authenticated (Kerberos SSO or a WCF
assertion) is entirely Keycloak's job. This is what lets two different passwordless methods use
the same API without changing it at all.

## 2. Libraries used and why

| Layer | Library / technology | For what |
|---|---|---|
| Desktop token acquisition (headless) | .NET `HttpClient` + `HttpClientHandler` + `CredentialCache` | Perform the SPNEGO/Negotiate handshake automatically via the OS Kerberos stack |
| PKCE | `System.Security.Cryptography` (SHA256, `RandomNumberGenerator`) | Generate `code_verifier`/`code_challenge` — mandatory for a public client |
| WPF reference client | `IdentityModel.OidcClient` (6.x) | Provide the OIDC `authorization_code`+PKCE flow, silent refresh, and 401→retry out of the box |
| WPF embedded browser | `Microsoft.Web.WebView2` | Invisible WebView2; carry the SSO cookie, show a form to the user only when needed |
| WCF assertion | `System.Security.Cryptography.RSA` (RS256, PKCS#1) | Sign the RFC 7523 JWT assertion |
| BackendApi | `Microsoft.AspNetCore.Authentication.JwtBearer` | Validate the token signature locally against the IdP's JWKS (`Authority` + `ValidAudience`) |
| IdP | Keycloak 26.7 + `jwt-authorization-grant` feature | SPNEGO federation + RFC 7523 JWT Bearer grant |
| KDC | MIT Kerberos (Docker) | Mint TGTs/keytabs in the absence of AD |

## 3. How the `authorization_code` + PKCE + redirect flow is managed

The user flow (Kerberos SSO) is exactly the standard OIDC Authorization Code + PKCE; the only
difference is that no password is asked. Steps (`src/ConsoleClient/Program.cs`):

1. **Generate PKCE:** a random `code_verifier`, and from it a SHA256 `code_challenge` (`S256`).
2. **Call `/auth`:** `client_id=boa-desktop&response_type=code&redirect_uri=...&code_challenge=...`.
   The `Negotiate` scheme is pinned on `HttpClientHandler.Credentials` (via `CredentialCache`),
   so when Keycloak returns `401 WWW-Authenticate: Negotiate`, .NET **automatically** produces a
   SPNEGO token from the OS's active Kerberos ticket and retries with the
   `Authorization: Negotiate <token>` header. The scheme is pinned to `Negotiate` specifically to
   disable the NTLM/Basic fallback.
3. **302 redirect:** Keycloak authenticates the user and redirects to `redirect_uri` with
   `?code=...`. Because `AllowAutoRedirect = false`, the client does not follow the redirect; it
   reads the `code` out of the `Location` header's query. **No real request is ever sent to the
   redirect address** — the loopback URL (`http://127.0.0.1:.../cb`) is just a marker that carries
   the code. (On the WPF side `LayeredBrowser` does this in `NavigationStarting` with
   `e.Cancel = true`; it extracts the code from the URL without sending any HTTP request.)
4. **Call `/token`:** `grant_type=authorization_code` + `code` + `code_verifier`. Keycloak matches
   the verifier against the challenge (PKCE verification) and returns `access_token` + `refresh_token`.
5. **Call the API:** the access token is sent to BackendApi as `Authorization: Bearer ...`.

Why loopback redirect + PKCE? `boa-desktop` is a **public client** (it has no client secret — a
secret can't be stored safely in a desktop app). PKCE prevents anyone who intercepts a stolen
`code` from using it; loopback (`127.0.0.1`) is the recommended redirect method for native apps
(RFC 8252).

## 4. How it runs on Windows / how the customer runs it

In the macOS PoC, the moment we run `kinit vedat` corresponds to **the moment a user logs into
the domain on Windows in the morning**. On a domain-joined Windows machine the user already holds
a TGT and does nothing extra. There is no change on the code side either — the same `HttpClient` +
`Negotiate` logic uses the OS's LSA/Kerberos stack on Windows.

**Prerequisites (customer machine):**
- The machine is **joined to the enterprise AD domain** and the user is logged in with a domain account.
- **.NET 9 Desktop Runtime** is installed (for the WPF client). The .NET 9 Runtime is enough for ConsoleClient.
- The Keycloak host name (the real FQDN, not `keycloak.bank.local`) is on the **Negotiate allowlist**.
  In an enterprise this is deployed via **GPO**, not `defaults write` on each machine:
  - `AuthServerAllowlist` = the IdP FQDN
  - `DisableAuthNegotiateCnameLookup` = recommended (avoids SPN issues behind a CNAME)
  - The same policy (`AuthServerAllowlist`) for Chrome/Edge; for WebView2 the app passes its own
    `--auth-server-allowlist` argument (`LayeredBrowser.cs`), so no GPO is needed there.
- Keycloak has an **SPN + keytab** for HTTP/FQDN (in production minted with `ktpass`, AES256).

**Running it (the production-shaped WPF client):**
```
# User logged into the domain; the app opens and acquires a token invisibly in the background.
WpfClient.Sample.exe
```
When `KeycloakAuthService.SignInAndCreateApiClientAsync()` is called:
1. `LayeredBrowser` opens WebView2 **off-screen** (invisible) and navigates to `/auth`.
2. SPNEGO completes automatically with Windows's current Kerberos ticket → Keycloak returns the redirect.
3. `NavigationStarting` catches the redirect URL, cancels the request, reads the code → the window
   closes without ever being shown. The user **sees nothing** (passwordless, windowless).
4. Only if interaction is required (password expired, OTP enrollment, etc.) does `NavigationCompleted`
   fire, and the window is brought to the center of the screen and shown **at that point** — the
   layered approach.

**Running it (headless verification, with ConsoleClient):** to test the same token logic on Windows
without needing WPF:
```
set KC_BASE=https://keycloak.bank.local/realms/bank/protocol/openid-connect
set API_BASE=https://boa-api.bank.local
dotnet run --project src\ConsoleClient
```
Run as a domain user, a token comes back without a password prompt.

### Testing from a domain-joined Windows PC (already logged in)

"I'm already logged into Windows with my domain account — can I just run it and get a token with no
password?" The answer depends on **which Keycloak you point at**, because your Windows login gives you
a Kerberos TGT **for your own AD domain only**.

**What your session already has.** After a normal domain login, Windows holds a TGT in the LSA. Check
it — `klist` is built into Windows (no `kinit`, no `KRB5_CONFIG`; those are Mac/Linux only, on Windows
.NET and browsers use these tickets automatically via SSPI/LSA):
```
whoami /upn          :: your domain identity, e.g. vedat@CORP.EXAMPLE.COM
klist                :: cached tickets: you should see a krbtgt/CORP.EXAMPLE.COM entry
```

**A) Against a Keycloak federated to *your* AD — the real, passwordless test.** This is the only setup
where your existing login produces true zero-prompt SSO. Server-side it requires: Keycloak bound to
your AD via LDAP + Kerberos federation (its `serverPrincipal`/keytab is for *your* realm, e.g.
`HTTP/keycloak.corp.example.com@CORP.EXAMPLE.COM`), an SPN registered in AD for the Keycloak host, and
the Keycloak FQDN on your machine's browser Negotiate allowlist. Then verify (no password expected):
```
:: 1) Can you get a service ticket for Keycloak's SPN? (proves the SPN exists and is reachable)
klist get HTTP/keycloak.corp.example.com

:: 2) Browser test — Edge/Chrome opens the account console with NO login form
start https://keycloak.corp.example.com/realms/bank/account

:: 3) The desktop client's core logic, headless:
set KC_BASE=https://keycloak.corp.example.com/realms/bank/protocol/openid-connect
set API_BASE=https://boa-api.corp.example.com
dotnet run --project src\ConsoleClient

:: 4) After it runs, klist should now list a ticket for HTTP/keycloak.corp.example.com
klist
```
If step 1 fails (`no such SPN`, wrong encryption type) the server SPN/keytab isn't set up — a server
task, not a client one. If the browser shows a login form, the FQDN isn't allowlisted. Quick
per-machine browser allowlist for a test (no GPO), then restart the browser:
```
reg add "HKCU\Software\Policies\Google\Chrome"    /v AuthServerAllowlist /t REG_SZ /d "keycloak.corp.example.com" /f
reg add "HKCU\Software\Policies\Microsoft\Edge"   /v AuthServerAllowlist /t REG_SZ /d "keycloak.corp.example.com" /f
```

**B) Against the Docker PoC Keycloak in this repo (`BANK.LOCAL` / MIT KDC).** Here your Windows domain
login does **not** help: the PoC's KDC is `BANK.LOCAL`, a separate realm your AD knows nothing about,
so your TGT cannot get a service ticket for `HTTP/keycloak.bank.local@BANK.LOCAL`. To exercise the flow
you must authenticate into `BANK.LOCAL` by hand — the same thing the Mac demo does, just on Windows:
- Install **MIT Kerberos for Windows** (gives you `kinit` for the MIT realm).
- Add `127.0.0.1 keycloak.bank.local kdc.bank.local` to `C:\Windows\System32\drivers\etc\hosts`.
- Get a `BANK.LOCAL` ticket manually, then run the client:
  ```
  set KRB5_CONFIG=%CD%\client-mac\krb5-client.conf
  kinit vedat        :: password: test123 — this REPLACES the "already logged in" premise
  dotnet run --project src\ConsoleClient
  ```
This is fine for exercising the *client code*, but it does **not** use your real domain login — it's a
manual `kinit`, exactly like the Mac. For a genuine "I logged into Windows and it just worked" test you
need setup (A).

**In one line:** passwordless-from-your-Windows-login works only when the Keycloak you hit trusts your
AD domain. The Docker PoC trusts `BANK.LOCAL`, not your company domain, so against it every client (Mac
or Windows) must `kinit` by hand.

> Note: `src/WpfClient.Sample` builds on Windows only (`net9.0-windows`, `UseWPF`). It does not need
> to build for the Mac demo; it is the reference code for the production client.

## 5. How the WCF backend's information is obtained and processed (RFC 7523 JWT Bearer)

The second passwordless method is **a trusted service, not a user**, authenticating itself. There is
no Kerberos here; the WCF service speaks with its own identity (`src/JwtBearerClient/Program.cs`):

1. **Load the private key** (in the PoC `pki/wcf-issuer.key`; in production inside the service/HSM).
2. **Build and sign the assertion JWT with RS256:** `iss` (the issuer identity), `sub` (on behalf of
   which user/service — the external user id), `aud` (the realm issuer URL), a short `exp` (≤300s),
   and a single-use `jti`.
3. **Call `/token`:** `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` + `assertion` +
   `client_id=boa-wcf` + `client_secret`.
4. **Keycloak validates:** it checks the signature against the public key registered on the
   `wcf-issuer` identity provider, resolves `sub` to a real Keycloak user (`wcf-user`) through the
   **federated-identity link**, and returns a real `access_token` (`aud=boa-api`).
5. This token, too, goes to BackendApi as a bearer — the two flows are indistinguishable to the API.

So the "information from the Windows/WCF side" is carried in two different ways:
- **In user SSO:** the information = the user's Kerberos **TGT/service ticket**, carried in the SPNEGO
  header; Keycloak resolves it with the keytab.
- **In the WCF service:** the information = the **JWT assertion the service signed**; Keycloak validates
  it with the public key. The service declares the user it acts on behalf of in the assertion's `sub`.

## 6. What production expects differently from the PoC

Short answer: **the flow and the code stay the same; the infrastructure and security settings become
real.** (See the "Mapping to production" table above for the detailed mapping.) The main differences:

- **KDC → Active Directory:** domain login is automatic instead of `kinit`/`kadmin`; the SPN/keytab is
  minted with `setspn` + `ktpass` (AES256). Keycloak binds to AD via LDAP + Kerberos federation.
- **HTTP → HTTPS:** the PoC uses `http` and `RequireHttpsMetadata=false`. Production uses a real
  certificate, `https`, and Keycloak `start` (prod mode) — not `start-dev`.
- **Allowlist deployment:** enterprise-wide **GPO**, not `defaults write` per machine.
- **WCF key:** instead of a committed demo key, the private key lives **inside WCF/HSM**; the public key
  is published on a **JWKS URL** and read on the `wcf-issuer` IdP with `useJwksUrl=true`; the key is
  **rotated**. (In the PoC it's a static public key with `useJwksUrl=false`.)
- **Assertion reuse:** in production the replay protection via `jti` + short `exp` is already on; keep
  `AssertionReuseAllowed=false` as well.
- **Profile / required actions:** the PoC disables `VERIFY_PROFILE` because the user imported from
  Kerberos has no profile form. In production, user profiles come from LDAP, so this step isn't needed.
- **Authentication flow:** the SPNEGO execution is set to `ALTERNATIVE` in the PoC so it can fall back
  to the login form when there's no ticket (fallback). Production wants the fallback too; keep this setting.

In short, production expects **both flows to work with the same code**; the only thing that changes is
replacing the demo shortcuts (password, http, committed key, manual allowlist) with their real
enterprise counterparts (domain, https, HSM/JWKS, GPO).
