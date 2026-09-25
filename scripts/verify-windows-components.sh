#!/bin/bash
# 용법: scripts/verify-windows-components.sh
#
# Windows 구성 요소 화면이 실제로 살아 있는지 한 번에 확인한다
# (시드 interview_20260924_153901 3단계, DELIVERED_OK).
#
# 네 가지를 차례로 본다.
#   1. locales 관문 — 빠진 열쇠말도, 남은 하드코딩 한국어도 없다.
#   2. WinUI C# 컴파일 — 구성 요소 화면을 그린 코드가 실제로 컴파일된다.
#      (Core 테스트만으로는 WinUI 쪽 using 누락이나 CS4014를 잡지 못해
#       Windows CI에 가서야 빨갛게 드러났다. 그 되먹임을 여기로 당겨 온다.)
#   3. Windows Core 테스트 — 도구 모음·승인·설치 계획·조사와,
#      "components screen is live in the running app"이 통과한다.
#   4. 그 테스트가 실제로 이름으로 통과했는지 — 목록에서 사라지면 실패다.
#
# 설치를 실행하지 않고, 앱을 띄우지 않고, 실제 사용자 파일을 건드리지 않는다.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

LIVE_TEST="components screen is live in the running app"

fail() { echo "✗ $1" >&2; exit 1; }

echo "[1/4] locales 관문"
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)" \
  || fail "locales 검사가 실패했습니다."

echo "[2/4] WinUI C# 컴파일"
bash scripts/check-winui-compile.sh || fail "WinUI C# 컴파일이 실패했습니다."

echo "[3/4] Windows Core 테스트"
CORE_LOG="$(mktemp -t mighty-win-core)"
trap 'rm -f "$CORE_LOG"' EXIT
dotnet run --project native/windows/MightyClaude.Core.Tests > "$CORE_LOG" 2>&1 \
  || { tail -20 "$CORE_LOG" >&2; fail "Windows Core 테스트가 실패했습니다."; }
tail -1 "$CORE_LOG"

echo "[4/4] 구성 요소 화면 테스트"
grep -qF "PASS ${LIVE_TEST}" "$CORE_LOG" \
  || fail "테스트가 목록에 없습니다: ${LIVE_TEST}"
grep -cE '^PASS ' "$CORE_LOG" | sed 's/^/  PASS 줄 /'

echo "DELIVERED_OK"
