---
title: 시작하기
order: 1
section: getting-started
---
Mighty Claude는 Claude Code, Codex CLI, Google의 Antigravity CLI를 한 창에서 나란히 쓰는 데스크톱 앱입니다. 프로젝트 폴더 하나에 여러 실행 창을 열고, 창마다 대화와 설정을 따로 유지합니다.

## 준비물 {#requirements}
- macOS 14 이상. Windows는 Windows 10(버전 2004) 이상 또는 Windows 11입니다.
- 사용할 AI CLI. 앱은 CLI를 직접 실행하므로 설치하고 로그인해야 합니다. Mac에서는 앱을 처음 열 때 나오는 {{ui:setup.title}} 화면에서 설치할 수 있습니다([첫 실행](#first-launch)). 쓰지 않을 CLI는 설치하지 않아도 됩니다.

- Claude Code: Mac `curl -fsSL https://claude.ai/install.sh | bash`, Windows `npm install -g @anthropic-ai/claude-code`
- Codex CLI: Mac `curl -fsSL https://chatgpt.com/codex/install.sh | sh`, Windows `npm install -g @openai/codex`
- Antigravity CLI: Mac `curl -fsSL https://antigravity.google/cli/install.sh | bash`, Windows [Antigravity](agent-pane.md#antigravity)

> [!tip]
> 앱을 연 뒤 {{ui:settings.nav.tools}}에서 어떤 CLI가 준비됐는지 봅니다. Mac은 그 자리에서 설치하고, Windows는 설치 명령을 복사합니다. 로그인은 {{ui:settings.nav.cli}} 탭에서 합니다. [설정](settings.md#components)을 보세요.

## 설치 {#install}
1. [다운로드 페이지](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)에서 내 운영체제용 파일을 받습니다.
2. macOS: `MightyClaude-macos.zip`을 풀고 `MightyClaude.app`을 응용 프로그램 폴더로 옮긴 뒤 엽니다.
3. Windows: `MightyClaude-windows-x64-setup.exe`(ARM PC는 `arm64`)를 실행해 설치하고 시작 메뉴에서 엽니다. 자세한 내용은 [Windows에서 다른 점](windows.md#install)에 있습니다.

> [!warning]
> 이 앱은 Apple 공증을 받지 않았습니다. macOS가 처음 열 때 막으면 Finder에서 앱을 Control-클릭한 뒤 **열기**를 고르거나, 시스템 설정 → 개인정보 보호 및 보안에서 열기를 허용하세요.

## 업데이트 {#update}
{{ui:settings.appUpdate.autoUpdateToggle}}가 켜져 있으면(처음부터 켜져 있습니다) 앱이 새 버전을 알아서 받고 설치합니다.

- 앱을 열 때와 실행 중에 6시간마다 새 버전이 있는지 확인합니다. 확인에 실패하면 다음 확인 때 다시 시도하고, 실패는 설정에만 보입니다.
- 새 버전이 있으면 묻지 않고 받아 서명과 파일을 확인한 뒤 준비해 둡니다. 준비가 끝나면 창 아래 상태 줄에 다음 실행 때 설치된다는 표시와 {{ui:window.status.updateRestartNow}} 단추가 보입니다.
- 앱을 종료하면 준비된 업데이트를 설치합니다. 앱은 다시 열리지 않고, 다음에 열 때 새 버전으로 열립니다. {{platform:mac}} 로그아웃, 재시동, 시스템 종료로 앱이 닫힐 때는 설치하지 않고 다음 종료를 기다립니다.
- 앱이 한동안 쉬고 있으면 창 위쪽에 1분 알림이 뜹니다. 시간이 다 되면 앱이 다시 시작하며 업데이트를 설치하고, 열려 있던 워크스페이스와 창은 그대로 돌아옵니다. {{ui:appUpdate.countdown.restartNow}}을 누르면 바로 다시 시작하고, {{ui:appUpdate.countdown.later}}를 누르면 4시간 동안 알림을 띄우지 않습니다. 그사이 앱을 종료하면 그때 설치됩니다.

앱이 쉬고 있다는 것은 아래가 모두 맞는다는 뜻입니다. 알림이 떠 있는 동안 이 가운데 하나라도 깨지면 알림은 조용히 사라지고 다음에 쉴 때를 기다립니다. 키보드나 마우스 입력만으로는 사라지지 않으므로 알림의 단추를 누를 수 있습니다.
- 실행 중이거나 시작을 기다리는 에이전트 작업, 답을 기다리는 카드, 대기열의 요청이 없습니다.
- 입력창에 보내지 않은 글이나 첨부 파일이 없습니다.
- 명령이 돌고 있는 터미널이 없고, CLI 로그인·업데이트·설치가 진행 중이지 않습니다.
- 휴대폰 업로드, 화면 공유, 이 컴퓨터가 다른 컴퓨터를 위해 돌리는 원격 실행이 없습니다.
- 설정이나 대화상자가 열려 있지 않습니다.
- 5분 동안 키보드·마우스 입력이 없었고, 3분 안에 끝난 실행이나 보낸 요청이 없습니다.

직접 업데이트하려면:

1. {{ui:menu.settings}}({{kbd:⌘,}})을 열고 {{ui:settings.nav.about}} 탭으로 갑니다.
2. {{ui:settings.appUpdate.sectionTitle}}에서 {{ui:settings.appUpdate.checkButton}}을 누릅니다.
3. 새 버전이 있는데 아직 받지 않았으면 {{ui:settings.appUpdate.downloadButton}} 단추를 누릅니다.
4. 준비가 끝나면 {{ui:settings.appUpdate.restartNowButton}}을 누릅니다. 앱이 종료된 뒤 새 버전으로 바뀌고 다시 열립니다.

{{ui:settings.appUpdate.autoUpdateToggle}}를 끄면 앱이 스스로 확인하거나 받지 않고, 1분 알림을 띄우지 않으며, 종료할 때도 설치하지 않습니다. 이미 받아 둔 업데이트도 {{ui:settings.appUpdate.restartNowButton}}을 눌러야 설치됩니다. 어느 쪽이든 업데이트 파일은 서명을 확인한 뒤에만 설치합니다.

Windows 앱도 같은 방식으로 업데이트합니다([Windows에서 다른 점](windows.md#install)).

> [!note]
> AI CLI의 업데이트는 앱 업데이트와 따로 합니다. {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliUpdate.sectionTitle}} 항목을 쓰세요.

## 첫 실행 {#first-launch}
처음 열면 아직 워크스페이스가 없어서 시작 화면이 보입니다.

![[welcome]]

### 필요한 도구 {#setup}
에이전트 CLI(Claude Code, Codex, Antigravity 중 하나)가 하나도 실행되지 않거나 Git이 준비되지 않았으면 앱을 열 때 {{ui:setup.title}} 화면이 먼저 열립니다. 에이전트 CLI, 기본 도구(Git, Homebrew, Node, Bun), 스타일 도구(uv, Ouroboros, oh-my-claudecode, gstack), Tailscale, gh·glab을 묶어서 보여 줍니다.

1. 설치할 항목에 체크합니다. 처음 열 때 빠진 Git과, 에이전트 CLI가 하나도 없으면 Claude Code가 미리 체크되어 있고, 고른 항목에 필요한 도구(예: gh에 필요한 Homebrew)는 함께 들어갑니다.
2. {{ui:setup.button.install}}를 누르면 실행할 명령을 순서대로 모두 보여 줍니다. {{ui:setup.confirm.run}}을 눌러야 실행됩니다.
3. 명령은 앱이 따로 여는 터미널 창 하나에서 홈 폴더를 작업 폴더로 차례로 실행됩니다. 이 창은 실행 창이 아니라서 워크스페이스에 저장되지 않습니다. 비밀번호나 확인을 묻는 단계는 터미널에서 직접 답하세요. 앱은 sudo를 쓰지 않고 비밀번호를 받지 않습니다. 한 단계가 실패해도 다음 단계로 넘어갑니다.
4. 끝나면 앱이 다시 확인합니다. 앱이 앞에 있으면 이 화면을 다시 열고, 뒤에 있으면 상태 줄에 {{ui:setup.finishedBadge}} 단추를 보여 줍니다. 결과는 명령의 종료 코드가 아니라 다시 확인한 상태로 정해집니다. CLI가 새로 생겼으면 {{ui:setup.button.signIn}} 단추가 나옵니다.

Git(Xcode Command Line Tools)은 시스템 설치 창이 열리고 그 창에서 마칩니다. 스크립트는 30분까지 기다리고, Control-C를 누르면 기다림만 끝내고 다음 단계로 넘어갑니다. Xcode 라이선스에 동의하지 않아 git이 실행되지 않으면 {{ui:setup.status.attention}}로 보입니다. Tailscale은 공식 내려받기 페이지를 엽니다. 너무 오래된 Claude Code는 {{ui:setup.status.outdated}}로 보이고 {{ui:setup.button.update}} 단추가 나옵니다.

{{ui:setup.button.later}}로 닫을 수 있고, {{ui:setup.button.neverShow}}에 체크하고 닫으면 앱을 열 때 다시 나오지 않습니다. 필수 도구가 빠져 있는 동안 시작 화면에 {{ui:setup.title}} 단추가 있고, CLI가 없어 막힌 실행 창에는 {{ui:setup.blocked.install}} 단추가 있습니다. 같은 목록이 설정 → {{ui:settings.nav.tools}}에도 있습니다.

{{platform:windows}} Windows에도 같은 화면이 있습니다. 명령은 앱이 따로 여는 PowerShell 창 하나(Windows Terminal이 있으면 그 탭, 없으면 콘솔 창)에서 홈 폴더를 작업 폴더로 차례로 실행되고, 마지막에 Enter를 누를 때까지 창이 열려 있습니다. Claude Code, Codex, Antigravity, uv는 각자의 공식 PowerShell 설치 스크립트로, Git과 glab은 winget으로 이 사용자에게만, Node.js와 gh는 winget으로 모든 사용자에게 설치합니다. 모든 사용자용 설치는 Windows가 직접 관리자 확인 창을 띄울 수 있지만, 이 화면 자체는 관리자 권한을 요청하지 않습니다. 설치가 끝나면 앱이 PATH를 레지스트리에서 다시 읽어 확인합니다. Homebrew, Bun, gstack은 Mac에서만 설치합니다.

## 워크스페이스 열기 {#open-workspace}
워크스페이스는 프로젝트 폴더 하나입니다. 실행 창, 탭 배치, 대화 기록이 워크스페이스마다 따로 저장됩니다.

1. {{ui:layout.welcome.openProject}} 단추를 누르거나 {{kbd:⌘O}}를 누릅니다.
2. 작업할 폴더를 고릅니다.
3. 사이드바에 워크스페이스가 생기고, 실행 창이 없는 폴더이면 Claude 실행 창이 하나 바로 열립니다. 창을 더 열려면 {{ui:menu.newClaudePane}}({{kbd:⌘N}}) 항목을 씁니다.

다른 폴더를 더 열려면 사이드바의 {{ui:sidebar.workspacesHeader}} 머리글 오른쪽에 있는 폴더 단추를 누르거나, 메뉴 막대의 {{ui:menu.openProject}}나 {{kbd:⌘O}}를 씁니다. 사이드바에서 워크스페이스를 오른쪽 클릭하면 {{ui:menu.rename}}, {{ui:menu.showInFinder}}, {{ui:workspace.menu.remove}} 항목이 있습니다. 목록에서 제거해도 디스크의 프로젝트 파일은 그대로 남습니다.

## 언어 바꾸기 {#language}
1. {{ui:menu.settings}} → {{ui:settings.nav.general}}을 엽니다.
2. {{ui:settings.display.languageLabel}}에서 언어를 고릅니다. {{ui:settings.display.languageSystem}}은 Mac의 언어를 따릅니다.
3. 메뉴 막대는 앱을 다시 열어야 바뀝니다. 다른 화면이 바로 바뀌지 않아도 앱을 다시 열면 모두 바뀝니다.

## 도움말 열기 {#help}
이 도움말은 앱에서 바로 열 수 있습니다. 앱에서 고른 언어로 열립니다.

- Mac: 메뉴 막대의 {{ui:menu.help}}({{kbd:⌘?}})이나 {{ui:menu.settings}} → {{ui:settings.nav.about}}의 {{ui:settings.appInfo.openHelpButton}} 단추
- Windows: 사이드바 아래의 도움말 단추, {{kbd:F1}}, {{ui:menu.settings}} → {{ui:settings.nav.about}}의 {{ui:settings.appInfo.openHelpButton}} 단추
- 폰: {{ui:phone.tabs.hosts}} 탭의 {{ui:phone.hosts.help}} 단추
