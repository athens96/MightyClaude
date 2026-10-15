---
title: 에이전트 위임
order: 6
section: delegation
---
에이전트 위임은 Claude 창(부모)이 일을 나눠 다른 Claude 창(자식)에게 맡기는 기능입니다. 자식은 저마다 자기 git worktree에서 일하고, 끝나면 보고서를 남깁니다. 부모는 알림을 받아 보고서를 읽고, 결과를 병합할지 정합니다. Mac 앱과 Windows 앱에서 됩니다. Mac에서는 실행 중인 Claude 창의 입력창에서 자식을 직접 띄울 수도 있습니다([새 worktree에서 실행하기](#worktree)).

> [!warning]
> 실험 기능입니다. Mac에서는 기본으로 켜져 있고 설정에서 끌 수 있습니다. Windows에서는 기본으로 꺼져 있고 설정 화면이 없습니다. 연습용 저장소에서 먼저 써 보세요.

## 켜고 끄기 {#enable}
{{platform:mac}} 끄지 않는 한 켜져 있습니다. 설정의 {{ui:settings.nav.general}} 탭에서 {{ui:settings.delegation.toggle}}을 켜고 끕니다. 앱을 다시 열지 않아도 다음에 시작하는 실행부터 적용됩니다. 같은 스위치가 defaults 키 `delegation.enabled`이므로 터미널에서 다음 명령으로 꺼도 되고, `defaults delete dev.mightyclaude.native delegation.enabled`로 다시 켭니다.

```sh
defaults write dev.mightyclaude.native delegation.enabled -bool false
```

{{platform:mac}} 예전 버전은 그 키를 켜야만 켜졌고, 끄는 방법이 `defaults delete …`였습니다. 이제 그렇게 하면 켜진 채로 남으므로, 계속 꺼 두려면 설정에서 {{ui:settings.delegation.toggle}}을 끄세요.

{{platform:windows}} 기본으로 꺼져 있습니다. 앱을 끈 채로 명령 프롬프트나 PowerShell에서 다음 명령을 실행하고 앱을 다시 엽니다.

```
reg add "HKCU\Software\MightyClaudeNative" /v delegation.enabled /t REG_DWORD /d 1 /f
```

Windows에서는 `HKEY_CURRENT_USER\Software\MightyClaudeNative` 아래의 `delegation.enabled` 값(REG_DWORD)이 0이 아니면 켜진 것입니다. 값이 없거나 0이면 꺼져 있습니다.

- 켜면 Claude 창에서 새 실행을 시작할 때마다 위임 도구가 붙습니다. 이미 돌고 있는 실행에는 붙지 않습니다.
- 부모가 쓰는 도구는 `delegate`(맡기기), `list_children`(자식 목록), `child_status`(상태와 보고서 읽기), `merge`(병합), `follow_up`(후속 지시)입니다. `discard`(폐기)는 부모가 부를 수 없고 사람만 카드에서 합니다.
- Windows에서 끄려면 `reg delete "HKCU\Software\MightyClaudeNative" /v delegation.enabled /f`를 실행하고 앱을 다시 엽니다. 꺼도 도구만 떨어지고(Mac에서는 {{kbd:⌘Enter}}가 다시 실행 중인 Claude에 바로 전달합니다), 이미 있는 자식 창과 카드, 정리는 그대로 동작합니다.

## 일을 맡기기 {#delegate}
1. git 저장소 폴더를 작업 공간으로 열고 Claude 창을 엽니다. 이 창이 부모입니다.
2. "delegate 도구로 Mighty 자식 창 두 개를 열어 이 일을 나눠 맡겨"처럼 도구 이름을 넣어 요청합니다. 그냥 "나눠서 해"라고 하면 Claude가 자기 안의 하위 에이전트로 처리할 수 있습니다.
3. 자식 창이 부모 옆에 열리고, 사이드바에서 부모 아래 한 칸 들여 보입니다.

![[delegation-tree]]

- 자식마다 브랜치 `mighty/<자식 id>`와 worktree(Mac은 `~/.mightyclaude/worktrees/<자식 id>`, Windows는 `%USERPROFILE%\.mightyclaude\worktrees\<자식 id>`)가 생깁니다. worktree는 프로젝트 폴더 밖에 있어서 부모의 작업을 건드리지 않습니다.
- 자식은 부모의 마지막 커밋에서 시작합니다. 부모 폴더에서 아직 커밋하지 않은 변경은 자식에게 가지 않습니다.
- 맡긴 일은 TASK.md에, 결과는 REPORT.md에 적힙니다. 두 파일은 worktree 안의 git이 무시하는 폴더에 있어 커밋되지 않습니다.
- 자식의 권한 모드는 부모가 고른 모드이고, 부모 창의 모드보다 넓을 수 없습니다. 넓히는 것은 사람만, 실행과 실행 사이에 자식 창 입력창의 권한 메뉴나 휴대폰 설정에서 할 수 있습니다.
- 부모는 자식의 승인 카드에 답하지 못합니다. 자식 창에 카드가 뜨면 그 창에서 직접 답하세요.
- 부모 하나에 `delegate` 도구로 만든 열린 자식은 3개까지이고(Mac에서는 직접 띄운 자식 3개가 따로 더 있습니다. [새 worktree에서 실행하기](#worktree)를 보세요), 자식은 다시 위임하지 못합니다. 보고했거나 병합된 자식도 창이 열려 있으면 3개에 들어가므로, 끝난 자식 창을 닫으면 자리가 납니다. 디스크 여유가 10 GB보다 적으면 맡기기가 거절됩니다.

## 새 worktree에서 실행하기 {#worktree}
{{platform:mac}} 자식을 직접 띄울 수도 있습니다. Claude 창이 실행 중일 때 요청을 쓰고 {{kbd:⌘Enter}}를 누르거나, 중지 단추 옆의 둥근 {{ui:queue.worktree}} 단추(브랜치 아이콘)를 누릅니다. 돌고 있는 작업은 그대로 이어지고, 요청은 자기 worktree를 가진 새 자식 창에서 바로 실행됩니다. 새 자식 창은 사이드바에서 그 창 아래에 나타납니다. 그사이 부모 창에 글을 쓰기 시작하지 않았다면 새 창이 곧바로 화면에 보입니다.

- {{kbd:⌘Enter}}는 worktree를 만들 수 있는 곳에서만 새 worktree를 씁니다. 커밋이 있는 브랜치가 체크아웃된 git 저장소여야 합니다. 그 밖의 곳(git 저장소가 아님, 커밋이 아직 없음, 브랜치가 아닌 커밋이 체크아웃됨)에서는 예전처럼 실행 중인 Claude에 바로 전달합니다.
- 부모의 `delegate`가 만들 때와 같은 방식으로 만들어집니다. 부모의 마지막 커밋에서 브랜치 `mighty/<자식 id>`를 만들고, worktree는 프로젝트 밖에 두며, 디스크 여유 10GB가 필요합니다. 직접 띄운 열린 자식은 창 하나에 3개까지이고, `delegate`가 만들 수 있는 3개와는 따로 셉니다. 어느 쪽도 다른 쪽을 막지 않습니다.
- 부모 창의 권한 모드로, 부모 창의 모델과 {{ui:composer.effort.label}}로 실행합니다. 모든 요청을 계획 모드로 시작하는 스타일에서는 계획 모드로 시작합니다. 첨부 파일도 함께 갑니다.
- 첫 메시지에는 입력한 글이 먼저 오고, 그 뒤에 앱이 자식이 일하는 곳을 알려 주며 끝나면 커밋하고 REPORT.md를 쓰라고 덧붙입니다. 그래야 카드에서 병합할 수 있습니다. 슬래시 명령(`/`로 시작하는 글)은 입력한 그대로 혼자 실행되고, 앱의 안내는 자식 창의 다음 요청으로 따라가므로 명령의 인자가 되지 않습니다.
- 직접 띄운 자식에 대해서는 부모가 알림을 받지 않습니다. 부모가 맡긴 일이 아니기 때문입니다. 부모의 도구도 이 자식을 목록에 보여 주지 않고, 병합하거나 후속 지시를 보내지 못합니다(`human_started`). 사이드바에서 상태를 보고, 카드의 {{ui:delegation.card.merge}}과 {{ui:delegation.card.discard}} 단추를 직접 씁니다.
- 자식을 만들 수 없거나 만든 뒤 시작하지 못하면(직접 띄운 열린 자식이 이미 3개, 디스크 부족, worktree나 첫 실행 실패 등) 창의 대화에 이유가 남습니다. 그러면 글은 실행 중인 Claude가 받을 수 있을 때(첨부 파일 없음) 그쪽에 바로 전달되고, 아니면 {{kbd:Enter}}를 누른 것처럼 그 창의 다음 요청이 됩니다. 대기열이 가득 차 있으면 입력창으로 돌아갑니다. 시작하지 못한 자식은 카드에서 폐기할 때까지 목록에 남습니다.
- 자식 창이나 위임을 끈 창에서는 {{kbd:⌘Enter}}가 예전처럼 실행 중인 Claude에 바로 전달하고, Codex와 Antigravity 창은 계속 대기열에 넣습니다. 휴대폰의 보내기 방식은 그대로여서, 휴대폰에서 바로 보내면 여전히 실행 중인 Claude로 갑니다.

## 자식의 상태 {#states}
사이드바에서 자식마다 상태가 보입니다.

| 상태 | 뜻 |
|---|---|
| {{ui:delegation.child.state.creating}} | worktree와 창을 만드는 중입니다. |
| {{ui:delegation.child.state.running}} | 일하고 있습니다. |
| {{ui:delegation.child.state.waiting}} | 승인이나 질문에 사람의 답을 기다립니다. |
| {{ui:delegation.child.state.reported}} | REPORT.md를 남기고 실행을 마쳤습니다. |
| {{ui:delegation.child.state.merged}} | 부모 브랜치에 병합되었습니다. |
| {{ui:delegation.child.state.ended}} | 새 보고 없이 실행이 끝났습니다. |
| {{ui:delegation.child.state.interrupted}} | 앱을 끄는 바람에 실행이 끊겼습니다. |
| {{ui:delegation.child.state.failed}} | 자식 창을 시작하지 못했습니다. |
| {{ui:delegation.child.state.closed}} | 창이 닫혔습니다. 폐기할 때까지 목록에 남습니다. |

자식이 새 실행을 시작하면 {{ui:delegation.child.state.reported}} 상태가 풀리고, 다시 보고하면 다음 보고 번호가 붙습니다.

## 알림과 보류 {#notices}
자식이 보고하거나 시작하지 못하면 부모에게 알림이 한 번 갑니다. 부모가 시작한 실행(처음 맡긴 일이나 후속 지시)이 보고 없이 끝날 때도 알림이 갑니다. 알림은 `[Mighty Claude notice …]`로 시작하는 짧은 안내일 뿐이고, 보고서 내용은 부모가 `child_status`로 읽습니다.

- 부모가 실행 중이면 알림이 그 실행에 바로 전달됩니다.
- 부모가 앱을 연 뒤 정상으로 끝나 쉬고 있으면 알림이 다음 실행을 시작합니다.
- 그 밖의 경우에는 알림이 보류됩니다. 멈춘 뒤, 오류 뒤, 앱을 다시 연 뒤, CLI 업데이트 중이 그렇습니다. 이미 보류된 항목이 있으면 새 알림도 그 뒤에 보류되어 순서가 지켜집니다.

보류된 알림은 부모 입력창 위 대기열 맨 위에 핀 표시가 붙은 행으로 보입니다. 이 행에는 지우기 단추가 없고, 보통 대기 요청과 달리 멈추거나 앱을 다시 열어도 남습니다.

![[delegation-held]]

- 다음에 무언가를 보내면 보류된 항목이 모두, 오래된 것부터, 내 글 앞에 붙어 실행 하나로 들어갑니다.
- {{ui:queue.runNext}} 단추를 누르면 보류된 항목만 실행 하나로 보냅니다.
- 알림은 앱을 다시 열어도 정확히 한 번만 전달됩니다. 보류된 행은 휴대폰 대기열에도 보이고, 휴대폰에서 보내도 함께 전달됩니다.

## 후속 지시 {#follow-up}
부모는 `follow_up` 도구로 자식에게 후속 지시를 두 번까지 보낼 수 있습니다. 후속 지시는 알림과 같은 규칙으로 자식 창에 전달되고, 하나에 48 KiB까지입니다.

## 병합하기 {#merge}
자식의 결과를 부모 브랜치에 넣는 방법은 둘입니다.

1. **부모의 `merge` 도구**: 빨리 감기(fast-forward)만 합니다. 부모는 `child_status`가 준 자식의 head를 함께 넘깁니다. 자식이 아직 보고하지 않았거나, 자식이나 부모 폴더에 커밋하지 않은 변경이 있거나, 자식의 head가 움직였거나, 부모 브랜치가 그새 더 나아갔거나(갈라짐), 부모 브랜치가 체크아웃되어 있지 않으면 거절하고 아무것도 바꾸지 않습니다. 이 도구에 승인 카드가 뜨는지는 부모 창의 권한 모드에 따릅니다.
2. **카드의 {{ui:delegation.card.merge}} 단추**: 사람이 누릅니다. 빨리 감기가 되면 그렇게 하고, 안 되면 병합 커밋을 정확히 하나 만듭니다. 먼저 충돌을 미리 살펴 보고, 거절되면 그 이유를 카드에 보여 줍니다.

> [!tip]
> 자식 하나를 병합하면 부모 브랜치가 앞으로 가므로, 나머지 자식은 보통 `merge` 도구에서 갈라짐으로 거절됩니다. 그때는 카드의 {{ui:delegation.card.merge}} 단추로 병합하세요.

병합한 뒤 자식이 새 실행을 시작하기 전까지는, 창을 닫아도 카드에 {{ui:delegation.card.undo}} 단추가 남습니다. 부모 브랜치가 그 병합에 머물러 있고, 부모 창이 쉬고 있고, 커밋하지 않은 변경이 없을 때만 됩니다. 되돌리면 파일이 병합 전으로 돌아가고, 창이 열린 자식은 다시 {{ui:delegation.child.state.reported}} 상태가 됩니다.

> [!note]
> 병합은 됐는데 위임 기록을 저장하지 못하면 카드가 그렇다고 알려 줍니다. 병합은 그대로이고, 기록은 다음에 저장할 때 다시 씁니다.

## 폐기하기 {#discard}
카드의 {{ui:delegation.card.discard}} 단추는 자식의 worktree와 브랜치를 병합하지 않은 작업까지 함께 지웁니다. 사람만 할 수 있고, 부모는 하지 못합니다.

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

## 한도와 지원 범위 {#limits}
| 항목 | 한도 |
|---|---|
| 부모 하나의 열린 자식 | `delegate`로 만든 3개, 직접 띄운 3개(Mac) |
| 위임 깊이 | 한 층(자식은 다시 위임하지 못함) |
| 자식 하나의 후속 지시 | 2개, 하나에 48 KiB |
| 맡기는 일(TASK.md) | 64 KiB |
| 필요한 디스크 여유 | 10 GB |

- 부모와 자식 모두 Claude 창이어야 합니다. Codex와 Antigravity(Gemini) 창은 위임하지 않습니다.
- Mac 앱은 기본으로 켜져 있고 설정에 스위치가 있습니다. Windows 앱은 숨은 스위치로 켭니다([켜고 끄기](#enable)). 휴대폰에는 새 화면이 없고, 보류된 행은 휴대폰 대기열에도 보입니다.
