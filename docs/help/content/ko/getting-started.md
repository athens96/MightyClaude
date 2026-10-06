---
title: 시작하기
order: 1
section: getting-started
---
Mighty Claude는 Claude Code, Codex CLI, Gemini CLI를 한 창에서 나란히 쓰는 데스크톱 앱입니다. 프로젝트 폴더 하나에 여러 실행 창을 열고, 창마다 대화와 설정을 따로 유지합니다.

## 준비물 {#requirements}
- macOS 14 이상. Windows는 Windows 10(버전 2004) 이상 또는 Windows 11입니다.
- 사용할 AI CLI. 앱은 CLI를 직접 실행하므로 먼저 설치하고 로그인해야 합니다. 쓰지 않을 CLI는 설치하지 않아도 됩니다.

| CLI | 설치 명령 |
|---|---|
| Claude Code | `npm install -g @anthropic-ai/claude-code` |
| Codex CLI | `npm install -g @openai/codex` |
| Gemini CLI | `npm install -g @google/gemini-cli` |

> [!tip]
> 앱을 연 뒤 {{ui:settings.nav.tools}}에서 어떤 CLI가 준비됐는지 보고, 설치 명령을 복사할 수 있습니다. 로그인은 {{ui:settings.nav.cli}} 탭에서 합니다. [설정](settings.md#components)을 보세요.

## 설치 {#install}
1. [다운로드 페이지](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)에서 내 운영체제용 파일을 받습니다.
2. macOS: `MightyClaude-macos.zip`을 풀고 `MightyClaude.app`을 응용 프로그램 폴더로 옮긴 뒤 엽니다.
3. Windows: zip을 폴더째 풀고 그 안의 `MightyClaude.exe`를 실행합니다. 자세한 내용은 [Windows에서 다른 점](windows.md#install)에 있습니다.

> [!warning]
> 이 앱은 Apple 공증을 받지 않았습니다. macOS가 처음 열 때 막으면 Finder에서 앱을 Control-클릭한 뒤 **열기**를 고르거나, 시스템 설정 → 개인정보 보호 및 보안에서 열기를 허용하세요.

## 업데이트 {#update}
앱은 하루 한 번, 앱을 열 때 새 버전이 있는지 확인합니다. 새 버전이 있으면 창 아래 상태 줄에 알림이 뜹니다. 그 알림을 누르면 설정이 열립니다.

1. {{ui:menu.settings}}({{kbd:⌘,}})을 열고 {{ui:settings.nav.about}} 탭으로 갑니다.
2. {{ui:settings.appUpdate.sectionTitle}}에서 {{ui:settings.appUpdate.checkButton}}을 누릅니다.
3. 새 버전이 있으면 {{ui:settings.appUpdate.downloadButton}} 단추를 누릅니다.
4. 준비가 끝나면 {{ui:settings.appUpdate.installButton}}을 누릅니다. 앱이 종료된 뒤 새 버전으로 바뀌고 다시 열립니다.

자동 확인을 끄려면 {{ui:settings.appUpdate.autoCheckToggle}}을 끄세요. 업데이트 파일은 서명을 확인한 뒤에만 설치합니다.

> [!note]
> AI CLI의 업데이트는 앱 업데이트와 따로 합니다. {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliUpdate.sectionTitle}} 항목을 쓰세요.

## 첫 실행 {#first-launch}
처음 열면 아직 워크스페이스가 없어서 시작 화면이 보입니다.

![[welcome]]

## 워크스페이스 열기 {#open-workspace}
워크스페이스는 프로젝트 폴더 하나입니다. 실행 창, 탭 배치, 대화 기록이 워크스페이스마다 따로 저장됩니다.

1. {{ui:layout.welcome.openProject}} 단추를 누르거나 {{kbd:⌘O}}를 누릅니다.
2. 작업할 폴더를 고릅니다.
3. 사이드바에 워크스페이스가 생깁니다. 이제 {{ui:menu.newClaudePane}}({{kbd:⌘N}})으로 첫 실행 창을 엽니다.

다른 폴더를 더 열 때도 메뉴 막대의 {{ui:menu.openProject}}나 {{kbd:⌘O}}를 씁니다. 사이드바에서 워크스페이스를 오른쪽 클릭하면 {{ui:menu.rename}}, {{ui:menu.showInFinder}}, {{ui:workspace.menu.remove}} 항목이 있습니다. 목록에서 제거해도 디스크의 프로젝트 파일은 그대로 남습니다.

## 언어 바꾸기 {#language}
1. {{ui:menu.settings}} → {{ui:settings.nav.general}}을 엽니다.
2. {{ui:settings.display.languageLabel}}에서 언어를 고릅니다. {{ui:settings.display.languageSystem}}은 Mac의 언어를 따릅니다.
3. 메뉴 막대는 앱을 다시 열어야 바뀝니다. 다른 화면이 바로 바뀌지 않아도 앱을 다시 열면 모두 바뀝니다.

## 도움말 열기 {#help}
이 도움말은 앱에서 바로 열 수 있습니다. 앱에서 고른 언어로 열립니다.

- Mac: 메뉴 막대의 {{ui:menu.help}}({{kbd:⌘?}})이나 {{ui:menu.settings}} → {{ui:settings.nav.about}}의 {{ui:settings.appInfo.openHelpButton}} 단추
- Windows: 사이드바 아래의 도움말 단추나 {{kbd:F1}}
- 폰: {{ui:phone.tabs.hosts}} 탭의 {{ui:phone.hosts.help}} 단추
