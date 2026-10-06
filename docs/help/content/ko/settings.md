---
title: 설정
order: 7
section: settings
---
{{ui:menu.settings}}({{kbd:⌘,}})이나 사이드바 아래의 설정 단추로 엽니다. 왼쪽에서 탭을 고르고, {{ui:settings.closeButton}} 단추나 {{kbd:Esc}}로 닫습니다. 마지막에 본 탭을 기억합니다.

## 일반 {#general}
{{ui:settings.nav.general}} 탭의 {{ui:settings.display.sectionTitle}}에서 앱의 모양을 바꿉니다.
- {{ui:settings.display.themeLabel}}: {{ui:settings.display.themeDarkMac}} 또는 {{ui:settings.display.themeLightMac}}. 사이드바 아래 단추로도 바꿉니다.
- {{ui:settings.display.languageLabel}}: 앱의 언어. [언어 바꾸기](getting-started.md#language)를 보세요.
- {{ui:settings.display.statusLineToggle}}: Claude 설정의 상태 줄 명령을 실행해 입력창 아래에 보여 줍니다.
- {{ui:settings.display.browserToggle}}: 브라우저 창을 켭니다. 앱을 다시 열어야 적용됩니다.
- {{ui:agentTerminal.urlOpen.settingTitle}}: 에이전트가 연 링크를 어디서 열지 정합니다.

![[settings-general]]

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
- {{ui:settings.components.sectionTitle}}: Claude, Codex, Gemini CLI와 필요한 플러그인의 상태입니다. {{ui:settings.components.statusInstalled}}, {{ui:settings.components.statusMissing}}, {{ui:settings.components.statusAttention}} 중 하나로 보입니다. 설치가 필요하면 설치 명령을 복사해 터미널에서 실행한 뒤 {{ui:settings.components.recheckButton}}을 누릅니다.
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
