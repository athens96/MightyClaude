---
title: 승인·질문·로그인 복구
order: 5
section: approvals
---
에이전트가 일하다가 사용자의 판단이 필요하면 실행 창에 카드가 뜹니다. 카드에 답하면 작업이 이어집니다. 작업 현황과 데스크톱 펫, 휴대폰에서도 같은 요청을 볼 수 있습니다.

## 권한 승인 {#permission}
에이전트가 권한 밖의 일(명령 실행, 파일 수정 등)을 하려 하면 승인 카드가 뜹니다.

![[permission-card]]

1. 카드에서 무엇을 하려는지 읽습니다. 명령이나 파일 경로가 상자에 보이고, 정확한 입력은 원본 JSON에서 봅니다.
2. {{ui:permission.allowOnce}} 또는 {{ui:permission.deny}}를 누릅니다.

승인은 이 요청 한 번에만 적용됩니다. 앞으로도 계속 허용하는 규칙은 만들지 않습니다.

> [!note]
> 승인 카드는 이 Mac의 Claude 창(Always ask 등)과 Codex의 {{ui:permission.label.onRequest}} 권한에서 뜹니다. 다른 권한에서는 CLI 설정에 따라 허용되거나 막힙니다.

## 질문에 답하기 {#questions}
Claude가 선택지를 주고 물으면 {{ui:phone.questionnaire.title}} 카드가 뜹니다.

![[question-card]]

1. 질문을 읽고 답을 고릅니다. 여러 개를 고를 수 있는 질문도 있습니다.
2. 알맞은 답이 없으면 {{ui:phone.questionnaire.custom}}에 직접 씁니다.
3. 질문이 여럿이면 {{ui:phone.questionnaire.next}} 단추로 넘어갑니다.
4. 모두 답한 뒤 {{ui:phone.questionnaire.submit}}를 누릅니다. 고르기만 해서는 보내지지 않습니다.

## 로그인이 끊겼을 때 {#login-recovery}
CLI 로그인이 만료되어 요청이 실패하면 입력창 위에 로그인 복구 카드가 뜹니다.
1. {{ui:loginRecovery.loginButton}}을 누릅니다.
2. 브라우저가 열리면 로그인을 승인합니다. 브라우저에 코드가 보이면 카드에 붙여 넣고 {{ui:loginRecovery.codeSubmit}}을 누릅니다.
3. 로그인이 끝나면 실패한 요청을 저절로 다시 보냅니다.

이미 다른 곳에서 로그인했다면 {{ui:loginRecovery.resendButton}}를 누르세요. 브라우저 로그인이 어려우면 {{ui:loginRecovery.terminalButton}}을 씁니다.

> [!tip]
> {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.autoLoginToggle}}을 켜 두면 로그인이 끊긴 순간 바로 다시 로그인을 시작합니다.

## 백그라운드 작업 {#background}
Claude는 오래 걸리는 명령이나 하위 에이전트를 백그라운드에서 돌리기도 합니다. 이때는 Claude의 답이 끝나도 일이 남아 있습니다.
- 입력창 위에 턴이 끝났지만 백그라운드 작업이 실행 중이라는 줄이 보입니다. {{ui:plan.background.show}}를 누르면 작업별 종류, 상태, 걸린 시간을 봅니다.
- 마이티 보기에는 {{ui:graph.block.task}} 블록으로 보입니다.
- 이때 보낸 요청은 백그라운드 작업이 끝난 뒤 시작합니다. 바로 끼워 넣으려면 {{kbd:⌘Enter}}를 누릅니다.

![[background-work]]
