# 파이프라인 보기: 상태마다 색 구분 (카드 + 열 배경)

2026-10-25 · 사용자 결정 (brainstorming)

## 목표
마이티 보기의 **파이프라인**(구조/칸반: 단계 열, 열마다 카드)에서 실행·대기·완료·오류·중지를 색으로 한눈에 구분한다. 지금은 단계 칩만 상태 색을 쓰고, 카드는 실행(움직이는 파란 테두리)·대기(노란 테두리)만 다르며 완료·오류·중지 카드는 같아 보이고, 열 배경은 상태와 무관하다.

## 사용자가 정한 것
- 범위: **카드 + 열 배경** (선택지 2).
- 맥·윈도우·폰 모두 같은 규칙.

## 상태 → 색 (공통)
기존 디자인 토큰의 톤(`run, wait, done, err, stop, idle`)을 그대로 쓴다. 새 색은 만들지 않는다.

| 상태 문자열 | 톤 |
|---|---|
| running, 그 밖의 알 수 없는 진행 상태 | run |
| waiting (권한·질문·계획) | wait |
| completed, done | done |
| error, failed | err |
| stopped, cancelled, interrupted | stop |
| pending (아직 안 한 단계) | 없음 (바탕 그대로) |

- 열(단계) 상태: `done→done`, `current→run`(그 요청이 waiting이면 `wait`), `error→err`, `stopped→stop`, `pending→없음`.
- 폰 `toneOf`가 failed·cancelled를 놓치던 것을 맥(`MobileMightySupport.status`)과 같게 고친다.

## 열 배경
- 각 단계 열 전체(머리 칩 아래 카드 영역)에 그 톤의 `soft` 색을 **열 투명도**로 깐다.
- pending 열은 지금처럼 바탕색.

## 카드
- 모든 카드(실행·결과·하위 에이전트·작업 알림 카드 등 파이프라인 보기의 카드) 왼쪽에 **3pt 상태 막대**(톤의 `mark` 색).
- 카드 배경에 톤의 `soft` 색을 **카드 투명도**로 깐다. 카드 투명도 > 열 투명도라서 같은 톤 열 안에서도 카드가 구분된다.
- 실행 중 움직이는 테두리, 대기 노란 테두리, 상태 알약 글자·기호(✓ ✕ ■)는 그대로 둔다(색만으로 구분하지 않게).
- 이미 3pt 왼쪽 막대가 있는 하위 에이전트 카드는 같은 막대를 쓴다(중복 막대 없음).

## 공유 값
`native/contracts/fixtures/design-tokens.json`에 `pipeline` 묶음을 더한다:
- `columnTintOpacity` (예: 0.45), `cardTintOpacity` (예: 0.85), `cardBarWidth` 3.
맥·윈도우(Core·웹)·폰이 이 값을 그대로 읽거나 같은 상수를 두고, 기존 토큰 동등성 검사(맥 `DesignTokenParityTests`, 윈도우 `DesignTokenVerification`, 폰 jest, 웹 tokens)에 포함한다.

## 플랫폼
- **맥**: `MightyStructureView.swift`(열 배경·카드 막대/배경), 톤 매핑 헬퍼는 MightyCore(순수 함수, 테스트).
- **윈도우 웹(기본 화면)**: `native/windows/web/src/mighty/Structure.tsx` + `mighty.css`; 대기 카드가 파란 `.live` 테두리를 쓰던 것을 wait 테두리로.
- **윈도우 WinUI(WebView2 없을 때)**: `MainWindow.MightyStructure.cs` 같은 규칙.
- **폰**: `mobile/src/components/structure-view.tsx`, `mobile/src/lib/status-tone.ts`.
- 다크 모드는 각 테마 토큰.

## 테스트
- 상태 문자열 → 톤, 단계 → 톤(waiting 포함) 순수 함수 테스트: 맥(MightyCore), 윈도우 Core, 폰 jest, 웹 unit.
- 토큰 동등성 검사에 `pipeline` 값.
- 빌드·테스트 실행은 사용자가 요청할 때만(2026-10-23 규칙). 텍스트 검사(locales, design-tokens, help, style-freeze)는 돌린다.

## 문서
- 도움말 4개 언어 `agent-pane.md`의 파이프라인 설명에 색 의미 한 줄.
- 도움말 스크린샷(`agent-structure`)은 다음 CI 촬영으로 갱신.

## 범위 밖
- 흐름(다이어그램)·기록(타임라인) 보기의 색 변경, 새 색 토큰, 사용자 색 설정.
