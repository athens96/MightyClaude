---
title: 설정
order: 8
section: settings
---
{{ui:menu.settings}}({{kbd:⌘,}})이나 사이드바 아래의 설정 단추로 엽니다. 왼쪽에서 탭을 고르고, {{ui:settings.closeButton}} 단추나 {{kbd:Esc}}로 닫습니다. 마지막에 본 탭을 기억합니다.

## 일반 {#general}
{{ui:settings.nav.general}} 탭의 {{ui:settings.display.sectionTitle}}에서 앱의 모양을 바꿉니다.
- {{ui:settings.display.themeLabel}}: {{ui:settings.display.themeDarkMac}} 또는 {{ui:settings.display.themeLightMac}}. 사이드바 아래 단추로도 바꿉니다.
- {{ui:settings.display.languageLabel}}: 앱의 언어. [언어 바꾸기](getting-started.md#language)를 보세요.
- {{ui:settings.display.statusLineToggle}}: Claude 설정의 상태 줄 명령을 실행해 입력창 아래에 보여 줍니다.
- {{ui:settings.display.browserToggle}}: 브라우저 창을 켭니다. 앱을 다시 열어야 적용됩니다.
- {{ui:settings.delegation.toggle}}: 기본으로 켜져 있습니다. Claude 창이 자기 worktree를 가진 자식 창을 열 수 있고, 실행 중인 Claude 창에서 {{kbd:⌘Enter}}를 누르면 요청이 새 자식 창에서 실행됩니다. [마이티 오케스트레이션](delegation.md#enable)을 보세요.
- {{ui:agentTerminal.urlOpen.settingTitle}}: 에이전트가 연 링크를 어디서 열지 정합니다.

![[settings-general]]

### 글자 크기와 글꼴 {#text}
{{platform:mac}} 같은 탭의 {{ui:settings.text.sectionTitle}} 항목에서 앱 전체의 글자를 바꿉니다.
- {{ui:settings.text.sizeLabel}}: 85%에서 150% 사이에서 고릅니다. 대화, 입력창, 사이드바, 설정, 터미널이 함께 커지고 작아집니다.
- {{ui:settings.text.uiFontLabel}}: 사이드바, 메뉴, 단추, 설정 같은 앱 화면의 글꼴입니다. 이 Mac에 설치된 글꼴 가운데 고르고, {{ui:settings.text.fontSystem}}은 macOS의 기본 글꼴입니다.
- {{ui:settings.text.transcriptFontLabel}}: 에이전트의 답, 내가 보낸 말, 입력창, 마이티 카드와 보드의 본문 글꼴입니다. {{ui:settings.text.sameAsInterface}}로 두면 화면 글꼴을 따라갑니다.
- {{ui:settings.text.codeFontLabel}}: 코드 블록, 인라인 코드, 도구 출력과 명령 줄, 터미널 창의 글꼴입니다. 고정폭 글꼴만 고를 수 있고, 맨 위의 기본값은 지금까지와 같이 코드에 SF Mono, 터미널에 Menlo를 씁니다. 고른 글꼴이 없어지면 터미널은 Menlo로 돌아갑니다. 열려 있는 터미널에도 바로 적용됩니다. 경로, 버전, 개수 같은 앱 화면의 고정폭 글자는 시스템 고정폭 글꼴을 그대로 씁니다.
- 글꼴마다 아래에 그 글꼴로 쓴 한 줄 미리 보기가 나옵니다.
- {{ui:settings.text.reset}} 단추는 크기와 세 글꼴을 처음 상태로 되돌립니다.

보기 메뉴의 {{ui:menu.textBigger}}({{kbd:⌘=}}), {{ui:menu.textSmaller}}({{kbd:⌘-}}), {{ui:menu.textActualSize}}({{kbd:⌘0}})로도 크기를 바꿉니다. 바꾼 값은 바로 적용되고, 앱을 다시 열어도 그대로입니다.

## 모델 {#models}
{{ui:settings.nav.models}} 탭의 {{ui:settings.phaseModels.sectionTitle}}에서 작업 단계별로 쓸 모델을 이 Mac 전체에 정합니다. 단계는 {{ui:settings.phaseModels.phase.planning}}, {{ui:settings.phaseModels.phase.execution}}, {{ui:settings.phaseModels.phase.review}}, {{ui:settings.phaseModels.phase.subagents}}입니다.
- {{ui:settings.phaseModels.defaultOption}}으로 두면 CLI가 정합니다.
- {{ui:settings.phaseModels.paneModel}}은 실행 창의 모델 칩에서 고른 모델을 씁니다.
- 목록에 없는 모델 이름은 {{ui:settings.phaseModels.registeredTitle}}에 {{ui:settings.phaseModels.addButton}}합니다. 그 모델이 사고 강도를 지원하면 {{ui:settings.phaseModels.supportsEffortLabel}}을 켭니다.

![[settings-models]]

## 스타일 {#styles}
{{ui:settings.nav.styles}} 탭에서 {{ui:settings.styles.sectionTitle}} 목록을 관리합니다. 파일에서 스타일을 등록하고, 내용을 확인해 허용하거나, 허용을 거둡니다. 자세한 내용은 [스타일](styles.md#custom)에 있습니다.

![[settings-styles]]

## 구성 요소 {#components}
{{ui:settings.nav.tools}} 탭은 앱에 필요한 것이 준비됐는지 보여 줍니다.
- {{ui:settings.components.sectionTitle}}: Mac에서는 [{{ui:setup.title}}](getting-started.md#setup) 목록이 여기 있습니다. 에이전트 CLI, 기본 도구, 스타일 도구, Tailscale, gh·glab마다 {{ui:setup.status.installed}}, {{ui:setup.status.missing}}, {{ui:setup.status.outdated}}, {{ui:setup.status.installing}}, {{ui:setup.status.failed}} 중 하나가 보이고, 빠진 항목을 골라 {{ui:setup.button.install}}를 누르면 명령을 모두 확인한 뒤 앱의 터미널 창에서 설치합니다. {{ui:setup.button.recheck}}과 목록이 열려 있는 동안의 자동 확인은 CLI를 매번 다시 찾고, 너무 오래된 Claude Code에는 {{ui:setup.button.update}} 단추가 나옵니다. Windows에서는 Claude, Codex, Antigravity CLI와 필요한 플러그인의 상태가 {{ui:settings.components.statusInstalled}}, {{ui:settings.components.statusMissing}}, {{ui:settings.components.statusAttention}} 중 하나로 보이고, 설치 명령을 복사해 터미널에서 실행한 뒤 {{ui:settings.components.recheckButton}}을 누릅니다.
- {{ui:settings.toolkit.sectionTitle}}: 자주 쓰는 도구 목록입니다. 설치 전에 실행할 명령을 모두 보여 주고 {{ui:settings.toolkit.confirmTitle}}을 받습니다. 목록을 {{ui:settings.toolkit.exportButton}}하거나 {{ui:settings.toolkit.importButton}}할 수 있습니다.

![[settings-tools]]

## CLI {#cli}
{{ui:settings.nav.cli}} 탭에서 CLI의 상태, 로그인, 업데이트를 관리합니다.

### 로그인 {#cli-accounts}
{{ui:settings.cliAccounts.sectionTitle}}에서 CLI마다 로그인합니다.
1. {{ui:settings.cliAccounts.buttonLogin}}을 누릅니다. Claude는 {{ui:settings.cliAccounts.buttonLoginClaude}}과 {{ui:settings.cliAccounts.buttonLoginConsole}} 중에서 고릅니다.
2. 앱 안에 터미널 창이 열리고 CLI의 로그인이 시작됩니다. 브라우저에서 승인합니다.
3. 로그인이 끝나면 상태가 바뀝니다. 바뀐 계정은 다음 요청부터 쓰입니다.

계정을 바꾸려면 {{ui:settings.cliAccounts.buttonChange}}, 로그아웃하려면 {{ui:settings.cliAccounts.buttonLogout}}을 누릅니다. 로그인하려면 워크스페이스가 하나 이상 열려 있어야 합니다. {{ui:settings.cliAccounts.autoLoginToggle}}을 켜면 로그인이 끊겼을 때 자동으로 다시 로그인하고 실패한 요청을 다시 보냅니다.

### CLI 업데이트 {#cli-update}
{{ui:settings.cliUpdate.sectionTitle}}에서 설치된 CLI를 최신으로 바꿉니다.
- {{ui:settings.cliUpdate.updateButton}}: 지금 업데이트합니다.
- {{ui:settings.cliUpdate.autoUpdateToggle}}: 앱을 열 때, 그리고 6시간마다 확인합니다.
- {{ui:settings.cliUpdate.autoUpdatePluginsToggle}}: Claude와 Codex 플러그인도 함께 업데이트합니다.

CLI를 처음 설치한 방법(npm, Homebrew 등)을 그대로 씁니다.

![[settings-cli]]

## 모바일 리모트 {#mobile}
{{ui:settings.nav.mobile}} 탭에서 휴대폰을 연결하고 화면 보기를 허용합니다. [휴대폰 원격](phone.md)을 보세요.

## 원격 연결 {#remote}
{{ui:settings.remote.navTitle}} 탭에서 이 Mac의 워크스페이스를 Tailscale로 공유하거나, 다른 Mac이 공유한 워크스페이스에 연결합니다. [원격 연결 (Tailscale)](remote.md)을 보세요.

## 펫과 알림 {#companion}
{{ui:settings.nav.companion}} 탭에서 데스크톱 펫과 완료 알림을 정합니다.
- {{ui:companion.settings.enabled}}: 화면 위를 다니는 너구리 펫을 켭니다. 펫을 끌어 옮기고, 누르면 말풍선이 열리고 닫힙니다.
- {{ui:companion.settings.task}}: 말풍선에 요청과 지금 하는 일, 걸린 시간을 보여 줍니다. 말풍선에서 승인({{ui:permission.allowOnce}} / {{ui:permission.deny}})에 답할 수도 있습니다.
- {{ui:companion.settings.pet}}: 펫을 고릅니다. {{ui:companion.settings.import}}로 다른 펫을 더할 수 있습니다.
- {{ui:companion.settings.motion}}: 움직임을 줄입니다.
- {{ui:companion.settings.macNotifications}}: 요청이 성공적으로 끝나면 Mac 알림을 보냅니다. 알림을 누르면 그 실행 창이 열립니다. 알림에는 요청이나 결과 내용이 들어가지 않습니다.

![[settings-companion]]

![[companion-pet]]

## 앱 정보 {#about}
{{ui:settings.nav.about}} 탭에서는 앱을 업데이트하고 버전과 데이터 저장 위치를 봅니다.
- {{ui:settings.appUpdate.sectionTitle}}: [업데이트](getting-started.md#update)를 보세요.
- {{ui:settings.appInfo.stateLocationLabel}}: 앱의 설정과 기록이 있는 폴더입니다. {{ui:settings.appInfo.openFinderButton}}로 엽니다.

![[settings-about]]
