#!/bin/bash
# Turns the Mac into a "domain-joined client": hosts entry + krb5 config + Chrome permission
set -e

if ! grep -q "keycloak.bank.local" /etc/hosts; then
  echo ">> Updating /etc/hosts (requires sudo)"
  sudo sh -c 'echo "127.0.0.1 keycloak.bank.local kdc.bank.local" >> /etc/hosts'
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
cat > "$DIR/krb5-client.conf" << 'KRB'
[libdefaults]
  default_realm = BANK.LOCAL
  dns_lookup_kdc = false
  rdns = false

[realms]
  BANK.LOCAL = {
    kdc = kdc.bank.local:88
  }

[domain_realm]
  .bank.local = BANK.LOCAL
KRB

echo ">> Negotiate permission for Chrome (the Mac equivalent of the GPO at the bank)"
defaults write com.google.Chrome AuthServerAllowlist "keycloak.bank.local" 2>/dev/null || true

echo ""
echo "Setup complete. For the demo:"
echo "  export KRB5_CONFIG=$DIR/krb5-client.conf"
echo "  kinit vedat            # password: test123  (= simulates the moment of Windows login)"
echo "  klist                  # the TGT should appear"
