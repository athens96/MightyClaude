---
title: 휴대폰 원격
order: 8
section: phone
---
휴대폰 앱으로 밖에서도 Mac의 Mighty Claude를 보고 다룰 수 있습니다. 작업 상태를 보고, 요청을 보내고, 승인과 질문에 답합니다.

Mac과 휴대폰은 릴레이 서버를 거쳐 연결됩니다. 내용은 처음부터 끝까지 암호화되어 릴레이는 내용을 볼 수 없습니다. 공유기 포트를 열거나 VPN을 쓸 필요가 없습니다.

## 휴대폰 앱 받기 {#get-app}
[다운로드 페이지](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)에서 Android 앱(APK)을 받아 설치합니다.

## 연결하기 {#pair}
Mac에서:
1. {{ui:menu.settings}} → {{ui:settings.nav.mobile}} 탭을 엽니다.
2. {{ui:settings.mobileRemote.relayLabel}} 주소는 비워 두면 기본 릴레이를 씁니다. 직접 운영하는 릴레이가 있으면 주소를 넣고 {{ui:settings.mobileRemote.applyButton}}을 누릅니다.
3. {{ui:settings.mobileRemote.allowToggle}}을 켭니다. QR 코드가 나타납니다.

휴대폰에서:
1. 앱을 열고 {{ui:phone.tabs.hosts}} 탭의 {{ui:phone.hosts.addHost}}를 누릅니다. 처음 열면 연결 안내가 먼저 보입니다.
2. {{ui:phone.pair.mode.qr}}으로 Mac의 QR 코드를 찍습니다. 카메라를 쓸 수 없으면 Mac에서 {{ui:settings.mobileRemote.copyLinkButton}}를 눌러 링크를 보내고 {{ui:phone.pair.mode.manual}}에 넣습니다.
3. {{ui:phone.pair.connect}}를 누르면 연결됩니다.

![[settings-mobile]]

연결된 휴대폰은 Mac의 {{ui:settings.mobileRemote.connectedDevicesTitle}}에 보입니다. 휴대폰을 더 이상 쓰지 않으면 {{ui:settings.mobileRemote.revokeRowButton}}를 누릅니다.

> [!warning]
> {{ui:settings.mobileRemote.regenerateKeyButton}}를 누르면 연결된 휴대폰을 모두 다시 연결해야 합니다. QR 코드나 키가 남에게 보였을 때 쓰세요.

## 휴대폰에서 할 수 있는 일 {#use}
휴대폰 앱에는 탭이 네 개 있습니다.

| 탭 | 내용 |
|---|---|
| {{ui:phone.tabs.dashboard}} | {{ui:phone.dashboard.title}}: 실행 중, 응답 대기, 완료 개수와 세션 카드 |
| {{ui:phone.tabs.sessions}} | 모든 워크스페이스와 세션. 세션을 열어 대화를 보고 요청을 보냅니다. |
| {{ui:phone.tabs.alerts}} | 답이 필요한 승인·질문과 끝난 작업 |
| {{ui:phone.tabs.hosts}} | 연결한 Mac과 연결 상태 |

세션 안에서는 요청 보내기와 중지, 모델·권한·사고 강도 바꾸기, 슬래시 명령, 파일 첨부, 워크스페이스 파일 보기(읽기 전용)를 할 수 있습니다.

## 승인과 질문 {#approvals}
에이전트가 승인을 요청하거나 질문하면 {{ui:phone.tabs.alerts}} 탭에 개수가 표시됩니다.
- 권한 요청: {{ui:permission.allow}} 또는 {{ui:permission.deny}}를 누릅니다.
- 질문: 답을 고르고 {{ui:phone.questionnaire.submit}}를 누릅니다.
- 계획 카드: Mac과 같은 단추({{ui:plan.card.approveAuto}}, {{ui:plan.card.revise}} 등)로 답합니다.

휴대폰 알림(푸시)은 보내지 않습니다. 앱을 열어 확인하세요.

## 화면 보기 {#screen}
휴대폰에서 Mac 화면을 보고, 허용하면 조작할 수도 있습니다. {{ui:badge.beta}} 기능이며 지금은 Android 앱에만 있습니다.
1. Mac의 {{ui:settings.nav.mobile}} 탭 아래 {{ui:settings.screenShare.sectionTitle}}에서 휴대폰마다 {{ui:settings.screenShare.grantLabel}}을 {{ui:settings.screenShare.grantView}} 또는 {{ui:settings.screenShare.grantControl}}으로 정합니다. 새 휴대폰은 {{ui:settings.screenShare.grantNone}}으로 시작합니다.
2. {{ui:settings.screenShare.permissions.openButton}}으로 화면 기록, 손쉬운 사용 등 Mac 권한을 허용합니다.
3. 휴대폰에서 {{ui:phone.screenShare.open}}를 누르고 {{ui:phone.screenShare.mode.view}} 또는 {{ui:phone.screenShare.mode.control}}을 고릅니다.

조작을 처음 허용할 때는 두 화면에 보이는 지문이 같은지 확인합니다. 조작을 시작할 때마다 휴대폰이 생체 인증이나 PIN을 묻습니다.

> [!tip]
> Mac에서 {{kbd:⌃⌥⌘K}}를 누르면 모든 원격 화면 세션이 바로 멈춥니다. 조작은 10분, 보기는 30분 동안 아무 입력이 없으면 저절로 끝납니다.

## 릴레이 직접 운영하기 {#own-relay}
기본 릴레이 대신 직접 릴레이를 운영할 수 있습니다. 저장소의 `relay/` 폴더를 서버에 배포한 뒤 그 주소(`wss://…`)를 Mac의 {{ui:settings.mobileRemote.relayLabel}}에 넣으세요.
