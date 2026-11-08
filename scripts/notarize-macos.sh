#!/bin/bash
# Sign MightyClaude.app with a Developer ID, notarize it, staple it, and package
# it as both the update zip and a drag-to-Applications dmg.
#
#   scripts/notarize-macos.sh <MightyClaude.app> <output folder>
#
# Environment:
#   MIGHTY_CODESIGN_IDENTITY   "Developer ID Application: … (TEAMID)", in a keychain on the search list
#   MIGHTY_NOTARY_KEY_FILE     App Store Connect API key (.p8) for notarytool
#   MIGHTY_NOTARY_KEY_ID       its Key ID
#   MIGHTY_NOTARY_ISSUER_ID    its Issuer ID
#
# Writes <output>/MightyClaude-macos.zip (what the app's updater installs) and
# <output>/MightyClaude-macos.dmg (first install from the download page). The
# app inside both carries its notarization ticket, so Gatekeeper opens it
# without a warning even offline; the dmg is signed, notarized and stapled too.
set -euo pipefail

APP="$1"
OUT="$2"
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
: "${MIGHTY_CODESIGN_IDENTITY:?Developer ID identity}" "${MIGHTY_NOTARY_KEY_FILE:?API key file}"
: "${MIGHTY_NOTARY_KEY_ID:?Key ID}" "${MIGHTY_NOTARY_ISSUER_ID:?Issuer ID}"
case "$MIGHTY_CODESIGN_IDENTITY" in
    "Developer ID Application"*) ;;
    *) echo "공증에는 Developer ID Application 인증서가 필요합니다: $MIGHTY_CODESIGN_IDENTITY" >&2; exit 1 ;;
esac
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Submit one file and wait; fail with Apple's log unless it is Accepted.
notarize() {
    local file="$1" result status id
    result="$(xcrun notarytool submit "$file" --key "$MIGHTY_NOTARY_KEY_FILE" --key-id "$MIGHTY_NOTARY_KEY_ID" \
        --issuer "$MIGHTY_NOTARY_ISSUER_ID" --wait --timeout 60m --output-format json)"
    status="$(printf '%s' "$result" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status",""))')"
    id="$(printf '%s' "$result" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("id",""))')"
    echo "공증 $(basename "$file"): $status ($id)"
    if [ "$status" != "Accepted" ]; then
        [ -n "$id" ] && xcrun notarytool log "$id" --key "$MIGHTY_NOTARY_KEY_FILE" --key-id "$MIGHTY_NOTARY_KEY_ID" \
            --issuer "$MIGHTY_NOTARY_ISSUER_ID" >&2 || true
        exit 1
    fi
}

echo "== 서명 (Developer ID, hardened runtime)"
bash "$PROJECT_ROOT/scripts/sign-macos.sh" "$APP" "$MIGHTY_CODESIGN_IDENTITY" --hardened-runtime
(cd /tmp && "$APP/Contents/MacOS/MightyClaude" --verify-resources >/dev/null)

echo "== 앱 공증"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$WORK/submit.zip"
notarize "$WORK/submit.zip"
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"
spctl --assess --type execute --verbose=2 "$APP"
# The update zip holds the stapled app, packed as the old zip was.
rm -f "$OUT/MightyClaude-macos.zip"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/MightyClaude-macos.zip"

echo "== dmg"
# The install window (background, arrow, icon places, no toolbar) is written by dmgbuild
# from scripts/dmg/settings.py, not by Finder, so a headless runner builds the same one.
# DMGBUILD names the tool (the CI job installs scripts/dmg/requirements.txt in a venv).
DMGBUILD="${DMGBUILD:-dmgbuild}"
command -v "$DMGBUILD" >/dev/null || { echo "dmgbuild가 없습니다: pip install -r scripts/dmg/requirements.txt" >&2; exit 1; }
rm -f "$OUT/MightyClaude-macos.dmg"
# hdiutil's deprecation notices are dropped; dmgbuild's own exit status still decides.
"$DMGBUILD" -s "$PROJECT_ROOT/scripts/dmg/settings.py" -D app="$APP" -D background="$PROJECT_ROOT/assets/dmg/background.tiff" \
    "Mighty Claude" "$OUT/MightyClaude-macos.dmg" > >(grep -v "is deprecated" >&2) 2>&1 \
    || { echo "dmgbuild가 실패했습니다" >&2; exit 1; }
[ -f "$OUT/MightyClaude-macos.dmg" ] || { echo "dmg를 만들지 못했습니다" >&2; exit 1; }
codesign --force --timestamp --sign "$MIGHTY_CODESIGN_IDENTITY" "$OUT/MightyClaude-macos.dmg"
notarize "$OUT/MightyClaude-macos.dmg"
xcrun stapler staple "$OUT/MightyClaude-macos.dmg"
xcrun stapler validate "$OUT/MightyClaude-macos.dmg"
spctl --assess --type open --context context:primary-signature --verbose=2 "$OUT/MightyClaude-macos.dmg"

ls -l "$OUT"/MightyClaude-macos.zip "$OUT"/MightyClaude-macos.dmg
