# pki/ — demo signing key for the JWT Bearer flow

**Demo material only. Do not use in production.**

`wcf-issuer.key` / `wcf-issuer.pub` is a throwaway RSA-2048 key pair that stands in for
the private key a trusted backend (in production: the WCF service) uses to sign its own
JWT assertions. In the demo:

- The `JwtBearerClient` example signs an assertion (RFC 7523) with `wcf-issuer.key`.
- `keycloak/setup-realm.sh` registers `wcf-issuer.pub` on the `wcf-issuer` identity
  provider so Keycloak can validate that signature.

In production the private key never leaves the issuing service, the public key is published
via a JWKS URL, and the key is rotated. Here it is committed so `git clone` + one script gives
a working demo.

## Regenerate (optional)

```bash
openssl req -x509 -newkey rsa:2048 -keyout wcf-issuer.key -out wcf-issuer.crt \
  -days 3650 -nodes -subj "/CN=wcf-token-issuer/O=Bank Demo"
openssl rsa -in wcf-issuer.key -pubout -out wcf-issuer.pub
```

After regenerating, re-run `./keycloak/setup-realm.sh` so Keycloak picks up the new public key.
