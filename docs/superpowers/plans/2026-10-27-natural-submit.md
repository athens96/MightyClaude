# Natural Submit (A shared structure + B submit) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every input shows input → pending → result. A submitted request appears the moment Enter is pressed, shows a simple "전송 중" loading until the run starts, and is always scrolled into view. The Mac, Windows (web page + host) and the phone share one pending model, motion tokens and an action list.

**Architecture:** A shared fixture `native/contracts/fixtures/pending.json` fixes three things: the pending display rule (show after 150 ms, keep at least 300 ms), the submit-response shape (`accepted`, `entryId`, `runId`, `outcome`), and the scroll-target rules. Motion tokens go into `design-tokens.json`. Each platform gets one pending component and implements the submit flow against these rules. `docs/interaction.md` lists every user action.

**Tech Stack:** Swift/SwiftUI (MightyCore, MightyClaude), C# (MightyClaude.Core, WinUI host), React + TS + vitest (native/windows/web), React Native + jest (mobile).

Spec: `docs/superpowers/specs/2026-10-27-natural-submit-design.md` (read it; it holds the survey table and every decision).

## Global Constraints
- **Decisions:**
  - Own Enter always scrolls to the new request in every view, whatever the position. 파이프라인 returns to the newest board.
  - Queued requests (pane busy) never get a block; the queue row above the composer flashes for `motion.highlight`.
  - Pre-start state is one simple "전송 중" loading, with no phase names, until the first run event; then "실행 중".
- **Pending display:**
  - Show only once a pending action has lasted `pendingShowAfter` (150 ms).
  - Once shown, keep it at least `pendingMinVisible` (300 ms).
  - `failed` shows at once, in place.
  - Reduce Motion removes movement only; the show and hide timing is the same.
- **Motion tokens:**

  | Token | Value (ms unless noted) |
  |---|---|
  | `quick` | 150 |
  | `standard` | 220 |
  | `scroll` | 280 |
  | `easing` | `[0.2, 0, 0, 1]` (cubic-bezier) |
  | `pendingShowAfter` | 150 |
  | `pendingMinVisible` | 300 |
  | `highlight` | 1200 |

- **Submit failure:**
  - The block turns `failed` with the message.
  - The draft is restored only if the composer is still empty and unchanged since the send (Mac's current rule).
- **Submit response:** `{ accepted, entryId?, runId?, outcome? }`, where `outcome` is `started`, `queued` or `steered`. Old responses without the new fields must still be accepted.
- **Copy:** locale keys in `locales/{ko,en,zh,ja}.json` plus the synced copies (`scripts/check-locales.js`). No hard-coded Korean. Use the key `graph.state.sending` for "전송 중" (add it).
- **Scope:** WinUI fallback is out of scope, and so is part C (workspace, pane and view switching).
- **Process rules:**
  - Builds and tests only when the user asks. Implementers write tests and run only the quick text checks: `node scripts/check-locales.js --check`, `node scripts/check-design-tokens.js --check`, `bash scripts/check-style-freeze.sh`, `node scripts/help/build.mjs --check`. `swiftc -parse` and `npx tsc --noEmit` are allowed as syntax checks.
  - Work on main in `~/Work/MightyClaude`; do not push. The repo is public.
  - Never commit `.ouroboros_eval_artifact.md`.
  - `git config user.email` must be `11283764+athens96@users.noreply.github.com`.
  - Commit subjects are `(area) what the user now gets` and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Never launch the GUI app.

---

### Task 1: Shared contract, motion tokens, action list

**Files:**
- Create: `native/contracts/fixtures/pending.json`
- Modify: `native/contracts/fixtures/design-tokens.json` (add the `motion` group)
- Modify: `scripts/check-design-tokens.js` (validate `motion`: keys, numbers in ms, easing is 4 numbers in [0,1] for x)
- Create: `docs/interaction.md`
- Modify: `CLAUDE.md` or `docs/` contributor notes (one line: every new user action adds a row to `docs/interaction.md` and uses the platform pending component)
- Modify: `locales/{ko,en,zh,ja}.json` + synced copies (`graph.state.sending`: 전송 중 / Sending / 发送中 / 送信中)

**Fixture shape (`pending.json`):**
- `display`: cases `{ startedAt, endedAt|null, now, expectVisible }`. They cover:
  - not shown before 150 ms;
  - shown from 150 ms;
  - ended at 100 ms → never shown;
  - ended at 200 ms → shown until 450 ms (150 + 300) and hidden at 451 ms;
  - ended at 900 ms → hidden right at 900 ms (it has been visible for more than 300 ms);
  - `failed` → visible at once.
- `submitResponses`: raw JSON → parsed `{ accepted, entryId, runId, outcome }`. Include an old `{protocol, accepted}` response, a full one, `queued` and `steered`.
- `scrollTargets`: `{ view: "basic"|"diagram"|"pipeline"|"timeline", own: true|false, chosenBoard: "newest"|"older", nearBottom: bool }` → `{ scroll: "toRequest"|"follow"|"none", resetBoard: bool }`. Own submit → `toRequest`, with `resetBoard` when the board is older. Not own → today's follow rule: `follow` if near the bottom or following, else `none`.

**`docs/interaction.md`:**
- One table of every user action, taken from the spec's survey and the three survey summaries in the spec.
- Columns: action, Mac / Windows / phone entry points, pending display today → after this plan, failure display, scroll rule.
- Mark the rows this plan changes, and leave part C rows as "C: 다음 계획".

- [ ] Steps: write the fixture → add `motion` to the tokens and extend the token check → add the locale key and sync copies → write `docs/interaction.md` and the contributor rule → run the text checks → commit `(docs) one pending rule, motion tokens and a list of every user action shared by the Mac, Windows and the phone`.

### Task 2: Mac core

**Files:**
- Create: `native/macos/Sources/MightyCore/PendingDisplay.swift`. Contents:
  - `public enum PendingState { case idle, pending(since: Date), done(at: Date, since: Date), failed(String) }`
  - `public static func visible(state:, now:, motion:) -> Bool`
  - `public struct SubmitResponse { accepted, entryId, runId, outcome }` with `parse(_ json:)`
  - `public enum SubmitScroll { static func target(view:own:chosenBoardIsNewest:nearBottom:following:) -> (scroll, resetBoard) }`
- Modify: `native/macos/Sources/MightyCore/DesignMetrics.swift` (`DesignMetrics.Motion` with the token values)
- Modify: the Mac mobile host's submit route (`MightyCore/Remote/MobileRemoteService.swift`, `/m1/sessions/{id}/submit`) and its delegate return, so the response carries `entryId`/`runId`/`outcome` (B5)
- Test: `native/macos/Tests/MightyCoreTests/PendingTests.swift` (fixture `display`, `submitResponses`, `scrollTargets`), and `DesignTokenParityTests` (`motion`)

- [ ] Steps: tests from the fixture → implement → update the mobile submit response (keep `protocol`/`accepted`) → text checks → commit `(mac) the pending rule, motion tokens and submit response ids in MightyCore`.

### Task 3: Mac app

**Files (read the spec's Mac rows first):**
- `native/macos/Sources/MightyClaude/PendingIndicator.swift` (new). `PendingIndicator` and `.pendingOverlay(_:)` follow `PendingDisplay` and use the `MightyGraphActivityIndicator` bar style, Reduce Motion aware.
- `AppStore.swift` `start` (~1064-1187): set the new graph run's status to `"starting"` and the user entry's pending flag, and flip them to running on the runner's first status/event. Failure follows the spec. `MightyGraphRun.status` already allows `"starting"`; show it as `graph.state.sending` with the pending indicator wherever "준비 중" is shown today (`MightyGraphView.swift:763`), in the structure and timeline cards, and as a small pending row under the user's bubble in the 기본 transcript (`AgentTranscriptFormat`).
- B2 scroll on own submit:
  - **기본:** force the scroll to the bottom on the next document update after an own submit, even when the user had scrolled up (`AgentTranscriptView`). Add a store "own submit token" per pane that the view consumes.
  - **흐름:** the camera follow gets an animated move of `Motion.scroll`, instant under Reduce Motion (`MightyGraphInteraction` camera jump).
  - **파이프라인:** reset `Navigation.chosen` to the newest board on an own submit (`MightyStructureBoard`, `MightyStructureView`).
  - **기록:** `scrollTo` the new group reliably (the `LazyVStack` short landing; scroll again after layout).
  - Use `SubmitScroll.target`.
- B3 queue highlight: `QueuedInputsView` flashes the accent wash for `Motion.highlight` when an item is appended, and new rows animate in (`Motion.standard`).
- Stop: move `stopping` from view `@State` to the store per session, and show `PendingIndicator` in the stop button.
- The same B2 scroll applies to queued runs when they start (`settleQueue` → `start`), to the 다음 실행 steer (focus the steer node), and to the ⌘Enter child when its tab is selected.

- [ ] Steps: component → submit pending state → per-view scroll → queue highlight → stop state → text checks → commit `(mac) a sent request shows 전송 중 until its run starts and every view moves to it, the queue row flashes when a request waits, and stopping shows a pending mark`.

### Task 4: Windows Core and host

**Files:**
- `native/windows/MightyClaude.Core/DesignMetrics.cs` (Motion), `PendingDisplay.cs` (same API as Task 2), Core checks reading `pending.json` (embed it like the other fixtures) + `DesignTokenVerification` motion.
- `RunManager.cs` (~223-252): publish the first `graph_run` at once when a run starts (and on each run-state change), not only from the 1 s `PublishGraphs` loop.
- `MainWindow.WebShell.Composer.cs` `send` bridge (~71-79) / `PaneView.Submit` (`MainWindow.cs:529`): return `{ accepted, entryId, runId, outcome, clientId }`, echoing a `clientId` the page sent. The user entry and run created in `Launch` (`DesktopService.cs:283`) carry the ids.
- Windows mobile host submit route (`MobileDesktopRouter.cs`): B5 response.
- Draft: accept a debounced `updateDraft` from the page and add `flushDraft` at send (no host change if the page simply debounces; keep `send` taking the text).

- [ ] Steps: tests from the fixture → implement → text checks → commit `(windows) the Core shares the pending rule and motion tokens, a run's diagram block is published the moment it starts, and a send answers with its entry and run ids`.

### Task 5: Windows web page

**Files (`native/windows/web/src`):**
- `theme/tokens.ts`: motion from the tokens (+ test).
- `pending/` (new):
  - `display.ts`, the A1 rule, with a vitest test reading `pending.json` like other fixture tests;
  - `usePending.ts`;
  - `Pending.tsx`.
  
  Replace the duplicate `.spinner` / `.card-spinner` with it.
- `store/`: an optimistic pending request per session `{ clientId, text, at }`.
  - On Enter (`composer/Composer.tsx` `send`), clear the textarea at once and add the pending request.
  - The conversation and the three Mighty views render it as a user bubble / request block with `<Pending>` and `graph.state.sending`.
  - When the host delta brings the real entry/run (matched by `entryId`/`runId` from the send result, or by `clientId`), replace it.
  - On failure, mark it failed with the message and restore the draft per the rule.
  - On `queued`, drop it and flash the queue row (B3). On `steered`, drop it and focus the steer node.
- B2 scroll:
  - **Conversation:** an own submit forces the bottom (`Conversation.tsx` follow).
  - **Diagram:** animated camera move (`camera.ts`), duration `scroll`.
  - **Structure:** reset to the newest board (`structureMotion.ts`).
  - **Timeline:** scroll to the last group.
  - Use the shared `scrollTargets` rule in a `submitScroll.ts` with a vitest test.
- A "실행 중" row between the run start and the first output, using the stored `activities` (today unused).
- Cards: permission and other answers lock with `<Pending>` while their bridge call runs (`cards/Cards.tsx`).
- View mode buttons switch optimistically (`MightyView.tsx` `mightySetMode` / `mightySetPresentation`), reverting on failure.
- Draft: debounce `updateDraft` 300 ms; flush on send.
- `scripts/perf-scenario.ps1` + `MainWindow.PerfScenario.cs`: a "send → block visible" phase (target ≤ 50 ms) — add the phase; do not run it.

- [ ] Steps: tokens + pending component → optimistic request + reconcile → scroll rules → running row → cards lock → view-mode optimistic → draft debounce → perf phase → text checks + `npx tsc --noEmit` → commit `(windows) the web window shows a sent request at once with 전송 중 until it starts and moves every view to it, clears the composer on Enter, locks answered cards and switches views without waiting`.

### Task 6: Phone

**Files (`mobile/src`, `mobile/app`):**
- `theme/index.ts`: `motion` (+ jest parity with the tokens).
- `components/pending.tsx` + `hooks/use-pending.ts` follow the A1 rule (jest reading `pending.json`). `Button busy` uses it.
- `api/types.ts` `SubmitResponse`: optional `entryId`, `runId`, `outcome` (parse old responses).
- Session screen `app/host/[hostId]/session/[sessionId].tsx`:
  - On send, clear the composer at once and add an optimistic pending entry (bubble / block with `graph.state.sending`).
  - Reconcile with the polled entry by `entryId`, else by text + time.
  - On failure, show it failed and restore the draft per the rule.
  - On `queued`, flash `QueuedList` instead.
- B2 scroll:
  - Follow the optimistic entry at once and again when the real one lands (fixes `followNewest` running before the entry exists).
  - 파이프라인 resets to `NEWEST_BOARD` on an own send (`structure-view.tsx`).
  - Use `scrollTargets` (jest).
- Stop and slash commands: `<Pending>` on the control and no double taps.

- [ ] Steps: tokens + pending component → types → optimistic send + reconcile → scroll → stop/slash → text checks + `npx tsc --noEmit` as a syntax check → commit `(phone) a sent request shows at once with 전송 중 and the session moves to it, stop and commands show a pending mark, and the pipeline returns to the newest board on send`.

## Verification when the user asks
- **Mac:** `--filter '\.(PendingTests|DesignTokenParityTests|ExecutionGraph[A-Za-z]*Tests|SessionHistoryTests)/'` + app build.
- **Windows:** Core checks + WinUI compile.
- **Web:** vitest + tsc.
- **Phone:** jest + typecheck.
- **Device checks:** send in each view while scrolled up / on an older board; a send while busy (queue flash); a failing send (e.g. a bad model) restores the draft; Windows perf phase "send → block visible".
