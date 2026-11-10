---
title: 에이전트 창
order: 3
section: agent-pane
---
에이전트 창은 Claude, Codex, Antigravity, pi, opencode, Hermes 중 하나와 대화하는 실행 창입니다. 아래쪽 입력창에 요청을 쓰고, 위쪽에서 진행과 결과를 봅니다.

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
- {{kbd:⌘Enter}} 또는 {{ui:queue.worktree}} 단추: 마이티 오케스트레이션이 켜진 이 Mac의 Claude 창이나 자식이 파일을 바꿀 수 있는 모드의 Codex·Antigravity 창이 커밋이 있는 브랜치의 git 저장소에 있으면, 이 창 옆에 자기 git worktree를 가진 새 자식 창을 열어 바로 실행합니다. 돌고 있는 작업은 그대로 이어지고, 자식이 끝나면 사이드바의 카드에서 결과를 병합합니다([새 worktree에서 실행하기](delegation.md#worktree)).
- 자식 창, 그런 저장소 밖의 창, 마이티 오케스트레이션을 끈 창에서는 {{kbd:⌘Enter}}도 {{kbd:Enter}}처럼 대기열에 넣습니다. 자식을 만들거나 시작하지 못했을 때도 글은 대기열로 갑니다([새 worktree에서 실행하기](delegation.md#worktree)). 보낸 글이 실행 중인 작업에 끼어드는 일은 이제 없습니다. 예전 기록의 마이티 보기에는 그렇게 보낸 글이 주황색 {{ui:graph.block.steer}} 블록으로 남아 있습니다. 실행 중인 Claude 부모에게 바로 전달된 위임 알림은 같은 색의 {{ui:graph.block.delegationNotice}} 블록으로 보입니다([알림과 보류](delegation.md#notices)).
- 대기열의 각 요청에는 ✕ 옆에 {{ui:queue.runNow}} 단추가 있습니다. 새 worktree를 만들 수 있는 곳(위 {{kbd:⌘Enter}}와 같은 조건)이면 그 요청을 새 자식 창에서 지금 실행하고, 돌고 있는 작업은 그대로 이어집니다. 오른쪽 클릭(Mac은 Control-클릭도 됩니다)하면 다른 에이전트를 고를 수 있습니다. 단추의 도움말이 어느 쪽으로 실행될지 알려 줍니다. 글 없이 첨부 파일만 있는 요청은 worktree로 가지 않습니다.
- worktree를 만들 수 없는 곳에서는 이 창에서 처리합니다. 오류로 멈춘 창처럼 쉬고 있으면 그 요청을 지금 시작하고, Claude의 답은 끝났고 백그라운드 작업만 남은 창이면 그 요청이 실행 중인 Claude에 들어가 마이티 보기에 {{ui:graph.block.steer}} 블록으로 보입니다. 첨부가 있는 요청은 작업이 끝날 때까지 기다립니다. 그 밖에는 단추가 흐리게 표시되고 도움말이 까닭을 알려 줍니다. 지금 작업이 먼저 끝나야 하거나, 이 창에 보류된 항목이 먼저 가야 하거나, CLI 업데이트 중인 경우입니다. 그 요청은 차례를 기다립니다. Antigravity 카드의 후속 요청은 늘 자기 창에서만 실행합니다.

작업을 중지하면 대기 중인 요청도 취소됩니다. 단, 백그라운드 작업만 기다리는 창에서 중지하면 대기열은 그대로 두고 첫 요청을 바로 시작합니다. 앱을 다시 열면 대기열은 비워집니다.

## 입력창 도구 {#composer}
입력창 아래 줄에는 왼쪽부터 첨부, 모델, 사고 강도, 권한, 추가 실행 설정(…)이 있습니다. 창이 좁으면 일부가 메뉴 하나로 접힙니다. 실행 중에는 바꿀 수 없고, 바꾼 값은 다음 요청부터 적용됩니다.

### 모델 {#model}
모델 칩을 누르면 {{ui:composer.label.runner}}(Claude, Codex, Antigravity, pi, opencode, Hermes)와 {{ui:composer.label.model}} 항목을 고를 수 있습니다. 목록이 맞지 않으면 {{ui:composer.model.refresh}}을 누르세요. 에이전트를 바꾸면 대화가 새로 시작됩니다.

### 사고 강도 {#effort}
{{ui:composer.effort.label}}는 모델이 얼마나 깊이 생각할지 정합니다. Auto, Low, Medium, High, XHigh, Max 중에서 고릅니다. 모델이 지원하는 단계만 보입니다. Antigravity에는 없고, pi는 Off, Minimal, Low, Medium, High, XHigh이고, opencode는 모델이 제공하는 변형인 Low, Medium, High, Max이고, Hermes는 Low, Medium, High, XHigh, Max입니다.

### 작업 권한 {#permission}
권한은 에이전트가 묻지 않고 할 수 있는 일의 범위입니다.

| 에이전트 | 고를 수 있는 권한 |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Antigravity | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |
| pi | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.fullAccess}} |
| opencode | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |
| Hermes | {{ui:permission.label.default}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude의 Always ask, Codex의 {{ui:permission.label.onRequest}}, {{ui:permission.label.fullAccess}}을 뺀 opencode의 모든 권한, Hermes의 파일 수정과 위험한 명령은 추가 권한이 필요할 때 창 안에서 승인을 묻습니다. [승인과 질문](approvals.md#permission)을 보세요.
- Auto mode는 설치된 Claude CLI가 지원할 때만 보입니다.
- Bypass와 {{ui:permission.label.fullAccess}} 권한은 프로젝트 밖의 파일과 명령에도 접근합니다. 꼭 필요할 때만 쓰세요.

### Antigravity {#antigravity}
Google 에이전트는 Antigravity CLI(`agy`)로 실행됩니다. Antigravity는 Claude와 GPT-OSS 모델도 실행할 수 있습니다.

- 설치: Mac에서는 `curl -fsSL https://antigravity.google/cli/install.sh | bash`를 실행하면 `~/.local/bin`에 `agy`가 설치됩니다. Windows에서는 PowerShell에서 `irm https://antigravity.google/cli/install.ps1 | iex`를 실행하면 `%LOCALAPPDATA%\agy\bin`에 `agy.exe`가 설치됩니다. 업데이트는 앱이 `agy update`로 합니다.
- 로그인: 터미널 창에서 `agy`를 한 번 실행하고 브라우저에서 Google 계정으로 로그인합니다. {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.buttonLogin}}을 누르면 앱이 대신 실행합니다. Antigravity에는 로그아웃 명령이 없으므로, 계정을 바꿀 때는 `agy` 안에서 `/logout`을 씁니다.
- 권한: Antigravity는 앱 안에서 물어볼 수 없습니다. {{ui:permission.label.default}}은 agy에 저장된 Agent Mode를 따릅니다(agy의 `/config`에서 바꾸지 않았다면 검토). 작업 폴더의 파일은 읽을 수 있지만, 물어봐야 하는 파일 수정과 셸 명령은 거절하고 그 턴을 끝낸 뒤, 거절한 작업을 모은 권한 카드로 묻습니다. {{ui:permission.allowOnce}}이나 {{ui:antigravity.permission.allowAlways}}을 누르면 같은 대화에서 이어서 진행합니다([Antigravity의 권한 카드](approvals.md#antigravity-permission)). {{ui:permission.label.plan}}은 agy 자체 폴더에 계획을 쓰고 프로젝트는 고치지 않은 채 멈춥니다. 실행하려면 다음 요청을 다른 모드로 보내세요. 카드에 없는 작업을 미리 허용하려면 `~/.gemini/antigravity-cli/settings.json`의 `permissions.allow`에 허용 규칙(예: `command(git status)`, 그 글자로 시작하는 모든 명령을 허용하니 되도록 좁게)을 직접 추가하거나, {{ui:permission.label.acceptEdits}}(파일 수정) 또는 {{ui:permission.label.fullAccess}}(모두 허용)를 고르세요.
- Antigravity 모델은 이름에 생각 깊이가 들어 있어 {{ui:composer.effort.label}}를 따로 고르지 않습니다.
- {{ui:graph.view.mighty}} 보기는 Claude, Codex와 같습니다. Antigravity가 띄운 하위 에이전트는 요청 아래에 따로 블록이 되고, 그 내용(받은 지시, 메시지, 도구 호출, 돌려보낸 답)은 `~/.gemini/antigravity-cli`(Windows에서는 `%USERPROFILE%\.gemini\antigravity-cli`)에 남는 하위 에이전트 자신의 기록에서 읽습니다. 읽은 파일은 이름만 보이고 내용은 옮기지 않습니다. 대화의 이전 요청도 같은 기록에서 불러오고, {{ui:resume.choice.resume}}에는 이 폴더의 Antigravity 대화가 나옵니다.
- 전에 Gemini CLI로 실행하던 창은 기록은 그대로지만 다음 요청부터 새 대화로 시작하고, Gemini CLI 모델을 골라 두었다면 기본값으로 바뀝니다.

### pi {#pi}
pi(`pi`)는 여러 모델 제공자를 쓰는 오픈소스 코딩 에이전트 CLI입니다.

- 설치: {{ui:setup.title}} 화면에서 설치합니다(Homebrew와 Node.js가 없으면 먼저 설치합니다). 직접 설치하려면 `npm install -g @mariozechner/pi-coding-agent`를 실행합니다. 업데이트는 앱이 `pi update self`로 합니다.
- 로그인: {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.buttonLogin}} 단추를 누르면 `pi`를 실행하는 터미널 창이 열립니다. 거기서 `/login`을 입력하고 구독(Claude Pro/Max, ChatGPT, GitHub Copilot)이나 API 키를 고릅니다. 환경 변수의 API 키(`ANTHROPIC_API_KEY` 등)나 `~/.pi/agent/auth.json`에 저장된 자격 증명도 로그인으로 봅니다. 로그아웃은 pi 안에서 `/logout`을 입력합니다.
- 모델: 로그인하면 `pi --list-models`의 목록이 `제공자/모델` 이름으로 나오고, 첫 항목은 pi 자체 설정을 따릅니다. 생각 기능이 없는 모델에는 {{ui:composer.effort.label}} 항목이 보이지 않습니다. 단계는 Off, Minimal, Low, Medium, High, XHigh입니다.
- 권한: pi에는 승인 창이 없어 모드가 두 가지입니다. {{ui:permission.label.defaultCodex}} 모드는 읽기 도구(read, grep, find, ls)만 켭니다. {{ui:permission.label.fullAccess}} 모드는 모든 도구를 묻지 않고 쓰며 프로젝트 밖도 건드릴 수 있습니다.
- 요청마다 창의 작업 폴더에서 `pi --mode rpc`를 실행하고 창의 세션을 이어 갑니다. 세션은 `~/.pi/agent/sessions`에 저장되고, {{ui:resume.choice.resume}} 항목에도 이 폴더의 pi 세션이 나옵니다.
- 질문: pi에는 질문 도구가 없어, 앱이 실행마다 `ask_user` 도구를 확장으로 더합니다(`~/.pi` 설정은 바꾸지 않습니다). pi가 이 도구로 물으면 [질문 카드](approvals.md#questions)가 뜨고, 답하면 같은 요청이 이어집니다. 다른 확장이 여는 고르기·확인·입력 창도 같은 카드로 뜹니다.
- 아직 안 되는 것: pi 창은 [마이티 오케스트레이션](delegation.md)에 참여하지 않고, [원격 작업 공간](remote.md)에서 열 수 없으며, 승인 카드도 없습니다. 지금은 Mac에서만 동작합니다.

### opencode {#opencode}
opencode(`opencode`)는 여러 모델 제공자를 쓰는 오픈소스 코딩 에이전트 CLI입니다. 자체 서비스인 opencode Zen의 무료 모델은 로그인 없이도 씁니다.

- 설치: {{ui:setup.title}} 화면에서 설치합니다(Homebrew와 Node.js가 없으면 먼저 설치합니다). 직접 설치하려면 `npm install -g @opencode/cli`를 실행합니다. 앱은 OpenCode 2를 씁니다. Mac에 OpenCode 1이 있으면 오래된 것으로 보이고, 구성 요소 화면에서 설치하면 npm의 `opencode-ai`를 먼저 지웁니다. 업데이트는 앱이 `opencode upgrade`로 합니다.
- 로그인: {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.buttonLogin}} 단추를 누르면 `opencode auth login`을 실행하는 터미널 창이 열립니다. 거기서 제공자를 고르고 그 제공자의 구독으로 로그인하거나 API 키를 입력합니다. opencode가 저장한 로그인(`opencode auth list`)이나 환경 변수의 API 키(`ANTHROPIC_API_KEY` 등)도 로그인으로 보고, 둘 다 없으면 opencode Zen의 무료 모델만 나옵니다. 로그아웃은 터미널에서 `opencode auth logout`을 실행합니다.
- 모델: `opencode models`의 목록이 `제공자/모델` 이름으로 나오고, 첫 항목은 opencode 자체 설정을 따릅니다.
- 권한: 고른 모드가 그 요청에만 쓰는 opencode 자체 규칙이 되고, 규칙이 묻도록 남긴 일은 Claude처럼 창에 승인 카드로 옵니다. {{ui:permission.label.default}}은 파일 수정, 명령, 웹, 프로젝트 밖 접근 전에 묻고, {{ui:permission.label.acceptEdits}}은 파일 수정을 묻지 않으며, {{ui:permission.label.plan}}은 파일을 고치지 않는 opencode의 plan 에이전트로 실행하고, {{ui:permission.label.fullAccess}}은 묻지 않습니다.
- 요청마다 창의 작업 폴더에서 `opencode acp`(OpenCode 2, Agent Client Protocol)를 실행하고 창의 세션을 이어 갑니다. 세션은 opencode의 `opencode.db`에 저장되고, {{ui:resume.choice.resume}} 항목에도 이 폴더의 opencode 세션이 나옵니다. 사용량에는 세션 비용, 쓰고 있는 컨텍스트, 요청의 토큰이 보입니다.
- 질문: opencode가 `question` 도구로 물으면 [질문 카드](approvals.md#questions)가 뜨고, 답하면 같은 요청이 이어집니다. 카드에서 {{ui:phone.questionnaire.cancel}}를 누르면 opencode가 그 요청을 끝냅니다.
- 아직 안 되는 것: opencode 창은 [마이티 오케스트레이션](delegation.md)에 참여하지 않고 [원격 작업 공간](remote.md)에서 열 수 없습니다. 지금은 Mac에서만 동작합니다.

### Hermes {#hermes}
Hermes(`hermes`)는 Nous Research의 오픈소스 에이전트 CLI(Hermes Agent)입니다. Nous Portal, OpenRouter, Anthropic, OpenAI 등 여러 모델 제공자를 씁니다.

- 설치: {{ui:setup.title}} 화면에서 설치합니다(Git이 없으면 먼저 설치합니다). 공식 설치 스크립트가 자체 Python 환경과 함께 `~/.hermes`에 설치하고 브라우저 도구도 내려받아 몇 분 걸립니다. 직접 설치하려면 `curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash`를 실행합니다. 업데이트는 앱이 `hermes update`로 합니다.
- 로그인: {{ui:settings.nav.cli}} 탭의 {{ui:settings.cliAccounts.buttonLogin}} 단추를 누르면 `hermes model`을 실행하는 터미널 창이 열립니다. 거기서 모델 제공자를 고르고 로그인하거나 API 키를 입력한 뒤 기본 모델을 고릅니다. 앱은 Hermes가 자격 증명을 찾은 제공자의 이름만 읽습니다. 자격 증명을 지우려면 터미널에서 `hermes auth`를 씁니다.
- 모델: 첫 항목은 Hermes 자체 설정(`hermes model`로 정한 기본 모델)을 따릅니다. Hermes는 세션을 열어야 모델 목록을 알려 주므로, 첫 요청을 보낸 뒤부터 `제공자:모델` 목록이 나옵니다.
- 권한: {{ui:permission.label.default}}은 파일을 고칠 때마다 창의 승인 카드로 묻고, {{ui:permission.label.acceptEdits}}은 작업 폴더 안의 수정을 묻지 않으며, {{ui:permission.label.fullAccess}}은 Hermes의 `--yolo`로 위험한 명령도 묻지 않습니다. 위험한 명령(`rm -rf` 등)은 Hermes의 승인 설정(`approvals.mode`)에 따라 카드로 묻고, 그 밖의 명령과 읽기는 묻지 않습니다. 계획 전용 모드는 없습니다. {{ui:permission.label.fullAccess}}은 세션을 시작할 때 정해지므로, 이미 시작한 세션에는 새 대화부터 적용됩니다.
- 요청마다 창의 작업 폴더에서 `hermes acp`(Agent Client Protocol)를 실행하고 창의 세션을 이어 갑니다. 실제 작업은 Hermes의 게이트웨이(백그라운드 프로세스)가 하며, 필요할 때 저절로 시작되고 쓰지 않으면 끝납니다. 세션은 `~/.hermes/state.db`에 저장되고, {{ui:resume.choice.resume}} 항목에도 이 폴더의 Hermes 세션이 나옵니다. 사용량에는 세션의 누적 토큰과 쓰고 있는 컨텍스트가 보이고, 비용은 Hermes가 알려 주지 않습니다.
- 질문: Hermes가 clarify 도구로 물으면 [질문 카드](approvals.md#questions)가 뜨고, 답하면 같은 요청이 이어집니다. Hermes는 편집기 연결(ACP)에서 이 도구를 기본으로 꺼 두므로, 앱이 첫 Hermes 요청 전에 Hermes 설정의 `platform_toolsets.acp` 한 항목을 `hermes config set`으로 켜고 기록에 알립니다. 직접 정해 둔 목록이 있으면 건드리지 않습니다. 새 대화부터 적용되고, 되돌리려면 터미널에서 `hermes config unset platform_toolsets.acp`를 실행합니다. Hermes는 답을 일정 시간(기본 90초)만 기다립니다. 그 뒤에는 카드가 닫히고 Hermes가 답 없이 이어 갑니다.
- 아직 안 되는 것: Hermes 창은 [마이티 오케스트레이션](delegation.md)에 참여하지 않고 [원격 작업 공간](remote.md)에서 열 수 없습니다. 지금은 Mac에서만 동작합니다.

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

### 보낼 대상 {#ask-target}
oh-my-claudecode가 설치된 Claude 창에는 입력창에 {{ui:composer.ask.menuLabel}} 스위치가 나타납니다. 설치된 다른 CLI(Codex, Antigravity 등)가 목록에 오르고, 하나를 고르면 이후 요청이 oh-my-claudecode의 `ask`를 거쳐 그 CLI로 갑니다. 답은 Claude의 답글 안에 돌아옵니다. {{ui:composer.ask.claude}}를 고르면 원래대로 Claude에게 보냅니다. Mac, Windows 앱의 웹 창, 휴대폰에서 쓸 수 있습니다.
- 다른 CLI에게 묻는 동안에는 첨부를 보낼 수 없습니다.
- oh-my-claudecode는 고른 CLI를 승인 확인과 샌드박스를 끈 채로 실행합니다. 그래서 그 CLI는 묻지 않고 이 폴더의 파일을 바꾸고 명령을 실행할 수 있습니다. Mighty Code의 권한 모드와 승인 카드는 그 CLI를 실행하는 Claude의 단계에만 적용됩니다. 그 CLI를 고르고 있는 동안 입력창에 이 경고가 한 줄로 표시됩니다.
- Windows에서 예전 버전의 oh-my-claudecode는 Antigravity에게 묻기를 거절합니다. 그런 답이 돌아오면 oh-my-claudecode를 업데이트하거나 Gemini CLI를 고르세요.
- `/`로 시작하는 글은 그대로 Claude에게 갑니다.

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

## 마이티 보기 {#views}
Claude, Codex, Antigravity 창은 {{ui:graph.view.mighty}} 보기로 보입니다. 요청, 하위 에이전트, 백그라운드 작업, 결과를 블록 흐름으로 보여 줍니다. 따로 바꿀 것은 없습니다.

### 마이티 흐름 {#diagram}
요청마다 요청 블록이 생기고, 바로 아래 {{ui:graph.block.execution}} 블록이 붙습니다. 요청 블록에는 보낸 요청만 담기고, 에이전트가 그 요청으로 한 일(메시지, 도구 호출, 진행 상태)은 {{ui:graph.block.execution}} 블록에 나옵니다. 하위 에이전트와 백그라운드 작업은 {{ui:graph.block.execution}} 블록 아래로 갈라져 나옵니다. 끝나면 {{ui:graph.block.result}} 카드가 붙습니다.

{{platform:mac}} {{ui:graph.block.execution}} 블록과 하위 에이전트 블록의 머리는 누가 일하는지 보여 줍니다. 왼쪽 끝의 색 띠와 아이콘 색이 역할입니다(메인 세션, 하위 에이전트). 제목 뒤에는 하위 에이전트의 종류(Explore, 직접 만든 에이전트의 이름 등)와 백그라운드, 포크, worktree 표시가 붙고, {{ui:graph.block.execution}} 블록에는 창에서 고른 사고 강도가 붙습니다. 모델은 토큰 표시에 함께 나오며, 아직 응답이 없으면 요청한 모델 뒤에 '요청'이라고 적습니다. 직접 만든 에이전트의 정의 파일에 `color`가 있으면 그 색을 씁니다. 블록이 좁으면 이 표시들이 먼저 숨고, 아이콘에 마우스를 올리면 전체를 볼 수 있습니다.

{{platform:mac}} Claude 창의 흐름에서는 실행 중인 하위 에이전트가 자신을 띄운 블록 아래에 작은 카드로 나란히 보입니다. 백그라운드로 시작한 하위 에이전트도 여기에 보입니다. 카드에는 이름, 종류와 모델, 지금 하는 일 한 줄, 그리고 상태와 함께 경과 시간, 도구 호출 수, 토큰이 나옵니다. 앱이 실제로 본 값만 나오고 진행률은 없습니다. 카드를 누르면 전체 블록으로 펼쳐지고, 블록 머리의 {{ui:graph.card.fold}} 단추로 다시 접습니다. 포그라운드 하위 에이전트는 끝나면 예전처럼 블록이 됩니다. 백그라운드 하위 에이전트는 끝나도 카드로 남고, 누르면 {{ui:graph.background.noticeRun}} 다이어그램에 있는 전체 블록으로 이동합니다.

백그라운드로 시작한 하위 에이전트와 백그라운드 명령(작업)은 도는 동안 요청의 블록으로 그려지지 않고 입력창 위의 백그라운드 작업 줄에 있습니다. Mac의 Claude 창은 하위 에이전트를 위의 카드로도 보여 줍니다. 행에는 종류, 이름, 걸린 시간과 마지막 출력 한 줄이 나오고, 행을 펼치면 실행한 명령(하위 에이전트는 프롬프트 첫 줄)과 출력의 마지막 줄들(최대 6줄)이 나옵니다. 작업이 끝나 알림이 가면 Claude의 답마다 새 {{ui:graph.background.noticeRun}} 다이어그램이 생기고, 끝난 작업마다 블록 하나가 종류 줄(하위 에이전트인지 백그라운드 실행인지와 끝난 상태), 명령, 출력의 마지막 줄들, 하위 에이전트의 보고를 담습니다. {{ui:graph.view.timeline}}에서는 이 다이어그램이 새 행으로, {{ui:graph.view.structure}}에서는 맨 끝의 새 카드(보드)로 붙습니다. 휴대폰의 마이티 보기와 파이프라인도 같습니다.

Claude나 Codex가 요청을 처리하며 할 일 목록을 만들면 흐름 왼쪽 위에 {{ui:graph.steps.title}} 패널이 떠서 단계마다 끝남, 진행 중, 대기를 보여 줍니다. 머리의 단추로 접을 수 있고, 새 요청을 보내면 앞 요청의 목록은 사라집니다. Codex는 단계가 끝났는지만 알려 주므로 끝나지 않은 첫 단계를 진행 중으로 보여 줍니다. Antigravity는 보여 줄 할 일 목록이 없어 패널이 나오지 않습니다.

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
- 그 아래에는 상태(스타일을 쓰는 창은 파이프라인 이름도)가 나옵니다. 단계는 칸마다 맨 위의 칩으로 보이고, 칩은 선으로 이어져 있습니다. 스타일이 없는 창은 {{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}, 스타일을 쓰는 창은 그 모드의 단계입니다.
- 내용은 단계마다 하나씩 나란히 놓인 칸에 담깁니다. 칸 머리에는 단계 칩과 카드 수가 있습니다. 스타일이 없는 창은 {{ui:graph.structure.column.request}} 칸에 보낸 요청, {{ui:graph.structure.column.execution}} 칸에 진행 상황(최근 메시지와 도구 호출)과 상태가 담긴 카드와 하위 에이전트마다 한 장씩의 카드, {{ui:graph.structure.column.result}} 칸에 결과가 놓입니다. 스타일을 쓰는 창은 단계 칸마다 그 단계의 요청 카드(요청, 실행 요약, 결과 한 줄)와 그 아래 하위 에이전트 카드가 놓입니다. 안쪽 하위 에이전트는 들여 쓰고 어느 블록 아래인지 적어 둡니다.
- 상태는 색으로도 보입니다. 칸 바탕은 그 단계의 상태 색으로 물들고(완료는 초록, 실행 중은 파랑, 기다리는 중은 노랑, 오류는 빨강, 중지는 회색), 지금 단계는 가장 최근 요청의 상태를 따르며, 아직 오지 않은 단계는 바탕 그대로입니다. 카드는 왼쪽 가장자리에 같은 색의 막대가 있고 바탕은 칸보다 옅습니다. 실행 중인 카드는 움직이는 테두리를, 기다리는 카드는 노란 테두리를 그대로 두고, 상태 글자도 그대로 보입니다.
- 긴 글은 접혀 있고 {{ui:graph.structure.more}}를 누르면 그 자리에서 펼쳐집니다. 카드 머리의 작은 버튼을 누르면 흐름에서 그 블록을 보여 줍니다.
- 보기는 가장 최근 것에서 열리고, 그대로 두면 새 요청이나 새 파이프라인으로 따라갑니다. 이전 것을 보는 동안 새로 생기면 자리는 그대로이고 ▶ 옆에 {{ui:graph.structure.board.newRequest}} 또는 {{ui:graph.structure.board.newPipeline}} 표시가 붙습니다. 표시를 누르면 가장 최근 것으로 갑니다.
- 칸은 저마다 따로 움직입니다. 칸 머리는 위에 그대로 있고 카드만 위아래로 움직입니다. 칸 위에서 트랙패드나 마우스 휠로 위아래로 굴리거나 빈 곳을 끌면 그 칸만 움직이고, 칸이 화면에 다 들어가지 않으면 옆으로 굴리거나 끌어 파이프라인을 옆으로 옮깁니다.
- 가장 최근 것에서 끝을 보고 있는 칸은 새 내용을 따라 내려갑니다. 직접 옮긴 칸이나 글을 펼친 칸은 그 자리에 머물고, 그 칸의 끝으로 돌아오면 다시 따라갑니다. 새 요청이 오면 모든 칸이 끝을 보여 주고, 이전 것은 칸마다 맨 위에서 열립니다. Windows 앱에서는 아직 파이프라인 전체가 한꺼번에 움직입니다.

![[agent-structure]]

### 결과 카드 {#result}
요청이 끝나면 결과 카드에 결과가 정리됩니다. 요청은 Claude의 답이 끝나면 완료로 바뀝니다. 그 요청이 띄운 백그라운드 작업(이미 끝난 하위 에이전트에 메시지를 보내 다시 깨운 것도 포함)이 아직 돌거나, 끝났지만 Claude가 그 알림에 아직 답하지 않았으면 결과 카드는 대기 색의 {{ui:graph.block.awaitingNotice}} 블록이 됩니다. 지금까지의 답 아래에 작업마다 {{ui:plan.background.awaitingNotice}} 또는 {{ui:plan.background.noticeDelivered}}이 붙고, "알림 1/2"처럼 받은 알림 수가 보입니다. 이 동안 펫과 창 머리는 {{ui:graph.state.awaitingNotice}}를 보입니다. 모든 알림에 답하면 {{ui:graph.block.result}}로 바뀝니다. 백그라운드 작업의 알림을 받고 Claude가 다시 답하면 그 답은 요청의 결과에 붙지 않고, 답 하나마다 새 다이어그램이 열립니다([백그라운드 작업](approvals.md#background)). 같은 요청의 다른 백그라운드 작업이 아직 돌고 있으면 그 답의 카드 제목은 "현재까지의 결과 · 백그라운드 작업 N개 기다리는 중"으로 남은 작업 수를 보여 줍니다. 결과에 파일이 나오면 {{ui:graph.resultFiles.openButton}} 단추로 목록을 엽니다. 블록 안의 파일 경로를 누르면 미리보기가 뜹니다. 가장 최근 결과 카드는 창 크기에 맞춰지고, 크기를 직접 바꿨다면 {{ui:graph.result.fitToWindow}} 단추가 생깁니다. 요청이 바꾼 소스·Markdown 파일이면 미리보기 머리에 {{ui:files.changes.show}} | {{ui:files.changes.full}} 전환이 생기고 {{ui:files.changes.show}}부터 보입니다. 바뀐 줄만 앞뒤 3줄과 함께 옛 줄 번호·새 줄 번호를 달아 보여 주며, 더한 줄은 초록, 지운 줄은 빨강 바탕입니다. 위 줄이 무엇과 비교했는지 알려 줍니다: 결과 파일 목록이나 블록에서 열면 그 요청이 파일을 처음 바꾸기 전 내용({{ui:files.changes.baseRun}}), 그 원본이 없으면 마지막 커밋입니다. 바뀐 것이 없으면 전환 없이 전체 파일만 보입니다.

![[result-card]]
