# 대기열 행마다 바로 실행

2026-10-29 · 사용자 요청: "대기열에 올린 후에 이 작업을 바로 실행할 수 있는 버튼이 있는데 이 버튼은 각각의 대기 작업마다 달리는 게 좋겠고 X 표시 옆에 바로 실행으로 버튼을 추가하는 게 맞겠다. 이때는 워크트리 만들어서 바로 실행하는 기능이 된다."

정한 것(사용자):
- 대기열 머리의 **다음 실행**(`queue.runNext`, "대기 중인 첫 요청을 지금 실행") 단추 하나를 없앤다. 대신 대기열의 모든 행이 X(지우기) 바로 옆에 **바로 실행** 단추를 가진다.
- 바로 실행은 그 행의 요청을 새 worktree 자식 창에서 바로 실행한다. ⌘Enter / Ctrl+Enter "새 worktree에서 실행"(마이티 오케스트레이션)과 같은 길이다. 요청은 대기열에서 빠진다. 기본 에이전트는 이 창의 에이전트다. 오른쪽 클릭(맥은 Control-클릭도)으로 다른 에이전트를 고른다. 입력창의 worktree 단추와 같다.
- worktree를 만들 수 없으면(git 저장소가 아님, 위임 스위치 꺼짐, 능력 규칙상 부모나 자식이 될 수 없는 에이전트, 원격 작업 공간 창 …) 바로 실행은 그 요청을 **이 창에서** 지금 실행한다. 예전 다음 실행이 하던 일이다. 단추의 도움말이 둘 중 무엇이 일어날지 말한다.
- 맥, 윈도우(웹 창, 그리고 대기열 행을 그리는 WinUI 대체 입력창), 폰 모두. 폰의 대기열 행도 같은 단추를 가진다. worktree냐 이 창이냐는 호스트가 정한다. 폰은 새 동작 `POST /m1/sessions/{id}/queue/{itemId}/run-now`를 보낸다. 이 동작이 없는 옛 호스트에서는 폰이 단추를 숨긴다. 옛 폰은 지금처럼 다음 실행(`/queue/run-next`)을 보내고, 호스트는 계속 답한다.

정한 것(첫 설계 뒤 사용자 답):
- **보류된 행:** 바로 실행은 **첫 보류된 행에만** 있다. 도움말은 보류된 항목 N개를 모두 이 창에 보낸다고 말한다(`queue.runNowHeldHelp`, `{count}`). 나머지 보류된 행에는 단추가 없다.
- **지금 실행할 수 없는 행:** 단추를 숨기지 않고 **비활성**으로 그린다. 도움말은 이유를 말한다(예: "지금 작업이 끝나면 순서대로 실행됩니다"). 그래서 사람의 행은 모두 단추 자리를 지킨다. 보류된 항목이 먼저 가야 해서 막힌 사람의 행도 맞는 이유와 함께 비활성 단추를 가진다. 폰도 비활성으로 그린다.
- **쉬는 창:** 이 창에서 실행한다.

## 지금 동작

### 맥
- **대기열 그리기:** `QueuedInputsView`(`native/macos/Sources/MightyClaude/QueuedInputsView.swift`)가 입력창 위에 그린다.
  - 머리 줄: 시계, "대기 중 N개", 그리고 조건이 맞으면 오른쪽 끝의 **다음 실행**(`queue-run-{세션}`).
  - 보류된 행(위임의 알림·후속 지시, `delegationHeld`): 위쪽에 핀이 붙고 지우기 단추가 없다.
  - 사람의 행: 번호, 글(두 줄), 첨부 이름, 그리고 X(`queue-remove-{행}`).
- **다음 실행이 보이는 때:** `DelegationQueueRows.offersRunNext(busy:held:steers:)` = 보류된 항목이 있거나, 창이 쉬고 있거나(오류로 멈춘 대기열), 첫 요청을 백그라운드 작업만 기다리는 실행에 넣을 수 있을 때(`BackgroundQueuePolicy.steersRunNext`: 로컬 Claude, 중지 중 아님, 턴이 끝나고 백그라운드 작업만 남음, 첫 요청에 첨부 없음).
- **다음 실행이 하는 일:** `AppStore.runNextQueuedInput(_:own:)`.
  - 보류된 항목이 있으면 `runNextHeld` → `DelegationCoordinator.runNext(in:)`. 보류된 항목만, 모두, 실행 하나로 보낸다. 실행 중인 Claude 실행이면 그 실행에 steer하고, 아니면 다음 실행으로 시작한다. 사람의 행은 그 뒤 실행에서 차례대로 간다.
  - 아니면 첫 행을 steer할 수 있을 때 `steerQueuedRow`로 그 실행에 새 턴으로 넣는다. 다이어그램에는 그 요청 아래 **중간 요청**으로 보이고, 결과에는 **중간 요청 이후** 절(`graph.result.afterSteer`)이 붙는다.
  - 아니면 `runNextQueuedRow` → `settleQueue(status: "completed")`로 첫 행을 시작한다. 보류된 항목이 있으면 그 실행의 앞에 붙는다(`releaseHeld`). 오류로 멈췄던 대기열은 이로써 다시 흐른다.
- **⌘Enter 새 worktree:** 바쁜 창에서 `submit(secondSend: true)`. `ComposerSecondSend.choose(kind:provider:mode:isChild:delegationOn:canHostWorktree:)`가 `.worktree`면 `runInNewWorktree`로 자식을 만든다.
  - `delegationOn`은 위임이 켜져 있고 원격 창이 아닐 때 참이다. `canHostWorktree`는 `worktreeReadiness` 캐시(git 저장소, 커밋 있는 브랜치)다.
  - 이 창의 에이전트가 그 모드에서 자식을 둘 수 없지만 다른 에이전트는 둘 수 있으면 `ComposerSecondSend.menuOnly`가 참이다. 단추를 누르면 에이전트 메뉴가 열린다.
  - 자식을 만들지 못하면 `runAfterNoWorktree`(대기열에 넣거나 지금 시작)다. 다른 에이전트를 골랐던 글이면 입력창으로 돌아간다(`returnToComposer`).
  - 글이 없는(첨부만 있는) 요청은 worktree로 가지 않는다.
  - 사람이 띄운 자식은 부모 창을 보고 있고 거기 글을 쓰고 있지 않으면 바로 선택된다(`createPane`). 그 자식의 첫 실행은 탭이 보일 때 "내 요청"이다(`paneTabShown`).
- **폰:** `POST /m1/sessions/{id}/queue/run-next` → `mobileRunNextQueued`. 다음 실행과 같지만 맥 화면은 따라가기만 한다(`own: false`).

### 윈도우
- 대기열은 WinUI `PaneView`가 가진다(`QueuedInputBuffer`, `MainWindow.QueuedComposer.cs`).
- **WinUI 대체 입력창**(`RenderQueuedInputs`)도 맥과 같은 행을 그린다. 머리의 다음 실행, 보류된 행의 핀, 사람 행의 X.
- **웹 창**(`native/windows/web/src/composer/Composer.tsx`)은 호스트가 준 `queue { held, items, runNext, waitsOnBackground }`(`MainWindow.WebShell.Composer.cs` `WebComposerState`)를 그린다. `queueRunNext`, `queueRemove` 다리를 부른다.
- 다음 실행은 `RunNext()`: 보류 → `RunNextAsync`, 아니면 `SteerFirstQueuedInput`, 아니면 `StartNextQueuedInput`.
- Ctrl+Enter는 `DeferBusyComposer` → `RunInNewWorktree`. WinUI의 worktree 단추는 오른쪽 클릭(Shift+F10)으로 에이전트 메뉴(`MainWindow.WorktreeMenu.cs`)를 연다. 웹 창의 worktree 단추에는 아직 에이전트 메뉴가 없다.
- 폰: `MobileDesktopRouter`의 `queue/run-next` → `"queue-next"` 동작 → `PaneView.MobileRunNext`.

### 폰
- `QueuedList`(`mobile/src/components/queued-list.tsx`)가 머리에 다음 실행(창이 실행 중이 아닐 때, `canRunNext={!running}`), 행마다 삭제를 그린다. 호스트가 `queue` 능력을 알릴 때만 다룰 수 있다.
- 다음 실행은 `client.runNext` → `/queue/run-next`. 성공하면 "다음 요청을 시작했습니다"를 띄우고 첫 행의 글로 내 요청을 지켜본다(`newPendingRequest`).
- 대기열 행의 모양은 `{ id, text }`다. 보류된 행인지는 모른다.
- 폰은 실행 중인 턴에 글을 넣지 않는다(`canSteer`는 늘 거짓).

## 바뀌는 동작

### 1. 규칙 하나: 바로 실행이 하는 일
맥 MightyCore `QueueRunNow`, 윈도우 Core `QueueRunNow`. 같은 픽스처를 읽는다. 행 하나와 창의 지금 상태를 받아 다음 중 하나를 돌려준다.

| 결과 | 뜻 | 단추 | 도움말 |
| --- | --- | --- | --- |
| `worktree` | 이 창 에이전트의 새 자식 창이 자기 worktree에서 이 행을 지금 실행한다. 오른쪽 클릭으로 다른 에이전트 | 브랜치, 활성 | `queue.runNowWorktreeHelpMac` / `queue.runNowWorktreeHelp` |
| `agentMenu` | 이 창 에이전트는 이 모드에서 자식을 둘 수 없고 다른 에이전트는 둘 수 있다. 누르면 에이전트 메뉴 | 브랜치, 활성 | `queue.worktreeMenuHintMac`(지금 문구 그대로) |
| `here` | 이 창의 다음 실행으로 지금 시작한다. 보류된 항목이 있으면 그 실행의 앞에 붙는다 | 재생, 활성 | `queue.runNowHereHelp` |
| `steer` | 백그라운드 작업만 기다리는 이 창의 실행에 새 턴으로 넣는다(중간 요청) | 재생, 활성 | `queue.runNowSteerHelp` |
| `held` | (첫 보류된 행) 이 창에 보류된 항목을 모두 함께 지금 이 창에 보낸다 | 재생, 활성 | `queue.runNowHeldHelp`(`{count}`) |
| `waitsForTurn` | 지금 실행할 수 없다. 지금 작업이 끝나면 차례대로 실행된다(알림 턴이 도는 동안의 첫 보류된 행도) | 재생, 비활성 | `queue.runNowUnavailableHelp` |
| `waitsForHeld` | 백그라운드 작업만 기다리는 실행에 넣을 수 있지만, 보류된 항목이 먼저 가야 한다 | 재생, 비활성 | `queue.runNowAfterHeldHelp` |
| `waitsForUpdate` | 창이 CLI 업데이트로 붙잡혀 있다 | 재생, 비활성 | `queue.runNowUpdateHelp` |
| `hidden` | (첫 보류된 행이 아닌 보류된 행) 단추 없음 | – | – |

판단 순서:
1. **보류된 행:** 첫 보류된 행(`heldIndex == 0`)이면 `held`, 나머지는 `hidden`. 보류된 항목은 언제나 함께 가므로 단추는 하나면 된다.
   - 바쁜 창은 보류된 항목을 중간 요청으로만 받는다. 그래서 창이 바쁘고 **알림 턴이 돌고 있으면**(Claude가 끝난 백그라운드 작업에 답하는 중: 창의 사실 `noticeTurnRunning`, `MightyGraphSupport.noticeTurnRunning`) 첫 보류된 행도 `waitsForTurn`이다. 그 턴에는 아무것도 끼워 넣지 않는다. 창이 쉬거나 알림 턴이 없으면 `held` 그대로다.
2. **창이 쉬고 있으면**(실행 중도, 시작 중도, CLI 업데이트로 붙잡힌 것도 아님. 오류로 멈춘 대기열이 그렇다) `here`. 돌고 있는 작업이 없으니 worktree로 나눌 까닭이 없다.
3. **CLI 업데이트로 붙잡힌 창**이면 `waitsForUpdate`. ⌘Enter도 그때는 worktree를 열지 않는다. 사용자가 시작한 CLI 업데이트나 플러그인 변경도 같다(맥 `manualUpdateHolds`, 윈도우 `ManualMutationBlockReason`). 마지막 실행의 끝을 아직 기록하는 자식 창은 바쁜 창으로 읽는다.
   - **Antigravity 권한 카드의 이어짐**(허용한 카드가 대기열에 넣은 후속 요청, 맥 `QueuedInput.antigravityGrant`/`antigravityCard`, `isContinuation`)이면 `waitsForTurn`. 그 허용 규칙과 카드는 이 창의 다음 실행 몫이라 worktree 자식, 에이전트 메뉴, steer 어느 길로도 가지 않는다. 창이 쉬면 2번에서 `here`다. 픽스처 행의 `continuation`.
4. 행에 **글**이 있고 `ComposerSecondSend.choose`가 `.worktree`면 `worktree`.
5. 행에 글이 있고 `menuOnly`면 `agentMenu`.
6. 창이 **백그라운드 작업만 기다리고**(로컬 Claude, 중지 중 아님, 턴이 끝남), 알림 턴이 돌고 있지 않고(`noticeTurnRunning` 거짓), 행에 첨부가 없으면, 보류된 항목이 없을 때 `steer`, 있을 때 `waitsForHeld`.
7. 나머지는 `waitsForTurn`.

- 4~5는 이유를 따로 받지 않는다. 이유는 모두 `choose`와 `menuOnly`의 입력에 들어 있다.
  - git 저장소가 아니거나, 커밋이 없거나, 브랜치가 아닌 것이 체크아웃됨: `canHostWorktree` 거짓.
  - 위임 스위치가 꺼짐, 또는 원격 작업 공간 창: `delegationOn` 거짓.
  - 자식 창: `isChild`.
  - 능력 규칙(`DelegationProviders.canParentByHuman`, `childCanWrite`): `kind`, `provider`, `mode`.
- **worktree가 steer를 이긴다.** 백그라운드 작업만 기다리는 창도 worktree를 만들 수 있으면 새 자식에서 돈다. 사용자 결정 2가 worktree를 기본으로 정했고, 원래 실행은 백그라운드 작업을 그대로 기다린다.
- **바쁜데 worktree도 steer도 안 되는 행은 비활성 단추를 가진다**(`waitsForTurn`). 지금 턴이 돌고 있으면 사람의 글은 그 턴에 끼어들지 않는다는 규칙(`BackgroundQueuePolicy`) 때문이다. 사용자 결정 3의 "이 창에서 지금"은 이 창이 받을 수 있을 때(쉬고 있거나, 백그라운드 작업만 기다릴 때)만 뜻이 있다. 첨부만 있는 행이 그런 창에 있을 때도 같다.
- **보류된 항목이 있을 때 사람의 행:** worktree는 그대로 된다. 보류된 항목은 이 창 몫이라 이 창에 남는다. steer는 하지 않는다(`waitsForHeld`). 보류된 항목은 이 창의 다음 요청보다 먼저 가야 하고, steer로는 둘을 함께 안전하게 넣을 수 없다. 도움말이 첫 보류된 행의 바로 실행으로 그것들을 먼저 보내라고 말한다. 창이 쉬고 있으면 `here`로 시작하는 실행의 앞에 보류된 항목이 붙는다(지금 `settleQueue`와 같다).
- **비활성 단추와 도움말:** 비활성 컨트롤에는 도움말이 뜨지 않는 플랫폼이 있다. 맥 SwiftUI와 WinUI는 도움말을 단추를 감싼 뷰에 단다. 웹은 `disabled` 대신 `aria-disabled="true"`를 쓴다. 그래서 마우스를 올리면 이유가 보이고, 키보드 포커스도 받는다(누르면 아무 일도 없다).

### 2. 화면

**맥 `QueuedInputsView`**
- 머리의 다음 실행을 없앤다. 머리 줄은 시계와 개수만 남는다.
- 사람의 행: 글 다음, X 바로 앞에 **바로 실행** 글자 단추를 둔다. 10pt medium, 강조색(예전 다음 실행과 같은 모양)이고, 아이콘은 위 표대로다. 식별자는 `queue-run-now-{행}`이다.
  - `worktree`: 누르면 이 창 에이전트의 자식. `.contextMenu`로 에이전트 메뉴. 메뉴는 입력창 worktree 메뉴와 같은 줄을 쓰되 "(⌘Enter)" 표시는 뺀다(`worktreeChoiceTitle(_:parent:marksShortcut:)`).
  - `agentMenu`: `Menu`로, 누르면 메뉴가 열린다.
  - `here`, `steer`: 누르면 실행.
  - `waitsForTurn`, `waitsForHeld`, `waitsForUpdate`: 같은 자리에 같은 모양의 단추를 흐린 색(`Palette.ink2`)으로 비활성 상태로 그리고, 감싼 뷰의 도움말이 이유를 말한다.
- 보류된 행: **첫 보류된 행**의 핀 앞에만 바로 실행(`held`)을 둔다. 도움말은 보류된 항목 N개를 모두 이 창에 지금 보낸다고 말한다. 다른 보류된 행에는 핀만 있다(`hidden`).
- 모달이 떠 있으면(`store.hasModal`) 활성 단추도 비활성이 된다(예전 다음 실행과 같다). 이때는 이유 도움말을 달지 않는다.
- 행의 손쉬운 사용: `.accessibilityElement(children: .combine)`을 `.contain`으로 바꾼다. 그래야 VoiceOver와 전체 키보드 접근에서 바로 실행과 X가 따로 닿는다. 메뉴는 Control-클릭, 오른쪽 클릭, VoiceOver의 메뉴 보기로 연다.
- 대기열이 보이는 동안 `refreshWorktreeReadiness(for:)`를 부른다. 그래야 git 판단이 캐시에 있다.

**윈도우 웹 창**
- `queue.runNext`를 없애고, `held[]`와 `items[]`의 각 행에 호스트가 정한 `runNow`를 싣는다. 값은 위 표의 결과 이름이고, `hidden`이면 null이다.
- 행마다 X 앞에 `RunNowButton`을 둔다. 맥과 같은 아이콘과 도움말을 쓴다(`queue.runNowWorktreeHelp`는 "오른쪽 클릭"만 말한다). `waitsFor…` 행은 `aria-disabled="true"`와 흐린 모양으로 그리고, `title`이 이유를 말한다.
- 오른쪽 클릭(그리고 키보드의 Shift+F10, 메뉴 키. WebView2에서 모두 `contextmenu` 이벤트다)은 `worktree`와 `agentMenu` 행에서 에이전트 메뉴를 연다. 메뉴 줄은 새 다리 `queueRunNowMenu`가 호스트의 `WorktreeChildChoices`로 만든다. 고르면 `queueRunNow { itemId, agent }`.
- `queueRunNext` 다리는 없앤다(웹만 부르던 것).

**윈도우 WinUI 대체 입력창:** 같은 행 단추. `worktree` 행은 `ContextFlyout`로 에이전트 메뉴(WinUI는 오른쪽 클릭, Shift+F10, 메뉴 키를 스스로 지원한다). `agentMenu` 행은 누르면 메뉴를 연다. `waitsFor…` 행은 비활성 단추이고, 도움말은 그 단추를 감싼 `Border`에 단다.

**폰 `QueuedList`**
- 머리의 다음 실행을 없앤다.
- 행의 `runNow`가 `"worktree"`나 `"here"`이면 삭제 앞에 **바로 실행** 단추를 둔다. 손쉬운 사용 이름은 "{글} 바로 실행"이고, 힌트는 worktree면 `phone.queue.runNowWorktreeHint`, 아니면 `queue.runNowHereHelp`다.
- `runNow`가 `"unavailable"`이면 같은 자리에 흐린 단추를 그린다(`Button`의 새 `dimmed`).
  - 누르면 실행하지 않고 이유를 알림으로 띄운다. 폰에는 마우스 도움말이 없기 때문이다.
  - 손쉬운 사용 상태는 비활성이고, 힌트는 그 이유다.
  - 이유는 `runNowReason`으로 고른다.
    - `"turn"` → `queue.runNowUnavailableHelp`
    - `"held"` → `queue.runNowAfterHeldHelp`
    - `"update"` → `queue.runNowUpdateHelp`
    - `"agent"` → `phone.queue.runNowAgentOnComputer`
    - 모르는 값이나 빈 값 → `"turn"`
- 폰은 에이전트를 고르지 않는다(오른쪽 클릭이 없다). `agentMenu` 행은 폰에서 `"unavailable"`, `"agent"`다. 이 창 에이전트는 자식을 둘 수 없고, 다른 에이전트는 컴퓨터에서 고른다는 뜻이다.

### 3. 누른 뒤

**worktree**
- 행은 대기열에서 바로 빠진다(두 번 누를 수 없다). 그 글과 첨부, 그리고 그 행의 계획 모드 결정이 ⌘Enter와 같은 길(`runInNewWorktree` / `RunInNewWorktree`)로 간다. 입력창의 초안은 건드리지 않는다. 지금 ⌘Enter 길은 초안을 비우는데, 대기열 행에서는 그러면 안 된다.
- 자식을 만들지 못하거나 시작하지 못하면 행은 **대기열 맨 앞**으로 돌아오고, 창의 대화에 `delegation.human.queued`("새 worktree에서 실행하지 못했습니다: {이유} 대신 이 창의 다음 요청으로 실행합니다")가 남는다.
  - 다른 에이전트를 골랐을 때도 같다. 대기열 행은 입력창으로 가지 않는다.
  - 그사이 창이 쉬게 되었고 대기열이 오류로 멈춘 것이 아니면 지금 시작한다.
  - 돌아올 때는 16개 한도를 보지 않는다. 원래 대기열에 있던 행이기 때문이다.

**here**
- 그 행을 대기열 맨 앞으로 옮기고 대기열의 보통 시작(`runNextQueuedRow` / `StartNextQueuedInput`)을 부른다. 그래서 보류된 항목이 그 실행의 앞에 붙는다. 실행할 수 없는 창의 처리, 계획 모드 결정, 실패 시 행을 지키는 규칙도 지금 그대로다.
- 시작이 거절되면 행은 맨 앞에 남는다(원래 자리로 돌아가지 않는다).
- 이 실행이 끝나면 대기열은 지금처럼 차례대로 흐른다. 오류로 멈췄던 대기열은 다음 실행이 하던 대로 다시 흐른다.

**steer:** `steerQueuedRow`를 행 id로 넓힌다. 거절되면 행은 그대로다.

**held:** 예전 다음 실행의 보류 길 그대로다(`runNextHeld` / 웹·WinUI `RunNext`의 보류 가지).

**waitsFor…:** 아무것도 하지 않는다. 화면이 바뀌어 누르는 순간 실행할 수 있게 되었더라도, 다음 그리기에서 활성 단추가 된 뒤에 누르게 한다.

**행 주변:** 바로 실행은 그 행 하나만 움직인다. 앞뒤 행은 순서를 지킨다. here만 그 행을 맨 앞으로 당긴다.

### 4. "내 요청"과 화면 이동(natural submit)
- **이 기기에서 누른 바로 실행은 내 요청이다.** 예전 다음 실행과 같다.
  - here는 그 실행의 요청으로 이동한다(`own`).
  - steer는 그 중간 요청으로 이동한다(`noteOwnSubmit(.steer)`).
  - held는 `ownHeldReleases`.
  - worktree는 ⌘Enter와 같다. 자식 창은 부모 창을 보고 있고 거기 글을 쓰고 있지 않으면 선택되고, 선택된 자식 창의 첫 요청이 내 요청이다(`paneTabShown`). 부모 창의 보기는 움직이지 않는다.
- **폰에서 누른 바로 실행:** 맥과 윈도우 화면은 따라가기만 한다(`own: false`).
  - worktree 자식은 맥·윈도우에서 **선택되지 않는다**. 새 탭은 생기지만 지금 보던 것을 그대로 둔다. 폰의 손짓이 책상 앞 화면을 옮기지 않게 하려는 것이다. 맥은 `quietChildParents`, 윈도우는 `AddingChild(..., quiet:)`로 막는다.
  - 폰 자신은 here일 때 예전 다음 실행처럼 그 행의 글로 내 요청을 지켜본다. worktree일 때는 "새 worktree의 자식 창에서 실행했습니다"를 띄우고 지켜보지 않는다. 요청이 이 세션에 생기지 않기 때문이다.
- `native/contracts/fixtures/pending.json`의 `scrollRule` 설명과 `docs/interaction.md`의 "다음 실행" 행을 "바로 실행(이 창: 시작이나 중간 요청)"으로 고친다. 판단 사례는 그대로다.

### 5. 폰으로 가는 것
- **대기열 행:** `queued: [{ id, text, runNow?, runNowReason? }]`.

  | 결과 | `runNow` | `runNowReason` |
  | --- | --- | --- |
  | `worktree` | `"worktree"` | – |
  | `here`, `steer`, `held` | `"here"` | – |
  | `waitsForTurn` | `"unavailable"` | `"turn"` |
  | `waitsForHeld` | `"unavailable"` | `"held"` |
  | `waitsForUpdate` | `"unavailable"` | `"update"` |
  | `agentMenu` | `"unavailable"` | `"agent"` |
  | `hidden` | – | – |

  - 이유는 낱말(코드)로 보낸다. 폰은 자기 언어로 그 문구를 고른다. 호스트와 폰의 언어가 다를 수 있기 때문이다.
  - `runNow`가 없으면(첫 보류된 행이 아닌 보류된 행, 옛 호스트) 단추가 없다. 이 필드가 곧 능력 표시다. `/m1/info`의 능력은 늘리지 않는다.
  - 폰은 모르는 `runNow` 값을 없는 것으로 읽는다(앞으로 값이 늘어도 안전하다). 옛 폰은 두 필드를 모두 읽지 않는다.
- **새 라우트** `POST /m1/sessions/{id}/queue/{itemId}/run-now` → `{ protocol, ok: true, ran: "worktree" | "here" }`.
  - 호스트는 누른 순간의 상태로 다시 판단한다.
  - 없는 행은 404 `remote.error.queueItemNotFound`.
  - 지금 바로 실행할 수 없는 행(`waitsFor…`, `agentMenu`, `hidden`)은 409 `queue.runNowRefused`. 폰은 흐린 단추로는 부르지 않으므로, 이 답은 그 사이 상태가 바뀐 때만 온다.
  - 실행할 수 없는 창은 409와 그 이유. 시작 실패는 409 `remote.error.startFailed`.
- **옛 라우트** `/queue/run-next`는 맥과 윈도우가 지금 그대로 답한다(옛 폰). 새 폰은 부르지 않는다. 폰 클라이언트의 `runNext`는 지운다.

| | 새 호스트 | 옛 호스트 |
| --- | --- | --- |
| 새 폰 | 행마다 바로 실행(활성이나 흐림). 다음 실행 없음 | `runNow`가 없으므로 단추 없음. 다음 실행도 없음(지우기만) |
| 옛 폰 | 머리의 다음 실행(쉬는 창), 호스트는 예전대로 답한다 | 지금 그대로 |

- 새 폰과 옛 호스트 조합에서는 쉬는 창의 멈춘 대기열을 폰에서 다시 흘릴 수 없다. 호스트를 올리면 된다. 사용자 결정("옛 호스트에서는 단추를 숨긴다")을 따른 것이다.

### 6. 대기열 머리와 안내
- "백그라운드 작업이 끝나야 시작합니다 · …" 안내(`queue.waitingOnBackground.*`)는 그대로다.
- `queue.heldHelp`와 `queue.heldNotRemovable`은 "다음 실행" 대신 바로 실행을 말하도록 고친다. 보류된 항목은 "이 창에서 다음 요청이 실행될 때 그 앞에 함께 간다(이 행의 바로 실행으로 지금 보낼 수도 있다)".

## 그대로 두는 것
- ⌘Enter / Ctrl+Enter와 입력창 worktree 단추, 그 메뉴.
- Enter로 대기열에 넣기, 16개 한도, 중지·오류 때 대기열 규칙, `BackgroundQueuePolicy.stopKeepsQueue`.
- 위임의 보류·전달(`DelegationCoordinator.send` / `runNext`), 보류된 행을 지울 수 없는 규칙.
- 중간 요청 그리기와 `graph.result.afterSteer`(**중간 요청 이후**). steer 바로 실행이 그 길을 쓴다.
- `/queue/run-next`, `mobileRunNextQueued`, 윈도우 `MobileRunNext`(옛 폰).
- 웹 창 입력창의 worktree 단추에 에이전트 메뉴가 없는 것. 이번에 대기열 행에는 메뉴를 넣지만, 입력창 단추는 다음 작업으로 남긴다.
- 고정된 스타일 엔진(`MightyCore/Styles/**`).

## 문구

| 키 | ko | en | zh | ja |
| --- | --- | --- | --- | --- |
| `queue.runNow` | 바로 실행 | Run now | 立即运行 | すぐ実行 |
| `queue.runNowWorktreeHelpMac` | 이 창 옆에 자식 창을 열어 새 worktree에서 이 요청을 바로 실행합니다. 오른쪽 클릭(또는 Control-클릭)하면 에이전트를 고릅니다. | Runs this request now in a child pane with its own new worktree, beside this one. Right-click (or Control-click) to pick its agent. | 在此窗格旁打开子窗格，立即在新的 worktree 中运行此请求。右键点按（或按住 Control 点按）可选择其代理。 | このペインの隣に子ペインを開き、新しいworktreeでこのリクエストをすぐ実行します。右クリック(またはControlクリック)でエージェントを選べます。 |
| `queue.runNowWorktreeHelp` | 이 창 옆에 자식 창을 열어 새 worktree에서 이 요청을 바로 실행합니다. 오른쪽 클릭하면 에이전트를 고릅니다. | Runs this request now in a child pane with its own new worktree, beside this one. Right-click to pick its agent. | 在此窗格旁打开子窗格，立即在新的 worktree 中运行此请求。右键单击可选择其代理。 | このペインの隣に子ペインを開き、新しいworktreeでこのリクエストをすぐ実行します。右クリックでエージェントを選べます。 |
| `queue.runNowHereHelp` | 이 창에서 이 요청을 지금 실행합니다. | Runs this request now in this pane. | 立即在此窗格中运行此请求。 | このペインでこのリクエストを今すぐ実行します。 |
| `queue.runNowSteerHelp` | 이 창의 실행이 백그라운드 작업만 기다리고 있어, 이 요청을 그 실행에 새 턴으로 지금 넘깁니다. | This pane's run only waits on background work: the request goes into it now, as a new turn. | 此窗格的运行只在等待后台任务：此请求会立即作为新回合送入该运行。 | このペインの実行はバックグラウンド作業を待っているだけなので、このリクエストを新しいターンとして今すぐ渡します。 |
| `queue.runNowHeldHelp` | 이 창에 보류된 항목 {count}개를 모두 함께 지금 이 창에 전달합니다. | Delivers all {count} items held for this pane to it now, together. | 立即把为此窗格保留的全部 {count} 个项目一起送到此窗格。 | このペインに保留された {count} 件をすべてまとめて今すぐこのペインに渡します。 |
| `queue.runNowRefused` | 지금은 이 요청을 바로 실행할 수 없습니다. 지금 작업이 끝나면 차례대로 실행됩니다. | This request cannot run now. It runs in its turn once the current task ends. | 此请求现在无法运行。当前任务结束后会按顺序运行。 | このリクエストは今は実行できません。今の作業が終わると順番に実行されます。 |
| `queue.runNowUnavailableHelp` | 지금은 바로 실행할 수 없습니다. 지금 작업이 끝나면 순서대로 실행됩니다. | Can't run now. It runs in its turn once the current task ends. | 现在无法立即运行。当前任务结束后会按顺序运行。 | 今はすぐ実行できません。今の作業が終わると順番に実行されます。 |
| `queue.runNowAfterHeldHelp` | 이 창에 보류된 항목이 먼저 전달되어야 합니다. 맨 위 보류된 행의 바로 실행으로 먼저 보내세요. | Items held for this pane go first: send them with Run now on the top held row. | 为此窗格保留的项目要先送出：请先用最上方保留行的立即运行送出。 | このペインに保留された項目が先に渡されます。いちばん上の保留行のすぐ実行で先に送ってください。 |
| `queue.runNowUpdateHelp` | CLI 업데이트가 끝나면 순서대로 실행됩니다. | Runs in its turn once the CLI update ends. | CLI 更新结束后会按顺序运行。 | CLIの更新が終わると順番に実行されます。 |
| `phone.queue.runNowLabel` | {text} 바로 실행 | Run {text} now | 立即运行 {text} | {text}をすぐ実行 |
| `phone.queue.runNowWorktreeHint` | 이 창 옆에 자식 창을 열어 새 worktree에서 바로 실행합니다. | Runs it now in a new child pane with its own worktree, beside this one. | 在此窗格旁打开子窗格，立即在新的 worktree 中运行。 | このペインの隣に子ペインを開き、新しいworktreeですぐ実行します。 |
| `phone.session.toast.runNowWorktree` | 새 worktree의 자식 창에서 실행했습니다 | Started in a new worktree child pane | 已在新 worktree 的子窗格中开始运行 | 新しいworktreeの子ペインで開始しました |
| `phone.queue.runNowAgentOnComputer` | 이 창의 에이전트는 지금 모드에서 자식 창을 둘 수 없습니다. 다른 에이전트는 컴퓨터에서 고릅니다. | This pane's agent can't have a child pane in its mode. Pick another agent on the computer. | 此窗格的代理在当前模式下不能有子窗格。请在电脑上选择其他代理。 | このペインのエージェントは今のモードでは子ペインを持てません。別のエージェントはコンピューターで選んでください。 |

고치는 키:

| 키 | ko | en | zh | ja |
| --- | --- | --- | --- | --- |
| `queue.heldHelp` | 이 창에 보류된 항목입니다. 이 창에서 다음 요청이 실행될 때 그 앞에 함께 전달되며(이 행의 바로 실행으로 지금 보낼 수도 있습니다), 지울 수 없습니다. | Held for this pane. It goes ahead of the next request this pane runs (or now, with its Run now), and cannot be removed. | 为此窗格保留的项目。此窗格运行下一个请求时，它会排在前面一起送出（也可以用此行的立即运行现在送出），且无法移除。 | このペインに保留された項目です。このペインで次のリクエストが実行されるとき、その前にまとめて渡されます(この行のすぐ実行で今すぐ送ることもできます)。削除はできません。 |
| `queue.heldNotRemovable` | 이 창에 보류된 항목이라 지울 수 없습니다. 이 창의 다음 요청과 함께, 또는 이 행의 바로 실행으로 전달됩니다 (held_not_removable). | This item is held for the pane and cannot be removed. It goes with the pane's next request, or with its Run now (held_not_removable). | 此项目为该窗格保留，无法移除。它会随该窗格的下一个请求，或通过此行的立即运行送出 (held_not_removable)。 | このペインに保留された項目のため削除できません。このペインの次のリクエストと一緒に、またはこの行のすぐ実行で渡されます (held_not_removable)。 |

- 지우는 키: `queue.runNext`, `queue.runNextHelp`. 도움말까지 아무도 쓰지 않게 된 뒤(마지막 작업)에 지운다. 옛 폰은 자기 번들 문구를 쓰므로 호스트에 이 키가 필요 없다.
- `queue.worktreeMenuHintMac`은 지금처럼 맥과 윈도우의 `agentMenu` 도움말로 쓴다.
- `phone.session.toast.nextStarted`는 폰의 here 바로 실행이 계속 쓴다.

## 범위
- **맥:** MightyCore `QueueRunNow`, `DelegationQueueRows.mobileItems`, `MobileQueuedItem.runNow`, 새 라우트. 앱의 `AppStore`(판단, 실행, worktree 되돌림, 조용한 자식), `QueuedInputsView`, `SessionPaneView`.
- **윈도우:** Core `QueueRunNow`, `MobileQueuedItem.RunNow`, 라우터, `AddingChild(quiet)`. WinUI `PaneView`의 대기열 행과 실행, `WorktreeMenu`, 웹 다리와 상태. 웹 `Composer.tsx`와 새 `composer/queue.ts`.
- **폰:** 타입, 클라이언트, `QueuedList`, 세션 화면.
- **문서**
  - `docs/mobile-remote.md`: 행 모양, 새 라우트, 옛/새 표
  - `native/contracts/README.md`: 라우트 표와 새 픽스처 절
  - `docs/interaction.md`: 동작 표와 화면 이동 규칙
  - `docs/mighty-mode.md`: 다음 실행을 말하는 두 줄
  - `docs/windows-parity.md`, `docs/windows-screen-checklist.md`
  - 도움말 4개 언어: `agent-pane.md`, `delegation.md`, `phone.md`

## 테스트
- **`native/contracts/fixtures/queue-run-now.json`**(새 파일, 맥·윈도우가 판단을 읽고, 폰은 `wire`를 읽는다).
  - 사례마다 행(`heldIndex`, 사람의 행은 null, `text`, `attachments`, `continuation`)과 창(`busy`, `heldForUpdate`, `kind`, `provider`, `mode`, `isChild`, `delegationOn`, `canHostWorktree`, `otherAgentReady`, `waitsOnBackgroundOnly`, `held`)을 둔다.
  - 기대 결과 `expect`와 폰 값 `wire`(`{ runNow, runNowReason? }` 또는 null)를 둔다.
  - 바쁜 Claude 창, git 저장소, 위임 켜짐 → worktree
  - 백그라운드 작업만 기다리는 같은 창 → worktree(worktree가 steer를 이긴다)
  - 쉬는 창(오류로 멈춤), worktree를 만들 수 있어도 → here
  - git 저장소가 아님, 백그라운드 작업만 기다림 → steer
  - git 저장소가 아님, 턴이 도는 중 → waitsForTurn
  - 위임 스위치 꺼짐, 백그라운드 작업만 기다림 → steer
  - 자식 창, 턴이 도는 중 → waitsForTurn
  - 원격 창(`delegationOn` 거짓, 로컬 아님), 턴이 도는 중 → waitsForTurn. 쉬는 원격 창 → here
  - Codex manual 모드, 다른 에이전트가 준비됨 → agentMenu(`wire` unavailable/agent)
  - Codex manual 모드, 준비된 다른 에이전트 없음 → waitsForTurn
  - 첨부만 있는 행, worktree 가능, 백그라운드 작업만 기다림 → waitsForTurn
  - 글과 첨부가 있는 행, worktree 가능 → worktree
  - CLI 업데이트로 붙잡힘 → waitsForUpdate
  - 보류된 항목이 있을 때 사람의 행, 백그라운드 작업만 기다림, worktree 불가 → waitsForHeld
  - 보류된 항목이 있을 때 사람의 행, 턴이 도는 중, worktree 불가 → waitsForTurn
  - 보류된 항목이 있을 때 사람의 행, 쉬는 창 → here
  - 첫 보류된 행, 바쁜 창 → held(`wire` here)
  - 둘째 보류된 행 → hidden(`wire` null)
  - 첫 보류된 행, 쉬는 창 → held
  - Antigravity 카드의 이어짐(`continuation: true`): worktree를 만들 수 있는 바쁜 창 → waitsForTurn, 메뉴만 되는 창 → waitsForTurn, 백그라운드 작업만 기다리는 창 → waitsForTurn, 쉬는 창 → here
- **맥:** 픽스처 테스트. `mobileItems`가 `runNow`와 `runNowReason`을 싣고 빈 값은 빼는 것. 새 라우트(`MobileRemoteTests`: 200과 `ran`, 404, 409). `offersRunNext`를 쓰던 테스트는 `QueueRunNow.decide`로 옮긴다.
- **윈도우 Core:** 같은 픽스처를 읽는 `QueueRunNowVerification`. `MobileItems`와 `RunNow`.
- **웹(vitest):** `readRunNow` 파싱, `runNowTip`(결과 → 키와 수, 비활성 여부).
- **폰(jest)**
  - `runNowOf`가 픽스처의 모든 `wire`를 읽는다. 모르는 `runNow`는 단추가 없고, 모르는 이유는 `"turn"`이다.
  - `client.runQueuedNow`의 경로와 `ran` 파싱.
  - `QueuedList`가 `runNow`가 있을 때만 단추를 그리고, `"unavailable"`이면 흐리게 그리고 눌러도 부르지 않는 것.

## 확인할 것
- 웹 창 입력창의 worktree 단추에도 에이전트 메뉴를 다는 일은 다음 작업이다.
- 폰은 보류된 행인지 모른다(`{ id, text }`). 보류된 행에도 지금처럼 삭제가 보이고 호스트가 409로 거절한다. `held` 표시를 싣는 일은 다음 작업이다.
- 맥 SwiftUI와 WinUI에서 비활성 단추의 도움말이 감싼 뷰에서 실제로 뜨는지는 사용자의 화면 확인 목록에 넣는다(빌드 없이는 확인하지 않는다).
