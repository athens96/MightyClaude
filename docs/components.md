# 필요한 도구 · 구성 요소 (Mac · Windows)

앱이 이 Mac에서 쓰는 도구를 한 목록으로 확인하고, 빠진 것을 앱 안의 터미널 창에서 설치한다. 같은 목록이 두 곳에 있다.

- **필요한 도구** 시트: 앱을 열 때 에이전트 CLI(Claude Code·Codex·Antigravity)가 하나도 실행되지 않거나 필수 항목인 Git(Command Line Tools)이 준비되지 않았으면 자동으로 열린다. Claude Code는 필수가 아니다: 에이전트 CLI는 셋 중 하나면 된다. 처음 보일 때 빠진 Git과, 에이전트 CLI가 하나도 없으면 Claude Code가 미리 체크된다(그 뒤에는 사용자가 고른 대로 둔다). "다음에 하기"로 닫고, "다시 보지 않기"에 체크하고 닫으면 다시 자동으로 열리지 않는다(`setup.neverShow` 기본값). 필수 항목이 빠져 있는 동안 시작 화면에 **필요한 도구** 단추가 있고, CLI가 없어 막힌 에이전트 창에는 **설치하기** 단추가 있다(그 CLI에 체크하고 그 줄로 스크롤한다). 스모크 실행과 도움말 캡처에서는 자동으로 열리지 않는다.
- **설정 → 도구 → 구성 요소**: 같은 목록과, 앱이 설치된 에이전트에 요구하는 플러그인(`ComponentCatalog.requiredPlugins`, 현재 비어 있음). 그 아래 **내 작업 도구 모음**은 그대로다.

Windows에도 같은 화면이 있다. 다른 점은 아래 [Windows](#windows)에 있다.

## 목록 (`native/contracts/fixtures/setup-catalog.json`)

항목은 앱에 같이 들어가는 읽기 전용 카탈로그에서 온다. 사용자가 고치는 파일(toolkit.json 포함)은 항목을 더할 수 없다. 형식과 닫힌 종류는 `native/contracts/README.md`의 "Setup catalog"에 있다.

| 무리 | 항목 | 확인 | 설치 | 공식 출처 |
|---|---|---|---|---|
| 에이전트 CLI | Claude Code | `claude`가 로그인 셸 PATH에 있고 `--version` 2.1.271 이상 | `https://claude.ai/install.sh`를 bash로 실행 (Node 필요 없음) | https://code.claude.com/docs/en/setup |
| | Codex CLI | `codex` | `https://chatgpt.com/codex/install.sh`를 sh로 실행 (~/.local/bin, Node 필요 없음) | https://github.com/openai/codex |
| | Antigravity CLI | `agy` | `https://antigravity.google/cli/install.sh`를 bash로 실행 (~/.local/bin) | https://antigravity.google/cli/install.sh |
| 기본 도구 | Git (Xcode Command Line Tools, 필수) | `xcode-select -p`가 있는 폴더를 가리키고 `/usr/bin/git --version`이 시간 안에 성공 (Xcode 라이선스 미동의는 "확인 필요") | `/usr/bin/xcode-select --install` — 시스템 창이 열리고, 스크립트는 끝날 때까지 최대 30분 기다린다. Control-C는 기다림만 끝낸다 | https://developer.apple.com/documentation/xcode/installing-the-command-line-tools/ |
| | Homebrew (Git 필요) | `brew` | `https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh`를 bash로 실행 — 터미널에서 비밀번호를 묻는다 | https://brew.sh |
| | Node.js (Homebrew 필요) | `node` | `brew install node` | https://formulae.brew.sh/formula/node |
| | Bun | `bun` 1.3.3 이상 | `https://bun.com/install`을 bash로 실행 | https://bun.com/docs/installation |
| 스타일 도구 | uv | `uvx` | `https://astral.sh/uv/install.sh`를 sh로 실행 | https://docs.astral.sh/uv/getting-started/installation/ |
| | Ouroboros (Claude, uv 필요) | 사용자 범위 플러그인 `ouroboros@…` | `claude plugin marketplace add Q00/ouroboros; claude plugin install ouroboros@ouroboros --scope user` | https://github.com/Q00/ouroboros |
| | oh-my-claudecode (Claude 필요) | 사용자 범위 플러그인 `oh-my-claudecode@…` | `claude plugin marketplace add https://github.com/Yeachan-Heo/oh-my-claudecode; claude plugin install oh-my-claudecode@omc --scope user` (그 뒤 Claude 창에서 `/omc-setup`) | https://github.com/Yeachan-Heo/oh-my-claudecode |
| | gstack (Git, Bun, Claude 필요) | `~/.claude/skills/gstack/SKILL.md`, 앱이 놓은 폴더는 `.mighty-setup-ok`도 | 커밋 `d258bcffb0cb7bda5f133f3e957f932d816d282f`(2026-10-17 main, v1.91.121.0)를 임시 폴더에 받아 `~/.claude/skills/gstack`으로 옮기고(이미 있으면 그 클론을 그 커밋으로 옮김) `./setup` 실행 | https://github.com/garrytan/gstack/commit/d258bcffb0cb7bda5f133f3e957f932d816d282f |
| 원격 연결 | Tailscale | `/Applications/Tailscale.app`, `~/Applications/Tailscale.app`, 또는 PATH의 `tailscale` | 공식 내려받기 페이지를 연다(https://tailscale.com/download/mac) — 실행하는 명령 없음 | https://tailscale.com/kb/1016/install-mac |
| Git 호스팅 CLI | GitHub CLI (Homebrew 필요) | `gh` | `brew install gh` | https://cli.github.com |
| | GitLab CLI (Homebrew 필요) | `glab` | `brew install glab` | https://gitlab.com/gitlab-org/cli |

Ouroboros의 명령은 스타일 매니페스트(`ouroboros.json`)의 설치 명령과 같다. 마켓플레이스로 설치하는 플러그인(Ouroboros·oh-my-claudecode)은 버전을 고정하지 않는다: 버전은 마켓플레이스가 정한다. Tailscale은 공식 안내(KB 1016)가 Homebrew를 다루지 않고 standalone 내려받기를 권하며, Homebrew cask 이름이 `tailscale`에서 `tailscale-app`으로 바뀐 적이 있고, 어떤 방식이든 시스템 설정에서 VPN·확장 승인을 직접 해야 하므로 페이지를 여는 방식으로 했다.

## 상태

각 줄은 **설치됨 · 필요 · 업데이트 필요 · 확인 필요 · 설치 중 · 실패** 중 하나다. 판단은 언제나 다시 확인한 결과로 하고, 명령의 종료 코드는 보지 않는다.

- 확인은 매번 새로 한다: 실행 파일은 에이전트 CLI와 같은 로그인 셸 PATH에서 찾고, ProviderService처럼 PATH의 사본을 차례로 `--version`이 답하는 것까지 본다. 최소 버전이 있는데 어느 사본도 버전을 말하지 않으면 "설치됨"으로 두고 버전을 확인하지 못했다고 덧붙인다. 시험판(`2.1.300-beta`)은 `ProviderService.supportsMods`처럼 최소 버전에 못 미친다. 로그인 셸 PATH 끝에는 공식 설치기가 쓰는 폴더(`~/.local/bin`, `~/.npm-global/bin`, `~/.bun/bin`, `/opt/homebrew/bin`, `/usr/local/bin`, `CLIEnvironmentResolver.wellKnownFolders` 한 곳에서 정의)가 없으면 덧붙는다. 그래서 셸 프로필이 아직 그 폴더를 넣지 않았어도 막 설치한 CLI를 찾는다.
- 목록이 열려 있으면 처음에 한 번 확인한다. 설치가 도는 동안에는 그 설치의 항목만 5초마다, 그 밖에는 빠진 항목이 있는 동안 30초마다 확인한다. **다시 확인**은 로그인 셸 환경도 새로 읽고, 확인 중에 들어온 이 요청은 버리지 않고 끝난 뒤 바로 한다.
- 확인한 에이전트 CLI의 버전이 앱이 들고 있던 실행기 정보와 다르면 실행기 정보를 다시 읽는다. 그래서 CLI가 없어 막혀 있던 창이 설치 뒤 바로 풀린다.
- Claude Code가 2.1.271보다 낮으면 "필요"가 아니라 **업데이트 필요**로 보이고, **업데이트** 단추가 기존 CLI 업데이트(docs/cli-updates.md)를 실행한다.
- 설치가 끝났는데 확인에서 보이지 않으면 **실패**이고 다시 고를 수 있다(Git도 같다). 에이전트가 아닌 도구가 최소 버전보다 낮으면(Bun) 체크해서 그 설치를 다시 실행할 수 있다.
- 에이전트 CLI가 생겼고 로그인되어 있지 않으면 **로그인** 단추가 기존 로그인 흐름(docs/cli-accounts.md)을 연다.

## 설치

1. 빠진 항목에 체크한다. 고른 항목의 설치에 필요한 항목이 빠져 있으면 "선택한 항목에 필요해 함께 설치합니다"와 함께 자동으로 들어가고, 필요한 항목이 먼저 실행된다(같은 순서 안에서는 카탈로그 순서).
2. **선택 항목 설치**를 누르면 확인 창 하나가 먼저 실행하는 줄(작업 폴더, PATH, npm 레지스트리)과 각 단계의 명령을 스크립트에 들어가는 그대로, 순서대로 모두 보여 준다. **실행**을 눌러야 실행된다.
3. 앱은 앱 데이터 폴더의 `setup/`에 스크립트를 쓴다. 그 폴더는 진짜 폴더여야 하고(링크면 거부), 이 사용자 소유에 그룹·기타 권한이 없어야 한다. 스크립트는 무작위 이름의 새 파일로 `O_CREAT|O_EXCL|O_NOFOLLOW`, 0700으로 만든다.
4. 앱은 실행 창이 아닌 **별도의 터미널 창**("도구 설치")을 `setup/` 폴더에서 열고 `/bin/bash '<스크립트>'`를 실행한다. 이 창은 워크스페이스에 저장되지 않고, 폰에 보이지 않고, 선택되지도 않는다. 워크스페이스가 없을 때 설치 화면에서 누른 **로그인**도 같은 종류의 창에서 연다(워크스페이스가 있으면 종전처럼 실행 창). 홈 폴더를 워크스페이스로 추가하지 않는다. 설치 화면은 열린 채로 진행을 따라간다.
5. 스크립트는 먼저 시작 표시를 만들고, `cd "$HOME"`, PATH 끝에 설치기 폴더 덧붙이기, `npm_config_registry=https://registry.npmjs.org/`를 한 뒤 단계를 돈다. 프로젝트의 `.npmrc`나 `.claude` 설정이 설치를 바꾸지 못하게 하려는 것이다. 단계마다 `[n/전체] 이름` 머리글을 찍고 각 단계를 서브셸에서 실행하며, 실패해도 다음 단계로 넘어간다. 공식 스크립트는 `/usr/bin/curl --proto '=https' --proto-redir '=https' --tlsv1.2 -fsSL`로 받아 둔 뒤 셸에 스크립트로 넘겨서, 그 스크립트가 묻는 비밀번호·확인은 터미널에서 직접 답한다. 앱은 sudo를 쓰지 않고 비밀번호를 받지 않는다.
6. 시작·끝 표시는 `mkdir`로 만드는 폴더라 이미 있는 링크를 따라가지 않는다. 앱은 2초마다 끝 표시나 창이 닫혔는지를 본다. 20초가 지나도 시작 표시가 없으면 "터미널 창을 확인하세요" 안내와 **설치 끝내기** 단추를 보여 준다. 끝나면 모든 항목을 다시 확인하고, 앱이 앞에 있으면 시트를 다시 열고 뒤에 있으면 상태 줄에 "도구 설치가 끝났습니다" 단추를 띄운다.

스크립트는 카탈로그에서만 만든다. 모든 값은 작은따옴표로 감싼다. `argv`는 `/usr/bin/xcode-select`(옵션 `--install`만)와 `npx`(옵션 `--global`, `--agent`만)뿐이다. 공식 스크립트·내려받기 페이지·저장소 주소는 허용된 호스트(claude.ai, chatgpt.com, antigravity.google, raw.githubusercontent.com, astral.sh, bun.com, github.com, tailscale.com)만 받고, github.com·raw.githubusercontent.com은 카탈로그가 쓰는 저장소(Homebrew/install, Q00/ouroboros, anthropics/claude-plugins-community, Yeachan-Heo/oh-my-claudecode, garrytan/gstack)만, 마켓플레이스의 owner/repo 줄임 형식도 같은 목록만 받는다(`.`·`..` 거부).

## Windows

같은 카탈로그의 `windows` 항목을 쓴다(`MightyClaude.Core/SetupCatalog.cs`, `SetupPlan.cs`, `SetupProbe.cs`; 화면은 `MightyClaude.WinUI/MainWindow.Setup.cs`). 디코더는 Mac과 같은 규칙으로 닫혀 있고, `winget`은 Windows 항목에만, Homebrew·Command Line Tools·앱 번들·`xcode-select`·고정 커밋 스킬은 Mac 항목에만 쓸 수 있다.

| 무리 | 항목 | 확인 | 설치 | 공식 출처 |
|---|---|---|---|---|
| 에이전트 CLI | Claude Code | `claude` 2.1.271 이상 | `https://claude.ai/install.ps1` (공식 `irm https://claude.ai/install.ps1 \| iex`), `%USERPROFILE%\.local\bin` | https://code.claude.com/docs/en/setup |
| | Codex CLI | `codex` | `https://chatgpt.com/codex/install.ps1` (공식 `irm https://chatgpt.com/codex/install.ps1 \| iex`), `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`. Windows에서 직접 돈다(WSL 필요 없음) | https://github.com/openai/codex (`scripts/install/install.ps1`) |
| | Antigravity CLI | `agy` | `https://antigravity.google/cli/install.ps1`, `%LOCALAPPDATA%\agy\bin` | https://antigravity.google/cli/install.ps1 |
| 기본 도구 | Git for Windows (필수) | `git` | `winget install --exact --id Git.Git --source winget --scope user --accept-package-agreements --accept-source-agreements` | https://git-scm.com/downloads/win, 매니페스트 `manifests/g/Git/Git` (user·machine 설치기) |
| | Node.js LTS | `node` | `winget install --exact --id OpenJS.NodeJS.LTS --source winget --scope machine …` — MSI가 모든 사용자용뿐이라 Windows가 관리자 확인 창을 띄울 수 있다 | https://nodejs.org/en/download, 매니페스트 `manifests/o/OpenJS/NodeJS/LTS` |
| 스타일 도구 | uv | `uvx` | `https://astral.sh/uv/install.ps1` (공식 `irm https://astral.sh/uv/install.ps1 \| iex`), `%USERPROFILE%\.local\bin` | https://docs.astral.sh/uv/getting-started/installation/ |
| | Ouroboros · oh-my-claudecode | Mac과 같음 | Mac과 같은 `claude plugin marketplace add …; claude plugin install … --scope user` | Mac과 같음 |
| 원격 연결 | Tailscale | `tailscale` (PATH 또는 `C:\Program Files\Tailscale`) | 공식 내려받기 페이지를 연다(https://tailscale.com/download/windows) | https://tailscale.com/kb/1022/install-windows |
| Git 호스팅 CLI | GitHub CLI | `gh` | `winget install --exact --id GitHub.cli --source winget --scope machine …` — MSI가 모든 사용자용뿐 | https://cli.github.com, 매니페스트 `manifests/g/GitHub/cli` |
| | GitLab CLI | `glab` | `winget install --exact --id GLab.GLab --source winget --scope user …` | https://gitlab.com/gitlab-org/cli, 매니페스트 `manifests/g/GLab/GLab` |

Homebrew, Bun, gstack은 Windows 항목이 없다. gstack의 `./setup`은 bash 스크립트라 Windows에서는 Git Bash가 필요한데, 앱의 PowerShell에서 어느 bash를 부를지(`C:\Windows\System32\bash.exe`는 WSL이다) 앱이 보증할 수 없고, 고정한 커밋의 Windows 동작도 확인하지 못했다. Bun은 gstack 때문에만 있다. Tailscale은 winget 패키지가 서비스를 설치하는 모든 사용자용이라, Mac처럼 공식 페이지를 연다. winget 패키지는 사용자 범위 설치기가 있으면 `--scope user`(Git, glab), 모든 사용자용만 있으면 `--scope machine`(Node.js, gh)으로 고정한다. 이때의 관리자 확인 창은 Windows가 띄우는 것이고 사용자가 보고 답한다. 앱은 스스로 권한을 올리지 않는다.

- **확인**: 실행 파일은 PATH(레지스트리의 HKLM·HKCU 값을 다시 읽은 것)와 공식 설치기 폴더(`%USERPROFILE%\.local\bin`, `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`, `%LOCALAPPDATA%\agy\bin`, `%APPDATA%\npm`, `%LOCALAPPDATA%\Microsoft\WinGet\Links`, Git for Windows의 사용자·전체 폴더, `C:\Program Files\nodejs`, `C:\Program Files\GitHub CLI`, `C:\Program Files\Tailscale`)에서 PATHEXT의 확장자로 찾는다. `--version`은 4초 안에 답해야 한다. 설치가 끝나면 레지스트리에서 PATH를 다시 읽는다: 돌고 있는 앱은 새 PATH를 저절로 보지 못한다.
- **스크립트**: `%LOCALAPPDATA%\MightyClaudeNative\setup`(`--profile`이면 그 안의 `setup`)에 둔다. 폴더는 정션·링크가 아닌 진짜 폴더이고 이 사용자 소유여야 하며, 실행할 때마다 쓰기 전에 이 사용자만 접근하는 보호된 ACL을 다시 씌운다. 앱이 관리자 권한으로 실행 중이면 설치하지 않고 그렇다고 말한다(`setup.windows.error.elevated`). 스크립트는 무작위 이름의 새 `.ps1`(UTF-8, BOM 포함 — Windows PowerShell은 BOM이 없으면 한국어를 깨뜨린다)이다. 먼저 실행하는 줄은 `Set-Location -LiteralPath $HOME`, PATH 끝에 설치기 폴더 덧붙이기, `npm_config_registry`, TLS 1.2, 그리고 두 함수다. `Get-MightyHttps`는 리디렉션을 직접 따라가며 https가 아닌 주소를 거부하고 16 MB보다 큰 응답은 읽다 말고 거부하며, `Invoke-MightyScript`는 받은 공식 스크립트를 setup 폴더에 새 파일로 쓰고 그 파일을 PowerShell 하나로 따로 실행한다. 그래서 공식 스크립트 안의 `exit`는 그 단계만 끝낸다. 단계마다 `try`/`catch`로 감싸 실패해도 다음 단계로 넘어간다. 모든 값은 작은따옴표로 감싸고, PowerShell이 따옴표로 읽는 ‘ ’ ‚ ‛도 두 번 쓴다.
- **창**: 로그인과 같은 방식이다. Windows Terminal(`wt.exe`)이 있으면 setup 폴더에서 새 탭, 없으면 콘솔 창(conhost)에서 `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File <스크립트>`를 실행한다. `-NoProfile`은 사용자 프로필 스크립트가 PATH·별칭·프록시를 먼저 바꾸지 못하게, `-ExecutionPolicy Bypass`는 이 프로세스 하나에만 적용되어 기본 정책이 `.ps1`을 막는 PC에서도 앱이 쓴 스크립트가 돌게(정책 자체는 바꾸지 않는다. 그룹 정책이 막으면 시작하지 않고 20초 뒤 안내가 나온다), `-File`은 경로를 코드로 읽지 않고 파일로 실행하게 한다. `wt.exe`는 PATH에서 찾은 전체 경로로, 콘솔 창은 시스템 폴더의 `conhost.exe`로 띄우고, `%`나 `;`가 든 명령은 Windows Terminal이 바꾸지 않게 콘솔 창으로 보낸다. 창의 환경에는 `NoDefaultCurrentDirectoryInExePath=1`을 넣어, `npx`·`claude` 같은 npm `.cmd` shim이 단계의 작업 폴더에 심어 둔 `node.exe`를 실행하지 않게 한다(앱이 직접 실행하는 명령과 로그인 창도 같다; 실제 Windows에서 아직 확인하지 않음). 창은 마지막에 Enter를 누를 때까지 열려 있다.
- **끝**: 시작 표시(`started-…` 폴더, 안에 그 PowerShell의 프로세스 ID)와 끝 표시(`done-…` 폴더)는 `Test-Path -LiteralPath`로 아무것도 없을 때만 만들어, 이미 무엇이 있으면 따라가지 않고 실패한다(Windows PowerShell 5.1의 `New-Item`에는 `-LiteralPath`가 없다). 앱은 2초마다 끝 표시를 보고, 끝 표시 없이 그 PowerShell이 사라졌으면(창을 닫음) 그것도 끝으로 본다. Windows Terminal 탭은 앱이 기다릴 프로세스를 남기지 않기 때문이다.
- **버전 확인**은 시스템 폴더(`Environment.SystemDirectory`)를 작업 폴더로 실행한다. **다시 보지 않기**가 켜져 있으면 앱을 열 때 아무것도 확인하지 않는다.
- **들어가는 곳**: 앱을 열 때, 시작 화면의 **필요한 도구** 단추, 설정 → 도구 → 구성 요소(종전의 설치 명령 복사 줄 대신 같은 목록), CLI가 없어 막힌 에이전트 창의 **설치하기**. 이 창은 휴대폰 동작과 자동 로그인 복구를 막지 않는다. **다시 보지 않기**는 레지스트리 `HKCU\Software\MightyClaudeNative`의 `setup.neverShow`(REG_DWORD)다.

## 플러그인

`ComponentCatalog.requiredPlugins`에 등록된 마켓플레이스 플러그인은 해당 CLI가 설치된 경우에만 검사하고, **플러그인 설치**가 기존 Claude/Codex 플러그인 서비스로 사용자 범위에 설치한다. 현재 목록은 비어 있다. Mighty 모드·펫·모바일 리모트가 쓰는 Claude Mod(`mods/mighty-bridge`)는 앱에 내장되어 실행마다 `--plugin-dir`로 연결되므로 에이전트에 따로 설치할 것이 없다.
