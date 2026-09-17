#!/bin/bash
# Replace the installed app only while it is not running. Swapping the bundle
# underneath a running MightyClaude breaks the macOS input method connection
# (Korean composition falls apart into jamo) and can leave Keychain and other
# services confused until the app restarts.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="${1:-$PROJECT_ROOT/release/native-macos/MightyClaude.app}"
DESTINATION="${MIGHTY_INSTALL_PATH:-/Applications/MightyClaude.app}"
BINARY="$DESTINATION/Contents/MacOS/MightyClaude"

running() { ps -axo command | grep -F "$BINARY" | grep -v grep >/dev/null; }

[ -d "$SOURCE" ] || { echo "설치할 앱이 없습니다: $SOURCE" >&2; exit 1; }
codesign --verify --deep --strict "$SOURCE"

if running; then
  echo "MightyClaude가 실행 중입니다. 앱을 종료(⌘Q)하면 교체합니다…"
  while running; do sleep 1; done
fi

if [ -d "$DESTINATION" ]; then
  BACKUP="/tmp/MightyClaude-app-backup-$(date +%Y%m%d-%H%M%S)"
  mkdir -p "$BACKUP"
  ditto "$DESTINATION" "$BACKUP/MightyClaude.app"
  echo "이전 앱 백업: $BACKUP/MightyClaude.app"
  rm -rf "$DESTINATION"
fi
ditto "$SOURCE" "$DESTINATION"
codesign --verify --deep --strict "$DESTINATION"
echo "설치 완료: $DESTINATION ($(ls -la "$BINARY" | awk '{print $6, $7, $8}'))"
open "$DESTINATION"
