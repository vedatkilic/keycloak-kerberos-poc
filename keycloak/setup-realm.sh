#!/bin/bash
# Sets up the bank realm, the Kerberos federation and the boa-desktop client in Keycloak.
# Usage: after docker compose up -d, run once when Keycloak has come up.
set -e
KC="docker exec poc-keycloak /opt/keycloak/bin/kcadm.sh"

echo ">> Admin session"
$KC config credentials --server http://localhost:8080 --realm master --user admin --password admin

echo ">> Realm: bank"
$KC create realms -s realm=bank -s enabled=true || echo "   (already exists)"

echo ">> JWT Bearer: identity provider wcf-issuer (RFC 7523 assertion trust anchor)"
# The trusted backend signs assertions with pki/wcf-issuer.key; Keycloak validates them
# with the matching public key registered here.
PUBKEY=$(grep -v -- '-----' "$(dirname "$0")/../pki/wcf-issuer.pub" | tr -d '\n')
if $KC get identity-provider/instances/wcf-issuer -r bank >/dev/null 2>&1; then
  echo "   (already exists)"
else
  $KC create identity-provider/instances -r bank \
    -s alias=wcf-issuer \
    -s providerId=jwt-authorization-grant \
    -s enabled=true \
    -s 'config.issuer=https://token-issuer.bank.local' \
    -s 'config.jwtAuthorizationGrantEnabled=true' \
    -s 'config.jwtAuthorizationGrantAssertionReuseAllowed=false' \
    -s 'config.jwtAuthorizationGrantMaxAllowedAssertionExpiration=300' \
    -s 'config.useJwksUrl=false' \
    -s "config.publicKeySignatureVerifier=$PUBKEY" \
    -s 'config.publicKeySignatureVerifierKeyId=wcf-demo-key'
fi

echo ">> JWT Bearer: demo user wcf-user linked to wcf-issuer (external sub=wcf-user-001)"
# A DISTINCT username on purpose: the Kerberos SSO flow imports its own user 'vedat'
# from the KDC principal, so the JWT Bearer demo user must not collide with it.
if $KC get users -r bank -q username=wcf-user --fields id --format csv --noquotes 2>/dev/null | grep -q .; then
  echo "   (user already exists)"
else
  $KC create users -r bank -s username=wcf-user -s enabled=true \
    -s email=wcf-user@bank.local -s emailVerified=true -s firstName=WCF -s lastName=Service
fi
WUID=$($KC get users -r bank -q username=wcf-user --fields id --format csv --noquotes | head -1)
if $KC get users/$WUID/federated-identity -r bank 2>/dev/null | grep -q wcf-issuer; then
  echo "   (link already exists)"
else
  $KC create users/$WUID/federated-identity/wcf-issuer -r bank \
    -s identityProvider=wcf-issuer -s userId=wcf-user-001 -s userName=wcf-user
fi

echo ">> Kerberos user federation"
# Components are not protected by a unique-name constraint the way realms/clients are,
# so a plain create would add a duplicate on every re-run. Guard it to stay idempotent.
if $KC get components -r bank -q type=org.keycloak.storage.UserStorageProvider \
     --fields name --format csv --noquotes 2>/dev/null | grep -qx kerberos-poc; then
  echo "   (already exists)"
else
  $KC create components -r bank \
    -s name=kerberos-poc \
    -s providerId=kerberos \
    -s providerType=org.keycloak.storage.UserStorageProvider \
    -s 'config.kerberosRealm=["BANK.LOCAL"]' \
    -s 'config.serverPrincipal=["HTTP/keycloak.bank.local@BANK.LOCAL"]' \
    -s 'config.keyTab=["/keytabs/keycloak.keytab"]' \
    -s 'config.debug=["true"]' \
    -s 'config.allowPasswordAuthentication=["true"]' \
    -s 'config.updateProfileFirstLogin=["false"]' \
    -s 'config.cachePolicy=["DEFAULT"]'
fi

echo ">> Client: boa-desktop (public, PKCE S256, loopback redirect)"
$KC create clients -r bank \
  -s clientId=boa-desktop \
  -s publicClient=true \
  -s standardFlowEnabled=true \
  -s directAccessGrantsEnabled=false \
  -s 'redirectUris=["http://127.0.0.1:*","http://localhost:*"]' \
  -s 'attributes={"pkce.code.challenge.method":"S256"}' || echo "   (already exists)"

CID=$($KC get clients -r bank -q clientId=boa-desktop --fields id --format csv --noquotes | head -1)

echo ">> Audience mapper (boa-api)"
$KC create clients/$CID/protocol-mappers/models -r bank \
  -s name=aud-boa-api \
  -s protocol=openid-connect \
  -s protocolMapper=oidc-audience-mapper \
  -s 'config={"included.custom.audience":"boa-api","access.token.claim":"true"}' || echo "   (already exists)"

echo ">> JWT Bearer: confidential client boa-wcf (presents the assertion, gets a token)"
$KC create clients -r bank \
  -s clientId=boa-wcf \
  -s enabled=true \
  -s publicClient=false \
  -s secret=wcf-demo-secret \
  -s standardFlowEnabled=false \
  -s directAccessGrantsEnabled=false \
  -s serviceAccountsEnabled=false \
  -s 'attributes."oauth2.jwt.authorization.grant.enabled"=true' \
  -s 'attributes."oauth2.jwt.authorization.grant.idp"=wcf-issuer' || echo "   (already exists)"

WCFID=$($KC get clients -r bank -q clientId=boa-wcf --fields id --format csv --noquotes | head -1)
echo ">> Audience mapper (boa-api) on boa-wcf"
$KC create clients/$WCFID/protocol-mappers/models -r bank \
  -s name=aud-boa-api \
  -s protocol=openid-connect \
  -s protocolMapper=oidc-audience-mapper \
  -s 'config={"included.custom.audience":"boa-api","access.token.claim":"true"}' || echo "   (already exists)"

echo ">> Browser flow adjustment: setting the Kerberos execution to ALTERNATIVE"
FLOWEXEC=$($KC get authentication/flows/browser/executions -r bank --format csv --fields id,providerId --noquotes | grep auth-spnego | cut -d, -f1)
$KC update authentication/flows/browser/executions -r bank -b "{\"id\":\"$FLOWEXEC\",\"requirement\":\"ALTERNATIVE\"}"

echo ">> Disable VERIFY_PROFILE required action"
# Kerberos-imported users have no profile form in this demo; otherwise the SSO flow gets
# redirected to a "complete your profile" page instead of returning the authorization code.
$KC update authentication/required-actions/VERIFY_PROFILE -r bank -s enabled=false || echo "   (skip)"

echo ">> Done. Test: http://keycloak.bank.local:8080/realms/bank/account"
