#!/bin/bash
set -euo pipefail

# Mechanical resizing/encoding only; the original artwork is never modified.
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ICON_SOURCE="${1:-$PROJECT_ROOT/assets/icons/mightyclaude.png}"
ICON_OUTPUT="${2:-$PROJECT_ROOT/assets/icons}"
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
# Never replace the selected toolchain with a hard-coded Command Line Tools path.
ICON_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/mightyclaude-icons.XXXXXX")"
trap 'rm -rf "$ICON_TEMP"' EXIT
mkdir -p "$ICON_OUTPUT" "$ICON_TEMP/MightyClaude.iconset" "$PROJECT_ROOT/native/macos/.build/module-cache"
xcrun swift -module-cache-path "$PROJECT_ROOT/native/macos/.build/module-cache" \
  "$PROJECT_ROOT/scripts/package-icons.swift" "$ICON_SOURCE" "$ICON_TEMP/MightyClaude.iconset" "$ICON_TEMP/MightyClaude.ico"
iconutil --convert icns --output "$ICON_TEMP/MightyClaude.icns" "$ICON_TEMP/MightyClaude.iconset"
cp "$ICON_TEMP/MightyClaude.icns" "$ICON_OUTPUT/MightyClaude.icns"
cp "$ICON_TEMP/MightyClaude.ico" "$ICON_OUTPUT/MightyClaude.ico"
printf 'Packaged icons: %s\n' "$ICON_OUTPUT"
