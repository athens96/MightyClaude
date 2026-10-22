---
title: 마이티 오케스트레이션
order: 6
section: delegation
---
마이티 오케스트레이션은 에이전트 창 하나가 일하는 동안 다른 일을 옆의 자식 창에 맡겨 함께 진행하는 기능입니다. 자식은 부모와 같은 에이전트(Claude, Codex, Antigravity)로 실행되고, 셋 중 다른 에이전트로도 실행할 수 있습니다([Codex와 Antigravity](#agents)). 자식 창은 저마다 자기 git worktree와 브랜치에서 일하므로 서로의 파일을 건드리지 않고, 끝나면 사이드바의 카드에서 결과를 부모 브랜치에 병합합니다. 예전 이름은 에이전트 위임입니다.

자식 창을 여는 방법은 둘입니다.

- **직접 띄우기**: 실행 중인 Claude 창이나, 자식이 파일을 바꿀 수 있는 모드의 Codex·Antigravity 창([Codex와 Antigravity](#agents))에 다음 요청을 쓰고 {{kbd:⌘Enter}}를 누릅니다. 요청이 새 worktree의 자식 창에서 바로 실행됩니다([새 worktree에서 실행하기](#worktree)).
- **Claude에게 나눠 맡기기**: Claude(또는 Codex)에게 일을 나눠 자식 창에 맡기라고 요청합니다. 에이전트가 자식을 만들고, 보고를 읽고, 병합까지 합니다([Claude에게 나눠 맡기기](#delegate)).

> [!warning]
> 실험 기능입니다. 기본으로 켜져 있고 설정에서 끌 수 있습니다. 연습용 저장소에서 먼저 써 보세요.

## 한눈에 보기 {#overview}
직접 띄울 때의 흐름입니다.

1. git 저장소 폴더를 작업 공간으로 열고, Claude 창에서 일을 시킵니다.
2. Claude가 일하는 동안 다음 요청을 쓰고 {{kbd:⌘Enter}}를 누릅니다. 돌고 있는 작업은 그대로 이어지고, 이 창 옆에 자식 창이 열려 요청을 바로 실행합니다.
3. 자식 창은 사이드바에서 부모 창 아래 한 칸 들여 보이고, 옆에 상태가 붙습니다.
4. 자식이 일을 마치고 커밋한 뒤 보고서를 남기면 상태가 {{ui:delegation.child.state.reported}}이 됩니다.
5. 자식의 카드에서 {{ui:delegation.card.merge}} 단추를 눌러 결과를 부모 브랜치에 넣습니다.
6. 병합되면 그 자식 창을 바로 폐기할지 묻습니다. {{ui:delegation.mergedDiscard.confirm}}를 고르면 자식 창과 worktree, 브랜치가 정리됩니다. 병합한 내용은 부모 브랜치에 남습니다.

![[delegation-tree]]

| 하고 싶은 일 | 방법 |
|---|---|
| 지금 작업 뒤에 이어서 실행 | {{kbd:Enter}} (대기열에 들어감) |
| 지금 작업과 따로, 바로 실행 | {{kbd:⌘Enter}} 또는 {{ui:queue.worktree}} 단추 |
| 결과를 부모 브랜치에 넣기 | 카드의 {{ui:delegation.card.merge}} 단추 |
| 병합을 무르기 | 카드의 {{ui:delegation.card.undo}} 단추 |
| 결과를 버리기 | 카드의 {{ui:delegation.card.discard}} 단추 |

## 새 worktree에서 실행하기 {#worktree}
Claude·Codex·Antigravity 창이 실행 중일 때 요청을 쓰고 {{kbd:⌘Enter}}를 누르거나, 중지 단추 옆의 둥근 {{ui:queue.worktree}} 단추(브랜치 아이콘)를 누릅니다. 돌고 있는 작업은 그대로 이어지고, 요청은 자기 worktree를 가진 새 자식 창에서 바로 실행됩니다. 그사이 부모 창에 글을 쓰기 시작하지 않았다면 새 창이 곧바로 화면에 보입니다.

- 실행 중인 창에서 {{kbd:Enter}}는 요청을 대기열에 넣어 지금 작업이 끝난 뒤 실행합니다. 입력창의 안내 문구가 두 키가 하는 일을 보여 줍니다.
- 새 worktree는 만들 수 있는 곳에서만 씁니다. 커밋이 있는 브랜치가 체크아웃된 git 저장소여야 합니다. 그 밖의 곳(git 저장소가 아님, 커밋이 아직 없음, 브랜치가 아닌 커밋이 체크아웃됨)에서는 {{kbd:⌘Enter}}가 {{kbd:Enter}}처럼 요청을 대기열에 넣습니다.
- 자식은 부모의 마지막 커밋에서 시작합니다. 브랜치 `mighty/<자식 id>`가 생기고, worktree는 프로젝트 폴더 밖(Mac은 `~/.mightyclaude/worktrees/<자식 id>`, Windows는 `%USERPROFILE%\.mightyclaude\worktrees\<자식 id>`)에 생겨 부모의 작업을 건드리지 않습니다. 부모 폴더에서 아직 커밋하지 않은 변경은 자식에게 가지 않습니다.
- 창과 같은 에이전트의 자식은 부모 창의 권한 모드로, 부모 창의 모델과 {{ui:composer.effort.label}}로 실행합니다. 모든 요청을 계획 모드로 시작하는 스타일에서는 계획 모드로 시작합니다. 부모와 같은 보기로 열리고, 부모가 마이티 보기에서 스타일을 쓰고 있었다면 같은 스타일로 시작합니다(스타일의 진행 상태는 자식의 첫 요청부터 따로 셉니다). 첨부 파일도 함께 갑니다.
- 첫 메시지에는 입력한 글이 먼저 오고, 그 뒤에 앱이 자식이 일하는 곳을 알려 주며 끝나면 커밋하고 REPORT.md를 쓰라고 덧붙입니다. 그래야 카드에서 병합할 수 있습니다. 슬래시 명령(`/`로 시작하는 글)은 입력한 그대로 혼자 실행되고, 앱의 안내는 자식 창의 다음 요청으로 따라가므로 명령의 인자가 되지 않습니다.
- {{platform:mac}} 요청을 다른 에이전트로 실행하려면 {{ui:queue.worktree}} 버튼을 오른쪽 클릭(또는 Control-클릭)해서 Claude, Codex, Antigravity 중에서 고릅니다. {{kbd:⌘Enter}}와 그냥 클릭은 창과 같은 에이전트를 씁니다. 흐리게 표시된 항목은 실행할 수 없는 까닭을 함께 보여 줍니다. CLI가 설치되어 있지 않거나 로그인되어 있지 않은 경우(설정 › CLI에서 확인), 창의 모드가 허용하는 범위 안에서 파일을 바꿀 수 있는 모드가 없는 경우, 직접 시작한 자식이 이미 3개 열려 있는 경우입니다. 다른 에이전트의 자식은 파일을 바꿀 수 있으면서 창의 모드가 허용하지 않는 일은 하지 않는 자기 모드 가운데 가장 넓은 모드로 시작하고([Codex와 Antigravity](#agents)), 그 에이전트의 새 창이 받을 모델과 {{ui:composer.effort.label}}를 쓰며, 창과 같은 보기로 열리지만 스타일은 이어받지 않습니다. 그 자식을 만들거나 시작하지 못하면 글과 첨부 파일은 입력창으로 돌아가고 대화에 까닭이 적힙니다. 창 자신에게는 보내지 않습니다. 창의 에이전트는 그 모드에서 자식을 둘 수 없지만 다른 에이전트는 둘 수 있으면 버튼이 그대로 보이고, 클릭하면 메뉴가 열리며, {{kbd:⌘Enter}}는 요청을 대기열에 넣습니다.
- {{platform:windows}} 요청을 다른 에이전트로 실행하려면 {{ui:queue.worktree}} 버튼을 오른쪽 클릭(또는 버튼에서 Shift+F10)해서 Claude, Codex, Antigravity 중에서 고릅니다. {{kbd:Ctrl+Enter}}와 그냥 클릭은 창과 같은 에이전트를 씁니다. 나머지는 위의 Mac과 같습니다. 흐리게 표시된 항목은 실행할 수 없는 까닭을 보여 주고, 다른 에이전트의 자식은 창의 모드가 허락하지 않는 일은 하지 않는 자기 모드 중 가장 넓은 모드에서 시작하고, 그 에이전트의 새 창이 받을 모델과 {{ui:composer.effort.label}}를 쓰며, 자식을 띄우지 못한 글은 입력창으로 돌아옵니다. 다른 에이전트의 자식만 가능한 곳에서는 클릭하면 메뉴가 열리고, {{kbd:Ctrl+Enter}}는 예전처럼 요청을 대기열에 넣습니다.
- 자식 창은 같은 에이전트의 보통 창처럼 씁니다. 승인 카드에 답하고, 후속 요청을 보내고, 권한 모드를 바꿀 수 있습니다.
- 직접 띄운 자식에 대해서는 부모 창의 Claude가 알림을 받지 않습니다. 부모가 맡긴 일이 아니기 때문입니다. 부모의 도구도 이 자식을 목록에 보여 주지 않고, 병합하거나 후속 지시를 보내지 못합니다(`human_started`). 사이드바에서 상태를 보고, 카드의 단추를 직접 씁니다.
- 열린 자식은 부모 창 하나에 3개까지이고, 디스크 여유가 10 GB 있어야 합니다. 끝난 자식 창을 닫거나 폐기하면 자리가 납니다.
- 자식을 만들 수 없거나 만든 뒤 시작하지 못하면(열린 자식이 이미 3개, 디스크 부족, worktree나 첫 실행 실패 등) 창의 대화에 이유가 남습니다. 그러면 글은 {{kbd:Enter}}를 누른 것처럼 그 창의 다음 요청이 됩니다. 대기열이 가득 차 있으면 입력창으로 돌아갑니다. 시작하지 못한 자식은 카드에서 폐기할 때까지 목록에 남습니다.
- 자식 창이나 마이티 오케스트레이션을 끈 창에서는 {{kbd:⌘Enter}}도 {{kbd:Enter}}처럼 대기열에 넣습니다. 휴대폰에서 실행 중인 창에 보낸 글도 언제나 대기열에 들어갑니다. 사람이 보낸 글은 실행 중인 작업에 바로 전달되지 않습니다.

## Claude에게 나눠 맡기기 {#delegate}
Claude나 Codex 창(부모)이 일을 나눠 다른 창(자식)에게 맡길 수도 있습니다. 다른 에이전트를 지정하지 않으면 자식은 부모와 같은 에이전트입니다. 자식이 보고하면 부모가 알림을 받아 보고서를 읽고, 결과를 병합할지 정합니다. Claude와 Codex 부모 모두 Mac 앱과 Windows 앱에서 됩니다.

1. git 저장소 폴더를 작업 공간으로 열고 Claude 창을 엽니다. 이 창이 부모입니다.
2. "delegate 도구로 Mighty 자식 창 두 개를 열어 이 일을 나눠 맡겨"처럼 도구 이름을 넣어 요청합니다. 그냥 "나눠서 해"라고 하면 Claude가 자기 안의 하위 에이전트로 처리할 수 있습니다.
3. 자식 창이 부모 옆에 열리고, 사이드바에서 부모 아래 한 칸 들여 보입니다.

- 부모가 쓰는 도구는 `delegate`(맡기기. `agent`로 자식의 에이전트를 `claude`, `codex`, `antigravity` 중에서 지정할 수 있습니다), `list_children`(자식 목록), `child_status`(상태와 보고서 읽기), `merge`(병합), `follow_up`(후속 지시)입니다. `discard`(폐기)는 부모가 부를 수 없고 사람만 카드에서 합니다.
- 자식마다 브랜치 `mighty/<자식 id>`와 worktree(Mac은 `~/.mightyclaude/worktrees/<자식 id>`, Windows는 `%USERPROFILE%\.mightyclaude\worktrees\<자식 id>`)가 생깁니다. 자식은 부모의 마지막 커밋에서 시작합니다.
- 맡긴 일은 TASK.md에, 결과는 REPORT.md에 적힙니다. 두 파일은 worktree 안의 git이 무시하는 폴더에 있어 커밋되지 않습니다.
- 자식의 권한 모드는 부모가 고른 모드이고, 부모 창의 모드보다 넓을 수 없습니다. 다른 에이전트의 자식은 부모의 모드가 하지 않는 일을 할 수 없습니다. 부모의 모드가 묻지 않고 파일을 바꿀 때만(편집 수락·auto 모드의 Claude, {{ui:permission.label.onRequest}}·{{ui:permission.label.acceptEditsCodex}} 모드의 Codex, {{ui:permission.label.acceptEdits}} 모드의 Antigravity, {{ui:permission.label.fullAccess}} 모드의 모든 에이전트) 묻지 않고 파일을 바꾸고, 부모의 모드가 파일을 바꿀 수 있을 때만 물어본 뒤 파일을 바꾸며, 부모의 모드가 묻지 않고 셸 명령을 실행할 때만(auto 모드의 Claude, {{ui:permission.label.defaultCodex}}·{{ui:permission.label.acceptEditsCodex}} 모드의 Codex, {{ui:permission.label.fullAccess}} 모드의 모든 에이전트) 묻지 않고 실행하고, {{ui:permission.label.fullAccess}} 모드 아래에서만 전체 권한을 가집니다. 그래서 셸 명령 전에 묻는 Claude의 편집 수락 모드 아래에서 Codex 자식은 {{ui:permission.label.acceptEditsCodex}} 모드가 아니라 {{ui:permission.label.onRequest}} 모드를 받고, 계획 모드나 수동 모드의 Claude 창에는 Codex·Antigravity 자식을 둘 수 없습니다. 넓히는 것은 사람만, 실행과 실행 사이에 자식 창 입력창의 권한 메뉴나 휴대폰 설정에서 할 수 있습니다.
- 자식 창은 부모 창과 같은 보기({{ui:graph.view.default}} | {{ui:graph.view.mighty}}, 마이티 보기라면 {{ui:graph.view.diagram}}, {{ui:graph.view.timeline}}, {{ui:graph.view.structure}} 중 같은 것)로 열립니다. 부모가 마이티 보기의 {{ui:graph.view.structure}}을 쓰고 있었다면 자식도 {{ui:graph.view.structure}}으로 보입니다.
- 부모는 자식의 승인 카드에 답하지 못합니다. 자식 창에 카드가 뜨면 그 창에서 직접 답하세요.
- 부모 하나에 `delegate` 도구로 만든 열린 자식은 3개까지이고, 직접 띄운 자식 3개와는 따로 셉니다. 어느 쪽도 다른 쪽을 막지 않습니다. 자식은 다시 위임하지 못합니다. 보고했거나 병합된 자식도 창이 열려 있으면 3개에 들어가므로, 끝난 자식 창을 닫으면 자리가 납니다. 디스크 여유가 10 GB보다 적으면 맡기기가 거절됩니다.
- 부모는 알림으로 자식의 보고를 알게 되고([알림과 보류](#notices)), `follow_up`으로 후속 지시를 보내고([후속 지시](#follow-up)), `merge`로 병합합니다([병합하기](#merge)).

## Codex와 Antigravity {#agents}
마이티 오케스트레이션은 Codex와 Antigravity 창에서도 됩니다. 기본으로 자식은 부모의 에이전트로 실행됩니다. Codex 창의 자식은 Codex 창이고, Antigravity 창의 자식은 Antigravity 창입니다.

어느 에이전트든 세 에이전트 중 아무 에이전트의 자식이나 둘 수 있습니다. {{ui:queue.worktree}} 버튼의 메뉴에서 고르거나([새 worktree에서 실행하기](#worktree)), Claude나 Codex 부모가 `delegate`의 `agent`(`claude`, `codex`, `antigravity`)로 지정합니다. 자식은 아래에 적은 자기 에이전트의 규칙대로 일합니다. 모드도, 작업을 누가 커밋하는지도(Claude 자식은 스스로 커밋하고, Codex·Antigravity 자식의 작업은 앱이 커밋합니다) 자식의 에이전트를 따릅니다. 알림과 병합은 부모의 에이전트를 따릅니다. `delegate`는 모르는 에이전트(`agent_unknown`), 설치되어 있지 않거나 로그인하지 않은 에이전트(`agent_unavailable`), 자식 에이전트의 모드가 아니거나, 그 모드에서 파일을 바꿀 수 없거나, 부모의 모드보다 많은 일을 할 수 있는 모드를 거절합니다(모드를 비교하는 방법은 [Claude에게 나눠 맡기기](#delegate)에 있습니다).

- **자식이 일할 수 있는 모드**: 자식은 부모의 권한 모드로 시작하므로, Codex와 Antigravity 창은 자식이 파일을 바꿀 수 있는 모드에서만 {{kbd:⌘Enter}}로 자식을 띄웁니다. Codex는 {{ui:permission.label.acceptEditsCodex}}·{{ui:permission.label.onRequest}}·{{ui:permission.label.fullAccess}} 모드, Antigravity는 {{ui:permission.label.acceptEdits}}·{{ui:permission.label.fullAccess}} 모드입니다. 그 밖의 모드에서는 {{kbd:⌘Enter}}가 예전처럼 요청을 대기열에 넣습니다.
- **Codex** 창에는 {{ui:permission.label.acceptEditsCodex}} 모드와 {{ui:permission.label.fullAccess}} 모드에서만 위임 도구가 붙습니다. {{ui:permission.label.defaultCodex}} 모드에서는 Codex가 도구를 거절하고, {{ui:permission.label.onRequest}} 모드에서는 앱이 보여 줄 수 없는 방식으로 묻기 때문입니다. `delegate`는 파일을 바꿀 수 없는 자식 모드를 거절합니다. Codex는 실행이 {{ui:permission.label.fullAccess}} 모드로 시작했을 때만 `merge`를 부를 수 있고, 그렇지 않으면 거절되므로(`merge_human_only`) 카드에서 병합합니다. 실행 중인 Codex 턴은 글을 받지 못하므로, 실행 중인 Codex 부모에게 가는 알림은 그 실행이 정상으로 끝난 뒤 다음 실행으로 가고, 중지나 오류로 끝나면 보류됩니다.
- **Antigravity** 창에는 위임 도구가 없습니다(Antigravity는 전역 설정에서만 도구를 받는데, 앱은 그 설정을 바꾸지 않습니다). 자식은 {{kbd:⌘Enter}}나 {{ui:queue.worktree}} 버튼으로만 띄웁니다.
- Codex와 Antigravity 자식은 {{ui:permission.label.fullAccess}} 모드가 아니면 자기 worktree에서 커밋할 수 없어서, 변경을 그대로 두라는 안내를 받습니다. 자식의 실행이 REPORT.md가 바뀐 채 정상으로 끝나면 앱이 변경을 자식 브랜치에 커밋하고(`mighty: <자식 id> report r<n>`) 그 커밋에서 보고를 기록합니다. 중지나 오류로 끝나면 다음 정상 종료를 기다립니다. .gitignore가 빼는 새 파일, 비밀 정보처럼 보이는 새 파일(`.env*`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.ppk`, `id_rsa*`, `id_ed25519*`, `id_ecdsa*`, `id_dsa*`, `credentials*`, `secrets.*`, `.npmrc`, `.pypirc`), 5 MB를 넘는 새 파일은 커밋하지 않고, 뒤의 두 가지는 자식 창에 이름을 알립니다. 커밋에는 git에 설정한 이름과 이메일을 쓰고(없으면 실패하고 창에 그렇게 알립니다) 서명하지 않습니다. worktree에 풀리지 않은 병합 충돌이 있으면 아무것도 커밋하지 않고 창에 그렇게 알립니다.
- 카드의 병합이 충돌로 거절되면 커밋할 수 있는 자식({{ui:permission.label.fullAccess}} 모드)에게만 충돌을 풀라고 요청합니다. 그렇지 않으면 자식을 {{ui:permission.label.fullAccess}} 모드로 바꾼 뒤 자식 창에서 요청하거나, 그 worktree의 터미널에서 직접 충돌을 풀고 커밋하세요.
- 저장소의 하위 폴더를 작업 공간으로 연 경우, Codex 자식은 REPORT.md가 있는 worktree 맨 위 폴더에도 쓸 수 있고, Antigravity 자식에게는 그 폴더가 추가됩니다.

## 자식의 상태 {#states}
사이드바에서 자식마다 상태가 보입니다.

| 상태 | 뜻 |
|---|---|
| {{ui:delegation.child.state.creating}} | worktree와 창을 만드는 중입니다. |
| {{ui:delegation.child.state.running}} | 일하고 있습니다. |
| {{ui:delegation.child.state.waiting}} | 승인이나 질문에 사람의 답을 기다립니다. |
| {{ui:delegation.child.state.reported}} | REPORT.md를 남기고 실행을 마쳤습니다. 병합할 수 있습니다. |
| {{ui:delegation.child.state.merged}} | 부모 브랜치에 병합되었습니다. |
| {{ui:delegation.child.state.ended}} | 새 보고 없이 실행이 끝났습니다. |
| {{ui:delegation.child.state.interrupted}} | 앱을 끄는 바람에 실행이 끊겼습니다. |
| {{ui:delegation.child.state.failed}} | 자식 창을 시작하지 못했습니다. |
| {{ui:delegation.child.state.closed}} | 창이 닫혔습니다. 폐기할 때까지 목록에 남습니다. |

자식이 새 실행을 시작하면 {{ui:delegation.child.state.reported}} 상태가 풀리고, 다시 보고하면 다음 보고 번호가 붙습니다. 자식이 보고 없이 끝났다면 자식 창에 커밋하고 REPORT.md를 쓰라고 다시 요청하세요.

## 병합하기 {#merge}
자식의 결과를 부모 브랜치에 넣는 방법은 둘입니다.

1. **카드의 {{ui:delegation.card.merge}} 단추**: 사람이 누릅니다. 직접 띄운 자식은 이 방법으로만 병합합니다. 빨리 감기(fast-forward)가 되면 그렇게 하고, 안 되면 병합 커밋을 정확히 하나 만듭니다. 먼저 충돌을 미리 살펴 보고, 거절되면 그 이유를 카드에 보여 줍니다.
   병합이 되면 그 자식 창을 바로 폐기할지 묻습니다. {{ui:delegation.mergedDiscard.confirm}}를 고르면 자식 창을 닫고 worktree와 브랜치를 지웁니다. 병합된 내용은 부모 브랜치에 그대로 남지만, 그 병합은 더 이상 되돌릴 수 없습니다. {{ui:delegation.mergedDiscard.later}}를 고르면 카드가 그대로 남습니다.
   충돌로 거절되면 앱이 자식 창에 부모 브랜치를 가져와 충돌을 풀고, 커밋하고, 다시 보고하라고 요청합니다. 카드에 그렇다고 나오고 자식은 {{ui:delegation.child.state.running}} 상태가 됩니다. 자식이 다시 보고하면 {{ui:delegation.card.merge}} 단추를 다시 누르세요. 이 요청은 보고마다 한 번만 갑니다. 자식 창이 지금 실행을 시작하지 못하면 보내지 않고 거절 이유만 카드에 남으며, 다음에 병합할 때 다시 시도합니다. 요청을 받고도 시작하지 못했거나, 자식이 계획 모드여서 계획만 세우고 끝났다면, 자식 창에 직접 충돌을 풀고 다시 보고하라고 요청하세요. Codex와 Antigravity 자식에게는 {{ui:permission.label.fullAccess}}일 때만 요청합니다([Codex와 Antigravity](#agents)).
2. **부모의 `merge` 도구**: Claude에게 맡긴 자식만 됩니다. 빨리 감기만 합니다. 부모는 `child_status`가 준 자식의 head를 함께 넘깁니다. 자식이 아직 보고하지 않았거나, 자식이나 부모 폴더에 커밋하지 않은 변경이 있거나, 자식의 head가 움직였거나, 부모 브랜치가 그새 더 나아갔거나(갈라짐), 부모 브랜치가 체크아웃되어 있지 않으면 거절하고 아무것도 바꾸지 않습니다. 이 도구에 승인 카드가 뜨는지는 부모 창의 권한 모드에 따릅니다. Codex 부모는 실행이 {{ui:permission.label.fullAccess}} 모드로 시작했을 때만 쓸 수 있고, 다른 모드에서는 `merge_human_only`로 거절되므로 카드에서 병합합니다.

> [!tip]
> 자식 하나를 병합하면 부모 브랜치가 앞으로 가므로, 나머지 자식은 보통 `merge` 도구에서 갈라짐으로 거절됩니다. 그때는 카드의 {{ui:delegation.card.merge}} 단추로 병합하세요.

병합한 뒤 자식이 새 실행을 시작하기 전까지는, 창을 닫아도 카드에 {{ui:delegation.card.undo}} 단추가 남습니다. 부모 브랜치가 그 병합에 머물러 있고, 부모 창이 쉬고 있고, 커밋하지 않은 변경이 없을 때만 됩니다. 되돌리면 파일이 병합 전으로 돌아가고, 창이 열린 자식은 다시 {{ui:delegation.child.state.reported}} 상태가 됩니다.

> [!note]
> 병합은 됐는데 위임 기록을 저장하지 못하면 카드가 그렇다고 알려 줍니다. 병합은 그대로이고, 기록은 다음에 저장할 때 다시 씁니다.

## 폐기하기 {#discard}
카드의 {{ui:delegation.card.discard}} 단추는 자식의 worktree와 브랜치를 병합하지 않은 작업까지 함께 지웁니다. 사람만 할 수 있고, 부모는 하지 못합니다. 병합한 자식은 병합 직후에 묻는 창에서도 폐기할 수 있습니다([병합하기](#merge)).

- 확인 창({{ui:delegation.discard.title}})이 자식 worktree 안에 들어 있어 함께 지워질 다른 worktree를 모두 보여 줍니다.
- 그 목록을 확인할 수 없으면 확인 창에 폐기 단추가 나오지 않고, 아무것도 멈추거나 지우지 않습니다.

> [!tip]
> 앱은 병합하지 않은 작업을 스스로 지우지 않습니다. 병합하지 않은 작업이 사라지는 길은 사람이 누르는 폐기뿐입니다.

## 창 닫기와 정리 {#close}
- **병합된 자식 창을 닫으면** 작업이 모두 병합되어 있고 남은 변경이 없을 때 앱이 worktree와 브랜치를 정리합니다(`git branch -d`, 강제 삭제는 하지 않음). 조건이 맞지 않으면 창만 닫히고 worktree는 남습니다.
- **병합하기 전에 자식 창을 닫으면** 앱에서는 더 이상 병합할 수 없습니다. 카드에는 폐기만 남고, 브랜치와 worktree는 폐기할 때까지 그대로 있습니다.
- **열린 자식이 있는 부모 창을 닫으면** 데스크톱 앱에서 먼저 묻습니다({{ui:delegation.parentClose.title}}). 부모 창만 닫히고 자식은 계속 일합니다.
- 부모가 닫힌 자식은 사이드바의 {{ui:delegation.tree.parentClosed}} 항목 아래 카드로 남습니다. 그 자식의 알림은 카드에 보류되어 개수가 보이고, 후속 지시는 더 받지 않습니다.
- 열린 자식이 있거나 정리되지 않은 자식 worktree가 남은 작업 공간은 제거할 수 없습니다.
- {{platform:windows}} 자식 worktree 안에 정션이나 심볼릭 링크(예: 링크로 만든 `node_modules`)가 있으면, 병합된 자식 창을 닫아도 앱이 worktree를 정리하지 않고 남깁니다. 링크를 따라가 그 대상 폴더까지 지우는 일을 막기 위해서입니다. 이때는 카드의 {{ui:delegation.card.discard}} 단추로 지웁니다. 폐기는 링크 자체만 먼저 떼고, 링크가 가리키는 폴더는 건드리지 않습니다.

![[delegation-parent-closed]]

## 알림과 보류 {#notices}
Claude에게 맡긴 자식이 보고하거나 시작하지 못하면 부모에게 알림이 한 번 갑니다. 부모가 시작한 실행(처음 맡긴 일이나 후속 지시)이 보고 없이 끝날 때도 알림이 갑니다. 알림은 `[Mighty Claude notice …]`로 시작하는 짧은 안내일 뿐이고, 보고서 내용은 부모가 `child_status`로 읽습니다. 직접 띄운 자식은 알림을 보내지 않습니다.

- 부모가 Claude로 실행 중이면 알림이 그 실행에 바로 전달됩니다. 실행 중인 Codex 부모는 그 실행이 정상으로 끝난 뒤 다음 실행으로 받습니다.
- 부모가 앱을 연 뒤 정상으로 끝나 쉬고 있으면 알림이 다음 실행을 시작합니다.
- 그 밖의 경우에는 알림이 보류됩니다. 멈춘 뒤, 오류 뒤, 앱을 다시 연 뒤, CLI 업데이트 중이 그렇습니다. 이미 보류된 항목이 있으면 새 알림도 그 뒤에 보류되어 순서가 지켜집니다.

보류된 알림은 부모 입력창 위 대기열 맨 위에 핀 표시가 붙은 행으로 보입니다. 이 행에는 지우기 단추가 없고, 보통 대기 요청과 달리 멈추거나 앱을 다시 열어도 남습니다.

![[delegation-held]]

- 다음에 무언가를 보내면 보류된 항목이 모두, 오래된 것부터, 내 글 앞에 붙어 실행 하나로 들어갑니다.
- {{ui:queue.runNext}} 단추를 누르면 보류된 항목만 실행 하나로 보냅니다.
- 알림은 앱을 다시 열어도 정확히 한 번만 전달됩니다. 보류된 행은 휴대폰 대기열에도 보이고, 휴대폰에서 보내도 함께 전달됩니다.

## 후속 지시 {#follow-up}
부모는 `follow_up` 도구로 Claude에게 맡긴 자식에게 후속 지시를 두 번까지 보낼 수 있습니다. 후속 지시는 알림과 같은 규칙으로 자식 창에 전달되고, 하나에 48 KiB까지입니다. 사람은 자식 창의 입력창에 직접 쓰면 되고, 횟수 제한이 없습니다.

## 켜고 끄기 {#enable}
{{platform:mac}} 끄지 않는 한 켜져 있습니다. 설정의 {{ui:settings.nav.general}} 탭에서 {{ui:settings.delegation.toggle}}을 켜고 끕니다. 앱을 다시 열지 않아도 다음에 시작하는 실행부터 적용됩니다. 같은 스위치가 defaults 키 `delegation.enabled`이므로 터미널에서 다음 명령으로 꺼도 되고, `defaults delete dev.mightyclaude.native delegation.enabled`로 다시 켭니다.

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} 예전 버전은 그 키를 켜야만 켜졌고, 끄는 방법이 `defaults delete …`였습니다. 이제 그렇게 하면 켜진 채로 남으므로, 계속 꺼 두려면 설정에서 {{ui:settings.delegation.toggle}}을 끄세요.

{{platform:windows}} 끄지 않는 한 켜져 있습니다. 설정의 {{ui:settings.nav.general}} 탭에서 {{ui:settings.delegation.toggle}}을 켜고 끕니다. 앱을 다시 열지 않아도 다음에 시작하는 실행부터 적용됩니다. 같은 스위치가 `HKEY_CURRENT_USER\Software\MightyClaudeNative` 아래의 `delegation.enabled` 값(REG_DWORD)이라서, 0이면 꺼지고 값이 없거나 다른 값이면 켜집니다. 그래서 명령 프롬프트나 PowerShell에서 다음 명령으로 꺼도 되고, `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f`로 다시 켭니다.

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 0 /f
```

{{platform:windows}} 예전 버전은 그 값을 1로 넣어야만 켜졌습니다. 그때 넣어 둔 1은 켜진 상태를 그대로 둡니다.

- 켜면 Claude나 Codex 창에서 새 실행을 시작할 때마다 위임 도구가 붙습니다. 이미 돌고 있는 실행에는 붙지 않습니다.
- 끄면 도구만 떨어지고 {{kbd:⌘Enter}}는 {{kbd:Enter}}처럼 요청을 대기열에 넣습니다. 이미 있는 자식 창과 카드, 정리는 그대로 동작합니다.

## 한도와 지원 범위 {#limits}
| 항목 | 한도 |
|---|---|
| 부모 하나의 열린 자식 | 직접 띄운 3개, `delegate`로 만든 3개 |
| 위임 깊이 | 한 층(자식은 다시 자식을 만들지 못함) |
| 자식 하나의 후속 지시(`follow_up`) | 2개, 하나에 48 KiB |
| 맡기는 일(TASK.md, 직접 띄운 요청) | 64 KiB |
| 필요한 디스크 여유 | 10 GB |

- 자식은 부모와 같은 에이전트로 실행되고, 다른 에이전트로도 실행됩니다([Codex와 Antigravity](#agents)). Claude와 Codex 창은 도구와 {{kbd:⌘Enter}}로, Antigravity 창은 {{kbd:⌘Enter}}로만 자식을 만듭니다([Codex와 Antigravity](#agents)).
- Mac 앱과 Windows 앱 모두 기본으로 켜져 있고 설정에 스위치가 있습니다([켜고 끄기](#enable)). 휴대폰에는 새 화면이 없고, 보류된 행은 휴대폰 대기열에도 보입니다.
