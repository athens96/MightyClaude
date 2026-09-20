# Windows CLI 계정

macOS `CLIAccounts.swift`·`CLIAccountsSettingsView.swift`를 Windows로 옮긴 것.
앱은 각 CLI에게 계정 레이블을 묻거나 계정 파일에서 읽을 뿐이며, 토큰을 저장하거나
표시하거나 기록하거나 전달하지 않는다.

구현: `native/windows/MightyClaude.Core/CliAccounts.cs`,
문구: `native/windows/MightyClaude.Core/CliAccountStrings.cs`,
검사: `cli account …`로 시작하는 Core.Tests 검사 6개 (Mac에서 통과).

## 상태 출처 (per-provider)

| 실행기 | 상태 출처 | 제한 |
|---|---|---|
| `claude` | `claude auth status --json` | 20초 타임아웃 |
| `codex` | `codex login status` 텍스트 + `$CODEX_HOME/auth.json`의 id 토큰 클레임에서 계정·플랜 추출 | 20초 타임아웃; 파일 1 MiB 크기 상한 |
| `gemini` | `~/.gemini/settings.json` + `oauth_creds.json` 존재 여부 + `google_accounts.json`에서 활성 계정 | 파일별 1 MiB 크기 상한; 상태 명령 없음 |

알 수 없는 응답은 로그아웃이 아니라 `상태를 확인하지 못했습니다.`(unknown)로 표시한다.

## 로그아웃

| 실행기 | 로그아웃 방법 |
|---|---|
| `claude` | `claude auth logout` |
| `codex` | `codex logout` |
| `gemini` | `oauth_creds.json` 삭제 + `google_accounts.json`에서 활성 계정을 old 목록으로 이동 (Gemini의 /auth 화면과 동일) |

`canSignOut`이 true일 때만 버튼을 보여주고, 실제 로그아웃은 확인 대화상자를 통해서만 실행한다.
API 키(환경 변수) 또는 Vertex AI로 로그인된 경우 `canSignOut = false`이며 로그아웃을 제공하지 않는다.

## 로그인·계정 변경 — 외부 터미널 규칙 (OS-bound)

로그인과 계정 변경은 외부 Windows 터미널 창을 열어 CLI 자체의 로그인 명령을 실행한다.

**사용 규칙**: `ProcessStartInfo(FileName = command[0], Arguments = ..., UseShellExecute = true)`로
실행하고, `FileName`에는 고정된 CLI 이름(claude·codex·gemini)만 넣는다. 신뢰할 수 없는
문자열로 명령줄을 조립하지 않는다.

**이유**: macOS는 앱 내 터미널 실행 패널을 쓰지만 Windows의 인터랙티브 PTY 터미널은
이후 단계(인터랙티브 터미널 기능) 항목이므로, 현 단계에서는 셸이 이미 열려 있는 외부
터미널 창에 CLI를 실행하는 것이 가장 간결하고 안전하다.

로그인 명령:
- `claude auth login` / `claude auth login --console`
- `codex login`
- `gemini`

터미널이 열려 있는 동안 섹션에는 대기 안내와 **대기 취소** 버튼을 표시하며,
사용자가 돌아오면 상태를 다시 확인한다.

## OS-bound 문구 대체

macOS와의 문구 대응표. **확인 필요** 행은 운영 체제 차이로 달라진 문구를 표시한다.

| 항목 | macOS 원문 | Windows 문구 | 비고 |
|---|---|---|---|
| `SectionDescription` | `로그인은 터미널 실행 창에서 진행됩니다. 앱이 명령을 실행해 두면 CLI가 브라우저를 엽니다. 다른 계정으로 바꿀 때는 브라우저에서 원하는 계정을 고르세요. 바꾼 계정은 다음 요청부터 적용됩니다.` | `로그인은 외부 터미널 창에서 진행됩니다. 앱이 명령을 실행해 두면 CLI가 브라우저를 엽니다. 다른 계정으로 바꿀 때는 브라우저에서 원하는 계정을 고르세요. 바꾼 계정은 다음 요청부터 적용됩니다.` | 확인 필요 — `터미널 실행 창` → `외부 터미널 창`; 앱 내 터미널이 아닌 외부 창을 사용하기 때문 |

그 외 모든 문구는 macOS와 동일하며 `CliAccountStrings`에 집중 관리한다.
WinUI는 한국어 문자를 직접 쓰지 않는다.

## 토큰 보이지 않음

- id 토큰은 메모리에서 클레임만 추출(`email`, `chatgpt_plan_type`)하고 즉시 버린다.
- `access_token`이나 원본 토큰 문자열은 어떤 필드에도 저장하지 않는다.
- 자격 증명 저장소를 사용하지 않는다.
- 오류·진단 출력에 파일 내용을 포함하지 않는다.
- 테스트는 모두 가짜 러너·임시 홈 폴더·픽스처 파일을 쓰며, 실제 프로파일을 읽지 않는다.

## 기기 미확인 항목

- 실제 `claude auth login` 브라우저 흐름이 외부 터미널 창에서 완료되는지
- 실제 Gemini OAuth 로그아웃이 앱의 파일 변경과 일치하는지
- Windows 터미널 선택 및 시작 동작 (인터랙티브 터미널 기능이 구현된 후 재검토)
