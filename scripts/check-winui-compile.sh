#!/bin/bash
# 용법: scripts/check-winui-compile.sh
#
# Windows WinUI 앱의 C# 컴파일을 맥에서 그대로 돌려 본다.
#
# 왜 필요한가: Core 테스트는 맥에서 돌지만 WinUI 프로젝트는 돌지 않아서,
# using 하나가 빠지거나 Click 처리기가 Task를 버리는 실수(CS4014)가
# Windows CI에 가서야 빨갛게 드러났다. csc는 맥에서도 도는 도구이므로
# XAML 표시 컴파일러와 매니페스트 병합(mt.exe, Windows 전용)만 비켜 가면
# 같은 진단을 여기서 먼저 받을 수 있다.
#
# 비켜 가는 방법 두 가지.
#   * 매니페스트 — 대상이 Inputs/Outputs를 가지므로 결과 파일을 미리 만들어
#     두면 MSBuild가 최신으로 보고 건너뛴다.
#   * XAML — 표시 컴파일러가 만드는 App.g.cs가 없으니 Program.cs의
#     InitializeComponent() 하나만 맥에서 못 찾는다. 이 한 건은 Windows에서는
#     반드시 존재하므로 예상된 유일한 오류로 보고, 그 밖의 오류는 실패로 본다.
#
# 앱을 띄우지 않고, 설치를 실행하지 않고, 실제 사용자 파일을 건드리지 않는다.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

PROJ="native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj"
# csproj의 기본값: Configuration=Debug, RuntimeIdentifier=win-x64.
OBJ="native/windows/MightyClaude.WinUI/obj/Debug/net10.0-windows10.0.19041.0/win-x64/Manifests"
# XAML 표시 컴파일러만 만들어 주는 멤버 — 맥에서만 나는 유일한 오류다.
EXPECTED='Program.cs(155,9): error CS0103'

mkdir -p "$OBJ"
cp native/windows/MightyClaude.WinUI/app.manifest "$OBJ/app.manifest"
touch "$OBJ/app.manifest"

LOG="$(mktemp -t mighty-winui-compile)"
trap 'rm -f "$LOG"' EXIT

dotnet build "$PROJ" -p:EnableWindowsTargeting=true -t:Compile --nologo -v q > "$LOG" 2>&1

# 오류 줄만 모아 중복을 없앤다 (MSBuild가 요약에서 한 번 더 찍는다).
UNEXPECTED="$(grep -oE '[A-Za-z0-9_.]+\.cs\([0-9]+,[0-9]+\): error [A-Z]+[0-9]+' "$LOG" \
  | sort -u | grep -vF "$EXPECTED")"

if [ -n "$UNEXPECTED" ]; then
  echo "✗ WinUI C# 컴파일 오류가 남아 있습니다:" >&2
  echo "$UNEXPECTED" >&2
  exit 1
fi

grep -qF "$EXPECTED" "$LOG" \
  || { echo "✗ 예상한 XAML 전용 오류가 사라졌습니다. 스크립트의 $EXPECTED 를 확인하세요." >&2; exit 1; }

echo "  WinUI: C# 컴파일 오류 없음 (XAML 전용 InitializeComponent 1건만 예상대로 남음)"
