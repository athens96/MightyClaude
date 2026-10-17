---
title: 원격 연결 (Tailscale)
order: 10
section: remote
---
Mac 두 대의 Mighty Claude를 [Tailscale](https://tailscale.com/kb/1017/install)로 이으면, 한쪽 Mac에서 다른 Mac의 워크스페이스를 열고 그 컴퓨터에서 Claude·Codex·Antigravity와 명령을 실행할 수 있습니다. 파일과 CLI 로그인은 실행하는 컴퓨터에 그대로 있고, 프로젝트 파일을 복사하거나 동기화하지 않습니다. {{platform:mac}} Windows 앱에는 아직 없습니다.

## 준비 {#prepare}
- 두 Mac에 Mighty Claude와 Tailscale을 설치하고 같은 tailnet에 로그인합니다.
- 일을 맡길 Mac(호스트)에 쓸 CLI를 설치하고 로그인합니다. 원격 실행은 호스트의 CLI와 계정을 씁니다.
- 공유기 포트 포워딩, Tailscale Serve·Funnel은 필요하지 않습니다. tailnet 정책이 두 기기 사이의 연결을 막지 않는지만 확인하세요.

## 이 컴퓨터 공유 {#share}
호스트 Mac에서:
1. {{ui:menu.settings}} → {{ui:settings.remote.navTitle}} 탭을 엽니다.
2. {{ui:settings.remote.shareTitle}} 항목에서 공유할 로컬 워크스페이스를 고릅니다. 포트는 보통 그대로 둡니다(43137).
3. {{ui:settings.remote.startSharing}} 단추를 누릅니다. 이 Mac의 Tailscale 주소와 연결 키가 나타납니다. {{ui:settings.remote.copyButton}} 단추로 복사해 연결할 쪽에 알려 줍니다.

공유 중에는 {{ui:settings.remote.allowFullAccess}} 스위치가 보입니다. 꺼져 있으면(기본) 연결한 컴퓨터는 원격 명령 창을 쓸 수 없고, 에이전트를 전체 권한으로 실행할 수 없습니다. 그런 요청은 실행 전에 거절되고 그 창에 이유가 보입니다. 공유를 시작할 때마다 다시 꺼집니다.

공유는 이 Mac의 Tailscale 주소에서만 연결을 받습니다. 앱을 다시 열면 공유는 항상 꺼진 상태로 시작합니다. {{ui:settings.remote.stopSharing}} 단추를 누르면 원격 실행이 멈추고 연결 키는 폐기됩니다. 다시 공유하면 새 키를 알려 줘야 합니다.

> [!warning]
> 연결 키를 가진 기기는 공유한 워크스페이스에서 작업과 명령을 실행할 수 있습니다. 권한 모드에 따라 호스트 사용자의 권한으로 폴더 밖에도 접근할 수 있습니다. 키는 신뢰하는 기기에만 알려 주세요.

## 다른 컴퓨터 연결 {#connect}
일을 시킬 Mac에서:
1. {{ui:menu.settings}} → {{ui:settings.remote.navTitle}} 탭을 엽니다. 사이드바 머리글의 네트워크 단추, {{ui:workspace.addPane}} 메뉴의 {{ui:remote.menu.connect}} 항목도 같은 곳을 엽니다.
2. {{ui:settings.remote.connectTitle}} 항목에 호스트의 주소(Tailscale IP 또는 MagicDNS 이름)와 연결 키를 넣고 {{ui:settings.remote.connectButton}} 단추를 누릅니다. 포트를 바꾸지 않았다면 주소만 넣으면 됩니다.
3. 연결한 컴퓨터 아래에 공유된 워크스페이스가 보입니다. {{ui:settings.remote.openButton}} 단추를 누르면 사이드바에 원격 워크스페이스로 추가됩니다.

원격 워크스페이스는 사이드바에서 컴퓨터 아이콘과 호스트 이름으로 구분되고, 워크스페이스 머리글과 아래 상태 막대에 연결 상태가 보입니다. 연결 키는 macOS Keychain에만 저장하고 워크스페이스 기록에는 넣지 않습니다. 앱을 다시 열면 연결은 저장된 상태로 남아 있다가, 원격 창에서 처음 보내거나 {{ui:settings.remote.refreshButton}} 단추를 누를 때 다시 연결합니다. 이때 macOS가 Keychain 접근을 물으면 "항상 허용"을 고르세요. 앱이 같은 서명으로 배포되므로 이 선택은 업데이트 뒤에도 유지됩니다.

## 원격 창 쓰기 {#use}
- 에이전트 창은 로컬 창처럼 쓰면 됩니다. 모델·권한·사고 강도·Fast 모드는 그대로 호스트로 전해지고, 호스트 CLI가 지원하지 않는 설정은 실행 전에 알려 줍니다.
- 승인·질문·계획 카드는 이 앱에 나타나고, 답하면 호스트의 실행으로 전해집니다. 실행 중인 Claude 요청에 {{kbd:⌘Enter}}로 끼워 넣을 수도 있습니다.
- {{ui:graph.view.default}} 보기와 {{ui:graph.view.mighty}} 보기({{ui:graph.view.diagram}}·{{ui:graph.view.timeline}}·{{ui:graph.view.structure}})가 로컬 실행과 똑같이 그려집니다.
- {{ui:remote.menu.newCommand}} 창은 명령마다 호스트의 공유 폴더에서 새 셸을 엽니다. 대화형 프로그램과 비밀번호 입력은 지원하지 않습니다.
- 파일을 첨부하면 내용 자체가 호스트로 갑니다. 한 번에 8개, 파일당 5 MiB, 합계 8 MiB까지입니다.

원격 워크스페이스에서는 다음을 쓸 수 없습니다. 모두 이 Mac의 디스크나 네트워크를 읽기 때문입니다.
- [마이티 오케스트레이션](delegation.md)(자식 작업 트리)
- 파일 창과 브라우저 창
- 프로젝트 파일을 읽는 [스타일](styles.md), 상태 줄, 이전 세션 이어가기

원격 워크스페이스는 [휴대폰 원격](phone.md)에 보이지 않습니다.

## 연결이 끊겼을 때 {#disconnect}
- 네트워크가 끊기면 실행 중인 원격 창은 오류로 끝나고 연결 목록에 이유가 표시됩니다. 호스트는 확인이 끊긴 실행을 약 30초 안에 스스로 멈춥니다.
- {{ui:settings.remote.disconnectButton}} 단추는 그 연결의 실행을 멈추고 연결 정보는 남깁니다. 다시 쓰려면 {{ui:settings.remote.refreshButton}} 단추를 누르거나 원격 창에서 보냅니다.
- 호스트가 공유를 다시 시작했다면 새 키가 필요합니다. 같은 주소로 새 키를 넣어 연결하면 저장된 연결의 키가 바뀝니다.
- 호스트 Mac이 잠들거나 Mighty Claude가 종료되면 새 작업을 보낼 수 없습니다.
