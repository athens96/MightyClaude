# Mighty Claude

<img src="assets/icons/mightyclaude.png" alt="Mighty Claude 너구리" width="128" />

**Claude Code · Codex CLI · Gemini CLI를 한 창에서 나란히 쓰는 데스크톱 앱입니다.**

[macOS 빌드](https://github.com/athens96/MightyClaude/actions/workflows/native-macos.yml) · [Windows 빌드](https://github.com/athens96/MightyClaude/actions/workflows/native-windows.yml) · [도움말](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/help/) · [다운로드](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/)

Mighty Claude는 프로젝트 폴더(워크스페이스)마다 여러 실행 창을 열고, 창마다 Claude·Codex·Gemini CLI를 직접 실행해 대화와 설정을 따로 유지합니다. 진행을 블록 다이어그램으로 보는 **마이티 보기**, 정해진 순서로 일을 진행하는 **스타일**(오로보로스·페이퍼신·슈퍼파워·클러드 플랜), 작업 상태를 알려 주는 **데스크톱 펫**, 휴대폰에서 Mac을 다루는 **휴대폰 원격**(화면 보기 포함)이 있습니다. macOS 앱과 Windows 앱(**베타**), Android 휴대폰 앱이 있고, 화면은 한국어·영어·중국어·일본어로 볼 수 있습니다.

현재 버전은 **0.4.14**입니다. 사용할 AI CLI는 따로 설치하고 로그인해야 합니다. 쓰지 않을 CLI는 설치하지 않아도 됩니다.

- **macOS:** Swift + SwiftUI/AppKit, macOS 14 이상
- **Windows (베타):** C# + .NET 10 + WinUI 3, x64·ARM64, Windows 10(버전 2004) 이상 또는 Windows 11
- **휴대폰:** React Native(Expo) Android 앱, 릴레이 서버를 거쳐 종단 간 암호화로 연결

## 다운로드와 설치

[다운로드 페이지](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/)에서 내 운영체제용 파일을 받습니다.

| 파일 | 설치 |
| --- | --- |
| `MightyClaude-macos.zip` | 풀고 `MightyClaude.app`을 응용 프로그램 폴더로 옮긴 뒤 엽니다. |
| Windows x64 / ARM64 zip (베타) | 폴더째 풀고 그 안의 `MightyClaude.exe`를 실행합니다. 함께 든 파일도 필요하므로 exe만 옮기지 마세요. 대부분의 PC는 x64, Snapdragon 같은 ARM PC는 ARM64입니다. |
| Android APK | 휴대폰에 받아 설치합니다. 연결 방법은 도움말의 [휴대폰 원격](docs/help/content/ko/phone.md)에 있습니다. |

AI CLI 설치 명령은 다음과 같습니다. 앱의 **설정 → 구성 요소**에서도 어떤 CLI가 준비됐는지 보고 명령을 복사할 수 있습니다.

| CLI | 설치 명령 |
| --- | --- |
| Claude Code | `npm install -g @anthropic-ai/claude-code` |
| Codex CLI | `npm install -g @openai/codex` |
| Gemini CLI | `npm install -g @google/gemini-cli` |

> **macOS:** 이 앱은 Apple 공증을 받지 않았습니다. 처음 열 때 막히면 Finder에서 앱을 Control-클릭한 뒤 **열기**를 고르거나, 시스템 설정 → 개인정보 보호 및 보안에서 열기를 허용하세요.
>
> **Windows:** 코드 서명이 없어 Windows(SmartScreen)가 처음 실행을 막을 수 있습니다. Visual C++ 런타임을 찾지 못한다고 하면 [Microsoft Visual C++ 재배포 가능 패키지(v14)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)를 설치하세요. 터미널과 브라우저 창에는 Microsoft Edge WebView2 런타임이 필요합니다. .NET 런타임은 zip에 들어 있습니다.

**업데이트:** 앱이 하루 한 번, 열 때 새 버전을 확인하고 상태 줄에 알립니다. **설정 → 앱 정보 → 앱 업데이트**에서 확인·내려받기를 하고 **설치하고 다시 실행**을 누르면 바뀝니다. 업데이트 파일은 서명을 확인한 뒤에만 설치합니다. AI CLI 업데이트는 따로 **설정 → CLI → CLI 업데이트**에서 합니다.

## 도움말

자세한 사용법은 [도움말 사이트](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/help/)에 있습니다(한국어·영어·중국어·일본어). 앱에서 고른 언어로 바로 열 수 있습니다.

- Mac: 메뉴 막대의 **Mighty Claude 도움말**(⌘?) 또는 **설정 → 앱 정보 → 도움말 열기**
- Windows: 사이드바 아래의 도움말 단추 또는 F1
- 휴대폰: **호스트** 탭의 **도움말** 단추

원문은 [`docs/help/content/ko/`](docs/help/content/ko/)이고, 다른 언어는 이 한국어 원문을 따릅니다.

## 주요 기능

각 항목의 자세한 내용은 링크한 도움말 원문에 있습니다.

**화면 구성** — [화면 구성](docs/help/content/ko/layout.md)
- 사이드바: **작업 현황**, 워크스페이스 검색(⌘K), 워크스페이스·실행 창 목록. 경계선을 끌어 폭을 바꾸고, 두 번 클릭하면 기본 폭, ⌃⌘S로 접고 펼칩니다.
- **작업 현황**: 모든 워크스페이스의 실행 창을 상태 순으로 모으고, 실행 중·응답 대기·완료 개수와 계정 사용 한도를 보여 줍니다.
- 실행 창은 에이전트(Claude·Codex·Gemini), 터미널, 브라우저, 파일 중 하나입니다. 탭으로 겹치거나 좌우·상하로 나누고, 배치는 워크스페이스마다 저장됩니다.
- Claude·Codex 창을 추가할 때 이 폴더의 이전 세션을 골라 이어갈 수 있습니다. Codex·Gemini 창에는 **베타** 표시가 붙습니다.

**에이전트 창** — [에이전트 창](docs/help/content/ko/agent-pane.md)
- 입력창 아래 줄에서 첨부, 모델, 사고 강도, 권한, 추가 실행 설정을 고릅니다. 첨부는 최대 8개, 파일당 5 MiB, 합계 8 MiB입니다.
- 실행 중에도 다음 요청을 쓸 수 있습니다. 중지 단추 옆의 둥근 단추로 **대기열에 추가**(Enter, 창당 16개까지)하거나, 이 Mac의 Claude 창에서는 **바로 전달**(⌘Enter)합니다.
- `/`를 치면 슬래시 명령 목록이 나오고, 목록에 없는 명령은 CLI의 스킬·플러그인 명령으로 그대로 전달됩니다.
- 보내기 단추 옆 고리에서 컨텍스트 사용량과 토큰·비용·세션 ID를 봅니다. CLI가 알려 준 값만 보여 줍니다.
- Claude·Codex 창은 **기본** | **마이티** 보기를 바꿀 수 있습니다. 마이티 보기는 요청·하위 에이전트·백그라운드 작업·**최종 결과**를 블록 다이어그램으로 보여 주고, **타임라인**으로 시간 순서 목록도 봅니다. 퍼즐 단추로 플러그인과 마켓플레이스를 엽니다.

**스타일과 계획** — [스타일](docs/help/content/ko/styles.md)
- 이 Mac의 Claude 창에서 마이티 보기일 때 입력창 위 메뉴로 스타일을 고릅니다. 기본 스타일은 오로보로스(Ouroboros), 페이퍼신(Paperthin), 슈퍼파워(Superpowers), 클러드 플랜입니다.
- 클러드 플랜은 새 요청마다 Claude의 계획 모드로 시작합니다. 다이어그램에서는 계획이 요청 아래 블록에 뜨고, 네 가지 답(**승인하고 실행 (자동 편집)**, **승인하고 실행 (매번 확인)**, **수정 요청**, **취소**)은 입력창 쪽에 나옵니다.
- 데스크톱 펫도 끝난 계획을 보여 주고, 펫에서 바로 승인하거나 **계획 검토…**로 계획 전체를 열 수 있습니다.
- 파일로 된 스타일은 **설정 → 스타일**에서 등록하고, 내용을 확인해 허용한 뒤에만 씁니다.

**승인·질문·로그인 복구** — [승인과 질문](docs/help/content/ko/approvals.md)
- 이 Mac의 Claude 창(Always ask 등)과 Codex의 **승인 요청** 권한에서는 권한 밖의 일을 승인 카드로 묻습니다(**이번만 허용** / **거부**). Claude의 질문은 **선택 요청** 카드로 답합니다.
- CLI 로그인이 만료되면 입력창 위의 복구 카드에서 **다시 로그인**하고, 끝나면 실패한 요청을 다시 보냅니다.
- 턴이 끝났지만 백그라운드 작업이 남아 있으면 입력창 위에 줄이 보입니다. 숨기거나 **설정 → 일반**에서 다시 켤 수 있습니다.

**터미널·파일·브라우저 창** — [터미널·파일·브라우저](docs/help/content/ko/terminal-files-browser.md)
- 터미널 창(⌘T)은 워크스페이스 폴더에서 열리는 실제 터미널입니다. 에이전트가 사용자가 봐야 할 명령을 실행하면 전용 에이전트 터미널이 생깁니다.
- 파일 창(⇧⌘E)은 읽기 전용으로 소스·Markdown·이미지·PDF를 미리 봅니다.
- 브라우저 창은 실험 기능이라 **설정 → 일반**에서 켜고 앱을 다시 열어야 합니다. Mac에서는 Apple Silicon에서만 동작합니다.

**설정** — [설정](docs/help/content/ko/settings.md)
- 일반(테마·언어·상태 줄·브라우저), 모델(페이즈별 모델), 스타일, 구성 요소(CLI·플러그인 상태, 내 작업 도구 모음), CLI(계정·업데이트), 모바일 리모트, 펫과 알림, 앱 정보(업데이트·데이터 위치) 탭이 있습니다.

**휴대폰 원격** — [휴대폰 원격](docs/help/content/ko/phone.md)
- Mac의 **설정 → 모바일 리모트**에서 연결을 허용하고 QR 코드로 휴대폰을 연결합니다. 공유기 포트나 VPN이 필요 없고, 릴레이는 내용을 볼 수 없습니다.
- 휴대폰 앱의 **현황**·**세션**·**알림**·**호스트** 탭에서 상태를 보고, 요청을 보내고, 승인·질문·계획에 답합니다. 푸시 알림은 보내지 않습니다.
- **화면 보기·조작**(베타, Android): Mac에서 휴대폰마다 허용 범위를 정하면 Mac 화면을 보거나 조작합니다. Mac에서 ⌃⌥⌘K를 누르면 모든 원격 화면 세션이 멈춥니다.
- 기본 릴레이 대신 `relay/`를 직접 배포해 쓸 수 있습니다.

**Windows에서 다른 점** — [Windows](docs/help/content/ko/windows.md)
- 같은 기능을 같은 이름으로 제공하며, 창 제목에 베타 표시가 붙습니다. ⌘ 대신 Ctrl을 쓰고, 사이드바 접기는 Ctrl+B입니다. 메뉴 막대는 없습니다.
- CLI 로그인은 별도의 Windows 터미널 창에서, 터미널 창은 Windows PowerShell로, 브라우저 창은 WebView2로 엽니다.

## 직접 빌드하기

```sh
git clone https://github.com/athens96/MightyClaude.git
cd MightyClaude
```

### macOS

macOS 14 이상과 Swift 6 이상의 도구(Xcode 또는 Command Line Tools)가 필요합니다. 첫 빌드에서 고정된 Swift 패키지(Ghostty 터미널, WebRTC 등)를 내려받습니다.

```sh
bash scripts/setup-codesign.sh          # 한 번만: 재빌드해도 macOS 권한 허용이 유지되는 로컬 서명 인증서
bash scripts/build-macos.sh             # release/native-macos/MightyClaude.app
bash scripts/test-native-macos.sh --filter "AppUpdateTests|AttachmentTests"
```

- 로컬에서는 테스트를 `--filter`로 골라 돌립니다. 전체 테스트는 CI에서 돌고, 로컬에서 꼭 전부 돌리려면 `MIGHTY_FULL_SUITE=1`을 붙입니다.
- `/Applications`에 설치하거나 갱신할 때는 `bash scripts/install-macos.sh`를 씁니다. 실행 중인 앱이 끝나기를 기다렸다가 백업 후 교체하고 다시 엽니다. 실행 중인 앱의 파일을 직접 덮어쓰면 macOS 입력기 연결이 끊겨 한글이 자모로 풀립니다.
- 내장 브라우저(Apple Silicon)를 넣으려면 엔진을 받은 뒤 옵션을 켜고 빌드합니다: `bash scripts/fetch-browser-engine.sh` → `MIGHTY_BROWSER_ENGINE=1 bash scripts/build-macos.sh`. 자세한 내용은 [브라우저 문서](docs/browser-pane.md)에 있습니다.

임시 프로필로 앱 시작과 셸 실행을 확인할 수 있습니다. 실제 AI 요청은 보내지 않습니다.

```sh
release/native-macos/MightyClaude.app/Contents/MacOS/MightyClaude \
  --smoke-test --smoke-exit --profile /tmp/mighty-native-smoke
```

### Windows

Windows 10 버전 2004(빌드 19041) 이상 또는 Windows 11, **.NET 10 SDK**, **PowerShell 7**, Windows SDK 빌드 도구(Visual Studio의 Windows 앱 개발 도구 또는 Build Tools)가 필요합니다. Windows App SDK 버전은 프로젝트에서 고정합니다.

```powershell
# C# 코어 검사 후 x64 / ARM64 앱 빌드
pwsh ./scripts/build-windows.ps1 -Architecture x64 -Configuration Release
pwsh ./scripts/build-windows.ps1 -Architecture arm64 -Configuration Release

# 격리된 프로필로 앱 실행 검사
pwsh ./scripts/test-native-windows.ps1 -Executable ./release/native-windows-x64/MightyClaude.exe
```

`build-windows.ps1`은 먼저 `MightyClaude.Core.Tests`를 돌린 뒤, self-contained 앱을 `release/native-windows-<아키텍처>/`에, 배포 zip과 SHA-256 파일을 `release/MightyClaude-windows-<아키텍처>.zip`에 만듭니다. 다른 출력 폴더는 `-OutputDirectory`로 지정합니다. 이 패키지는 코드 서명하지 않습니다.

Mac에서는 Windows 앱을 실행할 수 없지만, C# 코어 검사(`dotnet run --project native/windows/MightyClaude.Core.Tests --configuration Release`)와 WinUI 컴파일 검사(`bash scripts/check-winui-compile.sh`)는 돌릴 수 있습니다.

### 휴대폰 앱과 릴레이

```sh
cd mobile && npm install && npx expo start     # Expo 휴대폰 앱 (자세한 내용은 mobile/README.md)
cd relay && npm install && npm test            # 릴레이 서버 (자세한 내용은 relay/README.md)
```

연결 계약은 [docs/relay.md](docs/relay.md)와 [docs/mobile-remote.md](docs/mobile-remote.md)에 있습니다.

## 데이터와 소스 구조

앱의 설정과 기록은 이 컴퓨터에만 저장됩니다. 위치는 **설정 → 앱 정보**에서 보고 열 수 있습니다. 기본 위치는 macOS `~/Library/Application Support/MightyClaude Native/`, Windows `%APPDATA%\MightyClaudeNative\`입니다. 대화 기록 자체는 각 CLI의 세션으로 남습니다.

```text
native/macos/Sources/MightyClaude/       SwiftUI/AppKit 화면·앱 상태
native/macos/Sources/MightyCore/         Swift 실행기·저장·스타일·모바일 리모트
native/windows/MightyClaude.WinUI/       Windows WinUI 화면
native/windows/MightyClaude.Core/        C# 실행기·저장
native/windows/MightyClaude.Core.Tests/  C# 회귀 검사
native/contracts/                        Mac·Windows 클라이언트가 함께 따르는 JSON 계약과 테스트 벡터(휴대폰도 사용)
mobile/                                  휴대폰 앱 (React Native + Expo)
relay/                                   휴대폰 원격용 릴레이 서버
locales/                                 화면 문구 원본 (ko·en·zh·ja)
styles/                                  스타일 엔진의 골든·적합성 파일과 추가 스타일
mods/mighty-bridge/                      Claude Code function hooks
assets/                                  아이콘과 펫 스프라이트
docs/                                    설계·동작 문서, docs/help/ 도움말 원문
scripts/                                 빌드·설치·검사 스크립트
```

[JSON 계약](native/contracts/README.md) · [화면 문구와 번역](docs/i18n.md) · [디자인 시스템](docs/design-system/) · [도움말 원문 규칙](docs/help/README.md)

## 기여하는 분께

CI(GitHub Actions)는 빌드·테스트와 함께 다음 검사를 돌립니다. 바꾼 내용에 해당하는 검사는 올리기 전에 직접 돌려 주세요.

| 검사 | 하는 일 |
| --- | --- |
| `node scripts/check-locales.js --check` | ko·en 키 일치, 번역(zh·ja) 자리표시자, 클라이언트 사본 일치, 쓰지 않거나 없는 키, 하드코딩한 한국어 문구 개수(줄기만 함) |
| `node scripts/check-design-tokens.js --check` | 디자인 토큰을 거치지 않은 색상·간격 값 개수(줄기만 함) |
| `bash scripts/check-style-freeze.sh` | 고정된 스타일 엔진 파일이 태그 이후 바뀌지 않았는지 |
| `cd mods/mighty-bridge/tests && npm ci && npm run check` | Claude mod 타입 검사와 테스트 |

도움말 원문을 고쳤다면 `node scripts/help/build.mjs --check`로 페이지·언어 구조, UI 키, 내부 링크를 확인하세요. 화면 문구는 [docs/i18n.md](docs/i18n.md), 색상·간격 규칙은 [docs/design-system/](docs/design-system/)을 따릅니다.

## 라이선스

이 프로젝트는 MIT 라이선스입니다([LICENSE](LICENSE)). 릴레이 방식의 휴대폰 원격은 Apache License 2.0으로 배포되는 Paseo의 설계를 참고했으며, 고지는 [NOTICE.md](NOTICE.md)와 [licenses/](licenses/)에 있습니다. 앱에 포함한 타사 구성 요소의 라이선스는 [native/licenses/](native/licenses/)에, 아이콘 생성 안내는 [assets/icons/generation.md](assets/icons/generation.md)에 있습니다.
