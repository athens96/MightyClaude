---
title: 터미널·파일·브라우저 창
order: 6
section: terminal-files-browser
---
에이전트 창 옆에 터미널, 파일, 브라우저 창을 함께 열어 둘 수 있습니다.

## 터미널 창 {#terminal}
워크스페이스 폴더에서 열리는 실제 터미널입니다.
1. {{ui:menu.addTerminalPane}}({{kbd:⌘T}})를 누르거나 {{ui:workspace.addPane}} → {{ui:workspace.newTerminal}} 항목을 고릅니다.
2. 명령을 직접 입력합니다.

셸이 끝나면 {{ui:terminal.restart}}을 누릅니다. 예전 명령 기록은 실행 창 메뉴의 {{ui:pane.menu.terminalHistory}}에서 봅니다. 앱을 끄면 터미널은 다시 열리지 않습니다.

![[terminal-pane]]

### 에이전트 터미널 {#agent-terminal}
Claude나 Codex가 사용자가 봐야 하는 명령(개발 서버, 로그인 등)을 실행하면 에이전트 창 오른쪽에 전용 터미널 창이 생깁니다. 실행 중인 명령에 직접 입력할 수 있습니다. 창을 닫아도 명령은 앱을 끌 때까지 계속 돌고, 창 머리의 {{ui:agentTerminal.terminalPane.open}} 단추로 다시 엽니다.

## 파일 창 {#files}
워크스페이스의 파일을 둘러보는 읽기 전용 창입니다. 파일을 고치지는 않습니다.
1. {{ui:menu.showFiles}}({{kbd:⇧⌘E}})를 누르거나 워크스페이스 머리의 폴더 단추를 누릅니다.
2. 왼쪽 목록에서 파일을 고릅니다. {{ui:files.tree.filter}}로 이름을 찾고, 방향키와 {{kbd:Return}}으로 움직입니다.
3. 오른쪽에서 내용을 봅니다.

| 파일 | 보이는 방식 |
|---|---|
| 소스·텍스트 | 줄 번호와 색 강조. 큰 파일은 처음 1 MB만 보입니다. |
| Markdown | {{ui:files.markdown.rendered}}와 {{ui:files.markdown.source}} 보기를 전환합니다. |
| 이미지·PDF | {{ui:files.image.fit}}, {{ui:files.image.actualSize}}, {{ui:files.image.zoomIn}}, {{ui:files.image.zoomOut}}. PDF는 첫 쪽만 보입니다. |
| 그 밖 | {{ui:files.preview.unsupported}}. {{ui:menu.showInFinder}}로 엽니다. |

워크스페이스 밖의 파일은 보이지 않습니다. 파일이 바뀌었으면 {{ui:files.tree.refresh}}을 누릅니다.

![[files-pane]]

## 브라우저 창 {#browser}
앱 안에서 웹 페이지를 여는 창입니다. 실험 기능이라 처음에는 꺼져 있습니다.
1. {{ui:menu.settings}} → {{ui:settings.nav.general}}에서 {{ui:settings.display.browserToggle}}를 켭니다.
2. 앱을 다시 엽니다.
3. {{ui:workspace.addPane}} → {{ui:browser.newTab}}을 고릅니다.
4. 주소를 입력하고 {{ui:browser.back}}, {{ui:browser.forward}}, {{ui:browser.reload}}을 씁니다.

로그인 상태는 워크스페이스마다 따로 저장됩니다.

> [!note]
> 브라우저 엔진은 Apple Silicon Mac에서만 동작합니다. 엔진이 들어 있지 않은 빌드에서는 창에 안내가 보입니다.

### 에이전트가 링크를 열 때 {#agent-links}
에이전트가 웹 페이지를 열려고 하면 창 안에 {{ui:agentTerminal.urlOpen.dialogTitle}} 카드가 뜹니다.
- {{ui:agentTerminal.urlOpen.inAppButton}} 또는 {{ui:agentTerminal.urlOpen.externalButton}} 단추를 고릅니다.
- {{ui:agentTerminal.urlOpen.rememberToggle}}을 켜면 다음부터 묻지 않습니다.
- 30초 안에 고르지 않으면 앱에서 엽니다. 브라우저 창이 꺼져 있으면 시스템 브라우저로 엽니다.

이 선택은 {{ui:settings.nav.general}}의 {{ui:agentTerminal.urlOpen.settingTitle}}에서 바꿉니다: {{ui:agentTerminal.urlOpen.settingAsk}}, {{ui:agentTerminal.urlOpen.settingInApp}}, {{ui:agentTerminal.urlOpen.settingExternal}}.
