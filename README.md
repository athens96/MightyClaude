# Mighty Code

<img src="assets/icons/mightycode.png" alt="Mighty Code 너구리" width="128" />

**Claude Code · Codex · Antigravity · pi · opencode · Hermes를 한 창에서 나란히 쓰는 데스크톱 앱입니다.**

[macOS 빌드](https://github.com/athens96/MightyCode/actions/workflows/native-macos.yml) · [Windows 빌드](https://github.com/athens96/MightyCode/actions/workflows/native-windows.yml) · [도움말](https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/help/) · [다운로드](https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/)

Mighty Code는 프로젝트 폴더(워크스페이스)마다 여러 실행 창을 열고, 창마다 에이전트 CLI를 직접 실행해 대화와 설정을 따로 유지합니다. 요청·하위 에이전트·백그라운드 작업을 블록 흐름으로 보여 주는 **마이티 보기**, 정해진 순서로 일을 진행하는 **스타일**(오로보로스·클러드 플랜), 일을 자기 git worktree를 가진 자식 창에 나눠 맡기는 **마이티 오케스트레이션**, 작업 상태를 알려 주는 **데스크톱 펫**, 다른 컴퓨터의 워크스페이스를 여는 **원격 연결(Tailscale)**, 휴대폰에서 상태를 보고 답하는 **휴대폰 원격**이 있습니다. macOS 앱과 Windows 앱(**베타**), Android 휴대폰 앱이 있고, 화면은 한국어·영어·중국어·일본어로 볼 수 있습니다.

사용할 AI CLI는 따로 설치하고 로그인해야 합니다. 쓰지 않을 CLI는 설치하지 않아도 됩니다. 배포 중인 버전은 [다운로드 페이지](https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/)에, 이 저장소의 버전은 [`VERSION`](VERSION)에 있습니다.

- **macOS:** Swift + SwiftUI/AppKit, macOS 14 이상, Apple Silicon
- **Windows (베타):** C# + .NET 10 + WinUI 3 호스트에 WebView2로 띄운 React 화면(`native/windows/web`), x64·ARM64, Windows 10(버전 2004) 이상 또는 Windows 11
- **휴대폰:** React Native(Expo) Android 앱, 릴레이 서버를 거쳐 종단 간 암호화로 연결(같은 Tailscale 네트워크면 같은 암호화로 컴퓨터에 직접)

## 다운로드와 설치

[다운로드 페이지](https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/)에서 내 운영체제용 파일을 받습니다.

| 파일 | 설치 |
| --- | --- |
| `MightyCode-macos.dmg` | 열고 Mighty Code를 응용 프로그램 폴더로 끌어다 놓습니다. Apple 공증을 받아 처음 열 때도 보안 경고가 없습니다. Apple Silicon Mac용입니다. |
| `MightyCode-macos.zip` | 같은 앱의 zip입니다. 풀고 `MightyCode.app`을 응용 프로그램 폴더로 옮깁니다. |
| `MightyCode-windows-x64-setup.exe` / `-arm64-setup.exe` (베타) | 실행해서 설치합니다. 관리자 권한 없이 이 사용자에게만 `%LOCALAPPDATA%\Programs\MightyCode`에 설치되고, 제거는 Windows 설정 → 앱에서 합니다. 대부분의 PC는 x64, Snapdragon 같은 ARM PC는 ARM64입니다. |
| Windows x64 / ARM64 zip (베타) | 설치 없이 쓸 때: 폴더째 풀고 그 안의 `MightyCode.exe`를 실행합니다. 함께 든 파일도 필요하므로 exe만 옮기지 마세요. |
| Android APK | 휴대폰에 받아 설치합니다. 0.4.54부터 새 앱(Mighty Code)이라, 그 전의 Android 앱이 있으면 지우고 설치한 뒤 다시 연결합니다. 연결 방법은 도움말의 [휴대폰 원격](docs/help/content/ko/phone.md)에 있습니다. |

AI CLI는 앱을 처음 열 때 나오는 **필요한 도구** 화면에서 골라 설치할 수 있습니다(Mac은 터미널 창, Windows는 PowerShell 창에서 실행하고, 실행 전에 명령을 모두 보여 줍니다). 나중에는 **설정 → 구성 요소**에서 준비 상태를 보고, 로그인은 **설정 → CLI**에서 합니다. 직접 설치하려면:

| CLI | Mac | Windows |
| --- | --- | --- |
| Claude Code | `curl -fsSL https://claude.ai/install.sh \| bash` | `npm install -g @anthropic-ai/claude-code` |
| Codex CLI | `curl -fsSL https://chatgpt.com/codex/install.sh \| sh` | `npm install -g @openai/codex` |
| Antigravity CLI (`agy`) | `curl -fsSL https://antigravity.google/cli/install.sh \| bash` | `irm https://antigravity.google/cli/install.ps1 \| iex` |
| pi | `npm install -g @mariozechner/pi-coding-agent` | 같음 |
| opencode (OpenCode 2) | `npm install -g @opencode/cli` | 같음 |
| Hermes | `curl -fsSL https://hermes-agent.nousresearch.com/install.sh \| bash` | `irm https://hermes-agent.nousresearch.com/install.ps1 \| iex` |

pi와 opencode는 Node.js가, Mac의 Hermes는 Git이 필요합니다.

> **Windows:** 코드 서명이 없어 Windows(SmartScreen)가 처음 실행을 막을 수 있습니다. 그러면 **추가 정보** → **실행**을 누르세요. Visual C++ 런타임을 찾지 못한다고 하면 [Microsoft Visual C++ 재배포 가능 패키지(v14)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)를 설치하세요. 앱 화면과 터미널·브라우저 창은 Microsoft Edge WebView2 런타임으로 그립니다(없으면 이전 WinUI 화면으로 열리고 런타임이 없다고 알려 줍니다). .NET 런타임은 설치 프로그램과 zip에 들어 있습니다.

**업데이트:** **새 버전을 자동으로 받아 설치**가 켜져 있으면(처음부터 켜져 있습니다) 앱이 열 때와 6시간마다 새 버전을 확인하고, 받아서 서명을 확인한 뒤 준비해 둡니다. 준비된 업데이트는 앱을 종료할 때 설치되고, 앱이 한동안 쉬고 있으면 1분 알림 뒤 다시 시작하며 설치합니다(열려 있던 워크스페이스와 창은 그대로 돌아옵니다). 직접 하려면 **설정 → 앱 정보 → 앱 업데이트**에서 확인하고 **지금 다시 시작**을 누릅니다. 휴대폰 앱에서도 연결된 컴퓨터의 앱을 업데이트할 수 있습니다. AI CLI 업데이트는 따로 **설정 → CLI → CLI 업데이트**에서 합니다.

## 도움말

자세한 사용법은 [도움말 사이트](https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/help/)에 있습니다(한국어·영어·중국어·일본어). 앱에서 고른 언어로 바로 열 수 있습니다.

- Mac: 메뉴 막대의 **Mighty Code 도움말**(⌘?) 또는 **설정 → 앱 정보 → 도움말 열기**
- Windows: 사이드바 아래의 도움말 단추, F1 또는 **설정 → 앱 정보 → 도움말 열기**
- 휴대폰: **호스트** 탭의 **도움말** 단추

원문은 [`docs/help/content/ko/`](docs/help/content/ko/)이고, 다른 언어는 이 한국어 원문을 따릅니다.

## 주요 기능

각 항목의 자세한 내용은 링크한 도움말 원문에 있습니다.

**시작하기** — [시작하기](docs/help/content/ko/getting-started.md)
- 워크스페이스는 세 가지로 더합니다: 기존 폴더 열기(⌘O), 새 폴더 만들기(빈 Git 저장소로 시작 가능), Git 주소에서 내려받기(이 컴퓨터의 자격 증명으로 접근을 확인한 뒤 받음).
- 에이전트 CLI나 Git이 없으면 앱을 열 때 **필요한 도구** 화면이 먼저 열려, 에이전트 CLI·기본 도구·스타일 도구·Tailscale·gh·glab을 골라 설치합니다. 앱은 sudo를 쓰지 않고 비밀번호를 받지 않습니다.

**화면 구성** — [화면 구성](docs/help/content/ko/layout.md)
- 사이드바: 워크스페이스 검색(⌘K), **작업 현황**, 워크스페이스·실행 창 목록. 경계선을 끌어 폭을 바꾸고, 두 번 클릭하면 기본 폭, ⌃⌘S로 접고 펼칩니다.
- **작업 현황**: 모든 워크스페이스의 실행 창을 상태 순으로 모으고, 실행 중·응답 대기·완료 개수와 계정 사용량을 보여 줍니다.
- 실행 창은 에이전트(Claude, Codex, Antigravity, pi, opencode, Hermes), 터미널, 브라우저, 파일 중 하나입니다. 탭으로 겹치거나 좌우·상하로 나누고, 배치는 워크스페이스마다 저장됩니다. Claude를 뺀 에이전트 창에는 **베타** 표시가 붙습니다.
- 에이전트 창을 추가할 때 이 폴더의 이전 세션을 골라 이어갈 수 있습니다.

**에이전트 창** — [에이전트 창](docs/help/content/ko/agent-pane.md)
- 입력창 아래 줄에서 첨부, 모델, 사고 강도, 권한, 추가 실행 설정을 고릅니다. 첨부는 최대 8개, 파일당 5 MiB, 합계 8 MiB입니다.
- 실행 중에도 다음 요청을 쓸 수 있습니다. Enter는 **대기열에 추가**(창당 16개까지)하고, ⌘Enter나 중지 단추 옆의 **새 worktree에서 실행** 단추는 마이티 오케스트레이션이 켜진 git 저장소에서 요청을 새 자식 창에 바로 실행합니다. 대기열의 요청마다 **바로 실행** 단추가 있습니다.
- `/`를 치면 슬래시 명령 목록이 나오고, 목록에 없는 명령은 CLI의 스킬·플러그인 명령으로 그대로 전달됩니다.
- oh-my-claudecode가 있는 Claude 창은 **보낼 대상**으로 요청을 다른 CLI에게 보내고 답을 Claude의 답 안에 받습니다.
- 보내기 단추 옆 고리에서 컨텍스트 사용량과 토큰·비용·세션 ID를 봅니다. CLI가 알려 준 값만 보여 줍니다.
- 에이전트마다 설치·로그인·권한이 다릅니다. Antigravity, pi, opencode, Hermes의 자세한 내용은 도움말에 있습니다. pi, opencode, Hermes 창은 아직 마이티 오케스트레이션과 원격 워크스페이스에서 쓸 수 없습니다.

**마이티 보기** — [에이전트 창의 마이티 보기](docs/help/content/ko/agent-pane.md)
- 모든 에이전트 창은 요청 → 그 사이에 일어난 일 → **최종 결과**를 블록 흐름으로 보여 줍니다. 하위 에이전트와 백그라운드 명령은 작은 카드로 접혀 있고 누르면 펼쳐지며, Claude가 할 일 목록을 만들면 단계마다 카드가 섭니다.
- 흐름 왼쪽 위의 **진행 상황** 패널에 화면에 보이는 요청의 상태와 단계, 그 요청이 띄운 백그라운드 작업이 나옵니다.
- **흐름** | **기록** | **파이프라인** 전환으로 시간 순서 목록이나 칸반식 파이프라인으로도 봅니다. 결과 카드에서 결과 파일을 열고, 요청이 바꾼 줄만 따로 볼 수 있습니다.
- Claude와 Codex 창은 머리의 퍼즐 단추로 플러그인과 마켓플레이스를 엽니다.

**스타일과 계획** — [스타일](docs/help/content/ko/styles.md)
- Claude 창(Mac·Windows)에서 입력창 위 메뉴로 스타일을 고릅니다. 기본 스타일은 오로보로스(Ouroboros)와 클러드 플랜입니다.
- 클러드 플랜은 새 요청마다 Claude의 계획 모드로 시작합니다. 계획 카드의 네 가지 답(**승인하고 실행 (자동 편집)**, **승인하고 실행 (매번 확인)**, **수정 요청**, **취소**)으로 진행하고, 데스크톱 펫에서도 끝난 계획을 보고 답할 수 있습니다.
- 파일로 된 스타일은 **설정 → 스타일**에서 등록하고, 내용을 확인해 허용한 뒤에만 씁니다.

**마이티 오케스트레이션** — [마이티 오케스트레이션](docs/help/content/ko/delegation.md)
- 에이전트 창 하나가 일하는 동안 다른 일을 옆의 자식 창에 맡깁니다. 자식은 저마다 자기 git worktree와 브랜치(`mighty/<자식 id>`)에서 일하고, 끝나면 사이드바의 카드에서 결과를 부모 브랜치에 병합하거나 버립니다.
- 직접 띄우거나(⌘Enter), Claude·Codex에게 일을 나눠 맡기라고 요청합니다. 자식은 Claude, Codex, Antigravity 중 아무 에이전트로 실행할 수 있습니다.
- 실험 기능이며 기본으로 켜져 있고, **설정 → 일반 → 마이티 오케스트레이션**에서 끕니다.

**승인·질문·로그인 복구** — [승인과 질문](docs/help/content/ko/approvals.md)
- Claude 창(Always ask 등), Codex의 **승인 요청** 권한, opencode·Hermes 창은 권한 밖의 일을 승인 카드로 묻습니다(**이번만 허용** / **거부**). Claude, pi, opencode, Hermes의 질문은 **선택 요청** 카드로 답합니다.
- Antigravity는 일하는 도중에 물을 수 없어서, 물어봐야 하는 작업을 건너뛰고 턴을 끝낸 뒤 거절한 작업을 모은 카드로 묻습니다(**이번만 허용** / **항상 허용** / **거절**). 허용하면 같은 대화에서 이어서 진행하고, 위험한 명령은 이번만 허용할 수 있습니다.
- 에이전트가 터미널에서 실행한 명령이 입력을 기다리면 입력창 위의 **터미널이 입력을 기다립니다** 카드에서 바로 답합니다.
- CLI 로그인이 만료되면 입력창 위의 복구 카드에서 **다시 로그인**하고, 끝나면 실패한 요청을 다시 보냅니다.
- 턴이 끝났지만 백그라운드 작업이 남아 있으면 입력창 위의 줄에서 끝난 수와 도는 수를 봅니다. 휴대폰에도 같은 카드와 줄이 뜹니다.

**터미널·파일·브라우저 창** — [터미널·파일·브라우저](docs/help/content/ko/terminal-files-browser.md)
- 터미널 창(⌘T)은 워크스페이스 폴더에서 열리는 실제 터미널입니다. 에이전트가 사용자가 봐야 할 명령을 실행하면 전용 에이전트 터미널이 생깁니다.
- 파일 창(⇧⌘E)은 읽기 전용으로 소스·Markdown·이미지·PDF를 미리 봅니다.
- 브라우저 창은 실험 기능이라 **설정 → 일반**에서 켜고 앱을 다시 열어야 합니다. Mac에서는 Apple Silicon에서만 동작합니다.

**설정** — [설정](docs/help/content/ko/settings.md)
- 일반(테마·언어·글자 크기와 글꼴·상태 줄·브라우저·마이티 오케스트레이션), 모델(작업 단계별 모델), 스타일, 구성 요소(필요한 도구, 내 작업 도구 모음), CLI(로그인·업데이트), 모바일 리모트, 원격 연결 (Tailscale), 펫과 알림, 앱 정보(업데이트·데이터 위치) 탭이 있습니다.

**원격 연결 (Tailscale)** — [원격 연결](docs/help/content/ko/remote.md)
- Mac이나 Windows 컴퓨터 두 대의 Mighty Code를 Tailscale로 이어, 한쪽에서 다른 컴퓨터의 워크스페이스를 열고 그 컴퓨터의 CLI·계정으로 Claude·Codex·Antigravity와 명령을 실행합니다. 파일은 복사하지 않습니다.
- 공유하는 컴퓨터는 **설정 → 원격 연결 (Tailscale) → 이 컴퓨터 공유**에서 워크스페이스를 고르고 주소와 연결 키를 알려 줍니다. 연결하는 컴퓨터는 **다른 컴퓨터 연결**에 그 주소와 키를 넣고 워크스페이스를 엽니다.
- 원격 창도 마이티 보기와 승인·질문·계획 카드를 그대로 씁니다. 마이티 오케스트레이션, 파일·브라우저 창, 파일을 읽는 스타일, pi·opencode·Hermes 창은 원격 워크스페이스에서 쓸 수 없고, 휴대폰에는 보이지 않습니다. Windows에서 공유하려면 방화벽 규칙 하나를 더합니다(관리자 확인 한 번).

**휴대폰 원격** — [휴대폰 원격](docs/help/content/ko/phone.md)
- 컴퓨터(Mac 또는 Windows)의 **설정 → 모바일 리모트**에서 연결을 허용하고 QR 코드로 휴대폰을 연결합니다. 공유기 포트나 VPN이 필요 없고, 릴레이는 내용을 볼 수 없습니다. 같은 Tailscale 네트워크에 있으면 릴레이 없이 바로 연결합니다(**Tailscale로 직접 연결**, 기본 켜짐).
- 휴대폰 앱의 **현황**·**세션**·**알림**·**호스트** 탭에서 상태를 보고, 요청을 보내고, 승인·질문·계획에 답합니다. Android 앱은 푸시 알림도 받습니다(작업 완료·실패, 권한 요청·질문·계획·로그인 만료). 0.4.58에서 기본 릴레이가 새 서버로 바뀌었으니, 그 전에 연결한 휴대폰에 알림이 오지 않으면 QR로 한 번 다시 연결하세요.
- 컴퓨터 앱에 새 버전이 있으면 휴대폰에서 **데스크톱 앱 업데이트**로 설치하고 다시 시작하게 할 수 있습니다.
- **화면 보기·조작**(베타, Android)은 0.4.58부터 잠시 멈춰 있습니다. 휴대폰마다 정한 허용 범위는 그대로 남아 다시 켜지면 돌아옵니다.
- 기본 릴레이 대신 `relay/`를 직접 배포해 쓸 수 있습니다. 계약은 [docs/mobile-remote.md](docs/mobile-remote.md)와 [docs/relay.md](docs/relay.md)에 있습니다.

**Windows에서 다른 점** — [Windows](docs/help/content/ko/windows.md)
- 같은 기능을 같은 이름으로 제공하며, 창 제목에 베타 표시가 붙습니다. ⌘ 대신 Ctrl을 쓰고, 사이드바 접기는 Ctrl+B, 원격 화면 멈추기는 Ctrl+Alt+Esc입니다. 메뉴 막대는 없습니다.
- CLI 로그인은 별도의 Windows 터미널 창에서, 터미널 창은 Windows PowerShell로, 브라우저 창은 WebView2로 엽니다.

## 직접 빌드하기

```sh
git clone https://github.com/athens96/MightyCode.git
cd MightyCode
```

### macOS

macOS 14 이상과 Swift 6 이상의 도구(Xcode 또는 Command Line Tools)가 필요합니다. 첫 빌드에서 고정된 Swift 패키지(Ghostty 터미널, WebRTC 등)를 내려받습니다.

```sh
bash scripts/setup-codesign.sh          # 한 번만: 재빌드해도 macOS 권한 허용이 유지되는 로컬 서명 인증서
bash scripts/build-macos.sh             # release/native-macos/MightyCode.app
bash scripts/test-native-macos.sh --filter "AppUpdateTests|AttachmentTests"
```

- 로컬에서는 테스트를 `--filter`로 골라 돌립니다. 전체 테스트는 CI에서 돌고, 로컬에서 꼭 전부 돌리려면 `MIGHTY_FULL_SUITE=1`을 붙입니다.
- `/Applications`에 설치하거나 갱신할 때는 `bash scripts/install-macos.sh`를 씁니다. 실행 중인 앱이 끝나기를 기다렸다가 백업 후 교체하고 다시 엽니다. 실행 중인 앱의 파일을 직접 덮어쓰면 macOS 입력기 연결이 끊겨 한글이 자모로 풀립니다.
- 내장 브라우저(Apple Silicon)를 넣으려면 엔진을 받은 뒤 옵션을 켜고 빌드합니다: `bash scripts/fetch-browser-engine.sh` → `MIGHTY_BROWSER_ENGINE=1 bash scripts/build-macos.sh`. 자세한 내용은 [브라우저 문서](docs/browser-pane.md)에 있습니다.

임시 프로필로 앱 시작과 셸 실행을 확인할 수 있습니다. 실제 AI 요청은 보내지 않습니다.

```sh
release/native-macos/MightyCode.app/Contents/MacOS/MightyCode \
  --smoke-test --smoke-exit --profile /tmp/mighty-native-smoke
```

### Windows

Windows 10 버전 2004(빌드 19041) 이상 또는 Windows 11, **.NET 10 SDK**, **Node.js 22**, **PowerShell 7**, Windows SDK 빌드 도구(Visual Studio의 Windows 앱 개발 도구 또는 Build Tools)가 필요합니다. Windows App SDK 버전은 프로젝트에서 고정합니다.

창의 화면은 WebView2에 띄운 React 페이지입니다(`native/windows/web`, [README](native/windows/web/README.md)). `dotnet build`·`build-windows.ps1`이 `npm ci`(처음 한 번)와 `npm run build`를 직접 돌려 `Assets\Web`에 넣습니다. 상태 폴더(`%APPDATA%\MightyCode` 또는 `--profile` 폴더)에 빈 `ui-winui` 파일을 두면 이전 WinUI 화면으로 열립니다(비상구).

```powershell
# C# 코어 검사 후 x64 / ARM64 앱 빌드
pwsh ./scripts/build-windows.ps1 -Architecture x64 -Configuration Release
pwsh ./scripts/build-windows.ps1 -Architecture arm64 -Configuration Release

# 격리된 프로필로 앱 실행 검사: WinUI 비상구가 뜨는지 짧게, 그다음 웹 화면과 그 곁의 동작 전부
pwsh ./scripts/test-native-windows.ps1 -Executable ./release/native-windows-x64/MightyCode.exe
pwsh ./scripts/test-native-windows.ps1 -Web -Executable ./release/native-windows-x64/MightyCode.exe

# 웹 화면 검사(빌드 전에도)
cd native/windows/web; npm ci; npm run typecheck; npm test
```

`build-windows.ps1`은 먼저 `MightyCode.Core.Tests`를 돌린 뒤, self-contained 앱을 `release/native-windows-<아키텍처>/`에, 배포 zip과 SHA-256 파일을 `release/MightyCode-windows-<아키텍처>.zip`에 만듭니다. 다른 출력 폴더는 `-OutputDirectory`로 지정합니다. `-Installer`를 붙이면 [Inno Setup 6](https://jrsoftware.org/isinfo.php)으로 설치 프로그램 `release/MightyCode-windows-<아키텍처>-setup.exe`도 만듭니다(`native/windows/installer/MightyCode.iss`, `ISCC.exe` 경로는 `MIGHTY_ISCC`로 지정 가능). `pwsh ./scripts/test-windows-installer.ps1 -Setup <setup.exe> -Version <버전>`은 그것을 조용히 설치해 확인하고 다시 제거합니다. 이 패키지는 코드 서명하지 않습니다.

Mac에서는 Windows 앱을 실행할 수 없지만, C# 코어 검사(`dotnet run --project native/windows/MightyCode.Core.Tests --configuration Release`)와 WinUI 컴파일 검사(`bash scripts/check-winui-compile.sh`)는 돌릴 수 있습니다.

### 휴대폰 앱과 릴레이

```sh
cd mobile && npm install && npx expo start     # Expo 휴대폰 앱 (자세한 내용은 mobile/README.md)
cd relay && npm install && npm test            # 직접 서버에 올리는 Node 릴레이 (자세한 내용은 relay/README.md)
cd relay/cloudflare && npm install && npm test # 앱의 기본 릴레이인 Cloudflare Workers 판
```

연결 계약은 [docs/relay.md](docs/relay.md)와 [docs/mobile-remote.md](docs/mobile-remote.md)에, Cloudflare 릴레이 배포는 [docs/relay-cloudflare.md](docs/relay-cloudflare.md)에 있습니다.

## 데이터와 소스 구조

앱의 설정과 기록은 이 컴퓨터에만 저장됩니다. 위치는 **설정 → 앱 정보**에서 보고 열 수 있습니다. 기본 위치는 macOS `~/Library/Application Support/MightyCode/`, Windows `%APPDATA%\MightyCode\`입니다. 대화 기록 자체는 각 CLI의 세션으로 남습니다.

```text
native/macos/Sources/MightyCode/       SwiftUI/AppKit 화면·앱 상태
native/macos/Sources/MightyCore/         Swift 실행기·저장·스타일·모바일 리모트·원격 워크스페이스
native/windows/MightyCode.WinUI/       Windows 호스트(WebView2 창, 페이지가 부르는 동작)와 이전 WinUI 화면
native/windows/web/                      Windows 창의 React 화면 (Vite + TypeScript)
native/windows/MightyCode.Core/        C# 실행기·저장
native/windows/MightyCode.Core.Tests/  C# 회귀 검사
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

기다림이 생기는 사용자 동작(단추·카드·단축키 등)을 새로 넣으면 [docs/interaction.md](docs/interaction.md) 표에 한 줄을 더하고, 처리 중 표시는 그 플랫폼의 공통 부품(맥 `PendingIndicator`, 윈도우 웹 화면·폰 `<Pending>`)을 씁니다.

## 라이선스

이 프로젝트는 MIT 라이선스입니다([LICENSE](LICENSE)). 릴레이 방식의 휴대폰 원격은 Apache License 2.0으로 배포되는 Paseo의 설계를 참고했으며, 고지는 [NOTICE.md](NOTICE.md)와 [licenses/](licenses/)에 있습니다. 앱에 포함해 배포하는 타사 구성 요소의 목록은 [NOTICE.md](NOTICE.md)에, 라이선스 전문은 [native/licenses/](native/licenses/)에, 아이콘 생성 안내는 [assets/icons/generation.md](assets/icons/generation.md)에 있습니다.
