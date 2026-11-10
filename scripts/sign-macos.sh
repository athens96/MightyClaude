#!/bin/bash
# Sign MightyCode.app from the inside out.
#
#   scripts/sign-macos.sh <MightyCode.app> <identity> [--hardened-runtime]
#
# Every nested binary is signed before the bundle that holds it, each with its
# own entitlements (native/macos/Entitlements), because `codesign --deep` would
# give them all the app's. Without --hardened-runtime this is the old signing:
# no runtime, no entitlements. With it, every binary gets the hardened runtime
# that notarization requires; a "Developer ID Application" identity also gets a
# secure timestamp. Any other identity (the self-signed "Mighty Claude Dev",
# ad-hoc "-") has no Team ID, so the runtime's library validation could never
# pass for the app's own frameworks: those builds also get
# disable-library-validation, which a Developer ID build never carries.
set -euo pipefail

APP="$1"
IDENTITY="$2"
HARDENED="${3:-}"
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENTITLEMENTS="$PROJECT_ROOT/native/macos/Entitlements"
[ -d "$APP/Contents/MacOS" ] || { echo "서명할 앱이 없습니다: $APP" >&2; exit 1; }

OPTIONS=(--force --sign "$IDENTITY")
WORK=""
entitlements() { echo "$ENTITLEMENTS/$1.entitlements"; }
if [ "$HARDENED" = "--hardened-runtime" ]; then
    OPTIONS+=(--options runtime)
    case "$IDENTITY" in
        "Developer ID Application"*) OPTIONS+=(--timestamp) ;;
        *)
            WORK="$(mktemp -d)"
            trap 'rm -rf "$WORK"' EXIT
            for name in MightyCode Helper Node; do
                cp "$ENTITLEMENTS/$name.entitlements" "$WORK/$name.entitlements"
                # PlistBuddy, not plutil: plutil reads the dots in the key as a path.
                /usr/libexec/PlistBuddy -c "Add :com.apple.security.cs.disable-library-validation bool true" "$WORK/$name.entitlements"
            done
            entitlements() { echo "$WORK/$1.entitlements"; }
            ;;
    esac
elif [ -n "$HARDENED" ]; then
    echo "알 수 없는 옵션: $HARDENED" >&2; exit 1
fi

sign() { codesign "${OPTIONS[@]}" "$@"; }
# Entitlements only mean something under the hardened runtime; the old signing
# carried none.
sign_with() {
    local name="$1"; shift
    if [ "$HARDENED" = "--hardened-runtime" ]; then sign --entitlements "$(entitlements "$name")" "$@"; else sign "$@"; fi
}
is_macho() { file -b "$1" | grep -q '^Mach-O'; }

# Loose Mach-O files outside any nested bundle: the bundled Node and any helper
# tool or dylib shipped as a resource.
while IFS= read -r -d '' file; do
    case "${file#"$APP"/}" in *.app/*|*.framework/*) continue ;; esac
    is_macho "$file" || continue
    case "$file" in
        */browser/node/bin/node) sign_with Node "$file" ;;
        *) sign "$file" ;;
    esac
done < <(find "$APP/Contents/Resources" "$APP/Contents/Frameworks" -type f -print0 2>/dev/null)

# The browser engine: its libraries, the framework, then the helper apps.
CEF_FW="$APP/Contents/Frameworks/Chromium Embedded Framework.framework"
if [ -d "$CEF_FW" ]; then
    for dylib in "$CEF_FW/Libraries/"*.dylib; do
        [ -f "$dylib" ] && sign "$dylib"
    done
    sign "$CEF_FW"
    for helper_app in "$APP/Contents/Frameworks/"*.app; do
        [ -d "$helper_app" ] && sign_with Helper "$helper_app"
    done
fi

# Other nested frameworks (WebRTC), then the app itself.
for framework in "$APP/Contents/Frameworks/"*.framework; do
    [ -d "$framework" ] || continue
    [ "$framework" = "$CEF_FW" ] && continue
    sign "$framework"
done
sign_with MightyCode "$APP"

codesign --verify --deep --strict "$APP"
