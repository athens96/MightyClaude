# Background Strip Counts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
- The background strip above the composer reads "백그라운드 작업 · 끝남 N개 · 작업 중 N개".
- No "끝나면 알림" / "알림 전달됨" / "모두 끝남 · 알림 전달됨" anywhere.
- The strip can no longer be hidden: no eye button, no Settings switch, no … menu item, no composer toggle, no saved `showsBackgroundWork`.
- This applies on Mac, Windows (WinUI and the web window) and the phone.

**Architecture:**
- **Counts:** one pure rule per platform counts the tasks the strip already holds (`RunSession.backgroundWork`, the current run's tasks). 작업 중 = `running`. 끝남 = `tasks.count − running.count`.
- **Visibility:** `showsBackgroundStrip` loses its `enabled` parameter. Otherwise the rule is unchanged: default view after the turn, Mighty view all along, at least one running or reported task, no style task list. All finished → the strip stays at "작업 중 0개" until the next request.
- **Phone wire:** a new optional `backgroundDone` (non-negative integer) on the session detail. It is sent, 0 included, whenever the Mighty view's strip shows.

**Tech Stack:** Swift (MightyCore, MightyClaude), C# (Core, WinUI host), React/TS (`native/windows/web`), Expo RN (`mobile/`).

**Spec:** `docs/superpowers/specs/2026-10-29-background-strip-counts-design.md`

## Global Constraints
- **Where and how:**
  - Work on main in `~/Work/MightyClaude`. Do not push. The repo is public: no personal paths, emails or IPs.
  - The Mac governs the Windows and phone ports. When Windows or the phone disagrees with the Mac, port the Mac rule.
- **Checks:** run builds and test suites only when the user asks, and they have not.
  - Implementers write the tests below but run only quick text checks:
    - `node scripts/check-locales.js`
    - `node scripts/check-design-tokens.js`
    - `bash scripts/check-style-freeze.sh`
    - `node scripts/help/build.mjs --check`
  - If cheap, also run web `cd native/windows/web && npx tsc --noEmit -p .` and phone `cd mobile && npm run -s typecheck`.
- **Frozen engine:** do not touch `native/macos/Sources/MightyCore/Styles/**` (frozen engine v7) or Windows `MightyClaude.Core/Styles/**`. The `.taskList` widget is out of scope. `plan.background.status` stays because the engine calls it.
- **Commits:**
  - Subjects are `(area) what the user now gets`.
  - Each message ends with exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - `git config user.email` is `11283764+athens96@users.noreply.github.com`.
  - Commit by path (`git commit -- <paths>`). Never commit `.ouroboros_eval_artifact.md`.
- **Locale keys:** add them to `locales/{ko,en,zh,ja}.json`, then copy the files byte for byte to `native/macos/Sources/MightyCore/Resources/Locales/` and `mobile/src/locales/`. Run `node scripts/check-locales.js`, which rewrites `docs/i18n.md`.
  - No hard-coded Korean. `scripts/korean-literal-budget.json` only goes down.

  | Key | ko | en | zh | ja |
  | --- | --- | --- | --- | --- |
  | `plan.background.title` | 백그라운드 작업 | Background tasks | 后台任务 | バックグラウンドタスク |
  | `plan.background.doneCount` | 끝남 {count}개 | Done: {count} | 已结束 {count} 个 | 終了 {count}件 |
  | `plan.background.runningCount` | 작업 중 {count}개 | Running: {count} | 进行中 {count} 个 | 実行中 {count}件 |
  | `graph.background.runWaiting` (value only, Task 1) | 백그라운드 실행 · 실행 중 | Background run · running | 后台运行 · 运行中 | バックグラウンド実行 · 実行中 |

  - `graph.background.agentWaiting` ("하위 에이전트 · 응답 대기") is unchanged.

  - **Removed in Task 3, Step 5 only:**
    - `plan.background.awaitingNotice`, `noticeDelivered`, `allNotified`, `listTitle`, `hideStrip`, `hideStripHelp`
    - `composer.backgroundWork.name`, `composer.backgroundWork.show`, `composer.backgroundWork.hide`
    - `pane.menu.backgroundWork`, `settings.display.backgroundWorkToggle`

    Windows, the web window and the help use them until then. `check-locales` fails on unused keys and does not scan the web window. Removing a key earlier breaks a later task's check or leaves a raw key on the web page.
  - **Kept:** `plan.background.status`, `plan.background.show`, `plan.background.hide`.
- **Rules:**
  - 작업 중 = tasks with status `running`. 끝남 = all other tasks the strip holds (`completed`, `failed`, `stopped`, `unknown`).
  - The header text is `doneCount · runningCount`, joined with `" · "`.
  - Row detail: with kind words, `kindText · elapsed`; without, `kindTitle · statusTitle · elapsed`. No notice word ever.
  - `backgroundDone` = the finished count when `showsBackgroundStrip(work, mighty: true, styleDrawsTasks: false)`, else absent.
- **Saved data:** `showsBackgroundWork` is no longer read or written. Old files that carry it still load; the next save drops it. `backgroundWorkOpen` is unchanged.

---

### Task 1: Mac (core, app, phone wire) + mighty-mode doc

**Files:**
- **Locales:** the four `locales/*.json` files and their copies. Add the three keys only.
- **Mac core (MightyCore):**
  - `PlanCardSupport.swift`, lines ~84-89 and ~186-223
  - `Models.swift`: `AppSnapshot.showsBackgroundWork` ~451, init ~460-464
  - `StateRepository.swift`: decode ~219, normalize ~344
  - `Remote/MobileRemoteModels.swift`: `MobileSessionDetail` ~329-339
- **Mac app (MightyClaude):**
  - `BackgroundWorkViews.swift`
  - `SessionPaneView.swift`: ~174-188, ~218-225, ~362-364, ~883-886, ~917
  - `SettingsViews.swift` ~303-304
  - `AppStore.swift`: ~1484-1492, smoke ~1914-1958
  - `AppStore+MobileRemote.swift`: ~481-505
- **Docs:** `docs/mighty-mode.md` line 9 (the strip sentence).
- **Tests:**
  - `native/macos/Tests/MightyCoreTests/PlanCardTests.swift` ~117-144
  - `StylesClaudePlanTests.swift` ~283-286
  - `SnapshotPreferencesTests.swift` ~113-136
  - `MobileRemoteExtensionTests.swift` (after `runningBackgroundWorkTravelsWithItsCommandAndTailButNoPath`; also the old `runWaiting` wording ~389, ~413 and ~416)
  - `mobile/src/__tests__/background-output.test.ts` ~40 (the `runWaiting` wording only)

**Interfaces (produces):**
```swift
extension PlanCardSupport {
    public static func showsBackgroundStrip(_ work: BackgroundWork?, mighty: Bool, styleDrawsTasks: Bool) -> Bool   // `enabled:` removed
    public static func backgroundFinished(_ work: BackgroundWork) -> Int
    public static func backgroundCounts(_ work: BackgroundWork) -> String
    public static func mobileBackgroundDone(_ work: BackgroundWork?) -> Int?
}
// Removed: backgroundSummary, backgroundNotice, backgroundStripSummary, AppSnapshot.showsBackgroundWork,
//          AppStore.showsBackgroundWork / setShowsBackgroundWork.
// MobileSessionDetail.backgroundDone: Int?   (trailing init parameter `backgroundDone: Int? = nil`)
```

- [ ] **Step 1: Locales.**
  - Add the three keys (see Global Constraints).
  - Change the value of `graph.background.runWaiting` in all four files.
  - Copy the files and run `node scripts/check-locales.js`. It fails on unused keys until Step 3 uses them; that is expected mid-task.
  - The new wording reaches every place that shows the key: the strip rows, diagram blocks, the web window and the phone.
  - Fix the tests that pin the old Korean wording, on every platform, in this task:
    - `MobileRemoteExtensionTests.swift` ~389, ~413 and ~416: "백그라운드 실행 · 알림으로 확인" → "백그라운드 실행 · 실행 중".
    - `mobile/src/__tests__/background-output.test.ts` ~40: the same change.
  - Windows tests read `Locale.Get`, and the web test uses its own mock dictionary (`'Run · by notice'`), so neither needs a change.

- [ ] **Step 2: Tests first.**
  - **`PlanCardTests`:** replace `aHiddenBackgroundLineDrawsNothingInEitherView` and `theBackgroundLineSaysTheAgentIsToldAndStaysOnceItWas` with:
  ```swift
  @Test func theBackgroundLineAlwaysShowsInBothViewsWhileItHasWork() {
      let task = BackgroundTask(id: "a", kind: "agent", description: "look", startedAt: "2027-01-15T08:00:00.000Z")
      for mighty in [false, true] {
          #expect(PlanCardSupport.showsBackgroundStrip(BackgroundWork(tasks: [task], turnEnded: true), mighty: mighty, styleDrawsTasks: false))
      }
      #expect(PlanCardSupport.showsBackgroundStrip(BackgroundWork(tasks: [task], turnEnded: false), mighty: true, styleDrawsTasks: false))
  }

  @Test func theBackgroundLineCountsWhatEndedAndWhatRunsAndStaysOnceAllEnded() {
      let running = BackgroundTask(id: "a", kind: "shell", description: "build", startedAt: "2027-01-15T08:00:00.000Z")
      var told = running; told.id = "b"; told.status = "completed"; told.endedAt = "2027-01-15T08:01:00.000Z"
      var lost = told; lost.id = "c"; lost.status = "unknown"
      let mixed = BackgroundWork(tasks: [running, told, lost], turnEnded: true)
      let ended = BackgroundWork(tasks: [told], turnEnded: true)
      #expect(PlanCardSupport.backgroundFinished(mixed) == 2)
      #expect(PlanCardSupport.showsBackgroundStrip(ended, mighty: false, styleDrawsTasks: false))
      #expect(PlanCardSupport.showsBackgroundStrip(BackgroundWork(tasks: [told], turnEnded: false), mighty: true, styleDrawsTasks: false))
      #expect(!PlanCardSupport.showsBackgroundStrip(BackgroundWork(tasks: [told], turnEnded: false), mighty: false, styleDrawsTasks: false))
      for mighty in [false, true] { #expect(!PlanCardSupport.showsBackgroundStrip(BackgroundWork(tasks: [lost], turnEnded: true), mighty: mighty, styleDrawsTasks: false)) }
      LocaleOverride.$language.withValue(.ko) {
          #expect(PlanCardSupport.backgroundCounts(BackgroundWork(tasks: [running], turnEnded: true)) == "끝남 0개 · 작업 중 1개")
          #expect(PlanCardSupport.backgroundCounts(mixed) == "끝남 2개 · 작업 중 1개")
          #expect(PlanCardSupport.backgroundCounts(ended) == "끝남 1개 · 작업 중 0개")
          // No row says what the agent hears.
          let rows = PlanCardSupport.backgroundStripRows(mixed, run: nil)
          #expect(!rows.isEmpty)
          for word in ["끝나면 알림", "알림 전달됨"] { #expect(!rows.contains { $0.kindText?.contains(word) == true }) }
      }
      #expect(PlanCardSupport.mobileBackgroundDone(mixed) == 2)
      #expect(PlanCardSupport.mobileBackgroundDone(BackgroundWork(tasks: [running], turnEnded: false)) == 0)
      #expect(PlanCardSupport.mobileBackgroundDone(BackgroundWork(tasks: [lost], turnEnded: true)) == nil)
      #expect(PlanCardSupport.mobileBackgroundDone(nil) == nil)
  }
  ```
  - **`StylesClaudePlanTests.theBackgroundStripShowsOutsideAStyleThatDrawsItsOwn`:** delete the `LocaleOverride` block with the two `backgroundSummary` checks.
  - **`SnapshotPreferencesTests`:** replace `backgroundWorkLineSwitchesPersistAndOlderStateShowsItFolded` with:
  ```swift
  @Test func backgroundWorkOpenPersistsAndTheOldHideSwitchIsDropped() async throws {
      // Older state may still carry the removed hide switch: it loads, and no save writes it again.
      let legacy = Data(#"{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300,"showsBackgroundWork":false,"backgroundWorkOpen":true}"#.utf8)
      #expect(StateRepository.decodeSnapshot(legacy).backgroundWorkOpen == true)
      #expect(AppSnapshot().backgroundWorkOpen == nil)
      #expect(StateRepository.normalize(AppSnapshot(backgroundWorkOpen: true), restoring: true).backgroundWorkOpen == true)
      let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-background-line-\(UUID().uuidString)")
      defer { try? FileManager.default.removeItem(at: directory) }
      for open in [true, false] {
          try await StateRepository(directory: directory, legacyStateURL: nil).save(AppSnapshot(backgroundWorkOpen: open))
          #expect(try await StateRepository(directory: directory, legacyStateURL: nil).load().backgroundWorkOpen == open)
          let raw = String(decoding: try Data(contentsOf: directory.appendingPathComponent("workspace-state.json")), as: UTF8.self)
          #expect(!raw.contains("showsBackgroundWork"))
      }
      // Only a JSON boolean counts; anything else reads as folded.
      var object = try #require(JSONSerialization.jsonObject(with: legacy) as? [String: Any])
      for invalid: Any in [0, "false", NSNull()] {
          object["backgroundWorkOpen"] = invalid
          #expect(StateRepository.decodeSnapshot(try JSONSerialization.data(withJSONObject: object)).backgroundWorkOpen == nil)
      }
  }
  ```
  - **`MobileRemoteExtensionTests`:** add:
  ```swift
  @Test func theFinishedCountTravelsWheneverTheStripShows() throws {
      let summary = MobileSessionSummary(id: "s1", workspaceId: "w1", title: "Claude", kind: "claude", provider: "claude", model: "default",
                                         status: "running", revision: 1, updatedAt: "2026-09-17T00:00:00Z")
      #expect(try encoded(MobileSessionDetail(revision: 1, session: summary, entries: [], backgroundDone: 0))["backgroundDone"] as? Int == 0)
      #expect(try encoded(MobileSessionDetail(revision: 1, session: summary, entries: [], backgroundDone: 3))["backgroundDone"] as? Int == 3)
      #expect(try encoded(MobileSessionDetail(revision: 1, session: summary, entries: []))["backgroundDone"] == nil)
      let data = try JSONEncoder().encode(MobileSessionDetail(revision: 1, session: summary, entries: [], backgroundDone: 2))
      #expect(try JSONDecoder().decode(MobileSessionDetail.self, from: data).backgroundDone == 2)
  }
  ```

- [ ] **Step 3: Core.**
  - **`PlanCardSupport.swift`:**
    - Delete `backgroundSummary`, `backgroundNotice` and `backgroundStripSummary`.
    - Keep `noticeDelivered`. Its doc becomes "A task that ended with the CLI's own end report (completed, failed, stopped); an `unknown` one ended with the process."
    - Keep `backgroundStatus`; it is the pane header's word.
  ```swift
  /// The expandable background list outside a style: in any view once the turn is over, and in the Mighty
  /// view all along, while background work runs or after it ended with its report. A style that draws its own
  /// task list keeps it. It stays until the next request starts a run and clears the work; nothing hides it.
  public static func showsBackgroundStrip(_ work: BackgroundWork?, mighty: Bool, styleDrawsTasks: Bool) -> Bool {
      guard !styleDrawsTasks, let work, work.turnEnded || mighty else { return false }
      return work.tasks.contains { $0.status == "running" || noticeDelivered($0.status) }
  }

  /// The strip's tasks that no longer run (ended with a report, or with the process): the current run's, so
  /// what ended since the request that started it.
  public static func backgroundFinished(_ work: BackgroundWork) -> Int { work.tasks.count - work.running.count }

  /// The strip's header counts, "끝남 N개 · 작업 중 N개", over every task it holds (not only the rows it lists).
  public static func backgroundCounts(_ work: BackgroundWork) -> String {
      L("plan.background.doneCount", ["count": "\(backgroundFinished(work))"]) + " · "
          + L("plan.background.runningCount", ["count": "\(work.running.count)"])
  }

  /// The phone's `backgroundDone`: the finished count whenever the Mighty view's strip shows, 0 included (its
  /// presence tells the phone the host counts); nil otherwise.
  public static func mobileBackgroundDone(_ work: BackgroundWork?) -> Int? {
      guard let work, showsBackgroundStrip(work, mighty: true, styleDrawsTasks: false) else { return nil }
      return backgroundFinished(work)
  }
  ```
  - **`Models.swift`:** delete `showsBackgroundWork`, its doc, its init parameter and its assignment. Edit the doc of `backgroundWorkOpen` to "The background strip was left open on its task rows. nil (older state) means folded."
  - **`StateRepository.swift`:** delete `showsBackgroundWork: jsonBool("showsBackgroundWork")` from the `AppSnapshot(…)` call ~219 and `output.showsBackgroundWork = …` ~344.
  - **`MobileRemoteModels.swift`:**
    - Add `public var backgroundDone: Int?` after `backgroundWork`, with the doc "How many of the strip's tasks ended, sent with the strip (0 included); absent when it does not show."
    - Add the trailing init parameter `backgroundDone: Int? = nil` and `self.backgroundDone = backgroundDone`.

- [ ] **Step 4: App.**
  - **`BackgroundWorkViews.swift`:**
    - In `BackgroundTaskRows.row`, the detail becomes:
    ```swift
    // The kind words already say waiting or how it ended.
    let parts = item.kindText.map { [$0, task.elapsed(now: now)] } ?? [task.kindTitle, task.statusTitle, task.elapsed(now: now)]
    ```
    - Fix the type doc: drop "whether the agent hears of it…".
    - Rewrite `BackgroundWorkStrip`'s doc: "…Whether it is open is app-wide and saved (`AppStore.backgroundWorkOpen`); it cannot be hidden."
    - Its body becomes the fold button alone, with no eye button:
    ```swift
    let rows = PlanCardSupport.backgroundStripRows(work, run: run, output: output.lines)
    let open = store.backgroundWorkOpen
    let counts = PlanCardSupport.backgroundCounts(work)
    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
        Button { store.setBackgroundWorkOpen(!open) } label: {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "square.stack.3d.up").foregroundStyle(Palette.accent)
                Text(L("plan.background.title")).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink)
                Text(verbatim: counts).font(.ui(size: 11).monospacedDigit()).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                Image(systemName: open ? "chevron.up" : "chevron.down").font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.ink2)
            }.contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(L("plan.background.title") + " · " + counts)
        .accessibilityValue(open ? L("plan.background.hide") : L("plan.background.show"))
        .accessibilityIdentifier("background-work-\(sessionId)")
        if open { BackgroundTaskRows(strip: rows) }
    }
    // padding, background and overlay unchanged
    ```
  - **`SessionPaneView.swift`:**
    - Delete `showsBackgroundWorkToggle` and `backgroundWorkToggle` (~174-188) and the `if showsBackgroundWorkToggle { backgroundWorkToggle }` line ~917.
    - In `actionsWidth` ~886, delete `+ (showsBackgroundWorkToggle ? 16 + gap : 0)`. Edit the comment above it to "…the context ring; the status-line toggle of Claude panes."
    - Delete the `pane.menu.backgroundWork` `Toggle` ~362-364.
    - Call `PlanCardSupport.showsBackgroundStrip(work, mighty: showsMightyGraph, styleDrawsTasks: …)` without `enabled:`. Its comment ends "…unless the pane's style draws its own task list."
  - **`SettingsViews.swift`:** delete the `settings.display.backgroundWorkToggle` `Toggle` and its identifier.
  - **`AppStore.swift`:**
    - Delete `showsBackgroundWork` and `setShowsBackgroundWork` (~1484-1492). Edit `backgroundWorkOpen`'s doc to "The background strip is open on its task rows; saved, so it opens the same way next time."
    - Rewrite `runBackgroundWorkLineSmoke`. Keep its name and the `backgroundWorkLine` result key; the caller ~1840-1850 is unchanged.
    ```swift
    /// The background strip through the calls its fold makes (no clicks: CI has no accessibility client, so the
    /// state decides): it is drawn in both views with no switch to hide it, its open state is saved, and the
    /// saved file never carries the removed hide switch. The open value is put back after.
    private func runBackgroundWorkLineSmoke() async -> [String: Any] {
        let originalOpen = snapshot.backgroundWorkOpen
        var result: [String: Any] = ["passed": false]
        func savedState() async throws -> AppSnapshot {
            try await flush()
            return try await StateRepository(directory: dataDirectory, legacyStateURL: nil).load()
        }
        let work = BackgroundWork(tasks: [BackgroundTask(id: "smoke-bg-1", kind: "agent", description: "smoke background agent", startedAt: "2026-01-01T00:00:00Z")], turnEnded: true)
        do {
            let drawn = PlanCardSupport.showsBackgroundStrip(work, mighty: false, styleDrawsTasks: false)
                && PlanCardSupport.showsBackgroundStrip(work, mighty: true, styleDrawsTasks: false)
            result["drawnInBothViews"] = drawn
            setBackgroundWorkOpen(true)
            let openRemembered = backgroundWorkOpen && (try await savedState()).backgroundWorkOpen == true
            setBackgroundWorkOpen(false)
            let foldRemembered = !backgroundWorkOpen && (try await savedState()).backgroundWorkOpen == false
            result["openRemembered"] = openRemembered && foldRemembered
            let raw = String(decoding: try Data(contentsOf: dataDirectory.appendingPathComponent("workspace-state.json")), as: UTF8.self)
            let noHideSwitch = !raw.contains("showsBackgroundWork")
            result["noHideSwitch"] = noHideSwitch
            result["passed"] = drawn && openRemembered && foldRemembered && noHideSwitch
        } catch { result["error"] = error.localizedDescription }
        snapshot.backgroundWorkOpen = originalOpen
        do { try await flush() } catch { result["restoreError"] = error.localizedDescription }
        result["restored"] = snapshot.backgroundWorkOpen == originalOpen
        return result
    }
    ```
  - **`AppStore+MobileRemote.swift`:**
    - Pass `backgroundDone: mobileBackgroundDone(session)` to the `MobileSessionDetail` ~481.
    - Add the helper and extend the digest so the phone wakes when the finished count changes:
    ```swift
    /// How many of the pane's strip tasks ended (`MobileSessionDetail.backgroundDone`), while the strip shows.
    func mobileBackgroundDone(_ session: RunSession) -> Int? {
        session.kind == SessionKind.claude ? PlanCardSupport.mobileBackgroundDone(session.backgroundWork) : nil
    }

    func mobileBackgroundDigest(_ session: RunSession) -> Int {
        guard session.kind == SessionKind.claude, let tasks = session.backgroundWork?.tasks else { return 0 }
        var hasher = Hasher()
        var running = false
        for task in tasks where task.status == "running" {
            running = true
            hasher.combine(task.id); hasher.combine(task.description); hasher.combine(task.toolUseId)
        }
        let done = mobileBackgroundDone(session)
        guard running || done != nil else { return 0 }
        hasher.combine(done)
        if running { hasher.combine(backgroundOutput.phoneRevision(forPane: session.id)) }
        return hasher.finalize()
    }
    ```
    - Edit the `background` digest field's doc ~39 to "The background work the phone lists and its finished count (`backgroundWork`, `backgroundDone`)."
- [ ] **Step 5: Doc.** In `docs/mighty-mode.md` line 9, the strip clause becomes: 입력창 위 **백그라운드 작업** 줄이 "끝남 N개 · 작업 중 N개"를 보인다(맥 `BackgroundWorkStrip`, 윈도우 WinUI와 웹 카드, 폰은 마이티 보기 위의 접힌 같은 줄). 줄은 숨길 수 없고, 모두 끝나도 다음 요청까지 "작업 중 0개"로 남는다.
- [ ] **Step 6: Check and commit.**
  - Run `git grep -n "showsBackgroundWork\|backgroundStripSummary\|backgroundNotice(\|backgroundSummary(\|setShowsBackgroundWork" -- native/macos`. It must be empty.
  - Run check-locales, check-design-tokens and check-style-freeze.
  - Commit. Subject: `(mac) the background strip says how many ended and how many still run, with no 끝나면 알림 words and no way to hide it, and a running background command reads 실행 중`

### Task 2: Phone + help in four languages + mobile-remote doc

**Files:**
- `mobile/src/api/types.ts`: `MobileSessionDetail.backgroundDone` ~718. Fix the doc of `MobileBackgroundWork` ~418.
- `mobile/src/lib/background-work.ts`: `parseBackgroundDone`, `backgroundCounts`, and `hostFiltersBackground` ~68
- `mobile/src/components/background-strip.tsx`
- `mobile/app/host/[hostId]/session/[sessionId].tsx`: ~575 and ~1399
- Tests: `mobile/src/__tests__/background-strip.test.ts`, `background-work.test.ts`
- Help: `docs/help/content/{ko,en,zh,ja}/approvals.md`, the `#background` section (ko lines ~70-77)
- Doc: `docs/mobile-remote.md` ~280-297

**Interfaces (consumes):** Task 1's `backgroundDone?` on the session detail.

- [ ] **Step 1: Tests first.**
  - **`background-work.test.ts`:**
  ```ts
  it('reads a non-negative whole finished count and nothing else', () => {
    expect(parseBackgroundDone({ backgroundDone: 0 })).toBe(0);
    expect(parseBackgroundDone({ backgroundDone: 3 })).toBe(3);
    for (const bad of [-1, 1.5, '2', null, undefined]) expect(parseBackgroundDone({ backgroundDone: bad })).toBeUndefined();
    expect(parseBackgroundDone(undefined)).toBeUndefined();
  });
  it('counts what ended and what runs, or only what runs for an older host', () => {
    expect(backgroundCounts(1, 2)).toBe('끝남 1개 · 작업 중 2개');
    expect(backgroundCounts(3, 0)).toBe('끝남 3개 · 작업 중 0개');
    expect(backgroundCounts(undefined, 2)).toBe('작업 중 2개');
  });
  ```
  - In the existing `knows a new host by …` case, add `expect(hostFiltersBackground({ backgroundDone: 0 })).toBe(true);`.
  - Make sure the file runs in Korean (`resetLanguage('ko')`) as `background-strip.test.ts` does.
  - **`background-strip.test.ts`:**
    - `mount` takes `(items, done?)` and passes `{ work: items, done }`.
    - Replace the first two cases. In "opens a row to its command and last lines", change the press label to `/작업 중 2개/`.
  ```ts
  it('draws nothing when nothing runs and nothing ended', () => {
    expect(mount([]).toJSON()).toBeNull();
    expect(mount([], 0).toJSON()).toBeNull();
  });

  it('is one collapsed row with both counts, and opens to the running rows', () => {
    const view = mount(work, 1);
    expect(texts(view)).toEqual(['▸', '백그라운드 작업', '끝남 1개 · 작업 중 2개']);
    act(() => pressable(view, /작업 중 2개/).props.onPress());
    const open = texts(view);
    expect(open[0]).toBe('▾');
    expect(open).toContain('npm test');
    // A row with no title is named for its kind; the folded row shows its last output line.
    expect(open).toContain('하위 에이전트');
    expect(open).toContain('three');
    expect(open).not.toContain('npm test --watch');
    act(() => pressable(view, /작업 중 2개/).props.onPress());
    expect(texts(view)).toEqual(['▸', '백그라운드 작업', '끝남 1개 · 작업 중 2개']);
  });

  it('shows the running count alone for a host that sends no finished count', () => {
    expect(texts(mount(work))).toEqual(['▸', '백그라운드 작업', '작업 중 2개']);
  });

  it('stays once all ended, with nothing to open', () => {
    const view = mount([], 3);
    expect(texts(view)).toEqual(['백그라운드 작업', '끝남 3개 · 작업 중 0개']);
    expect(view.root.findAll((node) => typeof node.props.onPress === 'function')).toHaveLength(0);
  });
  ```
- [ ] **Step 2: Code.**
  - **`types.ts`:** add to `MobileSessionDetail`:
  ```ts
  /** How many of the pane's background tasks ended, as the Mac's strip counts them; sent with the strip (0 included),
   *  absent when it does not show and from older hosts. Read through `parseBackgroundDone`. */
  backgroundDone?: number;
  ```
  - **`background-work.ts`:**
  ```ts
  /** The detail's finished count (`backgroundDone`): a whole number from 0, else undefined (an older host sends none). */
  export function parseBackgroundDone(detail: { backgroundDone?: unknown } | undefined): number | undefined {
    const raw = detail?.backgroundDone;
    return typeof raw === 'number' && Number.isInteger(raw) && raw >= 0 ? Math.min(raw, 999) : undefined;
  }

  /** The strip's header counts (the Mac's `PlanCardSupport.backgroundCounts`); without a finished count, the running one alone. */
  export function backgroundCounts(done: number | undefined, running: number): string {
    const runs = t('plan.background.runningCount', { count: running });
    return done === undefined ? runs : `${t('plan.background.doneCount', { count: done })} · ${runs}`;
  }
  ```
    - `hostFiltersBackground` also returns true when `detail.backgroundDone` is neither undefined nor null. Update its doc.
  - **`background-strip.tsx`:**
    - Signature: `BackgroundStrip({ work, done }: { work: readonly MobileBackgroundWork[]; done?: number })`.
    - The doc says it shows the pane's background work folded into one row: "백그라운드 작업 · 끝남 N개 · 작업 중 N개". Opened, it lists the running rows. Once all ended the row stays and does not open.
    - Keep the hooks above the early return:
  ```tsx
  if (work.length === 0 && (done ?? 0) === 0) return null;
  const counts = backgroundCounts(done, work.length);
  const head = (
    <>
      <Text style={styles.headTitle}>{t('plan.background.title')}</Text>
      <Text numberOfLines={1} style={styles.headCounts}>{counts}</Text>
    </>
  );
  return (
    <View style={styles.strip}>
      {work.length > 0 ? (
        <Pressable accessibilityRole="button" accessibilityLabel={`${t('plan.background.title')} · ${counts}`}
          accessibilityState={{ expanded: open }} onPress={() => setOpen((value) => !value)}
          style={({ pressed }) => [styles.head, pressed && styles.pressed]}>
          <Text style={styles.chevron}>{open ? '▾' : '▸'}</Text>
          {head}
        </Pressable>
      ) : (
        <View style={styles.head}>{head}</View>
      )}
      {open && work.length > 0 ? work.map((item) => <WorkRow key={item.id} item={item} now={now} />) : null}
    </View>
  );
  ```
    - Styles:
      - `headTitle: { color: palette.textMuted, fontSize: 13, fontWeight: '600' }` (no `flex`)
      - `headCounts: { color: palette.textFaint, flex: 1, fontSize: 13 }`
      - Use theme tokens only; no new raw spacing.
  - **Session screen:** `const backgroundDone = useMemo(() => parseBackgroundDone(detail), [detail]);` next to `backgroundWork`, then `<BackgroundStrip work={backgroundWork} done={backgroundDone} />`.
- [ ] **Step 3: Help.** Rewrite the `#background` bullets in all four languages. Use `{{ui:…}}` only for keys without placeholders.
  - **ko line 70:** "입력창 위에 {{ui:plan.background.title}} 줄이 보입니다(기본 보기는 턴이 끝난 뒤, 마이티 보기는 처음부터). 이번 요청이 띄운 작업 중 끝난 수와 아직 도는 수가 '끝남 1개 · 작업 중 2개'처럼 나옵니다. {{ui:plan.background.show}}를 누르면 작업별 종류, 상태, 걸린 시간을 봅니다."
  - **ko line 71:** "작업이 끝나면 Claude에게 알림이 가고, Claude가 그 결과를 받아 이어서 답합니다. 끝난 작업은 끝난 상태로 바뀌어 다음 요청을 보낼 때까지 줄에 남습니다. 모두 끝나면 줄은 '작업 중 0개'로 남습니다."
  - **ko line 72, phone sentence:** "휴대폰도 마이티 보기 위에 같은 두 수를 보이는 접힌 줄을 두고, 펼치면 도는 작업의 행이 보입니다."
  - **ko lines 76-77:** replace both bullets with one: "이 줄은 숨길 수 없습니다. 백그라운드 작업이 있는 동안 늘 보이고, 줄을 펼쳐 둔 상태는 기억합니다."
  - en/zh/ja say the same; the en counts example reads "Done: 1 · Running: 2".
  - Keep `![[background-work]]`. Leave `docs/help/shots.json` alone: the shot does not depend on the hide button.
  - Run `git grep -n "hideStrip\|awaitingNotice\|noticeDelivered\|allNotified\|listTitle\|composer.backgroundWork\|settings.display.backgroundWorkToggle\|pane.menu.backgroundWork" -- docs/help`. It must be empty.
- [ ] **Step 4: Doc.** In `docs/mobile-remote.md` §"백그라운드 작업과 알림 실행":
  - The `backgroundWork` bullet: the phone strip reads "백그라운드 작업 · 끝남 N개 · 작업 중 N개" (`plan.background.title`, `doneCount`, `runningCount`), not `plan.background.listTitle`.
  - A new bullet for **`backgroundDone`**:
    - It is a whole number from 0: the Mac strip's finished count.
    - It is sent whenever the Mighty view's strip shows, 0 included, and is absent otherwise.
    - It moves the long poll's digest.
    - It is evidence of a new host.
    - Once all work ends, the phone keeps the header alone and it does not open.
  - The old/new host and phone table from the spec §5.
- [ ] **Step 5: Check and commit.**
  - Run `npm run -s typecheck` if cheap, then check-locales, check-design-tokens and `node scripts/help/build.mjs --check`.
  - Commit. Subject: `(phone) the phone's background row says how many ended and how many still run, and the help no longer offers to hide the strip`

### Task 3: Windows (Core, WinUI, web) + key removal + final sweep

**Files:**
- **Core:**
  - `MightyClaude.Core/PlanCardSupport.cs`: ~93-118 and ~186-216
  - `Models.cs` ~332-338
  - `Remote/MobileDesktopRouter.cs`: ~12-18 and ~407-408
- **WinUI:**
  - `MainWindow.BackgroundWork.cs`
  - `MainWindow.Composer.cs` ~359
  - `MainWindow.PaneHeader.cs` ~421-440
  - `MainWindow.Settings.cs` ~110-116
  - `MainWindow.WebShell.Cards.cs` ~27, ~429-448
  - `MainWindow.WebShell.Composer.cs` ~119-125, ~581-585
  - `MainWindow.MobileRemote.cs` ~633-634, ~657-660
- **Web:**
  - `native/windows/web/src/cards/types.ts` ~45-51
  - `cards/Cards.tsx` ~243-262
  - `composer/Composer.tsx` ~377-380
  - `composer/types.ts` ~54-55
  - `store/models.ts` ~305
  - `VISUAL-PARITY.md` ~55
- **Tests:**
  - `MightyClaude.Core.Tests/WorkDashboardVerification.cs` ~94-157
  - `Verification.cs` ~307-309
  - `ClaudePlanStyleVerification.cs` ~180-197
  - `BackgroundNoticeRunsVerification.cs` ~220
- **Locales:** the four `locales/*.json` files and their copies (removal).

**Interfaces (ports Task 1):**
```csharp
public static bool ShowsBackgroundStrip(BackgroundWork? work, bool mighty, bool styleDrawsTasks);   // `enabled` removed
public static int BackgroundFinished(BackgroundWork work);
public static string BackgroundCounts(BackgroundWork work);
public static int? MobileBackgroundDone(BackgroundWork? work);
// Removed: BackgroundSummary, BackgroundNotice, BackgroundStripSummary, AppSnapshot.ShowsBackgroundWork.
// MobilePaneExtras gains a trailing `int? BackgroundDone = null`; the detail sends `backgroundDone`.
// internal sealed record WebBackgroundCard(string Counts, bool Open, IReadOnlyList<WebBackgroundTask> Tasks)
```

- [ ] **Step 1: Tests first.**
  - **`ClaudePlanStyleVerification`:**
    - Delete the `BackgroundSummary` check ~180 and the `Korean` block ~193-197.
    - Drop the `enabled` argument everywhere.
    - Add:
  ```csharp
  var mixed = work with { Tasks = [work.Tasks[0], work.Tasks[0] with { Id = "x", Status = "completed" }, work.Tasks[0] with { Id = "y", Status = "unknown" }] };
  Check(PlanCardSupport.BackgroundFinished(mixed) == mixed.Tasks.Count - mixed.Running.Count, "finished is every task that no longer runs");
  Check(PlanCardSupport.MobileBackgroundDone(mixed) == PlanCardSupport.BackgroundFinished(mixed) && PlanCardSupport.MobileBackgroundDone(lost) is null && PlanCardSupport.MobileBackgroundDone(null) is null,
      "the phone's finished count travels only while the strip shows");
  Korean(() =>
  {
      Check(PlanCardSupport.BackgroundCounts(told) == "끝남 1개 · 작업 중 0개", "all ended: the strip stays with nothing running");
      var rows = PlanCardSupport.BackgroundStripRows(mixed, null);
      // A running run now reads "백그라운드 실행 · 실행 중" (Task 1), so no row mentions a notice at all.
      Check(rows.All(r => !r.Detail(DateTimeOffset.UtcNow).Contains("알림", StringComparison.Ordinal)), "no row says what the agent hears");
      return 0;
  });
  ```
    - `told` and `lost` are the existing locals ~189-190. Put the new block after them.
  - **`WorkDashboardVerification`:** replace `BackgroundWorkLinePersistsAndHides` with `BackgroundWorkOpenPersistsAndTheOldHideSwitchIsDropped`:
    - Legacy JSON with `"showsBackgroundWork":false,"backgroundWorkOpen":true` loads with `BackgroundWorkOpen == true`.
    - `"backgroundWorkOpen":"true"` reads null.
    - `StateStore.Normalize` keeps `BackgroundWorkOpen`.
    - For `open` in `{ true, false }`: save, then reload restores it, and `File.ReadAllText(Path.Combine(directory, "workspace-state.json"))` does not contain `showsBackgroundWork`.
    - `ShowsBackgroundStrip(work, mighty, false)` holds for both views.
  - **The same file:** rename `BackgroundWorkHideSurvivesABriefReplaceBlock` to `BackgroundWorkOpenSurvivesABriefReplaceBlock`.
    - Keep its body. Switch every `ShowsBackgroundWork` to `BackgroundWorkOpen` (start `false`, alternate `i % 2 == 1`, end `true`).
    - The final check expects `stored.BackgroundWorkOpen == true` and `raw.Contains("\"backgroundWorkOpen\":true")`.
    - Its comment says the save-retry rule is what it guards.
  - **`Verification.cs` ~307-309:** register both new names. The SKIP line reads "background work open survives a brief replace block".
  - **`BackgroundNoticeRunsVerification` ~220:** add
  ```csharp
  Check(PlanCardSupport.MobileBackgroundDone(work) == 0 && PlanCardSupport.MobileBackgroundDone(new BackgroundWork([new("b3", "shell", "Done", Wire.Now(), "completed")], TurnEnded: true)) == 1,
      "the phone's backgroundDone: 0 while all runs, the finished count once some ended");
  ```
- [ ] **Step 2: Core.**
  - **`PlanCardSupport.cs`:**
    - Port Task 1's rules.
    - Delete `BackgroundSummary`, `BackgroundNotice` and `BackgroundStripSummary`. Keep `NoticeDelivered` and `BackgroundStatus`.
    - `BackgroundStripRow.Detail` becomes:
  ```csharp
  /// <summary>Kind · status · elapsed; kind words already say waiting or how it ended.</summary>
  public string Detail(DateTimeOffset now) => string.Join(" · ", (KindText is { } kind
      ? new[] { kind, Task.Elapsed(now) }
      : new[] { Task.KindTitle, Task.StatusTitle, Task.Elapsed(now) }).Where(s => s.Length > 0));

  public static bool ShowsBackgroundStrip(BackgroundWork? work, bool mighty, bool styleDrawsTasks) =>
      !styleDrawsTasks && work is not null && (work.TurnEnded || mighty) && work.Tasks.Any(t => t.Status == "running" || NoticeDelivered(t.Status));
  public static int BackgroundFinished(BackgroundWork work) => work.Tasks.Count - work.Running.Count;
  public static string BackgroundCounts(BackgroundWork work) =>
      Locale.Get("plan.background.doneCount", new Dictionary<string, string> { ["count"] = BackgroundFinished(work).ToString(CultureInfo.InvariantCulture) }) + " · "
      + Locale.Get("plan.background.runningCount", new Dictionary<string, string> { ["count"] = work.Running.Count.ToString(CultureInfo.InvariantCulture) });
  public static int? MobileBackgroundDone(BackgroundWork? work) => ShowsBackgroundStrip(work, true, false) ? BackgroundFinished(work!) : null;
  ```
  - **`Models.cs`:**
    - Delete `ShowsBackgroundWork` and its comment. System.Text.Json skips the old key on load.
    - Edit the `BackgroundWorkOpen` comment to "The background strip was left open on its task rows…".
  - **`MobileDesktopRouter.cs`:**
    - `MobilePaneExtras` gains a trailing `int? BackgroundDone = null`, documented like the Mac field.
    - The detail adds `backgroundDone = detail.BackgroundDone` next to `backgroundWork`.
- [ ] **Step 3: WinUI.**
  - **`MainWindow.BackgroundWork.cs`:**
    - Delete `ShowsBackgroundWork`, `SetShowsBackgroundWork`, the composer toggle (`backgroundWorkToggle`, `backgroundWorkGlyph`, `InitializeBackgroundWorkToggle`, `UpdateBackgroundWorkTip`), `HideBackgroundWork` and `ToggleBackgroundWorkShown`.
    - In `RefreshBackgroundWork`, drop the toggle lines and the `owner.ShowsBackgroundWork` argument.
    - The header's title `TextBlock` reads `Locale.Get("plan.background.title")`, and the summary reads `PlanCardSupport.BackgroundCounts(work)`. Drop the `count` local.
    - Delete the hide button. `strip.Children.Add(fold)` replaces the `line` grid.
    - Fix the docs that mention the eye or the four switches.
  - **`MainWindow.Composer.cs` ~359:** delete `InitializeBackgroundWorkToggle(toolbarActions);`.
  - **`MainWindow.WebShell.Cards.cs` ~21:** the doc comment's example words become "백그라운드 실행 · 실행 중".
  - **`MainWindow.PaneHeader.cs`:** delete the `backgroundWork` `ToggleMenuFlyoutItem` block and its line in `menu.Opening`.
  - **`MainWindow.Settings.cs`:** delete the background-work switch row and its comment.
  - **`MainWindow.WebShell.Cards.cs`:**
    - `WebBackgroundCard(string Counts, bool Open, IReadOnlyList<WebBackgroundTask> Tasks)`.
    - `WebBackground` returns `new(PlanCardSupport.BackgroundCounts(work!), open, tasks)` and calls `ShowsBackgroundStrip` without the switch.
    - `WebBackgroundWork` keeps only `"toggle"`.
  - **`MainWindow.WebShell.Composer.cs`:**
    - `composerToggle` keeps only the `statusLine` branch. Its comment names the status-line toggle alone.
    - `toggles = … ? new { statusLine = LocalPath(Workspace) is null ? (bool?)null : owner.service.Snapshot.StatusLineEnabled } : null`.
  - **`MainWindow.MobileRemote.cs`:**
    - The digest adds `mighty.Add(PlanCardSupport.MobileBackgroundDone(pane.BackgroundWork));` after the running-task loop.
    - `MobileSnapshot` passes `PlanCardSupport.MobileBackgroundDone(pane.BackgroundWork)` as the new last argument.
- [ ] **Step 4: Web.**
  - **`cards/types.ts`:** `export interface BackgroundCard { kind: 'background'; counts: string; open: boolean; tasks: BackgroundTask[] }`.
  - **`Cards.tsx` `Background`:** the line is the fold alone.
  ```tsx
  <div className="card-strip-line">
    <Fold open={card.open} label={t(card.open ? 'plan.background.hide' : 'plan.background.show')} onClick={() => void call('backgroundWork', { action: 'toggle' })}>
      <span className="card-strip-mark" aria-hidden="true"><Icon name="layers" size={14} weight={1.75} /></span>
      <strong>{t('plan.background.title')}</strong>
      <span className="card-strip-summary">{card.counts}</span>
    </Fold>
  </div>
  ```
  - **`Composer.tsx`:** delete the background toggle button ~377-380.
  - **`composer/types.ts`:** the toggles type becomes `toggles?: { statusLine: boolean | null } | null;` with the doc "A Claude pane's status-line toggle…".
  - **`store/models.ts`:** delete `showsBackgroundWork?: boolean;`.
  - **`VISUAL-PARITY.md` ~55:** "the strip with the layers glyph, its title, the two counts and the chevron."
  - Run `npx tsc --noEmit -p .` if cheap.
- [ ] **Step 5: Remove the keys and sweep.**
  - This must be empty:
    `git grep -n -E "plan\.background\.(awaitingNotice|noticeDelivered|allNotified|listTitle|hideStrip)|composer\.backgroundWork|pane\.menu\.backgroundWork|settings\.display\.backgroundWorkToggle|ShowsBackgroundWork|showsBackgroundWork" -- native mobile/src mobile/app docs/help docs/*.md ':!*.json' ':!docs/i18n.md'`
  - Remove the eleven keys (Global Constraints) from the four `locales/*.json` and their copies. Run `node scripts/check-locales.js`, which regenerates `docs/i18n.md`.
  - Run check-design-tokens, check-style-freeze and `node scripts/help/build.mjs --check`, and web `tsc` if cheap.
  - Run `git status --short`. Commit only this task's paths, never `.ouroboros_eval_artifact.md`.
  - Commit. Subject: `(windows) the background strip says how many ended and how many still run, with no 끝나면 알림 words and no way to hide it, in the web window and WinUI, and the phone hears the finished count`
