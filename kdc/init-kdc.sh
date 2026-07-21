#!/bin/bash
set -e
REALM=BANK.LOCAL

if [ ! -f /var/lib/krb5kdc/principal ]; then
  echo "*** KDC first-time setup: creating the realm"
  kdb5_util create -s -r "$REALM" -P masterkey
  kadmin.local -q "addprinc -pw test123 vedat"
  kadmin.local -q "addprinc -pw test123 demo"
  kadmin.local -q "addprinc -randkey HTTP/keycloak.bank.local"
  kadmin.local -q "ktadd -k /keytabs/keycloak.keytab HTTP/keycloak.bank.local"
  chmod 644 /keytabs/keycloak.keytab
  echo "*** Users: vedat / demo (password: test123)"
fi

echo "*** KDC starting"
exec krb5kdc -n
