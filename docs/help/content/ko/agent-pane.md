---
title: 에이전트 창
order: 3
section: agent-pane
---
에이전트 창은 Claude, Codex, Antigravity(Windows에서는 Gemini) 중 하나와 대화하는 실행 창입니다. 아래쪽 입력창에 요청을 쓰고, 위쪽에서 진행과 결과를 봅니다.

![[agent-basic]]

## 요청 보내기 {#send}
1. 입력창에 할 일을 적습니다.
2. {{kbd:Enter}}를 누르거나 보내기 단추를 누릅니다. 줄을 바꾸려면 {{kbd:⇧Enter}}를 누릅니다.
3. 실행 중에는 보내기 단추 자리에 중지 단추가 생깁니다. 멈추려면 그 단추를 누릅니다.

한글처럼 글자를 조합하는 중에 누른 {{kbd:Enter}}는 보내지 않고 조합만 끝냅니다.

![[composer]]

### 실행 중에 이어서 요청하기 {#queue}
작업이 도는 동안에도 다음 요청을 쓸 수 있습니다. 입력창에 글이 있으면 중지 단추 옆에 보내기 단추와 같은 둥근 단추가 생깁니다.
- {{kbd:Enter}} 또는 {{ui:queue.add}} 단추: 다음 요청으로 대기열에 넣습니다. 지금 작업이 끝나면 순서대로 실행합니다. 한 창에 16개까지 넣을 수 있습니다.
- {{kbd:⌘Enter}} 또는 {{ui:queue.worktree}} 단추: 마이티 오케스트레이션이 켜진 이 Mac의 Claude 창이 커밋이 있는 브랜치의 git 저장소에 있으면, 이 창 옆에 자기 git worktree를 가진 새 자식 창을 열어 바로 실행합니다. 돌고 있는 작업은 그대로 이어집니다([새 worktree에서 실행하기](delegation.md#worktree)).
- 자식 창, 그런 저장소 밖의 창, 마이티 오케스트레이션을 끈 창에서는 {{kbd:⌘Enter}} 또는 {{ui:phone.composer.steer}} 단추가 대신 실행 중인 Claude에 바로 전달합니다. 이 Mac의 Claude 창에서만 됩니다. 마이티 보기에는 주황색 {{ui:graph.block.steer}} 블록으로 보입니다.
- 대기열의 {{ui:queue.runNext}}을 누르면 첫 대기 요청을 지금 실행합니다.

작업을 중지하면 대기 중인 요청도 취소됩니다. 앱을 다시 열면 대기열은 비워집니다.

## 입력창 도구 {#composer}
입력창 아래 줄에는 왼쪽부터 첨부, 모델, 사고 강도, 권한, 추가 실행 설정(…)이 있습니다. 창이 좁으면 일부가 메뉴 하나로 접힙니다. 실행 중에는 바꿀 수 없고, 바꾼 값은 다음 요청부터 적용됩니다.

### 모델 {#model}
모델 칩을 누르면 {{ui:composer.label.runner}}(Claude, Codex, Antigravity)와 {{ui:composer.label.model}} 항목을 고를 수 있습니다. 목록이 맞지 않으면 {{ui:composer.model.refresh}}을 누르세요. 에이전트를 바꾸면 대화가 새로 시작됩니다.

### 사고 강도 {#effort}
{{ui:composer.effort.label}}는 모델이 얼마나 깊이 생각할지 정합니다. Auto, Low, Medium, High, XHigh, Max 중에서 고릅니다. 모델이 지원하는 단계만 보입니다. Antigravity와 Gemini에는 없습니다.

### 작업 권한 {#permission}
권한은 에이전트가 묻지 않고 할 수 있는 일의 범위입니다.

| 에이전트 | 고를 수 있는 권한 |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Antigravity (Windows: Gemini) | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude의 Always ask와 Codex의 {{ui:permission.label.onRequest}} 권한은 추가 권한이 필요할 때 창 안에서 승인을 묻습니다. [승인과 질문](approvals.md#permission)을 보세요.
- Auto mode는 설치된 Claude CLI가 지원할 때만 보입니다.
- Bypass와 {{ui:permission.label.fullAccess}} 권한은 프로젝트 밖의 파일과 명령에도 접근합니다. 꼭 필요할 때만 쓰세요.

### Mac의 Antigravity {#antigravity}
Mac에서는 Google 에이전트가 Antigravity CLI(`agy`)로 실행됩니다. Antigravity는 Claude와 GPT-OSS 모델도 실행할 수 있습니다. Windows 앱은 아직 Gemini CLI를 씁니다.

- 설치: `curl -fsSL https://antigravity.google/cli/install.sh | bash`를 실행하면 `~/.local/bin`에 `agy`가 설치됩니다. 업데이트는 앱이 `agy update`로 합니다.
- 로그인: 터미널 창에서 `agy`를 한 번 실행하고 브라우저에서 Google 계정으로 로그인합니다. {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.buttonLogin}}을 누르면 앱이 대신 실행합니다. Antigravity에는 로그아웃 명령이 없으므로, 계정을 바꿀 때는 `agy` 안에서 `/logout`을 씁니다.
- 권한: Antigravity는 앱 안에서 물어볼 수 없습니다. {{ui:permission.label.default}}은 agy에 저장된 Agent Mode를 따릅니다(agy의 `/config`에서 바꾸지 않았다면 검토). 작업 폴더의 파일은 읽을 수 있지만, 물어봐야 하는 파일 수정과 셸 명령은 거절하고 실행 창에 거절한 작업을 알려 줍니다. {{ui:permission.label.plan}}은 agy 자체 폴더에 계획을 쓰고 프로젝트는 고치지 않은 채 멈춥니다. 실행하려면 다음 요청을 다른 모드로 보내세요. `~/.gemini/antigravity-cli/settings.json`의 `permissions.allow`에 허용 규칙(예: `command(git)`)을 추가하거나, {{ui:permission.label.acceptEdits}}(파일 수정) 또는 {{ui:permission.label.fullAccess}}(모두 허용)를 고르세요.
- Antigravity 모델은 이름에 생각 깊이가 들어 있어 {{ui:composer.effort.label}}를 따로 고르지 않습니다.
- {{ui:graph.view.mighty}} 보기는 Claude, Codex와 같습니다. Antigravity가 띄운 하위 에이전트는 요청 아래에 따로 블록이 되고, 그 내용(받은 지시, 메시지, 도구 호출, 돌려보낸 답)은 `~/.gemini/antigravity-cli`에 남는 하위 에이전트 자신의 기록에서 읽습니다. 읽은 파일은 이름만 보이고 내용은 옮기지 않습니다. 대화의 이전 요청도 같은 기록에서 불러오고, {{ui:resume.choice.resume}}에는 이 폴더의 Antigravity 대화가 나옵니다.
- 전에 Gemini CLI로 실행하던 창은 기록은 그대로지만 다음 요청부터 새 대화로 시작하고, Gemini CLI 모델을 골라 두었다면 기본값으로 바뀝니다.

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

Codex와 Antigravity는 이름이 조금 다릅니다(예: Codex `/new`, `/approvals`, `/status`, Antigravity `/usage`, `/config`). 목록에 없는 `/이름`은 CLI의 스킬·사용자 명령·플러그인 명령으로 그대로 전달됩니다. Antigravity의 스킬도 이렇게 실행되며, 목록에는 agy에 들어 있는 스킬, `~/.gemini/antigravity-cli/skills`의 내 스킬, 폴더의 `.agents/skills`가 나옵니다. 다만 `/help`, `/skills` 같은 Antigravity 자체의 조회 명령은 터미널에서 `agy -p "/help"`처럼 따로 실행할 때만 답하므로, 앱에서는 agy의 오류로 끝납니다.

### 컨텍스트와 사용량 {#session-info}
보내기 단추 옆의 동그란 고리는 대화가 컨텍스트를 얼마나 썼는지 보여 줍니다. 누르면 {{ui:composer.sessionInfo.contextUsage}}, 입력·출력·캐시 토큰, 비용, 쓴 모델, 세션 ID를 봅니다. CLI가 알려 준 값만 보여 주고, 모르는 값은 추정하지 않습니다.

![[session-info]]

## 기본 보기와 마이티 보기 {#views}
Claude, Codex, Antigravity 창 머리에는 {{ui:graph.view.default}} | {{ui:graph.view.mighty}} 전환이 있습니다. 바꿔도 대화와 입력 중인 글은 그대로입니다. 창마다 따로 기억합니다. Gemini 창(Windows)은 기본 보기만 있습니다.
- {{ui:graph.view.default}}: 대화를 위에서 아래로 읽는 보기입니다.
- {{ui:graph.view.mighty}}: 요청, 하위 에이전트, 백그라운드 작업, 결과를 블록 흐름으로 보여 줍니다.

### 마이티 흐름 {#diagram}
요청마다 요청 블록이 생기고, 바로 아래 {{ui:graph.block.execution}} 블록이 붙습니다. 요청 블록에는 보낸 요청만 담기고, 에이전트가 그 요청으로 한 일(메시지, 도구 호출, 진행 상태)은 {{ui:graph.block.execution}} 블록에 나옵니다. 하위 에이전트와 백그라운드 작업은 {{ui:graph.block.execution}} 블록 아래로 갈라져 나옵니다. 끝나면 {{ui:graph.block.result}} 카드가 붙습니다.
- 빈 곳을 끌거나 스크롤해서 흐름을 움직입니다. 확대·축소 단추로 50~150%로 볼 수 있습니다.
- 블록을 누르면 그 블록 안만 스크롤합니다. 빈 곳을 누르거나 {{kbd:Esc}}를 누르면 선택이 풀립니다.
- 블록 오른쪽 아래 모서리를 끌면 크기가 바뀝니다. 오른쪽 클릭하면 {{ui:graph.block.resetSize}}가 있습니다. 크기는 세션마다 저장됩니다.
- 맨 위까지 스크롤하면 {{ui:graph.history.load}}로 예전 요청을 10개씩 불러옵니다. 불러온 기록은 보기 전용입니다.
- Claude와 Codex 창에서는 머리의 퍼즐 단추로 설치된 플러그인과 마켓플레이스를 엽니다.

![[agent-mighty]]

![[agent-mighty-overview]]

### 기록 {#timeline}
마이티 보기 위쪽의 {{ui:graph.view.diagram}} | {{ui:graph.view.timeline}} 전환으로 요청을 시간 순서의 목록으로 볼 수 있습니다. 요청 줄 다음에 {{ui:graph.block.execution}} 줄이, 그 다음에 하위 에이전트 줄이 옵니다.

![[agent-timeline]]

### 파이프라인 {#structure}
같은 전환의 {{ui:graph.view.structure}} 항목을 고르면 요청을 칸반식 파이프라인으로 볼 수 있습니다.
- 화면에는 한 번에 하나만 나옵니다. 스타일이 없는 창은 요청 하나, 스타일을 쓰는 창은 파이프라인 하나입니다. 파이프라인은 단계가 앞으로만 나아가는 요청들의 묶음이고, 요청의 단계가 앞 요청보다 이전으로 돌아가면 새 파이프라인이 시작됩니다.
- 맨 위의 ◀ {{ui:graph.structure.board.previous}}과 ▶ {{ui:graph.structure.board.next}}으로 앞뒤를 오가고, 가운데의 "요청 3 / 7" 같은 제목을 누르면 목록이 열려 번호, 제목, 상태, 단계를 보고 바로 고를 수 있습니다.
- 그 아래에는 상태(스타일을 쓰는 창은 파이프라인 이름도)가 나옵니다. 단계는 칸마다 맨 위의 칩으로 보이고, 칩은 선으로 이어져 있습니다. 스타일이 없는 창은 {{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}, 스타일을 쓰는 창은 그 모드의 단계입니다. 끝난 단계는 초록색, 지금 단계는 강조색, 남은 단계는 회색입니다.
- 내용은 단계마다 하나씩 나란히 놓인 칸에 담깁니다. 칸 머리에는 단계 칩과 카드 수가 있습니다. 스타일이 없는 창은 {{ui:graph.structure.column.request}} 칸에 보낸 요청, {{ui:graph.structure.column.execution}} 칸에 진행 상황(최근 메시지와 도구 호출)과 상태가 담긴 카드와 하위 에이전트마다 한 장씩의 카드, {{ui:graph.structure.column.result}} 칸에 결과가 놓입니다. 스타일을 쓰는 창은 단계 칸마다 그 단계의 요청 카드(요청, 실행 요약, 결과 한 줄)와 그 아래 하위 에이전트 카드가 놓입니다. 안쪽 하위 에이전트는 들여 쓰고 어느 블록 아래인지 적어 둡니다.
- 긴 글은 접혀 있고 {{ui:graph.structure.more}}를 누르면 그 자리에서 펼쳐집니다. 카드 머리의 작은 버튼을 누르면 흐름에서 그 블록을 보여 줍니다.
- 보기는 가장 최근 것에서 열리고, 그대로 두면 새 요청이나 새 파이프라인으로 따라갑니다. 이전 것을 보는 동안 새로 생기면 자리는 그대로이고 ▶ 옆에 {{ui:graph.structure.board.newRequest}} 또는 {{ui:graph.structure.board.newPipeline}} 표시가 붙습니다. 표시를 누르면 가장 최근 것으로 갑니다.
- 칸은 저마다 따로 움직입니다. 칸 머리는 위에 그대로 있고 카드만 위아래로 움직입니다. 칸 위에서 트랙패드나 마우스 휠로 위아래로 굴리거나 빈 곳을 끌면 그 칸만 움직이고, 칸이 화면에 다 들어가지 않으면 옆으로 굴리거나 끌어 파이프라인을 옆으로 옮깁니다.
- 가장 최근 것에서 끝을 보고 있는 칸은 새 내용을 따라 내려갑니다. 직접 옮긴 칸이나 글을 펼친 칸은 그 자리에 머물고, 그 칸의 끝으로 돌아오면 다시 따라갑니다. 새 요청이 오면 모든 칸이 끝을 보여 주고, 이전 것은 칸마다 맨 위에서 열립니다. Windows 앱에서는 아직 파이프라인 전체가 한꺼번에 움직입니다.

![[agent-structure]]

### 결과 카드 {#result}
요청이 끝나면 {{ui:graph.block.result}} 카드에 결과가 정리됩니다. 결과에 파일이 나오면 {{ui:graph.resultFiles.openButton}} 단추로 목록을 엽니다. 블록 안의 파일 경로를 누르면 미리보기가 뜹니다. 가장 최근 결과 카드는 창 크기에 맞춰지고, 크기를 직접 바꿨다면 {{ui:graph.result.fitToWindow}} 단추가 생깁니다.

![[result-card]]
