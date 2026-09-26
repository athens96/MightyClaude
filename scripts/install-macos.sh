#!/bin/bash
# Replace the installed app only while it is not running. Swapping the bundle
# underneath a running MightyClaude breaks the macOS input method connection
# (Korean composition falls apart into jamo) and can leave Keychain and other
# services confused until the app restarts.
#
# Every build is ad-hoc signed with a fresh signature and every copy of the
# bundle (build output, backups) registers with LaunchServices under the same
# bundle id, so after the swap this script unregisters the other copies and
# re-registers the installed one, keeps the backup under a name that is not an
# app bundle, and gives the old process a moment to disappear before relaunch.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="${1:-$PROJECT_ROOT/release/native-macos/MightyClaude.app}"
DESTINATION="${MIGHTY_INSTALL_PATH:-/Applications/MightyClaude.app}"
BINARY="$DESTINATION/Contents/MacOS/MightyClaude"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

# Serialize the entire wait/copy/relaunch sequence. Two pending installers used
# to wake on the same app exit and write the same timestamped backup together.
# Keep the lock inode: unlinking it permits another process to lock a new inode.
LOCK_KEY="$(printf '%s' "$DESTINATION" | shasum -a 256 | cut -d ' ' -f 1)"
LOCK_PATH="/tmp/mightyclaude-install-${UID}-${LOCK_KEY}.lock"
# The shell itself retains the locked file descriptor. A wrapper process would
# release its lock when cancelled while leaving this child installer running.
exec 9>"$LOCK_PATH"
/usr/bin/lockf -t 0 9

running() { ps -axo command | grep -F "$BINARY" | grep -v grep >/dev/null; }

[ -d "$SOURCE" ] || { echo "설치할 앱이 없습니다: $SOURCE" >&2; exit 1; }
codesign --verify --deep --strict "$SOURCE"

# Guard: verify that the source bundle resolves its resources before touching
# the installed app. A broken bundle must never replace a working installation.
VERIFY_OUT="$(cd /tmp && "$SOURCE/Contents/MacOS/MightyClaude" --verify-resources 2>&1)"
VERIFY_EXIT=$?
echo "$VERIFY_OUT"
if [ "$VERIFY_EXIT" -ne 0 ]; then
  echo "설치 거부: $SOURCE 의 --verify-resources 가 실패했습니다. 빌드를 다시 확인하세요." >&2
  exit 1
fi

if running; then
  echo "MightyClaude가 실행 중입니다. 앱을 종료(⌘Q)하면 교체합니다…"
  while running; do sleep 1; done
  # Let the input method and LaunchServices finish tearing down the old process.
  sleep 2
fi

# Keep two backups, named so LaunchServices does not treat them as apps.
ls -dt /tmp/MightyClaude-app-backup-* 2>/dev/null | tail -n +3 | xargs rm -rf 2>/dev/null || true
if [ -d "$DESTINATION" ]; then
  BACKUP="$(mktemp -d /tmp/MightyClaude-app-backup-XXXXXXXX)"
  ditto "$DESTINATION" "$BACKUP/MightyClaude.app.bak"
  echo "이전 앱 백업: $BACKUP/MightyClaude.app.bak (복원: ditto 그 폴더 → $DESTINATION)"
  rm -rf "$DESTINATION"
fi
ditto "$SOURCE" "$DESTINATION"
codesign --verify --deep --strict "$DESTINATION"
"$LSREGISTER" -u "$SOURCE" >/dev/null 2>&1 || true
"$LSREGISTER" -f "$DESTINATION" >/dev/null 2>&1 || true
echo "설치 완료: $DESTINATION ($(ls -la "$BINARY" | awk '{print $6, $7, $8}'))"
open "$DESTINATION"
