#!/bin/bash
# 용법: scripts/check-style-freeze.sh [<tag>]        # 기본 tag = mighty-style-engine-v5
#
# 태그 이후의 diff가 고정된 엔진 경로(3번)를 건드리지 않았는지 본다
# (docs/mighty-styles.md §8.3). git만 쓰므로 DEVELOPER_DIR이 필요 없다.
set -uo pipefail

TAG="${1:-mighty-style-engine-v5}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# git이 대답하지 못하면 검사가 성립하지 않는다. 그때는 통과가 아니라 2다 —
# 열려 있는 고정 장치는 고정 장치가 아니다.
fail_closed() { echo "$1" >&2; exit 2; }
mismatch() { fail_closed "태그 ${TAG}가 FREEZE와 맞지 않습니다."; }

git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1 || fail_closed "git 저장소가 아닙니다: ${ROOT}"

# 1. 태그의 존재만 보면 `git tag -f <tag> HEAD` 한 줄로 diff가 비고 검사가
#    스스로를 고정하지 못한다. 그래서 태그 커밋 T는 `styles/FREEZE` 하나만
#    더하고, 그 내용은 **T의 부모** P의 SHA다(자기 참조가 아니다). 세 가지가
#    모두 맞아야 한다: FREEZE의 SHA = T의 부모, 그리고 T가 건드린 파일은
#    `styles/FREEZE` 하나뿐. FREEZE를 새로 쓰지 않고 태그만 옮기면 이 셋 중 하나가
#    깨진다. 엔진을 바꾼 뒤 FREEZE까지 새로 써서 태그를 다시 거는 것은 막지 못한다
#    — 저장소 안의 검사로는 막을 수 없는 일이라, 고정한 두 커밋의 SHA를
#    docs/styles-followups.md 머리에 적어 두고 사람이 대조한다.
FROZEN="$(git -C "$ROOT" show "$TAG:styles/FREEZE" 2>/dev/null | tr -d '[:space:]')" || mismatch
case "$FROZEN" in
  [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]\
[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]\
[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]\
[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) ;;
  *) fail_closed "styles/FREEZE의 내용이 40자리 커밋 SHA가 아닙니다." ;;
esac
TAGGED="$(git -C "$ROOT" rev-parse --verify --quiet "$TAG^{commit}")" || mismatch
[ -n "$TAGGED" ] || mismatch
PARENT="$(git -C "$ROOT" rev-parse --verify --quiet "${TAGGED}^")" || mismatch
[ -n "$PARENT" ] || mismatch
[ "$PARENT" = "$FROZEN" ] || mismatch
# `--no-renames`가 없으면 이름 변경이 목적지 한 줄로만 나와 원본이 사라진 것이
# 보이지 않는다. 태그 커밋 자신에도 같은 잣대를 댄다.
TAG_ADDS="$(git -C "$ROOT" -c core.quotePath=false diff --no-renames --name-only "$TAGGED^" "$TAGGED")" || mismatch
[ "$TAG_ADDS" = "styles/FREEZE" ] || mismatch

# 2. 태그 이후에 손댄 경로 전부. 실패하면 목록이 비는 것이 아니라 검사가 끝난다.
CHANGED="$(git -C "$ROOT" -c core.quotePath=false diff --no-renames --name-only "${TAGGED}..HEAD")" \
  || fail_closed "태그 이후의 변경 목록을 읽지 못했습니다: ${TAG}..HEAD"

# 한 경로 조각을 뜻하는 자리에서 `*`는 `/`를 넘지 않는다. `case`의 `*`는 넘으므로
# 앞뒤를 떼고 남은 가운데에 `/`가 없는지 따로 본다.
segment_match() {
  local value="$1" head="$2" tail="$3" middle
  case "$value" in "$head"*"$tail") ;; *) return 1 ;; esac
  middle="${value#"$head"}"
  middle="${middle%"$tail"}"
  case "$middle" in */*) return 1 ;; esac
  return 0
}

# 3. 고정된 것은 엔진이다: 엔진 코드, 번들 매니페스트, 동등성 오라클 테스트, 폰 렌더러,
#    이 계약 문서, 이 검사와 그것을 부르는 워크플로. 나머지 앱 개발은 태그와 상관없다.
#    제삼자 스타일의 테스트 두 글롭만 오라클 이름 안에서 예외다.
frozen() {
  case "$1" in
    styles/FREEZE) return 0 ;;
    native/macos/Sources/MightyCore/Styles/*) return 0 ;;
    native/macos/Sources/MightyCore/Resources/Styles/*) return 0 ;;
    mobile/src/lib/styles.ts) return 0 ;;
    docs/mighty-styles.md) return 0 ;;
    scripts/check-style-freeze.sh) return 0 ;;
    .github/workflows/style-freeze.yml) return 0 ;;
  esac
  segment_match "$1" "native/macos/Tests/MightyCoreTests/StylesThirdParty" "Tests.swift" && return 1
  segment_match "$1" "mobile/src/__tests__/styles-thirdparty-" ".test.ts" && return 1
  segment_match "$1" "native/macos/Tests/MightyCoreTests/Style" ".swift" && return 0
  segment_match "$1" "native/macos/Tests/MightyCoreTests/SuperpowersStyle" ".swift" && return 0
  segment_match "$1" "mobile/src/__tests__/styles" ".test.ts" && return 0
  return 1
}

FROZEN_CHANGED=""
COUNT=0
while IFS= read -r path; do
  [ -n "$path" ] || continue
  COUNT=$((COUNT + 1))
  if frozen "$path"; then
    FROZEN_CHANGED="$FROZEN_CHANGED$path"$'\n'
  fi
done <<< "$CHANGED"

# 4. 고정된 경로가 하나라도 바뀌었으면 그 목록을 찍고 실패한다.
if [ -n "$FROZEN_CHANGED" ]; then
  echo "고정된 엔진의 변경입니다:" >&2
  printf '%s' "$FROZEN_CHANGED" >&2
  exit 1
fi

# 5.
echo "OK: ${COUNT} files changed since ${TAG}, none in the frozen engine"
