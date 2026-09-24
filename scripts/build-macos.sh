#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
# Never replace the selected toolchain with a hard-coded Command Line Tools path.
SWIFT_EXECUTABLE="$(xcrun --find swift)"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-release}"
BUILD_SCRATCH="${MIGHTY_BUILD_SCRATCH:-$PROJECT_ROOT/native/macos/.build}"
MODULE_CACHE="$BUILD_SCRATCH/module-cache"
APP_PATH="${MIGHTY_MACOS_APP_PATH:-$PROJECT_ROOT/release/native-macos/MightyClaude.app}"
mkdir -p "$MODULE_CACHE"
export CLANG_MODULE_CACHE_PATH="$MODULE_CACHE"
export SWIFTPM_MODULECACHE_OVERRIDE="$MODULE_CACHE"
bash "$PROJECT_ROOT/scripts/package-icons.sh"

"$SWIFT_EXECUTABLE" build --disable-sandbox --package-path "$PROJECT_ROOT/native/macos" --scratch-path "$BUILD_SCRATCH" -c "$BUILD_CONFIGURATION" -Xswiftc -module-cache-path -Xswiftc "$MODULE_CACHE"
BIN_PATH="$("$SWIFT_EXECUTABLE" build --disable-sandbox --package-path "$PROJECT_ROOT/native/macos" --scratch-path "$BUILD_SCRATCH" -c "$BUILD_CONFIGURATION" --show-bin-path)"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources/mods"
cp "$BIN_PATH/MightyClaude" "$APP_PATH/Contents/MacOS/MightyClaude"
# `ditto` merges rather than replaces, so a Styles/ left by an earlier build
# would satisfy the gate below even after the manifests stopped being packaged.
STYLE_BUNDLE="$APP_PATH/Contents/Resources/MightyClaude_MightyCore.bundle"
rm -rf "$STYLE_BUNDLE/Contents/Resources/Styles" "$STYLE_BUNDLE/Styles"
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
# `-s` rather than `-f`: a zero-byte ouroboros.json is exactly the failure this
# gate exists to catch, and it passes an existence check.
for style_name in ouroboros paperthin; do
  if [ ! -s "$STYLE_BUNDLE/Contents/Resources/Styles/$style_name.json" ] && [ ! -s "$STYLE_BUNDLE/Styles/$style_name.json" ]; then
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
<key>NSPrincipalClass</key><string>MightyApplication</string>
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
# Browser engine bundling: CEF framework, helper stubs, pinned Node, licence notices.
# Requires a warm engine cache (run scripts/fetch-browser-engine.sh first).
if [ "${MIGHTY_BROWSER_ENGINE:-}" = "1" ]; then
    LOCK_FILE="$PROJECT_ROOT/native/macos/BrowserEngine.lock"
    CEF_URL="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['cef']['url'])" "$LOCK_FILE")"
    NODE_URL="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['node']['url'])" "$LOCK_FILE")"
    NODE_VERSION="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['node']['version'])" "$LOCK_FILE")"
    LOCK_HASH="$(shasum -a 256 "$LOCK_FILE" | awk '{print $1}')"
    ENGINE_CACHE="${MIGHTY_BROWSER_ENGINE_CACHE:-$HOME/Library/Caches/MightyClaude/browser-engine/${LOCK_HASH:0:16}}"
    CEF_ARCHIVE="$ENGINE_CACHE/$(basename "$CEF_URL")"
    NODE_ARCHIVE="$ENGINE_CACHE/$(basename "$NODE_URL")"
    if [ ! -f "$CEF_ARCHIVE" ] || [ ! -f "$NODE_ARCHIVE" ]; then
        echo "browser engine not in cache — run scripts/fetch-browser-engine.sh first" >&2
        exit 1
    fi
    EXTRACT_TEMP="$(mktemp -d)"
    # shellcheck disable=SC2064
    trap "rm -rf '$EXTRACT_TEMP'" EXIT
    FW_DIR="$APP_PATH/Contents/Frameworks"
    mkdir -p "$FW_DIR"
    # CEF framework
    CEF_TOP="$(tar -tjf "$CEF_ARCHIVE" | head -1 | cut -d/ -f1)"
    tar -xjf "$CEF_ARCHIVE" -C "$EXTRACT_TEMP" 2>/dev/null
    ditto "$EXTRACT_TEMP/$CEF_TOP/Release/Chromium Embedded Framework.framework" \
          "$FW_DIR/Chromium Embedded Framework.framework"
    # Licence notices (CEF and Chromium names satisfy the check)
    LIC_DIR="$APP_PATH/Contents/Resources/ThirdPartyLicenses"
    cp "$EXTRACT_TEMP/$CEF_TOP/LICENSE.txt"  "$LIC_DIR/CEF-LICENSE.txt"
    cp "$EXTRACT_TEMP/$CEF_TOP/CREDITS.html" "$LIC_DIR/Chromium-CREDITS.html"
    # Node runtime (only the node binary is required by the check)
    NODE_BASE="node-v${NODE_VERSION}-darwin-arm64"
    tar -xzf "$NODE_ARCHIVE" -C "$EXTRACT_TEMP" "$NODE_BASE/bin/node" 2>/dev/null
    NODE_DST="$APP_PATH/Contents/Resources/browser/node/bin"
    mkdir -p "$NODE_DST"
    cp "$EXTRACT_TEMP/$NODE_BASE/bin/node" "$NODE_DST/node"
    chmod +x "$NODE_DST/node"
    # Real CEF helpers: call cef_execute_process via dlopen/dlsym so the Swift
    # package builds without CEF headers.  Each helper resolves the framework
    # relative to its own executable path at runtime.
    xcrun clang -o "$EXTRACT_TEMP/helper_bin" -arch arm64 -mmacosx-version-min=14.0 \
        -I "$EXTRACT_TEMP/$CEF_TOP" \
        "$PROJECT_ROOT/native/macos/BrowserBridge/helper.c"

    # Keep the native C API lifecycle implementation reviewable and compile it
    # against exactly the headers shipped with the pinned framework.
    xcrun clang -dynamiclib -o "$FW_DIR/MightyCEFBridge.dylib" \
        -arch arm64 -mmacosx-version-min=14.0 \
        -I "$EXTRACT_TEMP/$CEF_TOP" -framework CoreFoundation -framework AppKit \
        -install_name "@rpath/MightyCEFBridge.dylib" \
        "$PROJECT_ROOT/native/macos/BrowserBridge/cef_bridge.m"

    make_helper() {
        local name="$1" bundle_id="$2"
        local happ="$FW_DIR/${name}.app"
        mkdir -p "$happ/Contents/MacOS"
        cp "$EXTRACT_TEMP/helper_bin" "$happ/Contents/MacOS/$name"
        cat > "$happ/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$bundle_id</string>
<key>CFBundleExecutable</key><string>$name</string>
<key>CFBundleName</key><string>$name</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
</dict></plist>
PLIST
    }
    make_helper "MightyClaude Helper"           "dev.mightyclaude.native.helper"
    make_helper "MightyClaude Helper (GPU)"      "dev.mightyclaude.native.helper.gpu"
    make_helper "MightyClaude Helper (Renderer)" "dev.mightyclaude.native.helper.renderer"
    make_helper "MightyClaude Helper (Plugin)"   "dev.mightyclaude.native.helper.plugin"
    rm -rf "$EXTRACT_TEMP"
    trap - EXIT
fi
# Ad-hoc signatures differ per build, so the Keychain treats every rebuild as a
# new app and asks again for the remote connection key. A stable local
# code-signing certificate (Keychain Access → Certificate Assistant, type
# "Code Signing") makes "Always Allow" stick across rebuilds.
CODESIGN_IDENTITY="${MIGHTY_CODESIGN_IDENTITY:--}"
# Sign CEF inner dylibs explicitly (not reached by --deep on the main bundle)
if [ "${MIGHTY_BROWSER_ENGINE:-}" = "1" ]; then
    CEF_FW="$APP_PATH/Contents/Frameworks/Chromium Embedded Framework.framework"
    for dylib in "$CEF_FW/Libraries/"*.dylib; do
        [ -f "$dylib" ] && codesign --force --sign "$CODESIGN_IDENTITY" "$dylib"
    done
    for helper_app in "$APP_PATH/Contents/Frameworks/"*.app; do
        [ -d "$helper_app" ] && codesign --force --sign "$CODESIGN_IDENTITY" "$helper_app"
    done
    [ -f "$APP_PATH/Contents/Frameworks/MightyCEFBridge.dylib" ] && \
        codesign --force --sign "$CODESIGN_IDENTITY" \
            "$APP_PATH/Contents/Frameworks/MightyCEFBridge.dylib"
    codesign --force --sign "$CODESIGN_IDENTITY" "$CEF_FW"
fi
codesign --force --deep --sign "$CODESIGN_IDENTITY" "$APP_PATH"
# Building must not change LaunchServices registrations while the installed app
# may be running. install-macos.sh handles registration after the app has quit.
printf '%s\n' "$APP_PATH"
