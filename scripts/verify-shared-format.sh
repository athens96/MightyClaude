#!/bin/bash
# 용법: scripts/verify-shared-format.sh
#
# 공유 도구 모음 JSON 형식이 macOS와 Windows 양쪽에서 같은 파일을 읽는지
# 한 번에 확인한다 (시드 interview_20260924_153901 3단계 AC 5, SHARED_FORMAT_OK).
#
# 세 가지를 차례로 본다.
#   1. locales 관문 — 빠진 열쇠말도, 남은 하드코딩 한국어도 없다.
#   2. macOS 테스트 — ToolkitSharedFormatTests 묶음이 통과한다.
#   3. Windows Core 테스트 — shared-format 두 항목이 통과한다.
#
# 설치를 실행하지 않고, 앱을 띄우지 않고, 실제 사용자 파일을 건드리지 않는다.
set -uo pipefail

export DEVELOPER_DIR=/Library/Developer/CommandLineTools

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

fail() { echo "✗ $1" >&2; exit 1; }

SCRATCH="$(mktemp -d /tmp/mighty-shared-format-XXXXXXXX)"
trap 'rm -rf "$SCRATCH"' EXIT

echo "[1/3] locales 관문"
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)" \
  || fail "locales 검사가 실패했습니다."

echo "[2/3] macOS ToolkitSharedFormatTests"
MAC_LOG="$SCRATCH/mac.log"
bash scripts/test-native-macos.sh --scratch-path "$SCRATCH/mac" \
  --filter 'ToolkitSharedFormatTests' > "$MAC_LOG" 2>&1 \
  || { tail -30 "$MAC_LOG" >&2; fail "macOS ToolkitSharedFormatTests가 실패했습니다."; }
grep -q "Suite ToolkitSharedFormatTests passed" "$MAC_LOG" \
  || { tail -20 "$MAC_LOG" >&2; fail "Suite ToolkitSharedFormatTests passed 표시가 없습니다."; }
echo "  macOS: Suite ToolkitSharedFormatTests passed ✓"

echo "[3/3] Windows Core 테스트"
WIN_LOG="$SCRATCH/win.log"
dotnet run --project native/windows/MightyClaude.Core.Tests > "$WIN_LOG" 2>&1 \
  || { tail -20 "$WIN_LOG" >&2; fail "Windows Core 테스트가 실패했습니다."; }
tail -1 "$WIN_LOG"
grep -qF "PASS shared-format fixture round-trip" "$WIN_LOG" \
  || fail "테스트가 목록에 없습니다: shared-format fixture round-trip"
grep -qF "PASS shared-format platform table" "$WIN_LOG" \
  || fail "테스트가 목록에 없습니다: shared-format platform table"
echo "  Windows: shared-format fixture round-trip ✓"
echo "  Windows: shared-format platform table ✓"

echo "SHARED_FORMAT_OK"
