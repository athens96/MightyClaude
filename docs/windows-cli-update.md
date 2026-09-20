# Windows CLI 업데이트

macOS `CLIUpdateService.swift`·`CLIUpdateSettingsView.swift`를 Windows로 옮긴 것.
설치된 CLI만, 그 CLI의 설치 방식으로 업데이트한다. 새로 설치하지 않고,
권한 상승을 요구하지 않고, 설정 파일을 쓰지 않고, 무관한 패키지를 건드리지 않는다.

구현: `native/windows/MightyClaude.Core/CliUpdateService.cs`,
문구: `native/windows/MightyClaude.Core/CliUpdateStrings.cs`,
검사: `cli update …`로 시작하는 Core.Tests 검사 10개 (Mac에서 통과).

## 설치 방식 인식 규칙 (OS-bound, 각 규칙에 이유 한 줄)

| 방식 | 인식 규칙 | 업데이트 호출 | 이유 |
|---|---|---|---|
| `native` | 실행 파일(심볼릭 링크는 대상까지 해석)의 상위 또는 그 상위 폴더가 `%USERPROFILE%\.local\share\claude\versions` (또는 `XDG_DATA_HOME\claude\versions`) | `<claude> update` | macOS와 같은 설치 경로를 쓰지만 Windows 네이티브 설치는 버전마다 폴더를 하나 더 두므로 상위 두 단계까지 인정한다. |
| `winget` | 경로에 `…\WinGet\Packages\<PackageIdentifier>_<해시>\…`가 있고 `<PackageIdentifier>`가 winget 식별자 형식 | `winget upgrade --id <PackageIdentifier> --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity` | winget이 설치 위치 폴더 이름을 패키지 식별자로 짓기 때문에, 식별자를 추측하지 않고 이미 설치된 그 패키지에서 읽어 정확히 하나만 올린다. |
| `npm` | 실행 파일이 있는 폴더가 전역 prefix이고 `<prefix>\node_modules\<공식 패키지>\package.json`의 `name`이 공식 이름이며 `bin`에 이 CLI가 있음 (`<prefix>\bin` 형태면 `<prefix>\lib\node_modules`도 본다) | `<node> <prefix>\node_modules\npm\bin\npm-cli.js install --global --prefix <prefix> <패키지>@latest --no-audit --no-fund` | Windows의 npm 전역 설치는 shim(`.cmd`)을 prefix 바로 아래에 두므로 실행 파일의 폴더가 곧 prefix다. npm은 `.cmd` shim이 아니라 Node + npm 자체 스크립트로 실행해 셸 명령줄을 만들지 않는다. |
| `unknown` | 위 어느 것도 아님 | 없음 (건너뜀) | 설치 방식을 모르면 손대지 않는다. 수동 설치를 자동으로 바꾸지 않는다. |
| `missing` | PATH에서 실행 파일을 찾지 못함 | 없음 (건너뜀) | 설치되지 않은 CLI는 새로 설치하지 않는다. |

공식 npm 패키지 이름: `claude` → `@anthropic-ai/claude-code`,
`codex` → `@openai/codex`, `gemini` → `@google/gemini-cli` (macOS와 동일).

PATH 탐색은 앞에서 64개 항목까지, 항목마다 `<이름>.cmd` → `.exe` → `.bat` → 확장자 없음
순서로 본다. 이유: Windows는 확장자로 실행 가능 여부가 정해지고, npm shim은 `.cmd`다.

## 절대 하지 않는 것

- 권한 상승: `runas` 동사나 관리자 권한 요청을 쓰지 않는다. winget 호출에도
  `--disable-interactivity`만 있고 UAC를 부르는 인자는 없다. 이유: 이미 사용자
  범위에 설치된 패키지만 올리므로 상승이 필요 없다.
- 새 설치: `winget install`·`npm install` 신규 설치 경로를 만들지 않는다.
  `npm install --global --prefix <기존 prefix>`는 이미 그 prefix에 있는 공식
  패키지 하나만 지정한다.
- 무관한 패키지: `winget upgrade --all`을 쓰지 않는다. 항상 `--id <식별자> --exact`다.
- 셸 명령줄 조립: 모든 실행은 `ICliRunner.RunAsync(실행 파일, 인자 목록, …)`으로,
  텍스트를 이어 붙인 명령줄을 만들지 않는다.
- 설정 파일 쓰기: 업데이트 경로에서 어떤 구성 파일도 쓰지 않는다.
- 시험판 채널 변경: npm 패키지 버전이 `\A[0-9]+\.[0-9]+\.[0-9]+(\+…)?\z`가 아니면
  `skipped` + macOS 문장
  (`시험판 또는 확인할 수 없는 npm 채널은 자동 변경하지 않습니다. 기존 채널에서 직접 업데이트하세요.`).

## 상태와 문구

상태는 macOS와 같다: `updated` / `current` / `skipped` / `failed` / `cancelled` / `busy`.
표시 문구는 `CliUpdateStrings`에만 있고 WinUI는 새 문구를 적지 않는다.
행은 `{provider} · {status}`, 버전 줄은 `{before} → {after}`.

허용된 OS 이름 대체는 하나뿐이며 여기 기록한다:
`CliUpdateStrings.SectionDescription`은 macOS의
`이 Mac에 설치된 …`을 `이 PC에 설치된 …`으로 바꾼다.

Homebrew가 없는 자리에 들어간 winget 문장 두 개
(`DetailWingetPlan`·`DetailWingetRuntimeMissing`)는 macOS 원문이 없으므로
`docs/windows-parity.md`에 보류 행으로 남겼다.

## 동시 실행·취소·종료

- 한 번에 하나만 실행한다. 실행 중 두 번째 요청은 `busy`
  (`다른 CLI를 업데이트하고 있습니다.`).
- 취소는 실행 중인 설치 프로그램을 프로세스 그룹째 종료한다
  (`CliRunner`가 Windows Job Object로 끊는다). 결과는 `cancelled`.
- 앱 종료는 `ShutdownAsync()`로 새 요청을 막고 실행 중인 것을 취소한다.
  이후 요청은 `cancelled` + `앱이 종료 중입니다.`

## 시작 시 자동 업데이트

`AppSnapshot.AutoUpdateCLIs`가 JSON `true`일 때만 켜진다
(`CliUpdateService.ShouldRunAtStartup`). 기본값은 꺼짐이고, 없는 키나 잘못된
값은 꺼짐으로 읽힌다(`settings preferences …` 검사). 시작 검사는 창을 막지 않는
백그라운드 작업이며, 실패는 섹션 안에 상태로 표시되고 차단 대화상자로 뜨지 않는다.

## 진단 출력

러너가 1 MiB까지만 담고, 그중 마지막 8192자만 결과에 남는다. 제어 문자는
줄바꿈·탭만 남기고 제거한다. 사용자가 자세히 보기를 눌렀을 때만 표시하며
저장 상태에는 쓰지 않는다.

## 테스트가 실제 프로세스를 쓰지 않는 방법

`CliUpdateService`는 `ICliRunner`만으로 프로세스를 실행한다. 검사는 가짜 러너와
임시 폴더 픽스처(빈 파일·`package.json`·심볼릭 링크)로 인식 규칙과 호출 인자를
그대로 확인하므로, claude·codex·gemini·npm·winget이 실행되지 않고 네트워크도
쓰지 않는다.

## 기기 미확인 항목

- 실제 winget 설치본을 실제 `winget upgrade --id`로 올렸을 때의 종료 코드와 출력
- 실제 npm 전역 설치본을 Node + `npm-cli.js`로 올렸을 때의 동작
- 네이티브 Claude Code 설치의 `claude update` 결과
- 실행 중 취소가 실제 설치 프로그램의 자식 프로세스까지 끊는지 (기기 확인 필요)
