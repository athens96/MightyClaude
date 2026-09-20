# MightyClaude

<img src="assets/icons/mightyclaude.png" alt="MightyClaude 슈퍼 너구리" width="128" />

**Claude Code · Codex CLI · Gemini CLI를 하나의 워크스페이스에서 사용하는 네이티브 데스크톱 ADE입니다.**

[macOS 빌드](https://github.com/athens96/MightyClaude/actions/workflows/native-macos.yml) · [Windows 빌드](https://github.com/athens96/MightyClaude/actions/workflows/native-windows.yml) · [검증 기록](docs/native-verification.md) · [원격 연결](docs/remote-workspaces.md)

- **macOS:** Swift + SwiftUI/AppKit
- **Windows:** C# + .NET 10 + WinUI 3, x64·ARM64
- 데스크톱 앱에 Electron·웹 렌더러·Node 서버를 포함하지 않습니다. 각 AI CLI를 직접 실행하고 구조화된 이벤트를 화면에 표시합니다.
- 워크스페이스마다 탭·분할 배치와 대화 기록을 유지합니다. Tailscale로 연결한 다른 컴퓨터에서도 작업을 실행하고 제어할 수 있습니다.

현재는 **0.1.0 개발 버전**입니다. 작업을 실행할 컴퓨터에 사용할 CLI를 별도로 설치하고 로그인하세요. CLI가 필요로 하는 Node.js 등의 런타임은 해당 CLI의 설치 안내를 따릅니다.

## 다운로드와 실행

[Native clients — macOS](https://github.com/athens96/MightyClaude/actions/workflows/native-macos.yml)·[Native clients — Windows](https://github.com/athens96/MightyClaude/actions/workflows/native-windows.yml) 빌드 목록에서 해당 운영체제·아키텍처 작업이 성공한 실행을 열고 **Artifacts**의 파일을 내려받습니다. 각 배포 파일은 해당 플랫폼의 빌드·실행 검사를 통과한 경우에만 올라갑니다. GitHub 로그인이 필요할 수 있습니다.

Windows 기본판: [x64 다운로드](https://github.com/athens96/MightyClaude/actions/runs/35166591891/artifacts/10474998648) · [ARM64 다운로드](https://github.com/athens96/MightyClaude/actions/runs/35166591891/artifacts/10474839262) — `d5dd549` 빌드, 두 아키텍처 모두 실행 검사 통과.

- **Windows:** x64 또는 ARM64 Artifacts ZIP을 받은 뒤, 그 안의 `MightyClaude-windows-<아키텍처>.zip`도 폴더째 압축 해제하고 `MightyClaude.exe`를 실행합니다. 함께 제공되는 DLL·Assets·Mods 파일도 필요하므로 exe만 따로 옮기지 마세요. .NET/Windows App SDK 런타임을 포함한 self-contained 패키지이며 SHA-256 파일을 함께 제공합니다.
- **macOS:** Artifacts 안의 `MightyClaude-macos.zip`을 압축 해제하고 `MightyClaude.app`을 실행합니다. CI 빌드는 실행한 Mac 호스트의 단일 CPU 아키텍처용이며 universal 빌드가 아닙니다. 현재 로컬 서명 빌드이며 배포용 Developer ID 서명·공증은 포함하지 않습니다.

빌드 산출물은 Actions 보관 기간의 영향을 받습니다. 아래 명령으로 동일한 소스에서 직접 빌드할 수도 있습니다.

Windows에서 Visual C++ 런타임을 찾지 못하면 앱 아키텍처에 맞는 [Microsoft Visual C++ v14 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)을 설치하세요. unpackaged WinUI 앱의 [런타임 요구사항](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deployment-architecture#additional-requirements)입니다.

## 직접 빌드하기

```sh
git clone https://github.com/athens96/MightyClaude.git
cd MightyClaude
```

### Windows

Windows 10 버전 2004(빌드 19041) 이상 또는 Windows 11, **.NET 10 SDK**, **PowerShell 7**, Windows SDK 빌드 도구가 필요합니다. Visual Studio의 Windows 앱 개발 도구 또는 해당 Build Tools를 사용하세요. Windows App SDK NuGet 버전은 프로젝트에서 고정합니다.

PowerShell에서 실행합니다.

```powershell
# C# 코어·실행기·원격 연결 검증
dotnet run --project native/windows/MightyClaude.Core.Tests --configuration Release

# x64 앱 빌드
pwsh ./scripts/build-windows.ps1 -Architecture x64 -Configuration Release

# ARM64 앱 빌드
pwsh ./scripts/build-windows.ps1 -Architecture arm64 -Configuration Release
```

출력 폴더는 `release/native-windows-x64/` 또는 `release/native-windows-arm64/`입니다. 배포 ZIP과 SHA-256 파일도 `release/MightyClaude-windows-<아키텍처>.zip`에 생성합니다. 재빌드는 부모 경로까지 새로운 `-OutputDirectory`를 지정하거나, 기존 출력 폴더와 ZIP·SHA-256 파일을 별도로 보관·제거한 뒤 실행하세요.

WinUI 전체 패키징에는 Windows의 `mt.exe`·`MakePri.exe`가 필요합니다. Mac에서 C# 코어 검사를 실행할 수는 있지만 Windows 앱을 직접 실행할 수는 없습니다. 현재 Windows 개발 패키지는 코드 서명하지 않습니다.

### macOS

macOS 14 이상과 Swift 6 이상 도구가 필요합니다. Xcode Command Line Tools를 설치한 뒤 실행합니다.

```sh
bash scripts/test-native-macos.sh
bash scripts/build-macos.sh
open release/native-macos/MightyClaude.app
```

입력창에 `/`를 치면 그 실행기의 스킬·사용자 명령·플러그인 명령이 목록으로 나타나고 ↑↓·Enter·Tab으로 고를 수 있다([docs/slash-commands.md](docs/slash-commands.md)). 실행 중인 창에도 계속 입력할 수 있다. 이 Mac의 Claude 창은 보낸 글을 진행 중인 턴에 바로 전달하고 마이티 모드에 **중간 요청** 블록으로 표시하며, Codex·Gemini·원격·셸 창은 현재 요청이 끝난 뒤 순서대로 실행하는 대기열에 넣는다. 자세한 동작은 [docs/mighty-mode.md](docs/mighty-mode.md)를 참고한다.

설정의 **구성 요소**에서 에이전트 CLI 상태와 필요한 플러그인을 한 번에 확인하고 처리한다([docs/components.md](docs/components.md)).

휴대폰(iOS·Android)에서는 `mobile/`의 Expo 앱으로 이 Mac의 MightyClaude에 접속해 워크스페이스·실행 창을 보고 요청·중지·권한 답변을 보낼 수 있다. Mac과 휴대폰이 `relay/`의 릴레이 서버를 통해 종단 간 암호화로 연결되므로 포트 개방이나 VPN이 필요 없다. 설정의 **모바일 리모트**에 릴레이 주소를 넣고 QR로 페어링한다. 자세한 내용은 [docs/mobile-remote.md](docs/mobile-remote.md)와 [docs/relay.md](docs/relay.md).

`/Applications`에 설치한 앱을 갱신할 때는 `bash scripts/install-macos.sh`를 사용하세요. 실행 중인 앱이 종료될 때까지 기다렸다가 백업 후 교체하고 다시 실행합니다. 실행 중인 앱의 번들을 디스크에서 바꾸면 macOS 입력기 연결이 끊겨 한글 조합이 자소 단위로 풀립니다.

첫 빌드에서 고정된 Ghostty Swift 패키지와 체크섬으로 검증하는 네이티브 라이브러리를 내려받습니다. Ghostty 앱을 따로 설치할 필요는 없습니다.

임시 프로필로 앱 시작·셸 실행을 검증할 수 있습니다. 실제 AI 요청은 보내지 않습니다.

```sh
release/native-macos/MightyClaude.app/Contents/MacOS/MightyClaude \
  --smoke-test --smoke-exit --profile /tmp/mighty-native-smoke
```

## 사용 방법

1. **폴더 추가**로 로컬 워크스페이스를 엽니다.
2. 워크스페이스의 **+**에서 AI 실행 창 또는 명령 창을 추가합니다. 새 창의 이름은 실행기 이름 그대로(Claude·Codex·Gemini·터미널)이며 필요하면 이름을 바꿉니다. 새 AI 실행 창은 같은 실행기의 가장 최근에 사용한 창에서 모델·사고 강도·권한·보기 모드를 이어받습니다(대화 기록과 이어가기 ID는 새로 시작).
3. 입력창 아래에서 프로바이더·모델·사고 강도·권한을 선택하고 요청을 입력합니다.
4. **Enter**로 전송하고 **Shift+Enter**로 줄을 바꿉니다. 한글 등 입력기 조합 중인 Enter는 전송하지 않습니다.
5. 작업 중에는 같은 실행 버튼이 **중지** 버튼으로 바뀝니다. 다음 요청의 초안과 첨부파일을 미리 준비할 수 있습니다.

입력란은 한 줄에서 시작하고 내용에 맞춰 늘어납니다. 긴 입력은 높이 제한 안에서 스크롤합니다. CLI가 준비되지 않거나 원격 연결이 끊겨도 초안은 편집할 수 있으며, 보내기가 불가능한 이유를 입력창에서 확인할 수 있습니다.

탭을 다른 그룹으로 드래그하면 합치거나 좌우·상하로 분할할 수 있습니다. 워크스페이스를 바꿔도 각 배치를 유지합니다. [탭·분할 사용법](docs/pane-layout.md)

### 프로바이더와 실행 설정

| 실행기 | 모델·사고 강도 | 실행 설정 |
| --- | --- | --- |
| Claude Code | 설치 CLI의 모델 메타데이터 우선, 조회 실패 시 별칭 안내 | Plan mode · Always ask · Accept file edits · Auto mode · Bypass, 최대 턴·요청별 비용 한도 |
| Codex CLI | `model/list`와 모델별 지원 강도 | 읽기 전용·프로젝트 폴더 수정·전체 접근, Fast, 웹 검색·프로젝트 네트워크 |
| Gemini CLI | CLI 설정·Auto·모델 이름 | 기본·계획·편집 허용·전체 접근 |

실행기가 지원하는 옵션만 선택할 수 있습니다. Fast는 사고 강도와 별개이며 모델·계정의 지원 범위에 따릅니다. Auto mode는 지원하는 Claude CLI에서만 표시합니다. 기존 권한 설정을 임의로 전체 접근으로 바꾸지 않습니다.

Claude Mods는 `mods/mighty-bridge`의 function hooks를 Claude Code 안에 로드합니다. 호환 기준은 Claude Code **2.1.271 이상**입니다. [Mods 분석](docs/claude-mods-analysis.md) · [실행 설정 계약](native/contracts/README.md#composer-settings)

### 선택 요청

Mac의 로컬 Claude가 `AskUserQuestion`을 요청하면 JSON 대신 질문 카드가 표시됩니다. 질문 제목·선택지 설명을 읽고 단일 또는 복수 선택을 하거나 **직접 입력**으로 답할 수 있습니다. 모든 질문에 답한 뒤 **답변 보내기**를 눌러 작업을 이어갑니다. 선택만으로 답이 전송되지는 않습니다.

대화 기록에 남은 같은 형식의 JSON도 질문·선택지 목록으로 표시합니다. 실행이 끝났거나 취소된 질문은 다시 제출할 수 없습니다. Windows와 원격 세션의 대화형 질문 전달은 아직 지원하지 않습니다.

### 파일과 이미지

첨부 버튼·파일 드롭·이미지 붙여넣기를 지원합니다. 첨부만 보내는 것도 가능하며, 전송 전에 미리보기·개별 삭제를 할 수 있습니다.

- 최대 8개, 파일당 5 MiB, 합계 8 MiB
- 이미지는 CLI의 이미지 입력으로, 일반 파일은 실행 중 읽을 수 있는 임시 사본으로 전달
- 원격 실행 시 파일 내용도 실행하는 컴퓨터에 전송
- 임시 사본은 실행 종료 시 삭제, 미전송 첨부는 앱 종료 시 삭제
- 명령 창에서는 첨부 미지원

### 플랫폼별 기능

| 기능 | macOS | Windows |
| --- | --- | --- |
| Claude·Codex·Gemini 실행, 설정·재개·첨부 | 지원 | 지원 |
| 워크스페이스별 탭·분할·이름 변경 | 지원 | 지원 |
| Markdown·도구 활동·경과 시간 | 지원 | 지원 |
| 세션 컨텍스트·사용량 상세 | 지원 | 지원 |
| Tailscale 원격 실행·중지 | 지원 | 지원 |
| 로컬 터미널 | Ghostty + PTY | 요청별 명령 실행 |
| Claude 추가 권한 요청의 앱 내 승인 (실행 창·펫 말풍선) | 로컬 세션 지원 | 미지원 |
| 마이티 그래프·플러그인 마켓플레이스 (Claude·Codex) | 지원 | 미지원 |
| CLI 자동 업데이트 설정 | 지원 | 미지원 |
| 데스크톱 펫·완료 알림 | 지원 | 미지원 |

Windows와 Mac의 구현 범위를 구분한 표입니다. Windows 명령 창은 대화형 PTY 터미널이 아닙니다. 원격 실행과 Codex·Gemini에는 앱 내 추가 권한 승인 채널이 없습니다.

Mac 마이티 모드는 Claude와 Codex 실행 창에서 요청·메인 에이전트·서브에이전트·결과를 그래프로 표시합니다. 빈 공간을 드래그하거나 스크롤해 다이어그램 안에서 이동하고, 블록을 클릭하면 해당 내용만 스크롤합니다. 오른쪽 아래 모서리를 드래그하면 블록 크기를 조절하고 세션별로 저장합니다. 퍼즐 아이콘에서 Claude·Codex의 설치된 플러그인을 확인하고 CLI가 제공하는 마켓플레이스에서 설치할 수 있습니다. Codex 플러그인은 사용자 범위에 설치되며 새 Codex 세션부터 사용합니다. [마이티 모드](docs/mighty-mode.md) · [플러그인 관리](docs/claude-plugins.md)

입력창에는 CLI가 보고한 세션 컨텍스트를 표시하며, 확인할 수 없는 값은 추정해 채우지 않습니다. 컨텍스트 버튼을 누르면 해당 대화의 토큰·비용 등 사용량 상세를 확인할 수 있습니다. [세션·사용량](docs/session-usage.md)

슈퍼 너구리 펫은 요청·현재 작업·경과 시간을 보여주고, 드래그 방향에 따라 걷거나 현재 작업에 맞는 동작을 합니다. 펫 클릭으로 말풍선을 토글하고, 말풍선을 누르면 해당 에이전트로 이동합니다. 완료 말풍선은 6초 후 숨깁니다. [펫과 알림](docs/agent-companion.md)

## Tailscale 원격 연결

1. 두 컴퓨터에 Tailscale을 설치하고 연결합니다.
2. 실행할 컴퓨터에서 Mac은 **설정 → Tailscale 원격 연결**, Windows는 사이드바의 **원격 연결**을 열고 공유할 로컬 워크스페이스를 선택합니다.
3. 제어할 컴퓨터에서 호스트 주소와 연결 키를 입력합니다.
4. 원격 워크스페이스를 가져와 실행·중지합니다. 모델 목록과 CLI 로그인은 원격 컴퓨터의 것을 사용합니다.

공유는 앱을 시작할 때 꺼진 상태입니다. 연결 키는 macOS Keychain·Windows DPAPI로 보호하며 워크스페이스 기록에 넣지 않습니다. [연결 방법과 범위](docs/remote-workspaces.md)

## 데이터와 소스 구조

네이티브 앱은 별도 프로필을 사용합니다. 첫 실행 시 기존 Electron 워크스페이스 기록을 복사해 가져오고 기존 프로필은 변경하지 않습니다. 이전 원격 연결 키는 다시 입력해야 합니다.

```text
assets/icons/                      기본 아이콘 PNG·ICNS·ICO
assets/pets/                       Codex 호환 펫 스프라이트
native/macos/Sources/MightyClaude/   SwiftUI/AppKit 화면·앱 상태
native/macos/Sources/MightyCore/     Swift 실행기·저장·Mods·원격 연결
native/windows/MightyClaude.WinUI/  Windows WinUI 화면
native/windows/MightyClaude.Core/   C# 실행기·저장·Mods·원격 연결
native/windows/MightyClaude.Core.Tests/  C# 회귀 검사
native/contracts/                  JSON·원격 프로토콜 계약
mods/mighty-bridge/                 Claude function hooks
scripts/                           플랫폼별 빌드·검증
```

`electron/`, `src/`, `shared/`, `package.json`은 동작 비교와 회귀 검증용 참조 구현입니다. 네이티브 앱 빌드에는 `npm install`이 필요하지 않습니다. [참조 구현 안내](docs/electron-reference.md)

이 프로젝트는 MIT 라이선스입니다(`LICENSE`). 릴레이 방식의 모바일 리모트는 Apache License 2.0으로 배포되는 Paseo의 설계를 참고했으며, 고지는 `NOTICE.md`와 `licenses/Apache-2.0.txt`에 있습니다. 기본 아이콘·펫의 생성 안내와 타사 라이선스는 `assets/`와 `native/licenses/`에 있습니다. 아이콘을 교체한 뒤 Mac에서 `bash scripts/package-icons.sh`를 실행하면 ICNS·ICO를 다시 생성합니다.

배포용 코드 서명·Mac 공증·앱 자체 자동 업데이트는 별도입니다. CLI 업데이트 기능과 앱 업데이트는 다릅니다. 실제 수행한 검사와 아직 검증하지 못한 범위는 [검증 기록](docs/native-verification.md)에서 확인할 수 있습니다.
