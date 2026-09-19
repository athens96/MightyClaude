#!/bin/bash
# 용법: scripts/check-style-freeze.sh [<tag>]        # 기본 tag = mighty-style-engine-v1
#
# 태그 이후의 diff가 매니페스트와 그 테스트만 담고 있는지 본다
# (docs/mighty-styles.md §8.3). git만 쓰므로 DEVELOPER_DIR이 필요 없다.
set -uo pipefail

TAG="${1:-mighty-style-engine-v1}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FREEZE="$ROOT/styles/FREEZE"

# 1. 태그의 존재만 보면 `git tag -f <tag> HEAD` 한 줄로 검사가 비어 버린다.
#    커밋 SHA를 허용 목록 밖의 파일에 박아 두어야 태그를 옮길 수 없다.
mismatch() { echo "태그 ${TAG}가 FREEZE와 맞지 않습니다." >&2; exit 2; }
[ -f "$FREEZE" ] || mismatch
FROZEN="$(tr -d '[:space:]' < "$FREEZE")"
[ -n "$FROZEN" ] || mismatch
TAGGED="$(git -C "$ROOT" rev-parse "$TAG^{commit}" 2>/dev/null)" || mismatch
[ "$TAGGED" = "$FROZEN" ] || mismatch

# 2. 태그 이후에 손댄 경로 전부.
CHANGED="$(git -C "$ROOT" diff --name-only "$TAG..HEAD")"

# 3. 금지 목록을 먼저, 그다음 허용 목록.
OUTSIDE=""
COUNT=0
while IFS= read -r path; do
  [ -n "$path" ] || continue
  COUNT=$((COUNT + 1))
  case "$path" in
    styles/FREEZE)
      OUTSIDE="$OUTSIDE$path"$'\n'; continue ;;
  esac
  case "$path" in
    styles/*|\
    native/macos/Tests/MightyCoreTests/StylesThirdParty*Tests.swift|\
    mobile/src/__tests__/styles-thirdparty-*.test.ts|\
    docs/styles-followups.md) ;;
    *) OUTSIDE="$OUTSIDE$path"$'\n' ;;
  esac
done <<< "$CHANGED"

# 4. 하나라도 바깥이면 그 목록을 찍고 실패한다.
if [ -n "$OUTSIDE" ]; then
  echo "고정된 엔진 밖의 변경입니다:" >&2
  printf '%s' "$OUTSIDE" >&2
  exit 1
fi

# 5.
echo "OK: ${COUNT} files, all inside the manifest-only allow-list"
