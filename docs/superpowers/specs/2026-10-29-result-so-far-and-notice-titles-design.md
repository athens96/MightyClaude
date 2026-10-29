# 현재까지의 결과, 그리고 알림마다 이름 붙은 다이어그램

2026-10-29 · 사용자 요청: 백그라운드 작업이 남아 있을 때 결과 카드는 최종 결과가 아니다. "현재까지의 결과"라고 하고, 기다리는 백그라운드 작업 수를 보인다. 원래 결과 끝에 계속 붙는 "알림 이후" 절은 없앤다. 대신 "백그라운드 작업 완료 · 어떤 작업" 같은 제목으로 따로 알린다.

0.4.46(백그라운드 작업 알림을 새 다이어그램으로, `2026-10-28-background-notice-runs-design.md`)에 이어지는 작은 변경이다.

정한 것:
- 요청의 결과는 나왔지만 그 요청이 띄운 백그라운드 작업이 아직 돈다. 이때 결과 카드 제목은 "현재까지의 결과 · 백그라운드 작업 {count}개 기다리는 중"이다. 남은 작업이 없으면 지금처럼 "최종 결과"다. 실패와 중단 제목은 그대로다.
- 이 규칙은 흐름의 결과 카드, 파이프라인의 답 카드, 기록의 결과 머리에 똑같이 적용한다. 맥, 윈도우(웹 창과 WinUI 대체 화면), 폰 모두 같다.
- 지금 "알림 이후" 절로 붙는 답은 모두 자기 알림 실행(다이어그램)을 연다. 원래 요청의 결과는 더 자라지 않는다.
- 알림 실행의 제목은 다룬 작업의 이름을 담는다. 이미 있는 백그라운드 작업 알림 실행도 같다.

## 지금 동작

### 결과 카드
- 요청의 턴이 끝나면(`result`) 요청은 완료가 된다. 결과 카드는 그때 "최종 결과"(`graph.block.result`)로 나온다.
- 그 요청이 띄운 백그라운드 작업이 아직 돌아도 같다. 0.4.46부터 그 블록은 화면에서 빠지므로(`BackgroundNoticeRuns.shownAgents`) 요청은 정리된 것(`settled`)으로 읽힌다.
- 남은 작업 수는 이미 센다. 맥은 `MightyGraphRun.backgroundWaiting`, 윈도우는 `MightyGraphSupport.BackgroundWaiting`이다. 화면에서 거른 실행(`shown`)도 거르기 전의 수(`shownBackgroundWaiting`)를 지닌다.
- 이 수는 지금 실행 블록과 기록의 상태 글자에만 쓴다. 문구는 `plan.background.status`, "턴 완료 · 백그라운드 {count}개 실행 중"이다.
- 폰은 이 수를 받지 못한다. 호스트가 요청 실행의 백그라운드 블록을 빼고 보내기 때문이다. 세션의 `backgroundWork`는 창 단위라서 실행 단위의 수가 아니다.

### "알림 이후" 절
흐름 추적기(맥 `ExecutionGraphTracker.swift` ~356, 윈도우 `ExecutionGraphTracker.cs` ~461)를 보자. 알림 결과(`ClaudeStream.isNotificationResult`, origin `task-notification`·`peer`)가 올 때 열린 알림 실행(`noticeTurn`)도 이어 답할 실행(`mergeTurn`)도 없으면, 그 답은 원래 요청 결과 끝에 붙는다. 붙는 모양은 `---`와 굵은 **알림 이후**(`graph.result.afterNotice`, `MightyGraphSupport.withNoticeSection`)다. 그런 턴은 다음과 같다.
- **도는 하위 에이전트의 메시지:** 하위 에이전트가 끝나기 전에 보낸 `<agent-message>`(SendMessage, 보고 틀 없음)가 연 턴. 메시지는 그 블록의 기록에만 붙고, Claude의 답은 요청 결과에 붙는다.
- **이 실행이 모르는 발신자의 메시지:** 이 세션이 모르는 에이전트의 보고나 메시지에 답한 턴.
- **블록이 없는 작업의 알림:** 이 실행이 띄우지 않은 작업의 `<task-notification>`이 연 턴. 예를 들어 이전 프로세스가 남긴 작업이다.
- **흡수된 알림 뒤의 알림 결과:** 요청 턴 안에 들어온 알림이 결과 없는 알림 실행(`absorbedRun`)에 들어갔는데, 그 뒤 알림 origin의 결과가 따로 온 경우. 이 경우 답이 요청 결과에 절로 붙었다가 요청 자신의 결과가 덮어쓴다.

### 위임 알림
- 위임 알림은 `<task-notification>`이 아니다. `DelegationDelivery`가 실행 중인 Claude에 넣는 steer다(`[Mighty Claude notice …]`). 그래서 `afterNotice` 경로를 지나지 않는다.
- 요청 턴이 끝난 뒤 들어온 위임 알림은 `steeredTurn`을 "위임 알림"으로 정한다. 그 턴의 답은 `graph.result.afterSteer`로 **위임 알림 이후** 절이 되어 요청 결과에 붙는다. 사용자가 본 "… 알림 이후" 글에는 이것도 들어간다.
- 원래 다이어그램에는 주황색 **위임 알림** steer 블록이 생긴다. 다음 루트 답이 그 블록의 답이 된다.

### 알림 실행 제목
- 0.4.46의 알림 실행은 늘 같은 제목, **백그라운드 작업 완료**(`graph.background.noticeRun`)다.
- 추적기가 main 노드에 이 제목을 넣지만, 실행 투영(`recordGraph`, 윈도우 `BuildRunLocked`)은 main 제목을 옮기지 않는다. 화면은 상수(`BackgroundNoticeRuns.title`/`Title`, 폰 `t('graph.background.noticeRun')`)를 쓴다.

## 바뀌는 동작

### 1. 현재까지의 결과
- **제목 규칙** 하나를 둔다. 맥 `MightyGraphSupport.resultTitle(status:waiting:)`, 윈도우 `MightyGraphBlockModel.ResultTitle(status, waiting)`, 폰 `resultTitle(status, waiting)`이다.
  - 실패: `graph.block.resultError`, 그대로.
  - 중단: `graph.block.resultStopped`, 그대로.
  - 그 밖에 `waiting > 0`이면 새 키 `graph.block.resultSoFar`, "현재까지의 결과 · 백그라운드 작업 {count}개 기다리는 중".
  - 나머지는 `graph.block.result`, "최종 결과".
- **waiting**은 새로 세지 않고 `backgroundWaiting`/`BackgroundWaiting`을 그대로 쓴다. 요청이 완료이고, 이 요청의 task와 백그라운드 하위 에이전트 중 끝나지 않은 것의 수다.
  - 작업이 끝나 알림이 오면 원래 블록이 끝난 상태가 되어 수가 준다.
  - 0이 되면 같은 카드가 "최종 결과"로 바뀐다.
  - 프로세스가 끝나면 남은 블록은 중지가 되어(`finish`) 역시 "최종 결과"다.
- **적용하는 곳**
  - 흐름의 결과 카드(맥 `MightyGraphView` `.result`, 윈도우 `MightyGraphBlockModel.Blocks`의 result 블록, 웹은 호스트가 준 블록 제목을 그린다)
  - 파이프라인의 답 카드(맥 `MightyStructureView.resultTitle`, 웹 `Structure.tsx` `ResultTitle`, WinUI `StructureResultTitle`, 폰 `resultHeading`)
  - 기록의 결과 머리(맥 `timelineResult`, 웹 `Timeline.tsx`, WinUI `MainWindow.MightyTimeline.cs`)
- **기록 결정:** 기록의 결과 머리도 같은 제목을 쓴다.
  - 기록의 그룹 상태 글자는 이미 "턴 완료 · 백그라운드 N개 실행 중"이다. 그런데 결과 머리만 "최종 결과"라면 서로 어긋난다.
  - 머리는 한 줄이라 길면 지금처럼 말줄임된다.
- 폰의 결과 카드는 완료일 때 "✓ 최종 결과"다. 기다리는 동안은 ✓ 없이 "현재까지의 결과 · …"다. 아직 끝난 게 아니기 때문이다.
- 알림 실행에는 해당하지 않는다. 알림 실행의 사본 블록은 끝난 작업이라 waiting이 늘 0이다.
- 고정된 스타일 엔진(`Styles/**`)의 상태 글자는 건드리지 않는다.

### 2. "알림 이후" 절 대신 새 알림 실행
- **규칙:** 알림이 연 턴의 답은 요청 결과에 붙지 않는다. 그 답이 갈 알림 실행이 없으면 새 알림 실행을 하나 연다. 요청의 결과와 실행 블록은 그 턴 동안 자라지 않는다.
- 추적기는 알림 실행이 무엇으로 열렸는지 기억한다(`BackgroundNoticeRuns.Opener`).
  - `work`: 끝난 백그라운드 작업. 0.4.46의 사본 블록, 또는 블록이 없는 작업의 알림.
  - `message`: 하위 에이전트의 메시지.
  - `delegation`: 위임 알림.
- **도는 하위 에이전트의 메시지**
  - 요청 턴이 끝난 뒤 루트에서 받으면 바로 알림 실행을 연다. 0.4.46의 알림 턴처럼 처음엔 실행 중이다.
  - 입력은 메시지 본문이고, 결과는 그 턴의 답이다.
  - 메시지는 지금처럼 그 에이전트 블록의 기록에도 남는다.
  - 사본 블록은 만들지 않는다. 그 에이전트는 아직 돌고, 끝나면 0.4.46대로 자기 사본을 가진 알림 실행에 들어간다.
- **모르는 발신자의 메시지와 보고:** 같은 방법으로 `message` 실행을 연다. 이름은 없다.
- **블록이 없는 작업의 알림**
  - 요청 턴이 끝난 뒤 루트에서 받으면 `work` 실행을 연다.
  - 입력은 알림의 `<summary>`(없으면 `<status>`)다.
  - 사본 블록은 없다.
  - 요약도 상태도 없는 알림은 실행을 바로 열지 않는다. 그 알림에 답이 오면 그 답을 가진 완료된 실행을 열고, 답이 없으면 아무것도 남지 않는다. 빈 "백그라운드 작업 완료" 카드를 만들지 않기 위해서다(Task 1 검토에서 정함, 윈도우도 같다).
- **요청 턴 안이나 앞에서 받은 위 메시지와 알림**
  - 새 실행을 바로 열지 않는다. 그 줄(본문이나 요약)을 이번 턴 몫으로 잠시 들고 있다.
  - 그 턴의 답이 알림 origin 결과로 오면 그때 결과까지 든 완료된 실행을 연다.
  - 요청 자신의 결과로 끝나면 버린다. 그 답은 요청의 답이기 때문이다.
- **흡수된 알림 뒤의 알림 결과:** 흡수된 실행(`absorbedRun`)이 열려 있으면, 답이 비어 있지 않을 때 그 실행의 결과가 된다. 같은 작업에 대한 답이기 때문이다. 그 턴이 들고 있던 메시지·알림 줄도 그 실행의 입력 줄에 들어간다.
- **위임 알림:** 요청 턴이 끝난 뒤 들어온 위임 알림 steer는 `delegation` 알림 실행을 연다. 입력은 알림 글이고, 그 턴의 답이 그 실행의 결과다.
  - **위임 알림** steer 블록은 원래 다이어그램에 그대로 생긴다. 이름, 답, 끝나지 못했을 때의 문구(`graph.block.unfinished.delegationNotice`)도 그대로다. 위임 쪽 전달과 대기열 규칙은 바뀌지 않는다.
  - 결과 절(**위임 알림 이후**)만 새 실행으로 바뀐다.
  - 요청 턴 안에 들어온 위임 알림은 지금처럼 그 턴의 일부다.
- **사용자의 다음 실행 steer**(`afterSteer`, **중간 요청 이후**)는 이번 범위가 아니다. 그대로 둔다.
  - 이 경로는 쓰이고 있다. 백그라운드만 기다리는 창에서 다음 실행을 누르면 지나간다(`BackgroundQueuePolicy.steersRunNext`).
  - `withNoticeSection`과 `noticeSectionMark`는 이 경로와 예전 저장 때문에 남는다.
- **한 턴 = 다이어그램 하나:** 같은 턴에 끝난 작업, 메시지, 위임 알림이 섞이면 처음 열린 실행 하나에 모인다. 사본은 사본으로, 나머지는 입력 줄로 들어간다.
- `graph.result.afterNotice`는 더 쓰는 곳이 없어 키를 지운다.

### 3. 알림 실행 제목
- **모양:** `{종류} · {이름}`(새 키 `graph.background.noticeRunTitle`, "{kind} · {name}").
  - **종류**
    - `work`는 `graph.background.noticeRun` "백그라운드 작업 완료"
    - `message`는 새 키 `graph.background.messageRun` "하위 에이전트 메시지"
    - `delegation`은 기존 `graph.block.delegationNotice` "위임 알림"
  - **이름**
    - 사본 블록이 있으면 사본들의 제목이다. 제목이 비면 입력(명령)을 쓴다. 0.4.46의 입력 줄과 같은 이름이다.
    - 사본이 있으면 종류는 늘 `work`다. 실행이 메시지나 위임 알림으로 열렸어도 같다.
    - 사본이 없으면 연 것이 준 이름이다. 메시지는 보낸 에이전트 블록의 제목, 곧 에이전트의 `name`이나 설명이다. 모르는 발신자, 블록 없는 작업, 위임 알림은 이름이 없다.
  - **수**
    - 이름이 하나면 그 이름이다.
    - 여럿이면 새 키 `graph.background.noticeRunMore` "{first} 외 {count}개"를 쓴다. count는 나머지 수이고, 같은 이름도 따로 센다.
    - 없으면 종류만 쓴다(`백그라운드 작업 완료`, `하위 에이전트 메시지`, `위임 알림`).
  - **짧게:** 이름은 첫 줄만 쓰고 32자를 넘으면 31자 + "…"로 자른다. "외 n개"는 자른 뒤에 붙인다.
- **예**
  - "백그라운드 작업 완료 · 코드 검토 에이전트"
  - "백그라운드 작업 완료 · Build the app 외 1개"
  - "하위 에이전트 메시지 · page-reader"
  - "백그라운드 작업 완료"(블록 없는 작업)
  - "위임 알림"
- **누가 짓나**
  - 추적기가 짓는다. 알림 실행 main 노드의 `title`에 넣고, 사본이나 입력 줄이 늘 때마다 다시 짓는다.
  - 실행 투영은 알림 실행이면 main 제목을 새 선택 필드 `MightyGraphRun.noticeTitle`(Codable, 저장됨)로 옮긴다.
  - 화면은 `BackgroundNoticeRuns.heading(run)`을 읽는다. `noticeTitle`이 없는 예전 저장은 "백그라운드 작업 완료"다.
- 제목은 호스트 언어로 저장된다. 0.4.46의 입력 줄("· 완료")과 블록 제목과 같은 방식이다.
- 이름을 가진 다른 것(입력 줄, 사본, 상태)은 0.4.46 그대로다.

### 4. 기록과 파이프라인
- 새 알림 실행은 실행이 하나 더 생긴 것이다. 0.4.46 구조를 그대로 따른다.
  - 기록: 맨 아래 다음 줄.
  - 파이프라인: 맨 끝 보드나 행.
  - 흐름: 새 다이어그램.
- 머리와 보드 제목은 상수 대신 `heading(run)`을 쓴다. 터미널 아이콘과 색은 그대로다.
- 요청 번호, 펫, 완료 알림 횟수에는 세지 않는다(`noticeRun`).
- 결과가 빈 알림 실행(`quietNotice`) 규칙도 그대로다.

### 5. 폰으로 가는 것
- **새 선택 필드:** `backgroundWaiting?: number`. 양의 정수일 때만 싣는다.
  - `MobileMightyRun`에는 요청 실행의 `backgroundWaiting`을 싣는다.
  - 구조 `Item`에는 그 실행의 같은 값을 싣는다.
  - 호스트의 다이제스트에 이 값과 `noticeTitle`을 넣어, 수가 바뀌면 긴 폴링이 깬다.
- **알림 제목:** 새 필드 없이 이미 있는 `title`에 싣는다.
  - `MobileMightyRun.title`과 구조 `Item.title`이다. 지금은 알림 실행에서 비어 있다.
  - 알림 실행의 `main` 블록 제목도 같은 제목이다.
- **조합별로 보이는 것**

  | 호스트 | 폰 | 결과 카드 | 알림 실행 제목 |
  | --- | --- | --- | --- |
  | 새 | 새 | 기다리는 동안 "현재까지의 결과 · …"를 보인다. | 이름 붙은 제목을 보인다. |
  | 새 | 예전 | `backgroundWaiting`을 모른다. 0.4.46처럼 "최종 결과"다. | `noticeRun`을 먼저 보고 "백그라운드 작업 완료"를 쓴다. `title`은 무시한다. `main` 블록 제목에는 새 제목이 보인다. |
  | 예전 | 새 | 필드가 없으니 "최종 결과"다. | `title`이 없으니 "백그라운드 작업 완료"다. |

- 예전 호스트에서는 "알림 이후" 절이 여전히 결과 안에 있다. 그 글은 그대로 보인다.

### 6. 지난 기록
- **불러온 Claude 기록:** `SessionHistory`가 Claude 기록을 다시 읽을 때도 같은 추적기를 지난다. 불러온 기록에는 새 규칙이 적용된다. 메시지와 알림에 답한 턴은 이름 붙은 알림 실행이 되고, 요청 결과에 "알림 이후" 절이 생기지 않는다.
- **이미 저장된 그래프:** 앱 상태에 저장된 `graphRuns`는 바꾸지 않는다(마이그레이션 없음).
  - 예전 요청 결과에 들어 있는 "**알림 이후**" 절은 글자 그대로 남는다. 그 결과의 첫 부분은 `refreshResult`가 `noticeSectionMark`로 나눠 main 블록과 맞춘다. 이것도 지금 그대로다.
  - `noticeTitle`이 없는 예전 알림 실행은 "백그라운드 작업 완료"다.

## 그대로 두는 것
- `RunSession.withoutNoticeRuns`는 고정 스타일 엔진에 알림 실행을 빼고 넘긴다. 메시지 실행과 위임 실행도 `noticeRun`이라 똑같이 빠진다.
- 대기열, 다음 실행, 중지 규칙(`BackgroundQueuePolicy`)은 그대로다. 위임 전달(`DelegationDelivery`)도 그대로다.
- 백그라운드 작업 줄, 폰의 `backgroundWork`, 사본 블록 규칙(`copiedIn`, `mergeTurn`)도 그대로다.

## 문구
`locales/{ko,en,zh,ja}.json`과 사본(`native/macos/Sources/MightyCore/Resources/Locales`, `mobile/src/locales`)에 넣는다. `scripts/check-locales.js`가 `docs/i18n.md`를 다시 쓴다.

| 키 | ko | en |
| --- | --- | --- |
| `graph.block.resultSoFar` | 현재까지의 결과 · 백그라운드 작업 {count}개 기다리는 중 | Result so far · waiting on {count} background tasks |
| `graph.background.messageRun` | 하위 에이전트 메시지 | Sub-agent message |
| `graph.background.noticeRunTitle` | {kind} · {name} | {kind} · {name} |
| `graph.background.noticeRunMore` | {first} 외 {count}개 | {first} and {count} more |

- zh와 ja도 같은 자리표시자로 넣는다.
- `graph.result.afterNotice`는 지운다.
- 하드코딩한 한국어는 없다. `scripts/korean-literal-budget.json`은 늘지 않는다.

## 범위
- Claude 창만이다.
- **맥:** MightyCore 추적기, 실행 투영, 공용 규칙, 폰 변환, 그리고 앱 화면(흐름, 기록, 파이프라인).
- **윈도우:** Core 추적기, 실행, 화면 모델, 폰 변환, 웹 창(`Timeline.tsx`, `Structure.tsx`), 그리고 WinUI 대체 화면.
- **폰:** 타입, 마이티 보기 결과 카드, 머리, 구조, 파이프라인 보드.
- **문서**
  - `docs/mighty-mode.md`: 결과 제목, 알림 실행 종류와 제목, 위임 알림의 결과 절
  - `docs/mobile-remote.md`: `backgroundWaiting`, 알림 실행의 `title`
  - 도움말 4개 언어(`agent-pane.md` 결과 카드, `approvals.md` 백그라운드 작업, `delegation.md` 알림)
- 고정된 스타일 엔진(`MightyCore/Styles/**`)은 건드리지 않는다.

## 테스트
- **`native/contracts/fixtures/agent-notices.json`**(맥과 윈도우가 같은 파일을 읽는다)
  - 새 키 `noticeRunTitles`: 모든 알림 실행의 main 제목, 번호 순. ko로 읽는다.
  - 새 키 `noticeRunOutputs`: 모든 알림 실행의 결과, null이면 없음.
  - `mainNoticeSections`는 지운다. 대신 모든 스트림에서 요청 결과에 알림 절 표시(`noticeSectionMark`)가 없음을 확인한다.
  - **도는 에이전트의 메시지 뒤 보고, 그 뒤 알림**(기존 사례 고침): 알림 실행 2개, 사본 수 [0, 1]이다.
    - 제목: ["하위 에이전트 메시지 · page-reader", "백그라운드 작업 완료 · page-reader"]
    - 첫 실행: 입력은 메시지 본문, 결과는 "Noted the progress."
  - **모르는 발신자의 보고**(기존 사례 고침): 알림 실행 1개, 사본 0개, 제목 "하위 에이전트 메시지", 결과는 그 답.
  - **새 사례: 블록이 없는 작업의 알림이 턴을 연다.** 알림 실행 1개, 사본 0개, 제목 "백그라운드 작업 완료", 입력은 요약, 결과는 답.
  - **새 사례: 도는 에이전트의 메시지 하나.** 알림 실행 1개, 제목 "하위 에이전트 메시지 · {이름}". 에이전트 블록은 그대로 실행 중이다.
  - **제목 규칙**(기존 사례에 `noticeRunTitles`를 더한다)
    - 하나: "백그라운드 작업 완료 · Lint the code"
    - 둘: "백그라운드 작업 완료 · Build the app 외 1개"
  - **새 절 `noticeTitles`:** 순수 제목 규칙의 표. 종류와 이름 목록을 넣으면 제목이 나온다. 이름 없음, 하나, 셋, 32자 넘는 이름을 다룬다.
  - **새 절 `resultTitles`:** 순수 결과 제목 표. 상태와 waiting을 넣으면 키와 count가 나온다. 완료+2는 resultSoFar(2), 완료+0은 result, 실패+1은 resultError, 중단+1은 resultStopped.
- **`native/contracts/graph-vectors.json`:** `background-task-run-in-background`(vec-cs-5)의 알림 실행 main 제목이 "백그라운드 작업 완료 · Build"가 된다.
- **위임 알림:** 맥 `ExecutionGraphTests`와 윈도우 `SessionHistoryVerification`에 사례를 더한다(픽스처는 steer를 넣지 못한다).
  - 턴 뒤에 들어온 위임 알림은 "위임 알림" 실행을 연다. 결과는 그 답이고, 요청 결과는 그대로다.
  - 위임 알림 steer 블록은 원래 실행에 있고 답을 가진다.
  - 사용자 steer(중간 요청 이후)는 지금 테스트 그대로 통과한다.
- **실행과 저장**
  - `noticeTitle`이 투영으로 옮겨지고, Codable로 왕복하고, 예전 JSON에서는 nil이다.
  - `heading(run)`이 없을 때 "백그라운드 작업 완료"를 돌려준다.
- **폰(jest)**
  - `backgroundWaiting` 파싱: 양의 정수만 받는다.
  - `resultTitle`과 결과 카드 제목.
  - `runHeading`, `itemHeading`, 보드 제목이 `title`을 쓰고, 없으면 일반 제목으로 돌아간다.
- **웹(vitest):** 기록과 파이프라인의 결과 머리가 호스트의 `resultTitle`을 쓰고, 없으면 "최종 결과"다.

## 확인할 것
- 도는 하위 에이전트가 진행 메시지를 자주 보내면 메시지마다 "하위 에이전트 메시지" 다이어그램이 생긴다. 사용자 결정 B를 그대로 따른 것이다. 너무 시끄러우면 다음에 같은 에이전트의 연속 메시지를 한 실행에 이어 붙이는 규칙을 둘 수 있다.
- 위임 알림 실행의 제목에는 자식 이름이 없다. 알림 글에는 자식 ID뿐이고, 추적기는 자식 창 이름을 모른다. 이름을 넣으려면 앱이 steer와 함께 자식 제목을 넘겨야 한다. 다음 작업으로 남긴다.
