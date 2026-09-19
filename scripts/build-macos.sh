#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
# Never replace the selected toolchain with a hard-coded Command Line Tools path.
SWIFT_EXECUTABLE="$(xcrun --find swift)"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-release}"
BUILD_SCRATCH="$PROJECT_ROOT/native/macos/.build"
MODULE_CACHE="$PROJECT_ROOT/native/macos/.build/module-cache"
APP_PATH="${MIGHTY_MACOS_APP_PATH:-$PROJECT_ROOT/release/native-macos/MightyClaude.app}"
mkdir -p "$MODULE_CACHE"
export CLANG_MODULE_CACHE_PATH="$MODULE_CACHE"
export SWIFTPM_MODULECACHE_OVERRIDE="$MODULE_CACHE"
bash "$PROJECT_ROOT/scripts/package-icons.sh"

"$SWIFT_EXECUTABLE" build --disable-sandbox --package-path "$PROJECT_ROOT/native/macos" --scratch-path "$BUILD_SCRATCH" -c "$BUILD_CONFIGURATION" -Xswiftc -module-cache-path -Xswiftc "$MODULE_CACHE"
BIN_PATH="$("$SWIFT_EXECUTABLE" build --disable-sandbox --package-path "$PROJECT_ROOT/native/macos" --scratch-path "$BUILD_SCRATCH" -c "$BUILD_CONFIGURATION" --show-bin-path)"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources/mods"
cp "$BIN_PATH/MightyClaude" "$APP_PATH/Contents/MacOS/MightyClaude"
# SwiftPM resource bundles contain Ghostty's terminfo and shell integration.
# They must travel with the app; a development checkout is not a runtime dependency.
for resource_bundle in "$BIN_PATH"/*.bundle; do
  if [ -d "$resource_bundle" ]; then
    ditto "$resource_bundle" "$APP_PATH/Contents/Resources/$(basename "$resource_bundle")"
  fi
done
# The bundled style manifests must travel inside that bundle. Losing them is
# silent at runtime — every pane just falls back to the plain CLI — so the
# second gate of docs/mighty-styles.md §3.2 is here, at assembly.
STYLE_BUNDLE="$APP_PATH/Contents/Resources/MightyClaude_MightyCore.bundle"
for style_name in ouroboros paperthin; do
  if [ ! -f "$STYLE_BUNDLE/Contents/Resources/Styles/$style_name.json" ] && [ ! -f "$STYLE_BUNDLE/Styles/$style_name.json" ]; then
    echo "번들 스타일 매니페스트가 없습니다: $style_name.json ($STYLE_BUNDLE)" >&2
    exit 1
  fi
done
ditto "$PROJECT_ROOT/native/licenses" "$APP_PATH/Contents/Resources/ThirdPartyLicenses"
cp "$PROJECT_ROOT/assets/icons/MightyClaude.icns" "$APP_PATH/Contents/Resources/MightyClaude.icns"
cp "$PROJECT_ROOT/assets/icons/mightyclaude.png" "$APP_PATH/Contents/Resources/mightyclaude.png"
if [ -d "$PROJECT_ROOT/assets/pets" ]; then
  ditto "$PROJECT_ROOT/assets/pets" "$APP_PATH/Contents/Resources/pets"
fi
ditto "$PROJECT_ROOT/mods/mighty-bridge" "$APP_PATH/Contents/Resources/mods/mighty-bridge"
cat > "$APP_PATH/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>MightyClaude</string>
<key>CFBundleDisplayName</key><string>MightyClaude</string>
<key>CFBundleIdentifier</key><string>dev.mightyclaude.native</string>
<key>CFBundleExecutable</key><string>MightyClaude</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>MightyClaude.icns</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSPrincipalClass</key><string>NSApplication</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/><key>NSAllowsArbitraryLoads</key><true/></dict>
</dict></plist>
PLIST
# Version and update address: VERSION file (or MIGHTY_APP_VERSION), commit count
# (or MIGHTY_BUILD_NUMBER), and the manifest URL the app checks (MIGHTY_UPDATE_URL).
APP_VERSION="${MIGHTY_APP_VERSION:-$(tr -d '[:space:]' < "$PROJECT_ROOT/VERSION" 2>/dev/null || echo 0.1.0)}"
BUILD_NUMBER="${MIGHTY_BUILD_NUMBER:-$(git -C "$PROJECT_ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"
plutil -replace CFBundleShortVersionString -string "$APP_VERSION" "$APP_PATH/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$BUILD_NUMBER" "$APP_PATH/Contents/Info.plist"
if [ -n "${MIGHTY_UPDATE_URL:-}" ]; then
  plutil -replace MightyUpdateManifestURL -string "$MIGHTY_UPDATE_URL" "$APP_PATH/Contents/Info.plist"
fi
# Base64 raw Ed25519 public key; with it the app accepts only signed manifests.
if [ -n "${MIGHTY_UPDATE_PUBLIC_KEY:-}" ]; then
  plutil -replace MightyUpdatePublicKey -string "$MIGHTY_UPDATE_PUBLIC_KEY" "$APP_PATH/Contents/Info.plist"
fi
# Ad-hoc signatures differ per build, so the Keychain treats every rebuild as a
# new app and asks again for the remote connection key. A stable local
# code-signing certificate (Keychain Access → Certificate Assistant, type
# "Code Signing") makes "Always Allow" stick across rebuilds.
CODESIGN_IDENTITY="${MIGHTY_CODESIGN_IDENTITY:--}"
codesign --force --deep --sign "$CODESIGN_IDENTITY" "$APP_PATH"
# The build output must not compete with the installed app for the bundle id
# in LaunchServices (duplicate registrations confuse the text-input session).
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$APP_PATH" >/dev/null 2>&1 || true
printf '%s\n' "$APP_PATH"
