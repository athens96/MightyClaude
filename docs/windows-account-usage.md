# Windows 계정 사용량 표시

macOS `AccountUsageService.swift` · `StatusBarUsage.swift` · `AccountUsageSnapshot.swift`
를 Windows로 옮긴 규칙이다. Core(`native/windows/MightyClaude.Core`)가 모든
판단을 하고, WinUI는 Core가 만든 줄을 그리고 입력만 넘긴다.

## 화면

하단 상태 줄에, **로컬 AI 실행 창이 있는 실행기마다** 칩이 하나씩 붙는다.
`shell` 실행 창에는 계정이 없고, 원격 워크스페이스는 이 PC의 계정을 빌려
쓰지 않으므로 칩이 없다. 칩을 누르면 팝오버가 열리고, 실행기마다 한 장의
카드에 창별 사용률·진행 막대·`초기화 {date}`·`{time} 확인`이 들어간다.
새로고침 버튼이 팝오버 머리글 오른쪽에 있다.

값이 아직 없을 때 Claude 칩은 `실행 후 표시`, 나머지는 `확인 중` 또는 `—`.
오래되었거나 실패한 값은 `마지막 확인값 · ` 접두사를 달고 남는다.

## 기본은 꺼짐

직접 조회 스위치(`ClaudeDirectUsageLookupEnabled`)는 **기본 꺼짐**이고
팝오버 안에 있다. 저장되는 설정이며 기본값이 있는 추가 필드라서 스냅샷
`Version`은 1로 남는다(`StateStore`는 `Version`이 1이 아니면 모든 상태를
지운다).

꺼져 있는 동안:

| 실행기 | 표시 내용 |
| --- | --- |
| Claude | 실행 중 CLI가 보고한 한도(`SessionUsage.RateLimits`)만 |
| Codex | 자기 CLI의 `codex app-server`에 stdio로 물어본 값 |
| Gemini | macOS와 같은 문장 — `/stats`에서 확인하라는 안내 |

켠 뒤에만 `https://api.anthropic.com`에 직접 묻는다.

## 로그인 값 취급

- Claude CLI가 쓰는 `%USERPROFILE%\.claude\.credentials.json`에서 읽는다
  (`CLAUDE_CONFIG_DIR`가 있으면 그 폴더). 읽기 전에 크기를 확인하고
  (256 KiB 상한), 파일을 **고치거나 옮기거나 회전시키지 않는다**.
- 요청 한 번 동안만 메모리에 있고 `Authorization: Bearer` 헤더로만 쓰인다.
- 대상은 `https://api.anthropic.com` 하나뿐이다. 다른 호스트, HTTPS가 아닌
  주소, 3xx 리디렉션은 모두 거절한다(따라가지 않는다).
- 제한 시간 10초, 재시도 없음. 429는 `Retry-After`만큼 물러선다.
- 스냅샷·그 문자열·로그·오류 메시지·진단·스모크 결과 어디에도 들어가지
  않는다. `account usage secret …` 검사가 이를 증명한다.
- `ANTHROPIC_API_KEY`·`ANTHROPIC_BASE_URL`·Bedrock/Vertex/Foundry 등 사용자
  지정 인증 환경 변수가 있으면 자격 증명을 읽기 전에 조회를 포기한다.

## OS 때문에 달라진 것

| 항목 | macOS | Windows | 이유 |
| --- | --- | --- | --- |
| 로그인 값 출처 | 로그인 Keychain 항목, 없으면 `.credentials.json` | `.credentials.json` 하나 | Windows에는 Keychain이 없다 |
| 권한 대화상자 | Keychain 접근 허용 창이 뜰 수 있다 | 없다 | 승인 대상 저장소가 없으므로 `permission` 상태 자체가 없다 |
| 스위치 이름 | `Claude 한도를 Keychain으로 직접 조회` | `Claude 한도를 직접 조회` | 위와 같은 이유 |
| 스위치 설명 | `끄면 Keychain 승인창이 열리지 않습니다. Claude 실행 때 CLI가 보고하는 한도만 표시합니다.` | `끄면 앱이 Anthropic에 직접 조회하지 않습니다. Claude 실행 때 CLI가 보고하는 한도만 표시합니다.` | 위와 같은 이유 |
| 공유 한도 설명 | `… 자동 조회는 Keychain 승인창을 띄우지 않습니다.` | `… 자동 조회는 직접 조회를 켜기 전에는 일어나지 않습니다.` | 위와 같은 이유 |
| 날짜·시각 표기 | `Date.formatted` | `ko-KR` 짧은 형식(`g`·`t`) | 같은 뜻의 OS 표준 형식 |

`AccountUsageStrings`의 나머지 상수는 macOS 원문 그대로이고,
`account usage strings match macOS` 검사가 값 하나하나를 macOS 원문과 맞춘다.

## 읽기 규칙

- 실행기마다 한 번에 한 번만 읽는다. 진행 중이면 그 결과를 함께 기다린다.
- 60초 안에 읽은 값은 다시 읽지 않고, 강제 새로고침도 서버 냉각 시간을
  넘기지 못한다.
- 실패하면 마지막으로 성공한 창을 `stale`로 유지하고 문장 끝에
  ` 마지막으로 확인한 값입니다.`를 붙인다. 인증 실패·미지원처럼 계정이
  바뀌었을 수 있는 실패는 이전 계정의 숫자를 버린다.
- 읽기는 UI 스레드에서 돌지 않는다. 창을 닫으면 진행 중인 읽기를 취소하고
  캐시를 비운다.

## 검사

Mac에서 `dotnet run --project native/windows/MightyClaude.Core.Tests --artifacts-path /tmp/mc-artifacts`
로 도는 `account usage …` 검사 8개. 모두 HTTP 처리기·시계·임시 폴더의 가짜
자격 증명 파일을 주입하고, 실제 CLI를 실행하거나 네트워크에 나가거나 실제
사용자 폴더를 읽지 않는다.

스모크 키는 `accountUsage`. 실제 칩과 실제 팝오버를 픽스처 한도로 구동한 뒤
세션과 저장된 스위치를 원래대로 돌려놓는다.
