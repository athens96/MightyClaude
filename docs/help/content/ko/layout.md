---
title: 화면 구성
order: 2
section: layout
---
창은 왼쪽 사이드바와 오른쪽 작업 영역으로 나뉩니다. 작업 영역에는 실행 창을 탭으로 겹치거나 좌우·상하로 나눠 놓을 수 있습니다.

![[overview]]

## 사이드바 {#sidebar}
사이드바에는 위에서부터 다음이 있습니다.
- {{ui:phone.dashboard.title}}: 모든 워크스페이스의 실행 창 상태를 한곳에서 봅니다.
- {{ui:sidebar.searchPlaceholder}}: {{kbd:⌘K}}를 누르면 검색칸으로 바로 갑니다.
- {{ui:sidebar.workspacesHeader}} 목록: 워크스페이스를 누르면 그 워크스페이스로 바뀝니다. 옆의 화살표를 누르면 실행 창 목록을 펼치거나 접습니다.
- 아래쪽: 화면 테마 단추(다크·라이트)와 설정 단추.

워크스페이스 목록을 펼치면 마지막 줄에 {{ui:workspace.addPane}} 단추가 있습니다. 실행 창 줄을 오른쪽 클릭하면 {{ui:menu.rename}}, {{ui:menu.closePane}} 항목이 있습니다.

### 사이드바 접기 {#sidebar-collapse}
작업 영역을 넓게 쓰려면 사이드바를 접습니다.
1. 사이드바 위쪽의 접기 단추를 누르거나 {{kbd:⌃⌘S}}를 누릅니다. 메뉴 막대의 보기 메뉴에도 {{ui:sidebar.collapse}} 항목이 있습니다.
2. 다시 펼칠 때도 같은 단추나 {{kbd:⌃⌘S}}를 씁니다({{ui:sidebar.expand}}).

접은 상태는 앱을 다시 열어도 유지됩니다. 접힌 상태에서 {{kbd:⌘K}}를 누르면 사이드바가 펼쳐지고 검색칸으로 갑니다.

![[sidebar-collapsed]]

## 작업 현황 {#dashboard}
사이드바 맨 위의 {{ui:phone.dashboard.title}}을 누르면 모든 워크스페이스의 실행 창을 상태 순으로 모아 봅니다.
- 위쪽 칸에서 {{ui:phone.dashboard.stat.running}}, {{ui:phone.dashboard.stat.waiting}}, {{ui:phone.dashboard.stat.done}} 개수를 봅니다.
- {{ui:dashboard.usage.title}} 카드에서 계정 사용량을 봅니다. 같은 계정을 쓰는 다른 앱·세션과 함께 쓰는 한도입니다.
- 워크스페이스마다 {{ui:files.pane.title}} 단추와 {{ui:workspace.addPane}} 메뉴가 있습니다.
- 카드를 누르면 그 실행 창으로 갑니다. 워크스페이스나 실행 창을 고르면 작업 현황에서 나옵니다.

![[dashboard]]

## 실행 창 추가 {#add-pane}
실행 창은 AI 에이전트(Claude, Codex, Gemini), 터미널, 브라우저, 파일 중 하나입니다.

1. 사이드바의 {{ui:workspace.addPane}} 단추를 누릅니다.
2. 만들 창을 고릅니다: Claude·Codex·Gemini 실행 창, {{ui:workspace.newTerminal}}, {{ui:browser.newTab}}. 다른 폴더는 같은 메뉴의 {{ui:menu.openProject}}로 엽니다.
3. 새 AI 실행 창은 같은 에이전트로 가장 최근에 쓴 창의 모델·사고 강도·권한·보기 방식을 이어받습니다. 대화는 새로 시작합니다.

단축키: {{ui:menu.newClaudePane}}은 {{kbd:⌘N}}, {{ui:menu.addTerminalPane}} 항목은 {{kbd:⌘T}}, {{ui:menu.showFiles}} 항목은 {{kbd:⇧⌘E}}입니다.

> [!note]
> Codex와 Gemini 실행 창에는 {{ui:badge.beta}} 표시가 붙습니다.

### 이전 세션 이어가기 {#resume}
Claude나 Codex 창을 추가할 때 이 폴더에 이전 세션이 있으면 고르는 창이 뜹니다.
1. 새 대화는 {{ui:resume.choice.startNew}}, 이전 대화는 {{ui:resume.choice.resume}} 단추를 누릅니다.
2. {{ui:resume.title}} 목록에서 세션을 고릅니다. {{ui:resume.search}}으로 찾을 수 있습니다.
3. 새 창이 그 세션을 이어갑니다.

![[resume-choice]]

![[resume-list]]

## 탭과 분할 {#tabs-splits}
- 탭을 끌어 다른 그룹 가운데에 놓으면 탭으로 합쳐집니다({{ui:layout.drop.merge}}).
- 그룹의 왼쪽·오른쪽·위·아래 가장자리에 놓으면 그쪽으로 나뉩니다.
- 나눈 경계선을 끌면 크기가 바뀌고, 두 번 클릭하면 반반으로 나뉩니다.
- 끌던 중 {{kbd:Esc}}를 누르면 취소됩니다.
- 탭을 오른쪽 클릭하면 {{ui:menu.rename}}, {{ui:menu.focusPane}}, {{ui:menu.closeTab}} 항목이 있습니다.

배치는 워크스페이스마다 저장됩니다. 워크스페이스를 바꿔도 각자의 배치가 그대로 남습니다.

### 실행 창 메뉴 {#pane-menu}
실행 창 머리의 … 단추에는 다음이 있습니다.
- {{ui:menu.rename}}
- {{ui:menu.focusPane}}: 이 창만 크게 봅니다. 다시 누르면 {{ui:pane.menu.restoreLayout}}로 돌아갑니다.
- {{ui:pane.menu.copyLog}}
- {{ui:pane.menu.newConversation}}: 다음 요청부터 이전 대화를 잇지 않습니다.
- {{ui:menu.closePane}}

## 단축키 {#shortcuts}
| 단축키 | 동작 |
|---|---|
| {{kbd:⌘O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{ui:menu.settings}} |
| {{kbd:Enter}} | 요청 보내기 |
| {{kbd:⇧Enter}} | 줄바꿈 |
| {{kbd:⌘Enter}} | 실행 중인 Claude에 바로 전달 |
| {{kbd:Esc}} | 시트 닫기, 슬래시 목록 닫기 |
