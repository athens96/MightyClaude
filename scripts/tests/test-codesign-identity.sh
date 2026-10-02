#!/bin/bash
# Validates the self-signed codesign certificate setup contract:
# - build-macos.sh defaults to the named identity, not ad-hoc ("-")
# - setup-codesign.sh exists, is syntactically valid, and uses the same name
# - the backup path and restore comment are present in setup-codesign.sh
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BUILD_SCRIPT="$PROJECT_ROOT/scripts/build-macos.sh"
SETUP_SCRIPT="$PROJECT_ROOT/scripts/setup-codesign.sh"
EXPECTED_NAME="Mighty Claude Dev"
FAIL=0

fail() { echo "FAIL: $*" >&2; FAIL=1; }

# 1. build-macos.sh must default to the named identity, not "-"
DEFAULT_ID="$(grep 'CODESIGN_IDENTITY=.*MIGHTY_CODESIGN_IDENTITY' "$BUILD_SCRIPT" | \
    sed 's/.*:-//' | tr -d '"}')"
if [ "$DEFAULT_ID" = "$EXPECTED_NAME" ]; then
    echo "PASS: build-macos.sh defaults to '$EXPECTED_NAME'"
else
    fail "build-macos.sh default identity is '$DEFAULT_ID', expected '$EXPECTED_NAME'"
fi

# 2. ad-hoc default ("-") must not appear as the fallback
if grep -q 'MIGHTY_CODESIGN_IDENTITY:-"*-"*}' "$BUILD_SCRIPT"; then
    fail "build-macos.sh still has ad-hoc default (-) as fallback"
else
    echo "PASS: ad-hoc default is not the fallback in build-macos.sh"
fi

# 3. setup-codesign.sh must exist
if [ -f "$SETUP_SCRIPT" ]; then
    echo "PASS: setup-codesign.sh exists"
else
    fail "setup-codesign.sh not found at $SETUP_SCRIPT"
fi

# 4. setup-codesign.sh must be syntactically valid bash
if bash -n "$SETUP_SCRIPT" 2>/dev/null; then
    echo "PASS: setup-codesign.sh is syntactically valid"
else
    fail "setup-codesign.sh has syntax errors"
fi

# 5. setup-codesign.sh must use the same certificate name as the build default
if grep -qF "CERT_NAME=\"$EXPECTED_NAME\"" "$SETUP_SCRIPT"; then
    echo "PASS: setup-codesign.sh CERT_NAME matches build default"
else
    fail "setup-codesign.sh CERT_NAME does not match '$EXPECTED_NAME'"
fi

# 6. setup-codesign.sh must reference the backup directory
if grep -q 'BACKUP_DIR' "$SETUP_SCRIPT"; then
    echo "PASS: setup-codesign.sh contains backup directory logic"
else
    fail "setup-codesign.sh is missing backup directory"
fi

# 7. setup-codesign.sh must contain restore instructions
if grep -q 'security import' "$SETUP_SCRIPT"; then
    echo "PASS: setup-codesign.sh documents restore via security import"
else
    fail "setup-codesign.sh missing restore instructions"
fi

# 8. install-macos.sh comment must not claim ad-hoc signing is still the default
if grep -q 'ad-hoc signed with a fresh signature' "$PROJECT_ROOT/scripts/install-macos.sh"; then
    fail "install-macos.sh still says ad-hoc signed with a fresh signature"
else
    echo "PASS: install-macos.sh comment updated for stable identity"
fi

# 9. build-macos.sh must mention setup-codesign.sh in its comment
if grep -q 'setup-codesign.sh' "$BUILD_SCRIPT"; then
    echo "PASS: build-macos.sh references setup-codesign.sh"
else
    fail "build-macos.sh missing reference to setup-codesign.sh"
fi

if [ "$FAIL" -ne 0 ]; then
    echo "One or more checks failed." >&2
    exit 1
fi
echo "All codesign identity checks passed."
