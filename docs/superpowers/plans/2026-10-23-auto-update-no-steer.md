# 자동 앱 업데이트(완전 자동) + 중간 요청(steer) 제거

브랜치 `main`(작업 시작 63ba94c8). 사용자가 결정한 두 기능(2026-10-23):
- **A. 완전 자동 업데이트:** 맥·윈도우가 새 버전을 스스로 받아 두고, 종료할 때 설치하거나 앱이 쉬고 있을 때 1분 알림 뒤 다시 시작해 설치한다.
- **B. 중간 요청 완전 제거:** 실행 중인 창에 사용자가 보내는 글은 언제나 대기열로 간다. 맥·윈도우·폰 모두.

맥이 기준이고 윈도우·폰은 맥과 같은 규칙·문구를 따른다. 작업 순서는 맥 → 문서 → 윈도우 → 폰.

## 지켜야 할 것 (모든 작업)
- 저장소는 **공개**다. 계획·코드·커밋·픽스처에 개인 경로·이메일·IP를 넣지 않는다.
- **GUI 앱을 로컬에서 실행하지 않는다.** 앱을 설치하지 않는다. 설치 스크립트나 도우미를 실제로 돌리지 않는다(스크립트 문자열과 계획만 테스트한다).
- 맥 테스트는 필터를 반드시 준다: `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-autoupd --filter '<앵커 정규식>'`
- 맥 앱은 컴파일만 확인한다: `DEVELOPER_DIR=/Library/Developer/CommandLineTools swift build --package-path native/macos --scratch-path /tmp/mc-autoupd --product MightyClaude`
- 윈도우:
  - `bash <windows-core-checks> <repo>` 결과가 `RESULT: BASELINE`이어야 한다.
  - `bash scripts/check-winui-compile.sh`
- 폰: `cd mobile && npm run typecheck && npx jest`. node_modules가 없으면 `mobile/` 안에서만 `npm ci`.
- 공통 검사:
  - `node scripts/check-locales.js --check`
  - `node scripts/check-locales.js --check --touched-since 63ba94c8`
  - `node scripts/check-design-tokens.js --check`
  - `node scripts/help/build.mjs --check`
  - `bash scripts/check-style-freeze.sh`(OK여야 함)
- 로케일:
  - 원본은 `locales/{ko,en,zh,ja}.json`이다.
  - 고친 뒤 `native/macos/Sources/MightyCore/Resources/Locales/`와 `mobile/src/locales/`에 바이트 그대로 복사한다.
  - 그다음 `node scripts/check-locales.js`(docs/i18n.md 다시 생성) → `--check` 순서로 돌린다.
  - 쓰이지 않는 키는 실패로 잡히므로, 그 키를 마지막으로 쓰던 코드를 지우는 작업이 키도 지운다.
  - 도움말의 `{{ui:키}}`는 사용으로 세지 않는다. 대신 없는 키는 `help/build.mjs --check`가 실패로 잡는다. 그래서 도움말 참조를 먼저 뺀다(T5가 T9보다 앞).
- **고정 경로(스타일 엔진 v7)는 건드리지 않는다:**
  - `native/macos/Sources/MightyCore/Styles/*`, `mobile/src/lib/styles.ts`, `docs/mighty-styles.md`, `native/macos/Tests/MightyCoreTests/Style*.swift`
  - `StyleComposer.enter`는 "답변/다시 쓰기/그대로"만 정하고 steer는 정하지 않으므로 바꿀 필요가 없다.
  - `docs/mighty-styles.md` §1.17.4의 "⌘Enter로 끼워 넣기"는 `docs/styles-followups.md`에 "다음 고정 때 맞출 것"으로 적는다.
  - 새 테스트 파일 이름은 `Style`로 시작하지 않는다.
- 커밋 제목은 `(영역) 사용자가 이제 얻는 것` 형식이고, 마지막 줄은 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`이다.
- 다른 세션도 main에 커밋할 수 있다. 커밋 전에 `git status`와 `git log -3`을 확인한다. 푸시는 하지 않는다(조정자가 한다).

---

## 기능 A — 완전 자동 앱 업데이트

### 지금
- 서명된 latest.json은 앱을 열 때만, 24시간에 한 번 확인한다.
  - 맥: `AppStore.checkForAppUpdateAutomatically()`, `appUpdateCheckInterval = 24h`
  - 윈도우: `AppUpdateCoordinator.CheckIntervalHours = 24`, `MainWindow.BeginAutomaticAppUpdateCheck()`
- 다운로드와 "설치하고 다시 실행"은 사용자가 누를 때만 한다.
- 맥 도우미 `AppUpdateService.installScript`는 이미 `relaunch:` 인자를 받는다.
- 윈도우 도우미 `AppUpdateReplacement.RunAsync`는 언제나 다시 실행한다. 63ba94c8부터 시작할 때 `InstallerRegistration.SyncDisplayVersion`이 설치된 앱의 버전을 맞춘다.

### 정한 동작 (두 플랫폼 같음, 기존 "자동" 스위치 뒤, 기본값 켬)
1. **확인 주기**
   - 시작할 때 마지막 확인에서 6시간이 지났으면 확인한다(저장된 마지막 확인 시각을 그대로 쓴다).
   - 실행 중에는 10분마다 6시간이 지났는지 본다.
   - 실패하면 다음 주기에 다시 시도한다. 메모리에 둔 마지막 시도 시각으로 매 틱마다 다시 치지 않게 한다.
2. **받기:** 새 서명 버전을 찾으면 묻지 않고 받고, 오늘과 같은 검증을 한다(서명·크기·sha256·번들 검사·스테이지). 같은 버전이 이미 스테이지되어 있으면 다시 받지 않는다.
3. **설치**
   - (a) 앱을 정상 종료하면, 스테이지된 업데이트를 **다시 실행하지 않고** 설치한다. 도우미는 pid가 끝날 때까지 기다린다.
   - (b) 앱이 **쉬고 있으면** 60초 카운트다운 배너를 띄운다. 버튼은 "지금 다시 시작"과 "나중에"다.
     - 시간이 다 되면 다시 시작하며 설치한다.
     - "나중에"를 누르면 4시간 동안 묻지 않는다. 그사이 종료하면 (a)로 설치된다.
     - 카운트다운 중에 다른 일(실행·초안·카드 등)이 생기면 조용히 취소한다. 키보드·마우스 입력만으로는 취소하지 않는다(알림 단추를 누를 수 있게). 입력이 있으면 카운트다운을 새로 시작하지는 않는다.
4. **표시:** 상태 줄과 설정에 "다음 실행 때 {version} 설치"와 "지금 다시 시작"을 보인다.
5. **실패:** 설정 행에만 보인다. `.ready` 상태는 그대로 지킨다.
6. **스위치를 끄면:** 루프·다운로드·카운트다운을 멈춘다. 스테이지된 업데이트는 수동 "지금 다시 시작"으로만 설치되고, 끈 상태에서는 종료할 때 설치하지 않는다.
7. **예외:** 스모크, 도움말 캡처, 키 없는 빌드에서는 자동 루프를 돌리지 않는다.

### "쉬고 있음" — 순수 함수(단위 테스트 대상)
- 맥: `MightyCore/AppUpdateAuto.swift`에 `AppIdleFacts`, `AppIdleBlocker`, `AppUpdateAuto.idleBlockers(_:now:)`를 둔다.
- 윈도우: `MightyClaude.Core/AppUpdateAutoPolicy.cs`에 같은 이름으로 둔다.

다음 중 하나라도 해당하면 쉬는 상태가 아니다.

| 막는 사유 | 맥에서 읽는 곳 |
|---|---|
| `agentRun` | `kind != "shell" && status == "running"`인 세션(원격 포함), `pendingRuns`, `heldForUpdate`, 위임 ending runs, 위임 pending |
| `pendingCard` | `toolPermissions`, `antigravityBoard` 카드, `webOpenRequests` |
| `queuedInput` | 비지 않은 `queuedInputs`. `delegationHeld`는 디스크에 남으므로 제외한다. |
| `draft` | 공백이 아닌 `drafts`, `attachmentDrafts`, `importingAttachments` |
| `terminal` | `kind == "shell" && status == "running"`, 살아 있는 에이전트 프로세스, setup run·터미널, CLI 로그인 진행 중 |
| `cliWork` | CLI 업데이트, 플러그인 관리, 모델 리셋, 도구 설치 진행 중 |
| `remote` | 화면 공유 중, 폰 업로드 중, 이 Mac이 호스트로 돌리는 원격 실행 |
| `modal` | 막는 모달, setup 시트, 열린 설정 |
| `userActive` | 키보드·마우스 입력이 5분 안에 있었음 |
| `recentAgentActivity` | 마지막 실행 종료나 입력이 3분 안 |

---

## 기능 B — 중간 요청(steer) 제거

### 사용자 경로 (모두 없앤다)
**맥**
- `BackgroundQueuePolicy.composerJoins`: 백그라운드만 기다리는 창에서 Enter가 끼워 넣는다. **사용자가 신고한 증상의 원인.**
- `AppStore.submit(_:steering:agent:)` → `deferInput(...steering:)` → `steer(_:item:)`(steerTasks)
- `ComposerSecondSend.steer`/`fallback(steerable:)`, `AppStore+Delegation.runAfterNoWorktree`
- 중지 옆 번개 단추(`composer-steer-<id>`), `queue.steerHintMac`, `composer.placeholder.busyMac`, `composer.inputHelp.steer`, `queue.waitingOnBackground.mac`
- 폰 호스트: `mobileSubmit` + `phoneSteers`, 요약의 `canSteer`
- 원격 작업 공간: `RemoteClientService.steer`, 호스트 `RemoteHostService`의 `case "steer"`

**윈도우**
- `BackgroundQueuePolicy.ComposerJoins/PhoneSteers`
- `MainWindow.QueuedComposer.cs`의 steer 단추·`DeferBusyComposer(steering)`, `MainWindow.cs`의 Ctrl+Enter·바쁠 때 자리표시자
- `ComposerSecondSends.Choose/Fallback`, `MainWindow.Delegation.cs`
- `MainWindow.MobileRemote.cs`, `MobileDesktopRouter.cs`(canSteer)
- `RemoteHostService.Runs.cs`, `RemoteClientService.Runs.cs`, `DesktopService.TrySteerAsync`의 원격 갈래
- 스모크

**폰**
- `mobile/src/lib/send.ts`의 `busySendChoices`, `composer.tsx`의 "바로 전달"(`phone.composer.steer`), 세션 화면의 prop, `icons.tsx`의 steer 아이콘

### 정한 동작
- **바쁜 창**(실행 중·시작 중·업데이트로 잡힘·백그라운드만 기다림):
  - Enter는 언제나 대기열.
  - ⌘Enter(윈도우 Ctrl+Enter)는 새 worktree 자식을 만들 수 있으면 그것을, 아니면 대기열.
  - worktree 자식이 실패했을 때 넘어가는 곳도 대기열.
- 백그라운드만 기다리는 창의 대기열 안내는 모든 스타일에서 보인다("백그라운드 작업이 끝나야 시작합니다 · 지금 시작하려면 중지").
- **호스트**
  - 폰이 보낸 글은 `mode`와 상관없이 바쁘면 대기열에 넣는다(`mode:"steer"`도 400이 아니라 queued).
  - 요약의 `canSteer`는 언제나 `false`다(구버전 폰을 위해 필드는 남긴다).
  - 원격 호스트의 steer 요청에는 `accepted:false`로 답한다(구버전 클라이언트는 자기 대기열로 넘긴다).
- 그래프 기록의 예전 steer 블록("중간 요청")은 그대로 그린다. 그래프·기록 쪽 steer 키·토큰과 `phone.session.accepted.steered`, `remote.error.badMode`는 남긴다.

### 위임의 내부 steer
- **유지한다.** 자식 알림이나 후속 지시를 실행 중인 Claude 부모에게 넣는 경로(`DelegationDelivery` → `deliver(.steer)` → `steerRunningTurn` → `ProcessRunner.steer`)는 위임 계약의 일부다. 사용자가 보내는 글이 아니다.
- 다만 이 경로가 그래프에 "중간 요청" 블록으로 보이므로 **T10에서 제목을 "위임 알림"으로 바꾼다.**

---

## 작업

### T1 (맥 Core) 자동 업데이트 정책·쉬는 상태 판정·종료할 때 설치하는 스크립트
- **새 파일 `MightyCore/AppUpdateAuto.swift`**
  - 상수: checkInterval 6h, tick 600s, userIdle 300s, agentIdle 180s, countdown 60s, snooze 4h
  - `AppIdleFacts`(개수와 Bool 값만), `AppIdleBlocker`(CaseIterable)
  - `checkDue(enabled:hasKey:lastCheck:lastAttempt:now:)`
  - `idleBlockers(_:now:)`
  - `AppUpdateRestartState { idle, countingDown(until), snoozed(until) }`
  - `restartOffer(..., blockers:)`: 입력은 ready 여부, 자동 여부, 막는 사유 목록, 지금 시각. 시작은 사유가 없을 때만, 진행 중인 카운트다운은 `.userActive`만 있으면 계속된다. 결과는 다음 상태와 동작(`.none/.startCountdown/.restart/.cancel`).
- **`AppUpdate.swift`의 `installScript(...relaunch:)`**
  - 교체를 이름 바꾸기로 한다: DESTINATION → `"$DESTINATION.update-old"` → NEW → DESTINATION. 실패하면 되돌리고, 성공하면 old를 지운다.
  - `relaunch == false`이면 교체 직전에 `pgrep -f "$DESTINATION/Contents/MacOS/"`를 보고, 실행 중이면 교체를 건너뛴다. 이때 `open -n`을 쓰지 않는다.
- **테스트**
  - 새 `Tests/MightyCoreTests/AppUpdateAutoTests.swift`:
    - checkDue: 6시간, 실패 뒤 다음 주기, 끈 상태, 키 없음
    - 사유마다 그 사유만 막는지, 사유가 없으면 빈 배열인지
    - 카운트다운 시작 → 막힘 → 취소, 나중에 → 4시간 snooze, 끔 → 취소
  - `AppUpdateTests`: relaunch:false에는 `open -n`이 없고 pgrep·`.update-old`가 있다. relaunch:true에는 `open -n`이 있다.
- **검증:** `--filter '^MightyCoreTests\.(AppUpdateTests|AppUpdateAutoTests)/'`

### T2 (맥 앱) 주기 확인·자동 다운로드·종료할 때 설치·스테이지 표시
- **`AppStore+AppUpdate.swift`**
  - `checkForAppUpdateAutomatically()`가 `AppUpdateAuto.checkDue`를 쓴다.
  - 새 `startAppUpdateLoop()`(cliAutoUpdateLoop 방식)를 `load()`에서 시작하고 `shutdown()`에서 취소한다. 저장 속성 `appUpdateLoop`, `appUpdateLastAttempt`를 더한다.
  - `checkForAppUpdate()`:
    - 같은 버전이 `.ready`면 그대로 둔다.
    - `.available`이고 자동이면 곧바로 `downloadAppUpdate()`를 부른다.
    - `.ready`인데 실패하면 phase는 그대로 두고 `AppUpdateState.lastError`에만 남긴다.
  - `appUpdateAutomatic` setter: 끄면 다운로드 취소·카운트다운 취소·루프 정지, 켜면 루프 시작.
  - 새 `launchAppUpdateOnQuit()`:
    - `shutdown()` **맨 앞**에서 부른다.
    - 조건: `.ready`, 자동 켬, `.installing` 아님, 전원 끄기·로그아웃 중 아님(`NSWorkspace.willPowerOffNotification`).
    - `installAppUpdateAndRelaunch()`와 같은 사전 검증을 공통 함수로 빼서 쓰고, `relaunch: false`로 도우미를 띄운다.
- **`AppUpdateSettingsView.swift`**
  - 스위치: `settings.appUpdate.autoUpdateToggle` + `settings.appUpdate.autoUpdateDescription`
  - ready 상태: `settings.appUpdate.stagedTemplate` + `settings.appUpdate.restartNowButton`(id `app-update-install` 유지)
  - 실패: `settings.appUpdate.retryNotice`
- **`WorkspaceView.swift` 상태 줄**
  - ready: `window.status.updateStaged` + 작은 "지금 다시 시작" 단추(`window.status.updateRestartNow`, id `app-update-restart`, `window.status.updateStagedHelp`)
  - available: 기존 배지
- **로케일 추가**(4개 언어): 위 키 8개. 예(ko):
  - "새 버전을 자동으로 받아 설치"
  - "6시간마다 확인하고, 받은 업데이트는 앱을 종료할 때 설치합니다. 앱이 한동안 쉬고 있으면 1분 알림 뒤 다시 시작해 설치합니다."
  - "다음 실행 때 {version} 설치"
  - "지금 다시 시작"
  - "다음 확인 때 다시 시도합니다."
  - `autoCheckToggle`, `readyTemplate`, `installButton`은 윈도우가 쓰므로 남긴다(T7에서 정리).
- **스모크(할 수 있으면):** 고정한 `.ready` 상태로 `app-update-restart`가 있는지 확인한다. 도우미는 띄우지 않는다.
- **검증:** T1 필터 + 앱 빌드 + 로케일·토큰 검사

### T3 (맥 앱) 쉴 때 60초 카운트다운 뒤 다시 시작
- **`appIdleFacts(now:)`**: 표의 출처를 모은다. 필요한 접근자를 더한다.
  - `ScreenShareMenuBarController.isActive`
  - `RemoteHostService.activeJobCount()`
  - `MobileRemoteService`의 업로드 수
  - `DelegationCoordinator`의 pending 수
  - 사용자 입력 유휴: `CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)`
- 새 `@Published var appUpdateRestart`. 루프 틱(카운트다운 중에는 1초)마다 `restartOffer`를 부르고, 결과가 `.restart`면 `installAppUpdateAndRelaunch()`를 부른다.
- **`WorkspaceView.swift`:** `appUpdateCountdownBanner`(id `app-update-countdown`)
  - `appUpdate.countdown.message`: "{version} 업데이트를 설치하려고 {seconds}초 뒤 다시 시작합니다. 열려 있던 작업 공간과 창은 그대로 돌아옵니다."
  - `appUpdate.countdown.restartNow`
  - `appUpdate.countdown.later`
- **테스트:** `AppUpdateAutoTests`에 카운트다운 1초 틱 시나리오를 넣는다. 앱은 빌드만 확인한다.

### T4 (맥 Core+앱) Enter와 ⌘Enter는 steer하지 않는다
- **`BackgroundQueuePolicy.swift`**
  - `composerJoins`, `phoneSteers`를 지운다.
  - `waitsOnBackground(work:queued:)`에서 launchesInPlan을 뺀다.
  - 머리 주석: "바쁜 창의 새 입력은 언제나 대기열".
- **`Delegation.swift`**
  - `ComposerSecondSend.steer`를 지운다.
  - `choose(...)`의 `steerable`을 지운다. 결과는 `.worktree` 또는 `.queue`다.
  - `fallback(steerable:)`을 지운다.
- **`MobileRemoteSupport.canSteer`** 대신 요약에 `canSteer: false`를 넣는다. `MobileRemoteModels.swift` 주석: "0.4.35부터 언제나 false, 구버전 폰용".
- **`RemoteHostService.swift`의 `case "steer"`:** 검증한 뒤 `accepted: false`로 답한다.
- **`AppStore.swift`**
  - `submit(_:secondSend:agent:)`: 바쁘면 secondSend이고 worktree가 될 때 `runInNewWorktree`, 아니면 `deferInput`(대기열).
  - `deferInput`의 steering·`DeferOutcome.steering`·steerTasks·`finishSteerChain`·`steer(_:item:)`를 지운다.
  - `steerRunningTurn`은 위임용으로 남기되 원격 갈래를 지운다. `RemoteClientService.steer`는 쓰는 곳이 없으면 지운다.
  - `canSteer(_:)`는 위임용으로 남긴다.
  - composer 스모크는 steer 단추가 언제나 없음을 기대한다.
- **`AppStore+Delegation.runAfterNoWorktree`:** 언제나 대기열(`delegation.human.queued`).
- **`AppStore+MobileRemote.mobileSubmit`:** 바쁘면 언제나 대기열로 보내고 `.immediate(.queued)`를 돌려준다. 요약은 `canSteer:false`.
- **기타**
  - `AppStore+AntigravityPermissions.swift`(steering 인자)
  - `SessionPaneView.swift`(.steer 갈래, 번개 단추, 자리표시자·도움말, 대기열 안내)
  - `SessionInfoDiagnostics.swift`, `ComposerPresentationDiagnostics.swift`의 `composer-steer-`
- **로케일**
  - 값 변경: `queue.waitingOnBackground.mac`(ko: "백그라운드 작업이 끝나야 시작합니다 · 지금 시작하려면 중지"), `settings.delegation.description`(끄면 ⌘Enter는 대기열)
  - 삭제: `queue.steerHintMac`, `composer.placeholder.busyMac`, `composer.inputHelp.steer`
- **테스트**
  - `BackgroundQueuePolicyTests`
  - `DelegationHumanChildTests`, `DelegationSwitchTests`, `DelegationCrossAgentTests`, `DelegationScenarioS1/S3Tests`, `ClaudePlanModeTests`
  - `MobileRemoteProviderTests`(canSteer false), `MobileRemote*Tests`(`mode:"steer"`면 queued)
  - `RemoteWorkspaceTests`(`/v2/runs/x/steer`면 `accepted:false`)
  - 위임 내부 steer 기대(`DelegationDeliveryTests` 등)는 **바꾸지 않는다.**
- **검증**
  - `--filter '^MightyCoreTests\.(BackgroundQueuePolicyTests|Delegation(HumanChild|Switch|CrossAgent|Providers|Delivery|ScenarioS[123])Tests|MobileRemote[A-Za-z]*Tests|RemoteWorkspaceTests|RemoteWireFixtureTests|ExecutionGraphTests|SessionHistoryTests|ClaudePlanModeTests)/'`
  - 앱 빌드, 로케일, 스타일 고정 검사

### T5 (문서·도움말, 4개 언어)
- **도움말 `docs/help/content/{ko,en,zh,ja}/`**
  - `getting-started.md#update`: 자동 받기, 종료할 때 설치, 1분 알림, 지금 다시 시작/나중에, 스위치를 끄면 수동. `{{ui:settings.appUpdate.autoUpdateToggle}}`, `{{ui:settings.appUpdate.restartNowButton}}`을 쓴다.
  - `faq.md`: installButton → restartNowButton
  - `windows.md`: 업데이트 설명과 Ctrl+Enter 표
  - `agent-pane.md`: `{{ui:phone.composer.steer}}`를 지운다. ⌘Enter는 worktree를 만들 수 없으면 대기열이다. 중간 요청은 "예전 기록에만 보임"으로 바꾼다.
  - `approvals.md`, `layout.md`
  - `delegation.md`: 넘어가는 곳은 대기열, 폰도 대기열이다. 부모에게 바로 전달하는 알림 설명은 그대로 둔다.
  - `remote.md`(끼워 넣기 문장 삭제)
  - `phone.md`: 실행 중에는 "대기열에 추가"만 있다.
- **docs/**
  - `app-update.md`: 자동 받기, 종료할 때 설치, 쉬는 상태 표, 카운트다운
  - `windows-app-update.md`: `--relaunch 0`, 도우미의 DisplayVersion
  - `mobile-remote.md`: canSteer는 언제나 false이고 구버전용, `steered`는 구버전 호스트만 보냄, `mode:"steer"`는 대기열
  - `windows-mobile-remote.md`, `mighty-mode.md`, `native-verification.md`
  - `remote-workspaces.md`: steer에는 `accepted:false`
  - `styles-followups.md`: §1.17.4
- **검증:** help build, locales

### T6 (윈도우) steer 제거 — 맥 T4와 같은 규칙
- **Core**
  - `BackgroundQueuePolicy.cs`(`WaitsOnBackground(work, queued)`)
  - `Delegation.cs`(`Steer`·`steerable`·`Fallback` 삭제)
  - `DesktopService.Delegation.cs`
  - `DesktopService.TrySteerAsync`: 원격 갈래 삭제, 로컬은 위임용으로 남김
  - `RemoteClientService.Runs.cs`(`SteerAsync`는 쓰는 곳이 없으면 삭제)
  - `RemoteHostService.Runs.cs`(accepted false)
  - `MobileDesktopRouter.cs`(`canSteer = false`)
- **WinUI**
  - `MainWindow.QueuedComposer.cs`: steer 단추 삭제, `DeferBusyComposer(bool secondSend)`
  - `MainWindow.Composer.cs`: `PaintSteer` → worktree만
  - `MainWindow.cs`(Ctrl+Enter, 자리표시자)
  - `MainWindow.ComposerGlyphs.cs`의 `SteerBolt`(쓰는 곳이 없으면 삭제)
  - `MainWindow.Delegation.cs`(대기열)
  - `MainWindow.MobileRemote.cs`
  - 스모크 `MainWindow.DesignSmoke.cs`, `MainWindow.SmallParitySmoke.cs`
- **로케일**
  - 값 변경: `queue.waitingOnBackground.windows`, `settings.delegation.descriptionWindows`
  - 삭제: `queue.steerHint`, `composer.placeholder.busy`, `delegation.human.steered`(쓰는 곳이 없으면)
- **Core.Tests**
  - `ClaudePlanStyleVerification`, `DelegationHumanChildVerification`, `DelegationVerification`, `DelegationCrossAgentVerification`, `ComposerParityVerification`, `QueuedComposerVerification`, `MobileHostVerification`, `RemoteHostVerification`, `RemoteLoopbackVerification`, `RemoteDesktopVerification`, `WindowsParityVerification`
  - `DelegationDeliveryVerification`은 그대로 둔다.
- **검증:** windows-core-checks BASELINE, WinUI 컴파일, locales

### T7 (윈도우 Core) 자동 업데이트 정책·다시 실행하지 않는 도우미
- **새 파일 `MightyClaude.Core/AppUpdateAutoPolicy.cs`:** 맥과 같은 상수와 `AppIdleFacts`, `AppIdleBlocker`, `CheckDue`, `IdleBlockers`, `RestartOffer`
- **`AppUpdateCoordinator.cs`**
  - 6시간 주기, `IsDue`는 정책을 따른다.
  - 같은 버전이 Ready면 그대로 둔다. 자동이면 `DownloadAsync`로 이어진다. Ready에서 실패하면 `LastError`에만 남긴다.
  - 새 `InstallOnQuitAsync()`
  - `AppUpdatePresentation`: Ready → StagedTemplate/RestartNowButton, RetryNotice
  - `AppUpdateSmoke.FixtureStates`
- **`AppUpdateReplacement.cs`**
  - `AppUpdateInstallPlan.Relaunch`(기본 true)와 `--relaunch 1|0`. 값이 없으면 true(구버전 호환).
  - `Relaunch == false`이면 `startApp`을 부르지 않는다. 성공하면 백업을 지운다.
- **`MightyClaude.WinUI/Program.cs`의 `RunUpdateHelper`:** 교체에 성공하면 `InstallerRegistration.SyncDisplayVersion(plan.InstallDirectory, <새 버전>)`
- **`AppUpdateStrings.cs`:** 새 키를 쓴다. 더 쓰지 않는 `autoCheckToggle`/`readyTemplate`/`installButton`은 로케일에서 지운다.
- **테스트(`AppUpdateVerification.cs` + 등록)**
  - 정책
  - `--relaunch 0` 왕복
  - relaunch 없는 교체는 앱을 시작하지 않고 백업을 지운다
  - 같은 버전이면 Ready를 지킨다
  - `AppUpdateStringsMatchMacOS`
- **검증:** windows-core-checks BASELINE, locales

### T8 (WinUI) 주기 확인·종료할 때 설치·쉴 때 다시 시작
- **`MainWindow.AppUpdate.cs`**
  - CheckDue + `DispatcherQueueTimer`(10분). 스모크에서는 끈다.
  - `CollectIdleFacts()`: 창 상태, 대기열, 초안·첨부, 권한·질문·계획 카드, CLI 업데이트, 화면 공유, 폰 업로드, 원격 호스트 작업, 명령 실행 중인 터미널, 열린 설정·대화상자, `GetLastInputInfo`
  - 카운트다운 InfoBar(맥과 같은 문구와 단추)
  - 도우미 실행은 둘로 나눈다: 수동·쉴 때는 relaunch true + `Close`, 종료할 때는 relaunch false.
- **`MainWindow.cs`의 `AppWindow.Closing`:** `ShutdownAppUpdateAsync()` 앞에 `await AppUpdate.InstallOnQuitAsync()`를 둔다. 이미 Installing이면 건너뛴다.
- **`MainWindow.Shell.cs`의 updateBadge:** Ready면 staged 문구 + "지금 다시 시작"
- **설정 섹션:** 새 스위치 문구, Ready 표시
- **CI GUI 스모크:** 새 Fixture 문구, Ready 배지와 카운트다운 InfoBar 렌더(도우미는 띄우지 않음)
- **검증:** WinUI 컴파일, windows-core-checks BASELINE, locales

### T9 (폰) "바로 전달" 제거
- `send.ts`의 `busySendChoices`: submitModes면 `['queue']`, 아니면 `['send']`. `canSteer` 옵션은 지운다.
- `composer.tsx`: canSteer prop과 steer 갈래를 지운다.
- 세션 화면의 prop을 지운다. `steered` 라벨은 구버전 호스트용으로 남긴다.
- `icons.tsx`의 steer 아이콘은 쓰는 곳이 없으면 지운다.
- `api/types.ts`의 canSteer 주석: "호스트 0.4.35부터 언제나 false, 폰은 읽지 않음". 폰은 바쁜 창에 언제나 `mode:'queue'`를 보낸다.
- 로케일 `phone.composer.steer`를 지운다.
- 테스트: `steer.test.ts` → `busy-send.test.ts`. canSteer가 true여도 queue만 나오고, "바로 전달"이 없음을 확인한다.
- **검증:** typecheck, jest, locales, help build, style-freeze

### T10 (맥·윈도우·폰) 위임 알림을 "중간 요청"이 아니라 "위임 알림"으로
- 입력이 `[Mighty Claude notice ` 또는 `[Mighty Claude follow-up `으로 시작하는 steer 블록을 새 키 `graph.block.delegationNotice`("위임 알림")로 부른다. 고칠 곳은 맥 `MightyGraphSupport.blockTitle`/`MightyTimelineText.kind`, 윈도우 `MightyGraph.cs`, 폰 `mighty.ts`다.
- 머리말의 `graph.header.steers`(중간 요청 n)는 사람이 보낸 예전 steer만 센다.
- wire의 kind는 `steer` 그대로 둔다.
- 고정 경로에 걸리면 하지 않고 보고한다.
- 테스트는 플랫폼마다 하나씩: 알림 입력이면 "위임 알림", 일반 입력이면 "중간 요청".

### 끝낸 뒤
- 모든 검사를 한 번에 돌린다.
- `git grep -n -i "phoneSteers\|composerJoins\|ComposerJoins\|PhoneSteers\|composer-steer\|queue.steerHint\|phone.composer.steer"`의 결과가 비어 있어야 한다.
- 릴리즈는 이 계획 밖이다.

## 정해 둔 값
- 스위치를 끈 채 종료하면 설치하지 않는다.
- 사람이 누르는 "다음 실행"으로 붙잡힌 위임 알림을 실행 중인 부모에게 보내는 동작은 유지한다(위임 동작).
- 수치: 확인 6시간, 틱 10분, 입력 유휴 5분, 에이전트 유휴 3분, 카운트다운 60초, 나중에 4시간.
