#!/bin/bash
# Creates a stable self-signed code-signing certificate "Mighty Claude Dev"
# in the login keychain. This replaces per-build ad-hoc signatures so that
# macOS Keychain "Always Allow" and TCC grants (Screen Recording, Accessibility)
# remain valid across rebuilds.
#
# Safe to re-run: exits immediately if the identity already exists.
# A PKCS#12 backup is written to ~/.mighty-codesign-backup/ so the identity
# can be restored after a fresh macOS install without a full permission re-grant.
#
# To restore from backup:
#   P="$(security find-generic-password -s "Mighty Claude codesign backup" -w)"
#   security import ~/.mighty-codesign-backup/mighty-codesign-<date>.p12 \
#       -k ~/Library/Keychains/login.keychain-db -P "$P" -T /usr/bin/codesign -t cert -f pkcs12
#   security add-trusted-cert -r trustRoot -p codeSign \
#       -k ~/Library/Keychains/login.keychain-db ~/.mighty-codesign-backup/mighty-codesign-<date>.cert.pem
# Keep a copy of the passphrase outside this Mac too (e.g. a password manager).
#
# Usage:
#   bash scripts/setup-codesign.sh
set -euo pipefail

CERT_NAME="Mighty Claude Dev"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
BACKUP_DIR="$HOME/.mighty-codesign-backup"

# Check if the identity (cert + private key pair) already exists.
if security find-identity -v -p codesigning "$KEYCHAIN" 2>/dev/null | grep -qF "$CERT_NAME"; then
    echo "✓ '$CERT_NAME' already exists in $KEYCHAIN"
    exit 0
fi

command -v openssl >/dev/null 2>&1 || { echo "openssl is required but not found" >&2; exit 1; }

echo "Creating self-signed code-signing certificate '$CERT_NAME'…"

WORK="$(mktemp -d)"
# shellcheck disable=SC2064
trap "rm -rf '$WORK'" EXIT

# 4096-bit RSA private key
openssl genrsa -out "$WORK/key.pem" 4096 2>/dev/null

# Self-signed certificate with the code-signing EKU required by codesign(1)
cat > "$WORK/cert.conf" << 'CONF'
[req]
default_bits       = 4096
prompt             = no
default_md         = sha256
distinguished_name = dn
x509_extensions    = v3_cs

[dn]
CN = Mighty Claude Dev

[v3_cs]
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid:always,issuer
basicConstraints       = critical,CA:false
keyUsage               = critical,digitalSignature
extendedKeyUsage       = codeSigning
CONF

openssl req -new -x509 \
    -key "$WORK/key.pem" \
    -out "$WORK/cert.pem" \
    -days 3650 \
    -config "$WORK/cert.conf" 2>/dev/null

# The backup passphrase is random and lives only in the login keychain
# (service "Mighty Claude codesign backup"); it is never printed.
BACKUP_SERVICE="Mighty Claude codesign backup"
PASSPHRASE="$(openssl rand -base64 32)"

# Bundle key + cert into PKCS#12, protected by the passphrase
openssl pkcs12 -export \
    -out "$WORK/codesign.p12" \
    -inkey "$WORK/key.pem" \
    -in "$WORK/cert.pem" \
    -passout "fd:3" \
    -name "$CERT_NAME" 3<<<"$PASSPHRASE" 2>/dev/null

# Import the key so that only codesign may use it (no -A: other apps must ask)
security import "$WORK/codesign.p12" \
    -k "$KEYCHAIN" \
    -P "$PASSPHRASE" \
    -T /usr/bin/codesign \
    -t cert \
    -f pkcs12 >/dev/null

# Trust the certificate for code signing (macOS asks for your password once)
security add-trusted-cert \
    -r trustRoot \
    -p codeSign \
    -k "$KEYCHAIN" \
    "$WORK/cert.pem"

# Keep the passphrase in the keychain, then back up the p12 and the certificate
security add-generic-password -U -a "$USER" -s "$BACKUP_SERVICE" -w "$PASSPHRASE" "$KEYCHAIN"
unset PASSPHRASE
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
STAMP="$(date +%Y%m%d)"
BACKUP_FILE="$BACKUP_DIR/mighty-codesign-$STAMP.p12"
cp "$WORK/codesign.p12" "$BACKUP_FILE"
cp "$WORK/cert.pem" "$BACKUP_DIR/mighty-codesign-$STAMP.cert.pem"
chmod 600 "$BACKUP_FILE" "$BACKUP_DIR/mighty-codesign-$STAMP.cert.pem"

echo "✓ '$CERT_NAME' created."
echo "  Backup: $BACKUP_FILE"
echo "  Set MIGHTY_CODESIGN_IDENTITY='$CERT_NAME' or rely on the default in build-macos.sh."
echo "  First build after migration resets TCC grants once — re-grant Screen Recording"
echo "  and Accessibility in System Settings."
