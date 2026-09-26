#!/bin/bash
# Prove that the --verify-resources guard works: green on a good build, red when
# catalogs or the default pet are removed. Prints RESOURCES_GUARD_OK only when
# all three outcome checks pass.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Library/Developer/CommandLineTools}"

SCRATCH="$(mktemp -d /tmp/mc-verify-selftest-XXXXXXXX)"
APP="$SCRATCH/MightyClaude.app"
BINARY="$APP/Contents/MacOS/MightyClaude"

cleanup() { rm -rf "$SCRATCH"; }
trap cleanup EXIT

echo "=== verify-resources selftest: building into $APP ==="
MIGHTY_BUILD_SCRATCH="$SCRATCH/build" MIGHTY_MACOS_APP_PATH="$APP" \
  bash "$PROJECT_ROOT/scripts/build-macos.sh" >/dev/null 2>&1

# --- Case 1: good build must exit 0 and print VERIFY_RESOURCES_OK ----------
echo "--- case 1: good build ---"
GOOD_OUT="$("$BINARY" --verify-resources 2>&1)"
GOOD_EXIT=$?
if [ "$GOOD_EXIT" -ne 0 ]; then
  echo "FAIL: good build exited $GOOD_EXIT" >&2
  echo "$GOOD_OUT" >&2
  exit 1
fi
if ! echo "$GOOD_OUT" | grep -q "VERIFY_RESOURCES_OK"; then
  echo "FAIL: VERIFY_RESOURCES_OK not printed for good build" >&2
  echo "$GOOD_OUT" >&2
  exit 1
fi
echo "case 1 passed: VERIFY_RESOURCES_OK"

# --- Case 2: Locales removed from MightyCore bundle must exit non-zero ------
echo "--- case 2: broken Locales ---"
COPY_NO_LOCALES="$SCRATCH/no-locales/MightyClaude.app"
cp -R "$APP" "$COPY_NO_LOCALES"
CORE_BUNDLE="$COPY_NO_LOCALES/Contents/Resources/MightyClaude_MightyCore.bundle"
# Remove both layout variants so neither path finds the catalogs.
rm -rf "$CORE_BUNDLE/Contents/Resources/Locales" "$CORE_BUNDLE/Locales"
NO_LOC_OUT="$("$COPY_NO_LOCALES/Contents/MacOS/MightyClaude" --verify-resources 2>&1)" || true
NO_LOC_EXIT=$?
if [ "$NO_LOC_EXIT" -eq 0 ]; then
  echo "FAIL: missing Locales did not cause non-zero exit" >&2
  echo "$NO_LOC_OUT" >&2
  exit 1
fi
if ! echo "$NO_LOC_OUT" | grep -qi "ko.json\|en.json\|MISSING\|FAILED"; then
  echo "FAIL: missing-Locales output does not name the resource" >&2
  echo "$NO_LOC_OUT" >&2
  exit 1
fi
echo "case 2 passed: broken Locales exits non-zero and names the missing resource"

# --- Case 3: pets/mighty-raccoon removed must exit non-zero -----------------
echo "--- case 3: broken pet ---"
COPY_NO_PET="$SCRATCH/no-pet/MightyClaude.app"
cp -R "$APP" "$COPY_NO_PET"
rm -rf "$COPY_NO_PET/Contents/Resources/pets/mighty-raccoon"
NO_PET_OUT="$("$COPY_NO_PET/Contents/MacOS/MightyClaude" --verify-resources 2>&1)" || true
NO_PET_EXIT=$?
if [ "$NO_PET_EXIT" -eq 0 ]; then
  echo "FAIL: missing pet did not cause non-zero exit" >&2
  echo "$NO_PET_OUT" >&2
  exit 1
fi
if ! echo "$NO_PET_OUT" | grep -qi "mighty-raccoon\|MISSING\|FAILED"; then
  echo "FAIL: missing-pet output does not name the resource" >&2
  echo "$NO_PET_OUT" >&2
  exit 1
fi
echo "case 3 passed: broken pet exits non-zero and names the missing resource"

echo "RESOURCES_GUARD_OK"
