# Background Notice Runs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Background work shows only in the strip above the composer while it runs. Each turn that a `<task-notification>` opens becomes its own "백그라운드 작업 완료" diagram, holding the finished work blocks and Claude's whole reply, on Mac, Windows and the phone.

**Architecture:**
- The graph tracker opens a new run id for each notice turn and emits a main node plus copies of the finished background blocks under that id. `RunSession.recordGraph` already groups nodes by `runId` into `MightyGraphRun`s, so every view (흐름, 기록, 파이프라인, phone, history) sees one more run.
- The original request's run keeps its background blocks as data. The views filter them out through one shared rule.
- The notice section and the 작업 알림 steer in the original run go away.

**Tech Stack:** Swift (MightyCore, MightyClaude), C# (Core, WinUI host), React/TS (`native/windows/web`), Expo RN (`mobile/`).

**Spec:** `docs/superpowers/specs/2026-10-28-background-notice-runs-design.md`

## Global Constraints
- **Notice run:**
  - Opened when a root-level `<task-notification>` opens a turn. That is the same moment the tracker today calls `addNotice` with `notice == true`.
  - Run id: `<original run id>:notice:<n>`, where n starts at 1 for each original run.
  - All notifications that arrive before that turn's result share the run: "one diagram per Claude reply".
- **Notice run main node:**
  - `title`: `L("graph.background.noticeRun")`, which reads "백그라운드 작업 완료".
  - `input`: the finished work, as `"<block title> · <완료/실패/중지>"` lines joined with `\n`.
  - `output`: the turn's whole result text. The state is completed when the result arrives.
- **Children of the notice run:** one copy per finished background block.
  - Each copy has the same kind (agent/task), title, input (command or prompt), state, output (summary, or the agent's hand-back report), `background: true`, `outputFile`, and `sourceBlockId: <original node id>`.
  - A copy's id is `<notice run id>:<original node id>`.
- **Absorbed notice:** a notification seen inside another request's turn (not root-level, or arriving inside that request's own turn) still gets a notice run, with the copies and no output. Its main is completed at once. The other request's result is untouched.
- **Removed from the original run:**
  - The `graph.result.afterTaskNotice` section on the main output.
  - The `steer:notice:` 작업 알림 block.
  - Delegation notices (`graph.result.afterNotice`) and 다음 실행 steers are unchanged.
  - `noticeReply` is no longer set; the field stays readable for old saves.
- **Display filter:** one rule on every platform.
  - Views of a run (흐름, 기록, 파이프라인, phone blocks and structure) omit agents with `background == true` unless the run is a notice run.
  - A notice run is recognised by `MightyGraphRun.noticeRun == true`, a new optional Codable field the projection sets when the run id contains `:notice:`.
- **Strip:**
  - Rows show kind, name, elapsed and the last output line (as in 0.4.45).
  - An expanded row shows the command (or the agent's first prompt line) and the tail (≤6 lines).
  - It lists running work only, plus work that ended within the existing strip rules.
- **Phone wire:**
  - Session detail gains the optional `backgroundWork?: [{ id, kind: "agent"|"run", title, status, startedAt, command?, outputTail? }]`, running items only.
  - It never carries an output file path.
  - The phone shows a collapsed "백그라운드 작업 N개" row above the Mighty view.
  - Notice runs travel as ordinary runs, with `noticeRun?: true` on the run and wire models.
- **Not counted:** a notice run is not a user request. It adds nothing to user request numbering, the pet, or "own submit".
  - The existing completion notification for a notice turn still fires once.
- **Locale keys:** `graph.background.noticeRun` ("백그라운드 작업 완료") and `graph.background.stripTitle` ("백그라운드 작업 {count}개"), in `locales/{ko,en,zh,ja}.json` plus the copies.
  - Reuse an existing key if one already says this.
  - No hard-coded Korean.
- **Process:**
  - Work on main in `~/Work/MightyClaude` and do not push. The repo is public.
  - Never commit `.ouroboros_eval_artifact.md`. `git config user.email` is `11283764+athens96@users.noreply.github.com`.
  - Commit subjects are `(area) what the user now gets`, with the exact trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Do not touch `native/macos/Sources/MightyCore/Styles/**`. Never launch the GUI app.
- **Checks you may run:**
  - Mac: focused tests via `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-notice --filter '\.(<Suites>)/'`, and the app build via `cd native/macos && DEVELOPER_DIR=/Library/Developer/CommandLineTools swift build --product MightyClaude --scratch-path /tmp/mc-notice-app`. Run both in the foreground.
  - Windows: Core via `cd native/windows && dotnet run --project MightyClaude.Core.Tests -c Release` (23 known macOS-only failures), and `bash scripts/check-winui-compile.sh`.
  - Web: tsc and vitest. Phone: tsc and jest.
  - Repo checks: check-locales, check-design-tokens, check-style-freeze, and `help/build.mjs --check`.

---

### Task 1: Contract cases + Mac core (tracker, run flag, filter)

**Files:**
- Modify: `native/contracts/fixtures/agent-notices.json`
- Modify: `native/macos/Sources/MightyCore/ExecutionGraphTracker.swift`:
  - `settleTasks` ~654-690
  - `addNotice` ~718-732
  - the notice `result` branch ~318-345
  - `clearNoticeTurn`
- Modify: `native/macos/Sources/MightyCore/ExecutionGraph.swift` (`ExecutionGraphNode.sourceBlockId: String?`) and `native/macos/Sources/MightyCore/MightyGraph.swift` (`MightyGraphRun.noticeRun: Bool?`; the projection in `recordGraph` ~370-420 sets it, and copies `sourceBlockId` onto `MightyGraphAgent`)
- Create: `native/macos/Sources/MightyCore/BackgroundNoticeRuns.swift` (the display filter)
- Test: `native/macos/Tests/MightyCoreTests/AgentNoticeTests.swift` (extend the fixture keys) and `native/macos/Tests/MightyCoreTests/BackgroundNoticeRunsTests.swift`

**Interfaces (produces):**
```swift
public enum BackgroundNoticeRuns {
    public static let marker = ":notice:"
    public static func runID(original: String, index: Int) -> String   // "<original>:notice:<index>"
    public static func isNoticeRun(_ runID: String) -> Bool
    /// Agents a view draws for a run: background blocks only inside a notice run.
    public static func shownAgents(_ run: MightyGraphRun) -> [MightyGraphAgent]
}
```
- `MightyGraphRun.noticeRun: Bool?`
- `ExecutionGraphNode.sourceBlockId: String?` and `MightyGraphAgent.sourceBlockId: String?`. Both are trailing init parameters that default to nil.

- [ ] **Step 1: Fixture.**
  - In `agent-notices.json`, update the existing notice cases to the new expectations:
    - no `mainNoticeSections` (expect 0);
    - `noticeBlocks: 0` in the original run;
    - no `noticeReply`.
  - Add expectation keys:
    - `noticeRuns`: the number of notice runs;
    - `noticeRunChildren`: an array of child counts;
    - `noticeRunOutput`: the main output of the first notice run, or null;
    - `noticeRunInput`: the first notice run's input.
  - Add cases:
    - one command notification then its reply;
    - two notifications in one message then one reply;
    - an agent notification plus a hand-back, where the copy's output is the report;
    - an absorbed notification inside another request's turn, which gives a notice run with null output and leaves that request's result unchanged.
  - Keep the cases that are not about notices unchanged.
- [ ] **Step 2: Tests.**
  - Make `AgentNoticeTests` read the new keys by grouping the emitted nodes by `runId`.
  - In `BackgroundNoticeRunsTests`, test `runID`/`isNoticeRun`, and `shownAgents` (a background block is hidden in a request run and shown in a notice run).
  - Test that the `recordGraph` projection sets `noticeRun` and keeps `sourceBlockId`.
  - Test that `MightyGraphRun` with `noticeRun` round-trips through Codable, and that old JSON without the field reads as nil.
- [ ] **Step 3: Implement the tracker.**
  - Replace the `addNotice` steer and the `afterTaskNotice` section with a notice run:
    - On the first root notice of a turn, allocate `noticeIndex += 1` and emit the notice run's main node with `kind "main"`, `state "running"`, `parentId nil`, the noticeRun title and the input lines.
    - For each settled background block, emit its copy under that run.
    - Later notices in the same turn add copies and update the main input.
    - The notice turn's `result` sets the notice main output and state, and leaves the original main alone.
  - Absorbed notices (`notice == false`, or one arriving while the original request's own turn is running) emit a completed notice run without output.
  - Keep `taskAliases`, `settleTasks` and the hand-back settling of the original blocks, because the original blocks still settle.
  - Remove `replyTarget`, `turnNotices` and `noticeReply` writes. Do not delete the `noticeReply` field.
  - Use `ExecutionGraphSupport.mainNodeID(runId:)` for the notice run's main id. Read how `recordGraph` derives runs, and make the notice run's input and finalOutput come out right from its main node.
- [ ] **Step 4: Filter.** Implement `BackgroundNoticeRuns`. In the projection, set `noticeRun = BackgroundNoticeRuns.isNoticeRun(node.runId) ? true : nil`.
- [ ] **Step 5: Run.** Run the focused tests (`AgentNoticeTests|BackgroundNoticeRunsTests|ExecutionGraphTests|SessionHistoryTests|BackgroundOutputTailTests`) and fix any failures. Report the result line.
- [ ] **Step 6: Commit.** Subject: `(mac) a background notice opens its own 백그라운드 작업 완료 run with the finished work and Claude's reply, instead of a section on the old request`

### Task 2: Mac app (views use the filter, strip expands, phone fields, copy)

**Files:**
- Modify:
  - Mac views that list a run's agents: `MightyGraphView.swift`, `MightyGraphTimelineView.swift` (or the 기록 view), `MightyStructureView.swift`, and `MightyStructure.swift` (MightyCore)
  - `BackgroundWorkViews.swift` (expandable rows), `PlanCardSupport.swift` (row data: command, tail)
  - the phone conversion (`MobileMightySupport`, `MobileStructure`, `MobileRemoteModels.swift`: run `noticeRun`, detail `backgroundWork`)
  - `locales/*.json` and the copies

**Interfaces:**
- Consumes: `BackgroundNoticeRuns.shownAgents`, `MightyGraphRun.noticeRun`.
- Produces: `MobileSessionDetail.backgroundWork: [MobileBackgroundWork]?` (`id`, `kind`, `title`, `status`, `startedAt`, `command?`, `outputTail?`) and `noticeRun: Bool?` on the mobile run and structure models.

- [ ] **Step 1:** Add the locale keys, then sync and run `--check`.
- [ ] **Step 2:** Every place that iterates a run's agents for display uses `BackgroundNoticeRuns.shownAgents(run)`: 흐름 layout and blocks, 기록, 파이프라인 structure building, and the phone block and structure conversion. Search with `grep -rn "\.agents" native/macos/Sources/MightyClaude native/macos/Sources/MightyCore/MightyStructure.swift native/macos/Sources/MightyCore/Remote`.
  - Leave data paths unchanged: background output polling, digests and history.
- [ ] **Step 3:** In a notice run's views, the main block shows the run's title and its input lines, and the result is the reply. The blocks use the existing background block rendering from 0.4.45.
- [ ] **Step 4: Strip.** Rows can be expanded per row.
  - An expanded row shows the command, or the agent's first prompt line, with the tail lines (≤6) from `BackgroundOutputModel` for that pane.
  - Join the row to the graph block through `toolUseId`, as in 0.4.45.
  - Keep the existing open/close of the whole strip and the per-pane redraw scoping. Follow the perf lessons: no whole-window redraw.
- [ ] **Step 5: Phone fields.**
  - `backgroundWork` holds running items, built from the strip rows, with no path.
  - Add `noticeRun` on runs.
  - Include `backgroundWork` in the detail digest.
  - Tests in MightyCore: the encoding, and that a request run's background blocks are left out of the mobile blocks.
- [ ] **Step 6:** Run the focused tests and the app build.
- [ ] **Step 7: Commit.** Subject: `(mac) running background work lives in the strip with its command and output, and each finished batch shows as its own 백그라운드 작업 완료 diagram`

### Task 3: Windows Core + host + web

**Files:**
- Modify: Core `ExecutionGraphTracker.cs`, `ExecutionGraph.cs`, `MightyGraph.cs`; create `BackgroundNoticeRuns.cs`; extend `AgentNoticeVerification.cs` and add a `BackgroundNoticeRuns` verification in `Verification.cs`.
- Modify: the host strip (`MainWindow.BackgroundWork.cs`, `MainWindow.WebShell.Cards.cs`), the mobile detail and run models.
- Modify: web `src/mighty/*` (Block, Diagram, Timeline, Structure use the filter; notice run title) and `src/cards/Cards.tsx` (expandable rows with command and tail).

**Interfaces:**
- C# `BackgroundNoticeRuns.RunId`, `IsNoticeRun`, `ShownAgents`, plus `MightyGraphRun.NoticeRun` and `SourceBlockId`, mirroring the Mac.
- The web models gain `noticeRun?: boolean` on runs and `command?`/`tail?` on strip rows.

- [ ] **Step 1:** Port the tracker change, the field and the filter with exactly the Mac rules. `AgentNoticeVerification` reads the same fixture keys.
- [ ] **Step 2:** Host and web views use the filter. The strip rows expand. The phone gets `backgroundWork` and `noticeRun`, with no path.
- [ ] **Step 3:** Run Core, the WinUI compile check, web tsc and vitest (src/mighty, src/cards), and the locale and token checks.
- [ ] **Step 4: Commit.** Subject: `(windows) running background work lives in the strip, and each finished batch shows as its own 백그라운드 작업 완료 diagram in the web window and on the phone`

### Task 4: Phone + docs + help

**Files:**
- Modify: `mobile/src/api/types.ts` (`backgroundWork`, `noticeRun`), the Mighty view (`mighty-blocks.tsx` and the screen), `structure-view.tsx`, a new `mobile/src/lib/background-work.ts` with its test.
- Modify: `docs/mobile-remote.md` and `docs/help/content/{ko,en,zh,ja}/` (the Mighty or background work section).

- [ ] **Step 1:**
  - `parseBackgroundWork(detail)` is defensive and running-only.
  - Add a filter that mirrors `shownAgents` for runs and blocks the phone draws; the host already filters, so this is defence for old hosts. Test both.
- [ ] **Step 2:**
  - Add a collapsed "백그라운드 작업 N개" row above the Mighty view. Expanded, it shows the rows with the command and tail.
  - Notice runs show their title (`graph.background.noticeRun`).
  - Use theme tokens only; the raw spacing must not rise.
- [ ] **Step 3: Docs.**
  - In `docs/mobile-remote.md`, describe `backgroundWork`, `noticeRun` and the notice-run behaviour.
  - Rewrite the background work help section in all 4 languages: running work is in the strip, and each finished batch is a new "백그라운드 작업 완료" diagram with the reply.
- [ ] **Step 4:** Run tsc and the full phone jest, then check-locales, check-design-tokens and `help --check`.
- [ ] **Step 5: Commit.** Subject: `(phone) the phone lists running background work above the Mighty view and shows each finished batch as its own diagram; the contract and help say how`
