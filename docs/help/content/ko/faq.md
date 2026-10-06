---
title: 자주 묻는 질문과 문제 해결
order: 10
section: faq
---
## 요청을 보낼 수 없어요 {#cannot-send}
- 입력창 아래에 보내지 못하는 이유가 보입니다. CLI가 설치되지 않았거나 로그인이 필요하면 {{ui:menu.settings}} → {{ui:settings.nav.tools}}와 {{ui:settings.nav.cli}}를 확인하세요.
- 터미널 창에는 파일을 첨부할 수 없습니다. AI 실행 창을 쓰세요.
- 실행 중일 때 {{kbd:Enter}}는 바로 보내지 않고 대기열에 넣습니다. [실행 중에 이어서 요청하기](agent-pane.md#queue)를 보세요.

## 모델 목록에 원하는 모델이 없어요 {#model-missing}
1. 모델 칩을 열고 {{ui:composer.model.refresh}}을 누릅니다.
2. 그래도 없으면 {{ui:settings.nav.cli}} 탭에서 {{ui:settings.cliAccounts.resetModelsButton}} 단추를 누릅니다.
3. 직접 쓰는 모델 이름은 {{ui:settings.nav.models}} 탭의 {{ui:settings.phaseModels.registeredTitle}}에 더할 수 있습니다.

## 로그인이 자꾸 끊겨요 {#login}
{{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.autoLoginToggle}}을 켜세요. 끊긴 순간 다시 로그인을 시작하고, 실패한 요청을 다시 보냅니다. [로그인이 끊겼을 때](approvals.md#login-recovery)를 보세요.

## 한글 입력이 자모로 풀려요 {#korean-input}
메뉴 막대의 {{ui:menu.workspace}} 메뉴에서 {{ui:menu.reconnectInputMethod}} 항목을 누르세요. 그래도 계속되면 {{ui:menu.saveInputDiagnostics}}으로 진단 파일을 저장해 알려 주세요.

> [!warning]
> 앱이 실행 중일 때 앱 파일을 덮어쓰거나 앱을 두 개 띄우지 마세요. macOS 입력기 연결이 끊어져 한글이 자모로 풀릴 수 있습니다. 업데이트는 앱 안의 {{ui:settings.appUpdate.installButton}}을 쓰세요.

## 브라우저 창이 비어 있어요 {#browser-empty}
브라우저 창은 처음에 꺼져 있습니다. {{ui:settings.nav.general}}에서 {{ui:settings.display.browserToggle}}를 켜고 앱을 다시 여세요. [브라우저 창](terminal-files-browser.md#browser)을 보세요.

## 휴대폰이 연결되지 않아요 {#phone}
- Mac의 {{ui:settings.nav.mobile}}에서 {{ui:settings.mobileRemote.allowToggle}}이 켜져 있는지 봅니다.
- 상태가 릴레이 연결 중에서 멈춰 있으면 {{ui:settings.mobileRemote.reconnectButton}} 단추를 누릅니다.
- 휴대폰 {{ui:phone.tabs.hosts}} 탭에 {{ui:phone.hosts.reachability.unauthorized}}가 보이면 Mac의 키가 바뀐 것입니다. QR 코드로 다시 연결하세요.
- Mac이 잠자기 상태이면 연결되지 않습니다.

## 실행 창을 닫으면 대화가 사라지나요? {#history}
아니요. 대화 기록은 CLI의 세션으로 남습니다. 같은 폴더에서 Claude나 Codex 창을 새로 추가할 때 {{ui:resume.choice.resume}} 단추를 고르면 이어갈 수 있습니다. [이전 세션 이어가기](layout.md#resume)를 보세요.

## 내 데이터는 어디에 저장되나요? {#data}
앱의 설정과 기록은 이 컴퓨터에만 저장됩니다. 위치는 {{ui:settings.nav.about}} 탭의 {{ui:settings.appInfo.stateLocationLabel}}에서 봅니다. AI에게 보내는 요청은 각 CLI가 그 회사의 서비스로 보냅니다. 휴대폰 원격을 쓰면 내용이 암호화된 채로 릴레이를 지나갑니다.
