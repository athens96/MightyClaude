---
title: 에이전트 창
order: 3
section: agent-pane
---
에이전트 창은 Claude, Codex, Gemini 중 하나와 대화하는 실행 창입니다. 아래쪽 입력창에 요청을 쓰고, 위쪽에서 진행과 결과를 봅니다.

![[agent-basic]]

## 요청 보내기 {#send}
1. 입력창에 할 일을 적습니다.
2. {{kbd:Enter}}를 누르거나 보내기 단추를 누릅니다. 줄을 바꾸려면 {{kbd:⇧Enter}}를 누릅니다.
3. 실행 중에는 보내기 단추 옆에 중지 단추가 생깁니다. 멈추려면 그 단추를 누릅니다.

한글처럼 글자를 조합하는 중에 누른 {{kbd:Enter}}는 보내지 않고 조합만 끝냅니다.

![[composer]]

### 실행 중에 이어서 요청하기 {#queue}
작업이 도는 동안에도 다음 요청을 쓸 수 있습니다.
- {{kbd:Enter}}: 다음 요청으로 대기열에 넣습니다. 지금 작업이 끝나면 순서대로 실행합니다. 한 창에 16개까지 넣을 수 있습니다.
- {{kbd:⌘Enter}}: 실행 중인 Claude에 바로 전달합니다. 이 Mac의 Claude 창에서만 됩니다. 마이티 보기에는 주황색 {{ui:graph.block.steer}} 블록으로 보입니다.
- 대기열의 {{ui:queue.runNext}}을 누르면 첫 대기 요청을 지금 실행합니다.

작업을 중지하면 대기 중인 요청도 취소됩니다. 앱을 다시 열면 대기열은 비워집니다.

## 입력창 도구 {#composer}
입력창 아래 줄에는 왼쪽부터 첨부, 모델, 사고 강도, 권한, 추가 실행 설정(…)이 있습니다. 창이 좁으면 일부가 메뉴 하나로 접힙니다. 실행 중에는 바꿀 수 없고, 바꾼 값은 다음 요청부터 적용됩니다.

### 모델 {#model}
모델 칩을 누르면 {{ui:composer.label.runner}}(Claude, Codex, Gemini)와 {{ui:composer.label.model}} 항목을 고를 수 있습니다. 목록이 맞지 않으면 {{ui:composer.model.refresh}}을 누르세요. 에이전트를 바꾸면 대화가 새로 시작됩니다.

### 사고 강도 {#effort}
{{ui:composer.effort.label}}는 모델이 얼마나 깊이 생각할지 정합니다. Auto, Low, Medium, High, XHigh, Max 중에서 고릅니다. 모델이 지원하는 단계만 보입니다. Gemini에는 없습니다.

### 작업 권한 {#permission}
권한은 에이전트가 묻지 않고 할 수 있는 일의 범위입니다.

| 에이전트 | 고를 수 있는 권한 |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Gemini | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude의 Always ask와 Codex의 {{ui:permission.label.onRequest}} 권한은 추가 권한이 필요할 때 창 안에서 승인을 묻습니다. [승인과 질문](approvals.md#permission)을 보세요.
- Auto mode는 설치된 Claude CLI가 지원할 때만 보입니다.
- Bypass와 {{ui:permission.label.fullAccess}} 권한은 프로젝트 밖의 파일과 명령에도 접근합니다. 꼭 필요할 때만 쓰세요.

### 추가 실행 설정 {#run-settings}
… 단추를 누르면 {{ui:settings.run.title}}이 열립니다.
- Claude: {{ui:settings.run.maxTurnsLabel}}와 {{ui:settings.run.maxBudgetLabel}}. 비워 두면 한도를 두지 않습니다.
- Codex: {{ui:settings.run.webSearchLabel}} 설정과 {{ui:settings.run.shellNetworkToggle}}. 입력창의 Fast 칩은 지원하는 모델과 계정에서 더 빠르게 답하지만 사용량을 더 씁니다.

값을 바꾼 뒤 {{ui:settings.run.applyButton}}을 누릅니다.

![[composer-settings]]

### 파일과 이미지 첨부 {#attachments}
클립 단추를 누르거나, 파일을 입력창에 끌어 놓거나, {{kbd:⌘V}}로 이미지를 붙여 넣습니다.
- 최대 8개, 파일당 5 MiB, 합계 8 MiB까지 첨부합니다.
- 보내기 전에 미리 보고 하나씩 뺄 수 있습니다. 첨부만 보내도 됩니다.
- 터미널 창에는 첨부할 수 없습니다.

### 슬래시 명령 {#slash}
입력창에 `/`를 치면 명령 목록이 나옵니다. {{kbd:↑}}{{kbd:↓}}로 고르고 {{kbd:Enter}}나 {{kbd:Tab}}으로 넣고, {{kbd:Esc}}로 닫습니다.

| 명령 (Claude 기준) | 하는 일 |
|---|---|
| `/model` | {{ui:slash.builtin.model}} |
| `/permissions` | {{ui:slash.builtin.permission}} |
| `/clear` | {{ui:slash.builtin.newConversation}} |
| `/cost`, `/usage` | {{ui:slash.builtin.usage}} |
| `/plugin` | {{ui:slash.builtin.plugin}} |
| `/config` | {{ui:slash.builtin.settings}} |
| `/rename` | {{ui:slash.builtin.rename}} |
| `/help` | {{ui:slash.builtin.help}} |

Codex와 Gemini는 이름이 조금 다릅니다(예: Codex `/new`, `/approvals`, `/status`). 목록에 없는 `/이름`은 CLI의 스킬·사용자 명령·플러그인 명령으로 그대로 전달됩니다.

### 컨텍스트와 사용량 {#session-info}
보내기 단추 옆의 동그란 고리는 대화가 컨텍스트를 얼마나 썼는지 보여 줍니다. 누르면 {{ui:composer.sessionInfo.contextUsage}}, 입력·출력·캐시 토큰, 비용, 쓴 모델, 세션 ID를 봅니다. CLI가 알려 준 값만 보여 주고, 모르는 값은 추정하지 않습니다.

![[session-info]]

## 기본 보기와 마이티 보기 {#views}
Claude와 Codex 창 머리에는 {{ui:graph.view.default}} | {{ui:graph.view.mighty}} 전환이 있습니다. 바꿔도 대화와 입력 중인 글은 그대로입니다. 창마다 따로 기억합니다. Gemini 창은 기본 보기만 있습니다.
- {{ui:graph.view.default}}: 대화를 위에서 아래로 읽는 기록입니다.
- {{ui:graph.view.mighty}}: 요청, 하위 에이전트, 백그라운드 작업, 결과를 블록 다이어그램으로 보여 줍니다.

### 마이티 다이어그램 {#diagram}
요청마다 블록이 하나 생기고, 하위 에이전트와 백그라운드 작업은 그 아래로 갈라져 나옵니다. 끝나면 {{ui:graph.block.result}} 카드가 붙습니다.
- 빈 곳을 끌거나 스크롤해서 다이어그램을 움직입니다. 확대·축소 단추로 50~150%로 볼 수 있습니다.
- 블록을 누르면 그 블록 안만 스크롤합니다. 빈 곳을 누르거나 {{kbd:Esc}}를 누르면 선택이 풀립니다.
- 블록 오른쪽 아래 모서리를 끌면 크기가 바뀝니다. 오른쪽 클릭하면 {{ui:graph.block.resetSize}}가 있습니다. 크기는 세션마다 저장됩니다.
- 맨 위까지 스크롤하면 {{ui:graph.history.load}}로 예전 요청을 10개씩 불러옵니다. 불러온 기록은 보기 전용입니다.
- 머리의 퍼즐 단추로 설치된 플러그인과 마켓플레이스를 엽니다.

![[agent-mighty]]

![[agent-mighty-overview]]

### 타임라인 {#timeline}
마이티 보기 위쪽의 {{ui:graph.view.diagram}} | {{ui:graph.view.timeline}} 전환으로 요청을 시간 순서의 목록으로 볼 수 있습니다.

![[agent-timeline]]

### 결과 카드 {#result}
요청이 끝나면 {{ui:graph.block.result}} 카드에 결과가 정리됩니다. 결과에 파일이 나오면 {{ui:graph.resultFiles.openButton}} 단추로 목록을 엽니다. 블록 안의 파일 경로를 누르면 미리보기가 뜹니다. 가장 최근 결과 카드는 창 크기에 맞춰지고, 크기를 직접 바꿨다면 {{ui:graph.result.fitToWindow}} 단추가 생깁니다.

![[result-card]]
