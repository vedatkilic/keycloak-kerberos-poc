# Keycloak Kerberos SSO — PoC (macOS / Linux)

A demo that shows **passwordless (Kerberos SSO) Keycloak integration** end to end,
without a Windows domain environment. What "the user logging into the domain in the morning"
is on Windows, `kinit` is here; the rest of the flow uses the same protocol as production.

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
- .NET 8 SDK
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
