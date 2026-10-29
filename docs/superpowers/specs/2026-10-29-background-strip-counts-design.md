# 백그라운드 작업 줄: 끝남·작업 중 수, 숨기기 없음

2026-10-29 · 사용자 요청: 입력창 위 백그라운드 작업 줄은 끝난 수와 작업 중인 수, 이 두 가지만 보이면 된다. "끝나면 알림" 같은 말은 필요 없다. 이 줄을 숨기는 단추도 필요 없다.

0.4.46(도는 작업은 줄에, 끝난 작업은 알림 실행에)과 "현재까지의 결과"(`2026-10-29-result-so-far-and-notice-titles-design.md`)에 이어지는 작은 정리다.

정한 것:
- 줄 머리는 "백그라운드 작업 · 끝남 N개 · 작업 중 N개"다. 두 수는 줄이 지금 가진 작업으로 센다.
- "끝나면 알림", "알림 전달됨", "모두 끝남 · 알림 전달됨"은 어디에도 나오지 않는다.
- 숨기기는 모두 없앤다. 줄의 눈 단추, 설정 → 일반 → 화면의 스위치, 창 … 메뉴 항목, 입력창의 백그라운드 작업 단추, 저장된 `showsBackgroundWork` 값이 대상이다.
- 맥, 윈도우(웹 창과 WinUI 대체 화면), 폰 모두 같다.

## 지금 동작

### 줄이 가진 작업
- 줄은 창의 `RunSession.backgroundWork`를 그린다. 이 값은 CLI 프로세스 하나(요청이 연 실행)의 백그라운드 작업 목록이다. 최대 32개다(`BackgroundTaskTracker`).
- 작업은 끝나도 목록에서 빠지지 않는다. 상태만 `completed`, `failed`, `stopped`가 되고, 끝 보고 없이 프로세스가 끝나면 `unknown`이 된다.
- 목록은 다음 요청이 실행을 시작할 때(`status` 이벤트가 `running`) 비워진다(`ClaudePlanMode.swift` ~604).
- 줄이 보이는 규칙은 `PlanCardSupport.showsBackgroundStrip`이다.
  - 스타일이 자기 작업 목록을 그리면 줄은 없다.
  - 기본 보기에서는 턴이 끝난 뒤, 마이티 보기에서는 처음부터 보인다.
  - 도는 작업이나 끝 보고를 한 작업(`noticeDelivered`: completed, failed, stopped)이 하나라도 있어야 한다. `unknown`만 남으면 줄은 없다.
  - 사용자가 숨기지 않았어야 한다(`enabled`).
- 그래서 줄은 **도는 작업과 이번 실행에서 끝난 작업을 함께** 가진다. 모두 끝나도 다음 요청까지 남아 "모두 끝남 · 알림 전달됨"을 보인다.

### 줄의 글
- **머리:** 굵은 "백그라운드 작업 {count}개"(`plan.background.listTitle`, count는 전체 작업 수), 그 뒤 요약(`backgroundStripSummary`)이 온다.
  - 도는 작업이 있으면 "턴 완료 · 백그라운드 N개 실행 중 · 끝나면 알림"이다.
  - 모두 끝났으면 "모두 끝남 · 알림 전달됨"이다.
- **행:** 종류 · 경과 시간에 "알림 전달됨"이 붙는다. 블록을 못 찾은 행에는 "끝나면 알림" 또는 "알림 전달됨"이 붙는다(`backgroundNotice`). 같은 행은 claude-plan 스타일의 작업 목록 위젯에도 쓰인다.
- **숨기기:** 저장값 `AppSnapshot.showsBackgroundWork`(nil이면 보임) 하나를 네 곳이 바꾼다.
  - 줄의 눈 단추(`plan.background.hideStrip`, `hideStripHelp`)
  - 설정 → 일반 → 화면의 "입력창 위에 백그라운드 작업 표시"(`settings.display.backgroundWorkToggle`)
  - 창 … 메뉴의 "백그라운드 작업 표시"(`pane.menu.backgroundWork`)
  - 입력창 오른쪽의 백그라운드 작업 단추(`composer.backgroundWork.*`). 0.4.12/0.4.16의 "입력창 백그라운드 작업 토글"이 바로 이 값을 바꾼다.
- 펼침 상태 `backgroundWorkOpen`은 따로 저장된다.

### 폰
- 세부 정보의 `backgroundWork`에는 **도는 작업만** 실린다. 하나도 없으면 필드가 없다.
- 폰은 마이티 보기 위에 접힌 "백그라운드 작업 N개" 줄을 두고, 도는 것이 없으면 줄도 없다.
- 끝난 작업의 수는 받지 못한다.

### `plan.background.status`
"턴 완료 · 백그라운드 {count}개 실행 중"은 줄 밖에서도 쓴다.
- 창 머리의 상태 글자(맥 `SessionPaneView` ~288, 윈도우 `PaneHeader`, 웹 머리)
- 기록의 요청 상태 알약(맥 `MightyGraphView` ~929, 윈도우 `MightyGraphViewModel` ~265)
- 고정된 스타일 엔진의 라벨(`Styles/StyleStateEngine`)

## 바뀌는 동작

### 1. 머리: 두 수
- 줄 머리는 다음 순서다: 아이콘, 굵은 **백그라운드 작업**(새 키 `plan.background.title`), 보조색 "끝남 N개 · 작업 중 N개", 접기 화살표.
- **작업 중** = 줄이 가진 작업 중 `running`인 것의 수.
- **끝남** = 줄이 가진 작업 중 `running`이 아닌 것의 수, 곧 `tasks.count − running.count`. completed, failed, stopped, unknown이 모두 들어간다.
  - 줄이 가진 작업은 이번 실행(지금 요청이 연 프로세스)의 것이다. 그래서 끝남은 "이번 요청 이후 끝난 백그라운드 작업"이다. 다음 요청이 실행을 시작하면 둘 다 0에서 다시 센다.
  - 펼친 행 목록(최대 8개)과 달리 수는 전체(최대 32개)로 센다. 지금 머리의 count도 전체 수다.
- 규칙 하나를 둔다. 맥 `PlanCardSupport.backgroundCounts(_:)`, 윈도우 `PlanCardSupport.BackgroundCounts`, 폰 `backgroundCounts(done, running)`다. 글은 `plan.background.doneCount`와 `plan.background.runningCount`를 " · "로 잇는다.

### 2. 모두 끝났을 때
- 보이는 규칙은 숨기기만 빼고 그대로다. 줄은 다음 요청까지 **"끝남 N개 · 작업 중 0개"로 남는다.**
  - 지금도 다음 요청까지 남는다.
  - 펼치면 끝난 행이 상태(완료/실패/중단)와 걸린 시간을 보인다.
  - 사라지게 하면 마지막 작업이 끝나는 순간 줄이 깜빡 없어져, 끝남 수를 보일 곳이 없다.
- `unknown`만 남은 경우(멈춤이나 앱 종료로 끝 보고 없이 끝남)는 지금처럼 줄이 없다.

### 3. 알림 말 지우기
- 머리 요약(`backgroundStripSummary`)과 행의 알림 말(`backgroundNotice`, "알림 전달됨" 덧붙이기)을 없앤다.
- 행은 블록을 찾으면 "종류 말 · 걸린 시간"(예: "하위 에이전트 · 완료 · 1분 5초"), 못 찾으면 "종류 · 상태 · 걸린 시간"이다. 스타일 작업 목록 위젯의 행도 같다(앱 쪽 그리기라 고정 엔진과 무관하다).
- `backgroundSummary`는 요약 말고는 쓰는 곳이 없어 함께 지운다. `noticeDelivered`는 보이는 규칙에 남는다(글자 없음).
- 지우는 키: `plan.background.awaitingNotice`, `plan.background.noticeDelivered`, `plan.background.allNotified`, `plan.background.listTitle`.
- **`plan.background.status`는 그대로 둔다.**
  - 이 글은 알림 약속이 아니라 요청의 상태다. 사용자가 말한 "턴 완료"도 이것이다.
  - 고정된 스타일 엔진이 이 키를 부르므로 지울 수 없다. 엔진의 글과 창 머리, 기록 알약이 서로 같아야 한다.
  - 줄 머리는 이제 이 글을 쓰지 않는다. 그래서 같은 화면에서 두 번 나오지도 않는다.
- `plan.background.show`/`hide`("목록 보기"/"목록 접기")는 접기 단추의 접근성 값과 웹 접기 라벨로 남는다.

### 4. 숨기기 없애기
- 줄은 위 규칙대로 백그라운드 작업이 있으면 늘 보인다.
- **없애는 것:** 줄의 눈 단추, 설정 → 일반 → 화면의 스위치, 창 … 메뉴 항목, 입력창의 백그라운드 작업 단추. 맥, WinUI, 웹 창 모두다.
- 입력창 도구 줄 너비 계산(맥 `composerToolbar`의 `actionsWidth`)에서 그 단추 몫(16 + 간격)을 뺀다.
- 웹 창의 `composerToggle`은 `statusLine`만 남는다. 상태 `toggles`는 `{ statusLine }`이 되고, `backgroundWork` 브리지의 `hide` 동작은 사라진다.
- **저장값:** `AppSnapshot.showsBackgroundWork`(맥 `Models.swift`/`StateRepository`, 윈도우 `Models.cs`)를 지운다.
  - 맥은 저장 파일을 손으로 읽으므로(`decodeSnapshot`) 그 키를 더 읽지 않는다. 저장은 합성 인코더라 다음 저장부터 키가 빠진다.
  - 윈도우는 System.Text.Json이 모르는 키를 무시하므로 예전 파일이 그대로 읽힌다. 다음 저장에서 빠진다.
  - 예전에 숨겨 둔 사람(`false`)도 다음 실행부터 줄을 본다. 사용자가 원한 결과다.
- `backgroundWorkOpen`(펼침 상태)은 그대로 둔다. 줄을 펼쳐 둘지는 여전히 뜻이 있다.

### 5. 폰
- 세부 정보에 새 필드 **`backgroundDone`**(0 이상의 정수)을 더한다. 맥 줄의 끝남 수와 같다.
  - 호스트는 마이티 보기의 줄이 보일 때(`showsBackgroundStrip(work, mighty: true, styleDrawsTasks: false)`) 0을 포함해 언제나 싣고, 아니면 뺀다.
  - 0도 싣는 까닭이 있다. 폰은 필드가 있으면 새 호스트임을 안다. 그래서 "끝남 0개"와 "모름"을 가린다.
  - 맥 `PlanCardSupport.mobileBackgroundDone`, 윈도우 `MobileBackgroundDone`가 값을 정한다. 롱 폴을 깨우는 요약값에도 들어간다.
- 폰 줄 머리는 "백그라운드 작업 · 끝남 N개 · 작업 중 N개"다. 작업 중은 받은 `backgroundWork` 행의 수다.
- 폰은 끝난 행을 받지 않는다. 끝난 작업은 알림 실행의 다이어그램에 있다. 그래서 모두 끝나면 머리만 남고 누를 수 없다.
- `backgroundWork`가 비고 `backgroundDone`도 없거나 0이면 줄이 없다.
- `backgroundDone`도 새 호스트의 증거다(`hostFiltersBackground`).
- **버전이 어긋날 때**

  | 호스트 | 휴대폰 | 줄 |
  | --- | --- | --- |
  | 새 | 새 | "끝남 N개 · 작업 중 N개". 모두 끝나면 머리만 남는다. |
  | 새 | 예전 | `backgroundDone`을 모른다. 예전처럼 "백그라운드 작업 N개"를 보이고, 도는 것이 없으면 줄이 없다. |
  | 예전 | 새 | `backgroundDone`이 없다. "백그라운드 작업 · 작업 중 N개"만 보인다. |

- 폰의 작업 중 수는 `other` 종류(하위 에이전트도 명령도 아닌 CLI 작업)를 세지 않는다. 그런 행은 원래 보내지 않는다. 맥 머리는 센다. 드물어서 맞추지 않는다.

## 문구
`locales/{ko,en,zh,ja}.json`과 사본(`native/macos/Sources/MightyCore/Resources/Locales`, `mobile/src/locales`)에 넣는다. `scripts/check-locales.js`가 `docs/i18n.md`를 다시 쓴다.

| 키 | ko | en | zh | ja |
| --- | --- | --- | --- | --- |
| `plan.background.title` | 백그라운드 작업 | Background tasks | 后台任务 | バックグラウンドタスク |
| `plan.background.doneCount` | 끝남 {count}개 | Done: {count} | 已结束 {count} 个 | 終了 {count}件 |
| `plan.background.runningCount` | 작업 중 {count}개 | Running: {count} | 进行中 {count} 个 | 実行中 {count}件 |

- 영어는 1개에도 맞게 "Done: {count}" 꼴로 쓴다.
- **값만 바꾸는 키:** `graph.background.runWaiting` → 백그라운드 실행 · 실행 중 / Background run · running / 后台运行 · 运行中 / バックグラウンド実行 · 実行中(아래 "최종 수정").
- **지우는 키:**
  - 알림 말: `plan.background.awaitingNotice`, `plan.background.noticeDelivered`, `plan.background.allNotified`, `plan.background.listTitle`
  - 숨기기: `plan.background.hideStrip`, `plan.background.hideStripHelp`, `composer.backgroundWork.name`, `composer.backgroundWork.show`, `composer.backgroundWork.hide`, `pane.menu.backgroundWork`, `settings.display.backgroundWorkToggle`
  - `check-locales`는 쓰지 않는 키에서 실패한다. 그래서 맥, 폰, 윈도우, 웹, 도움말 모두가 그 키를 놓은 마지막 작업에서 지운다.
- **남는 키:** `plan.background.status`, `plan.background.show`, `plan.background.hide`
- 하드코딩한 한국어는 없다.

## 범위
- Claude 창만이다.
- **맥:** `PlanCardSupport`, `BackgroundWorkViews`(줄과 행), `SessionPaneView`(줄 조건, 입력창 단추, … 메뉴), `SettingsViews`, `AppStore`(설정값, 스모크), `Models`/`StateRepository`, 폰 세부 정보.
- **윈도우:** Core `PlanCardSupport`, `Models`, `MobileDesktopRouter`. WinUI는 줄, 입력창, 머리 메뉴, 설정, 웹 카드, 폰 세부 정보. 웹 창은 `Cards.tsx`, `Composer.tsx`와 타입.
- **폰:** 줄, 세부 정보 파싱, 세션 화면.
- **문서:**
  - 도움말 4개 언어 `approvals.md`의 `#background`
  - `docs/mighty-mode.md`(줄 설명)
  - `docs/mobile-remote.md`(`backgroundDone`)
  - `native/windows/web/VISUAL-PARITY.md`(눈 단추)
- 도움말 사진 `background-work`(`docs/help/shots.json`)은 숨기기 단추에 기대지 않으므로 둔다. 다음 사진 생성 때 새 줄이 찍힌다.
- 고정된 스타일 엔진(`MightyCore/Styles/**`)과 그 `.taskList` 위젯은 건드리지 않는다.
- 기록 알약, 창 머리 상태 글자, 결과 카드 제목은 그대로다.

## 테스트
- **맥**
  - `PlanCardTests`: 숨김 테스트(`aHiddenBackgroundLineDrawsNothingInEitherView`)를 "줄은 두 보기 모두에서 그려진다"로 바꾼다.
  - 같은 파일: 알림 말 테스트를 두 수 테스트로 바꾼다. 도는 1 + 끝남 1, 모두 끝남, unknown 섞임, 행 글에 알림 말 없음을 본다.
  - `StylesClaudePlanTests`: `backgroundSummary` 확인을 지운다.
  - `SnapshotPreferencesTests`: 숨김 저장 테스트를 바꾼다. 예전 파일의 `showsBackgroundWork: false`는 읽히고, 펼침 상태는 왕복하며, 저장 파일에는 그 키가 없다.
  - `MobileRemoteExtensionTests`: `mobileBackgroundDone`과 `backgroundDone`의 인코딩을 본다.
  - 앱 스모크 `runBackgroundWorkLineSmoke`: 숨김 확인을 빼고 펼침 저장, 두 보기에서 그려짐, 저장 파일에 키 없음을 본다.
- **윈도우**
  - `WorkDashboardVerification`의 `BackgroundWorkLinePersistsAndHides`와 `BackgroundWorkHideSurvivesABriefReplaceBlock`를 펼침 값으로 바꾼다. 저장 재시도 검증은 남긴다. 이름과 `Verification.cs` 등록도 바꾼다.
  - `ClaudePlanStyleVerification`: 맥과 같이 바꾼다.
  - `BackgroundNoticeRunsVerification`: `MobileBackgroundDone`을 본다.
- **폰(jest):** `background-strip.test.ts`(두 수, 예전 호스트, 모두 끝남), `background-work.test.ts`(`parseBackgroundDone`, `backgroundCounts`, `hostFiltersBackground`).
- **웹:** 렌더 테스트가 없다. `tsc`로 본다.
- **CI:** 숨기기를 누르는 스모크는 맥 `runBackgroundWorkLineSmoke` 하나다. `.github/workflows`와 `scripts/test-native-windows.ps1`은 이름으로 부르지 않는다.

## 최종 수정(사용자 답)
- 도는 백그라운드 명령의 종류 말 `graph.background.runWaiting`을 "백그라운드 실행 · 알림으로 확인"에서 **"백그라운드 실행 · 실행 중"**으로 바꾼다. 키는 그대로이고 값만 바뀐다.
  - 네 언어 모두 바꾼다: en "Background run · running", zh "后台运行 · 运行中", ja "バックグラウンド実行 · 実行中". 사본도 같다.
  - 이 키를 쓰는 곳은 모두 따라 바뀐다: 줄의 행, 흐름 블록, 웹 창, 폰.
  - 하위 에이전트의 "하위 에이전트 · 응답 대기"(`graph.background.agentWaiting`)는 그대로다.
  - 도움말 본문은 이 말을 옮겨 적지 않으므로 고칠 곳이 없다.
  - 예전 문서(`2026-10-27-background-work-kinds-*`)는 기록이라 두고, 코드 주석(`MainWindow.WebShell.Cards.cs` ~21)은 고친다.
