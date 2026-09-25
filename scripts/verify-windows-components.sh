#!/bin/bash
# 용법: scripts/verify-windows-components.sh
#
# Windows 구성 요소 화면이 실제로 살아 있는지 한 번에 확인한다
# (시드 interview_20260924_153901 3단계, DELIVERED_OK).
#
# 세 가지를 차례로 본다.
#   1. locales 관문 — 빠진 열쇠말도, 남은 하드코딩 한국어도 없다.
#   2. Windows Core 테스트 — 도구 모음·승인·설치 계획·조사와,
#      "components screen is live in the running app"이 통과한다.
#   3. 그 테스트가 실제로 이름으로 통과했는지 — 목록에서 사라지면 실패다.
#
# 설치를 실행하지 않고, 앱을 띄우지 않고, 실제 사용자 파일을 건드리지 않는다.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

LIVE_TEST="components screen is live in the running app"

fail() { echo "✗ $1" >&2; exit 1; }

echo "[1/3] locales 관문"
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)" \
  || fail "locales 검사가 실패했습니다."

echo "[2/3] Windows Core 테스트"
CORE_LOG="$(mktemp -t mighty-win-core)"
trap 'rm -f "$CORE_LOG"' EXIT
dotnet run --project native/windows/MightyClaude.Core.Tests > "$CORE_LOG" 2>&1 \
  || { tail -20 "$CORE_LOG" >&2; fail "Windows Core 테스트가 실패했습니다."; }
tail -1 "$CORE_LOG"

echo "[3/3] 구성 요소 화면 테스트"
grep -qF "PASS ${LIVE_TEST}" "$CORE_LOG" \
  || fail "테스트가 목록에 없습니다: ${LIVE_TEST}"
grep -cE '^PASS ' "$CORE_LOG" | sed 's/^/  PASS 줄 /'

echo "DELIVERED_OK"
