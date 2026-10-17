# 필요한 도구 · 구성 요소 (Mac)

앱이 이 Mac에서 쓰는 도구를 한 목록으로 확인하고, 빠진 것을 앱 안의 터미널 창에서 설치한다. 같은 목록이 두 곳에 있다.

- **필요한 도구** 시트: 앱을 열 때 에이전트 CLI(Claude Code·Codex·Antigravity)가 하나도 실행되지 않거나 필수 항목(Claude Code, Git)이 빠졌으면 자동으로 열린다. "다음에 하기"로 닫고, "다시 보지 않기"에 체크하고 닫으면 다시 자동으로 열리지 않는다(`setup.neverShow` 기본값). 필수 항목이 빠져 있는 동안 시작 화면에 **필요한 도구** 단추가 있고, CLI가 없어 막힌 에이전트 창에는 **설치하기** 단추가 있다(그 CLI에 체크하고 그 줄로 스크롤한다). 스모크 실행과 도움말 캡처에서는 자동으로 열리지 않는다.
- **설정 → 도구 → 구성 요소**: 같은 목록과, 앱이 설치된 에이전트에 요구하는 플러그인(`ComponentCatalog.requiredPlugins`, 현재 비어 있음). 그 아래 **내 작업 도구 모음**은 그대로다.

Windows는 아직 이 화면이 없고 종전처럼 설치 명령 복사만 한다(docs/windows-parity.md).

## 목록 (`native/contracts/fixtures/setup-catalog.json`)

항목은 앱에 같이 들어가는 읽기 전용 카탈로그에서 온다. 사용자가 고치는 파일(toolkit.json 포함)은 항목을 더할 수 없다. 형식과 닫힌 종류는 `native/contracts/README.md`의 "Setup catalog"에 있다.

| 무리 | 항목 | 확인 | 설치 | 공식 출처 |
|---|---|---|---|---|
| 에이전트 CLI | Claude Code (필수) | `claude`가 로그인 셸 PATH에 있고 `--version` 2.1.271 이상 | `curl -fsSL https://claude.ai/install.sh` 내용을 bash로 실행 (Node 필요 없음) | https://code.claude.com/docs/en/setup |
| | Codex CLI | `codex` | `https://chatgpt.com/codex/install.sh`를 sh로 실행 (~/.local/bin, Node 필요 없음) | https://github.com/openai/codex |
| | Antigravity CLI | `agy` | `https://antigravity.google/cli/install.sh`를 bash로 실행 (~/.local/bin) | https://antigravity.google/cli/install.sh |
| 기본 도구 | Git (Xcode Command Line Tools, 필수) | `xcode-select -p`가 있는 폴더를 가리킴 | `xcode-select --install` — 시스템 창이 열리고, 스크립트는 끝날 때까지 기다린다 | https://developer.apple.com/documentation/xcode/installing-the-command-line-tools/ |
| | Homebrew (Git 필요) | `brew` | `https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh`를 bash로 실행 — 터미널에서 비밀번호를 묻는다 | https://brew.sh |
| | Node.js (Homebrew 필요) | `node` | `brew install node` | https://formulae.brew.sh/formula/node |
| | Bun | `bun` 1.3.3 이상 | `https://bun.com/install`을 bash로 실행 | https://bun.com/docs/installation |
| 스타일 도구 | uv | `uvx` | `https://astral.sh/uv/install.sh`를 sh로 실행 | https://docs.astral.sh/uv/getting-started/installation/ |
| | Ouroboros (Claude, uv 필요) | 사용자 범위 플러그인 `ouroboros@…` | `claude plugin marketplace add Q00/ouroboros; claude plugin install ouroboros@ouroboros --scope user` | https://github.com/Q00/ouroboros |
| | Superpowers (Claude 필요) | 사용자 범위 플러그인 `superpowers@…` | `claude plugin marketplace add anthropics/claude-plugins-community; claude plugin install superpowers@claude-community --scope user` | https://github.com/anthropics/claude-plugins-community |
| | Paperthin (Node 필요) | `~/.claude/skills/re0/SKILL.md` | `npx skills@latest add LilMGenius/paperthin --global --agent claude-code` | https://github.com/LilMGenius/paperthin |
| | oh-my-claudecode (Claude 필요) | 사용자 범위 플러그인 `oh-my-claudecode@…` | `claude plugin marketplace add https://github.com/Yeachan-Heo/oh-my-claudecode; claude plugin install oh-my-claudecode@omc --scope user` (그 뒤 Claude 창에서 `/omc-setup`) | https://github.com/Yeachan-Heo/oh-my-claudecode |
| | gstack (Git, Bun, Claude 필요) | `~/.claude/skills/gstack/SKILL.md` | `git clone --single-branch --depth 1 https://github.com/garrytan/gstack.git ~/.claude/skills/gstack && cd ~/.claude/skills/gstack && ./setup` | https://github.com/garrytan/gstack |
| 원격 연결 | Tailscale | `/Applications/Tailscale.app` | 공식 내려받기 페이지를 연다(https://tailscale.com/download/mac) — 실행하는 명령 없음 | https://tailscale.com/kb/1016/install-mac |
| Git 호스팅 CLI | GitHub CLI (Homebrew 필요) | `gh` | `brew install gh` | https://cli.github.com |
| | GitLab CLI (Homebrew 필요) | `glab` | `brew install glab` | https://gitlab.com/gitlab-org/cli |

Ouroboros·Superpowers·Paperthin의 명령은 각 스타일 매니페스트(`ouroboros.json`·`superpowers.json`·`paperthin.json`)의 설치 명령과 같다. Tailscale은 공식 안내(KB 1016)가 Homebrew를 다루지 않고 standalone 내려받기를 권하며, Homebrew cask 이름이 `tailscale`에서 `tailscale-app`으로 바뀐 적이 있고, 어떤 방식이든 시스템 설정에서 VPN·확장 승인을 직접 해야 하므로 페이지를 여는 방식으로 했다.

## 상태

각 줄은 **설치됨 · 필요 · 업데이트 필요 · 설치 중 · 실패** 중 하나다. 판단은 언제나 다시 확인한 결과로 하고, 명령의 종료 코드는 보지 않는다.

- 확인은 매번 새로 한다: 실행 파일은 에이전트 CLI와 같은 로그인 셸 PATH에서 찾고 `--version`을 읽는다. 로그인 셸 PATH 끝에는 공식 설치기가 쓰는 폴더(`~/.local/bin`, `~/.npm-global/bin`, `/opt/homebrew/bin`, `/usr/local/bin`)가 없으면 덧붙는다. 그래서 셸 프로필이 아직 그 폴더를 넣지 않았어도 막 설치한 CLI를 찾는다.
- 목록이 열려 있으면 처음에 한 번, 그 뒤 빠진 항목이 있는 동안 5초마다 다시 확인한다. **다시 확인**은 로그인 셸 환경도 새로 읽는다.
- 확인한 에이전트 CLI의 버전이 앱이 들고 있던 실행기 정보와 다르면 실행기 정보를 다시 읽는다. 그래서 CLI가 없어 막혀 있던 창이 설치 뒤 바로 풀린다.
- Claude Code가 2.1.271보다 낮으면 "필요"가 아니라 **업데이트 필요**로 보이고, **업데이트** 단추가 기존 CLI 업데이트(docs/cli-updates.md)를 실행한다.
- 설치가 끝났는데 확인에서 보이지 않으면 **실패**다. 시스템 창이 마저 설치하는 Git은 확인에서 보일 때까지 **설치 중**으로 남는다.
- 에이전트 CLI가 생겼고 로그인되어 있지 않으면 **로그인** 단추가 기존 로그인 흐름(docs/cli-accounts.md)을 연다.

## 설치

1. 빠진 항목에 체크한다. 필수 항목은 미리 체크되어 있다. 고른 항목의 설치에 필요한 항목이 빠져 있으면 "선택한 항목에 필요해 함께 설치합니다"와 함께 자동으로 들어가고, 필요한 항목이 먼저 실행된다(같은 순서 안에서는 카탈로그 순서).
2. **선택 항목 설치**를 누르면 확인 창 하나가 실행할 명령을 스크립트에 들어가는 그대로, 순서대로 모두 보여 준다. **실행**을 눌러야 실행된다.
3. 앱은 앱 데이터 폴더의 `setup/`에 스크립트를 쓰고(0700), 로컬 워크스페이스에 "도구 설치" 터미널 창을 하나 열어 `/bin/bash '<스크립트>'`를 실행한다. 워크스페이스가 하나도 없으면 홈 폴더를 워크스페이스로 추가한다.
4. 스크립트는 단계마다 `[n/전체] 이름` 머리글을 찍고, 각 단계를 서브셸에서 실행하며, 실패해도 다음 단계로 넘어간다. 공식 스크립트는 받아 둔 뒤 셸에 스크립트로 넘겨서, 그 스크립트가 묻는 비밀번호·확인은 터미널에서 직접 답한다. 앱은 sudo를 쓰지 않고 비밀번호를 받지 않는다.
5. 스크립트는 끝날 때(실패·Control-C 포함) 표시 파일을 남긴다. 앱은 2초마다 그 파일이나 창이 닫혔는지를 보고, 끝났으면 모든 항목을 다시 확인한 뒤 시트를 다시 열어 결과와 로그인 단추를 보여 준다.

스크립트는 카탈로그에서만 만든다. 모든 값은 작은따옴표로 감싸고, `argv`는 `xcode-select`와 `npx`만, 공식 스크립트·내려받기 페이지·저장소 주소는 허용된 호스트(claude.ai, chatgpt.com, antigravity.google, raw.githubusercontent.com, astral.sh, bun.com, github.com, tailscale.com)만 받는다.

## 플러그인

`ComponentCatalog.requiredPlugins`에 등록된 마켓플레이스 플러그인은 해당 CLI가 설치된 경우에만 검사하고, **플러그인 설치**가 기존 Claude/Codex 플러그인 서비스로 사용자 범위에 설치한다. 현재 목록은 비어 있다. Mighty 모드·펫·모바일 리모트가 쓰는 Claude Mod(`mods/mighty-bridge`)는 앱에 내장되어 실행마다 `--plugin-dir`로 연결되므로 에이전트에 따로 설치할 것이 없다.
