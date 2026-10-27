# Background Work Kinds Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In Claude panes, tell background sub-agents ("하위 에이전트 · 응답 대기") and background runs ("백그라운드 실행 · 알림으로 확인") apart in the 흐름 blocks, 파이프라인 cards and the strip above the composer on Mac, Windows and the phone. Each background run also shows its command, a live tail of its output, and once it ends the notice summary plus Claude's reply.

**Architecture:**
- **Tracker:** the graph tracker marks background blocks (`background`), and remembers a run's output file and the reply to the notice that ended it (`noticeReply`). These are persisted on the graph node.
- **Host tailing:** the host reads the end of each running run's output file once a second. A pure rule, shared through a contract fixture, turns those bytes into at most 6 lines. The lines live only in host memory (block id → lines), are never written to graph saves, and are pushed to the Windows web page and the phone as changed blocks.
- **Views:** the views pick the kind from `kind` + `background`.

**Tech Stack:**
- Mac: Swift and SwiftUI (MightyCore, MightyClaude).
- Windows: C# (MightyClaude.Core, WinUI host) with a React/TypeScript web front end (`native/windows/web`).
- Phone: Expo React Native (`mobile/`).

**Spec:** `docs/superpowers/specs/2026-10-27-background-work-kinds-design.md`

## Global Constraints
- **Scope:**
  - Claude panes only. Codex and Antigravity are unchanged.
  - Do not touch `native/macos/Sources/MightyCore/Styles/**` (the frozen style engine), and not the style panel's `taskList` widget either.
- **Tail rule:**
  - Reads at most `maxBytes` 8192 from the end of the file, keeps at most `maxLines` 6 lines, and cuts a line at `maxLineChars` 200 with `…`.
  - Drops the first line when the file is longer than `maxBytes`.
  - Strips ANSI escape sequences.
  - For a line overwritten with `\r`, keeps only its last segment.
  - Drops trailing empty lines.
  - Decodes invalid UTF-8 as U+FFFD.
- **Polling:**
  - The tail is read every 1 s, only for running `task` blocks that have an output file.
  - It is read once more when the block ends, then polling stops.
  - A file that is missing or unreadable gives no lines and no error.
  - This includes remote-workspace panes, where the file is on another computer.
- **Privacy:** the output file path is never shown and never sent to the phone. It is persisted on the graph node only.
- **Display rules:**
  - The reply to a notice is still appended to the root result as `작업 알림 이후`.
  - The root "작업 알림" steer block stays.
  - `noticeReply` goes on a task block only when that block's notification was the only one in the turn's user message.
- **Copy:**
  - Add these locale keys to `locales/{ko,en,zh,ja}.json` plus the synced copies (`node scripts/check-locales.js` syncs or checks them):

    | Key | Korean copy |
    |---|---|
    | `graph.background.agentWaiting` | "하위 에이전트 · 응답 대기" |
    | `graph.background.runWaiting` | "백그라운드 실행 · 알림으로 확인" |
    | `graph.background.agentEnded` | "하위 에이전트 · {status}" |
    | `graph.background.runEnded` | "백그라운드 실행 · {status}" |
    | `graph.background.reply` | "Claude의 답" |

  - `{status}` reuses the existing 완료/실패/중지 words (`graph.block.taskNotice.completed/failed/stopped` text without the prefix, or existing status labels; choose existing keys, do not duplicate).
  - No hard-coded Korean.
- **Phone wire:**
  - `MobileBlock` and the structure `Node` gain optional `background?: true`, `outputTail?: [string]` (≤6) and `noticeReply?: string`, clipped like `output`.
  - Old phones ignore them, and old hosts omit them.
- **Process rules:**
  - Work on main in `~/Work/MightyClaude` and do not push. The repo is public, so no personal paths, emails or IPs, including in fixtures; use `/tmp/example/...`.
  - Never commit `.ouroboros_eval_artifact.md`.
  - `git config user.email` must be `11283764+athens96@users.noreply.github.com`.
  - Commit subjects are `(area) what the user now gets`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Checks:**
  - No builds and no full test suites. Write the tests, but do not run Swift or C# tests.
  - Allowed checks:
    - `node scripts/check-locales.js --check`
    - `node scripts/check-design-tokens.js --check`
    - `bash scripts/check-style-freeze.sh`
    - `node scripts/help/build.mjs --check`
    - `xcrun swiftc -parse <files>`
    - `npx tsc --noEmit` (in `mobile/` or `native/windows/web/`)
    - jest or vitest on the files you touched
  - Never launch the GUI app.

---

### Task 1: Contract fixture + Mac MightyCore (tail rule, tracker fields)

**Files:**
- Create: `native/contracts/fixtures/background-output.json`
- Create: `native/macos/Sources/MightyCore/BackgroundOutputTail.swift`
- Modify: `native/macos/Sources/MightyCore/ExecutionGraph.swift` (`ExecutionGraphNode` at ~83)
- Modify: `native/macos/Sources/MightyCore/MightyGraph.swift` (`MightyGraphAgent` ~3, `backgroundWaiting` ~66, projection ~378)
- Modify: `native/macos/Sources/MightyCore/ExecutionGraphTracker.swift`:
  - Agent tool ~255-263
  - background tool ~273-284
  - `acknowledgeTask` ~636
  - `settleTasks` ~659
  - notice result ~315-321
- Modify: `native/contracts/fixtures/agent-notices.json` (add cases)
- Create: `native/macos/Tests/MightyCoreTests/BackgroundOutputTailTests.swift`
- Modify: `native/macos/Tests/MightyCoreTests/AgentNoticeTests.swift` (if it loops the fixture, no change is needed beyond the new expectation keys), `ExecutionGraphTests.swift`

**Interfaces:**
- Produces:
  - `public enum BackgroundOutputTail { static let maxBytes = 8192; static let maxLines = 6; static let maxLineChars = 200; static func lines(_ data: Data, truncated: Bool) -> [String]; static func read(path: String) -> [String]? }`
    - `read` returns nil when the file is unreadable.
    - It reads the last `maxBytes` and passes `truncated: fileSize > maxBytes`.
  - `ExecutionGraphNode` gains `public var background: Bool?`, `public var outputFile: String?` and `public var noticeReply: String?`. They are optional with default nil, Codable, and absent in old saves. Add them to `init` as trailing default-nil parameters.
  - `MightyGraphAgent` gains the same three fields, also trailing defaults, and the projection at ~378 copies them. It also gains `public var isBackgroundAgent: Bool { kind == nil && background == true }`.
  - `MightyGraphRun.backgroundWaiting` now counts `($0.isTask || $0.isBackgroundAgent) && !terminal`.

- [ ] **Step 1: Write the fixture** `native/contracts/fixtures/background-output.json`:

```json
{
  "about": "How a host turns the end of a background run's output file into the lines it shows. Mac BackgroundOutputTail, Windows Core BackgroundOutputTail, phone lib/background-output.ts read this file.",
  "maxBytes": 8192,
  "maxLines": 6,
  "maxLineChars": 200,
  "cases": [
    { "name": "short file", "base64": "<base64 of 'one\\ntwo\\nthree\\n'>", "truncated": false, "lines": ["one", "two", "three"] },
    { "name": "keeps the last six", "base64": "<base64 of '1\\n2\\n3\\n4\\n5\\n6\\n7\\n8\\n'>", "truncated": false, "lines": ["3", "4", "5", "6", "7", "8"] },
    { "name": "cut first line dropped when truncated", "base64": "<base64 of 'tial line\\nfull\\n'>", "truncated": true, "lines": ["full"] },
    { "name": "ansi colours stripped", "base64": "<base64 of '\\u001b[32mok\\u001b[0m done\\n'>", "truncated": false, "lines": ["ok done"] },
    { "name": "carriage return keeps the last segment", "base64": "<base64 of '10%\\r50%\\r100%\\nnext\\n'>", "truncated": false, "lines": ["100%", "next"] },
    { "name": "trailing empty lines dropped", "base64": "<base64 of 'a\\n\\n\\n'>", "truncated": false, "lines": ["a"] },
    { "name": "long line cut", "base64": "<base64 of 250 x 'x' + '\\n'>", "truncated": false, "lines": ["<200 x 'x' + '…'>"] },
    { "name": "invalid utf-8 replaced", "base64": "<base64 of bytes 61 ff 62 0a>", "truncated": false, "lines": ["a�b"] },
    { "name": "empty file", "base64": "", "truncated": false, "lines": [] }
  ]
}
```

  Generate the real base64 and the exact expected strings with a short `node -e` or `python3 -c` one-off. Do not leave the `<…>` placeholders in the file. "long line cut" expects exactly 200 `x` followed by `…` (U+2026).

- [ ] **Step 2: Write the failing Mac test** `BackgroundOutputTailTests.swift`. Load the fixture the way `PendingTests.swift` loads `pending.json` (copy its fixture-path helper). Check that the constants equal the fixture's, and that for each case `BackgroundOutputTail.lines(Data(base64Encoded: case.base64)!, truncated: case.truncated) == case.lines`. Also add one `read(path:)` test: write 9000 bytes to a temp file and assert that the result has no partial first line and at most 6 lines. Add another `read(path:)` test for a missing path that returns nil.

- [ ] **Step 3: Implement** `BackgroundOutputTail.swift`:

```swift
import Foundation

/// The lines a host shows for a background run: the end of its output file
/// (native/contracts/fixtures/background-output.json).
public enum BackgroundOutputTail {
    public static let maxBytes = 8192
    public static let maxLines = 6
    public static let maxLineChars = 200

    public static func lines(_ data: Data, truncated: Bool) -> [String] {
        let text = String(decoding: data, as: UTF8.self)
        let clean = text.replacingOccurrences(of: #"\u{1B}\[[0-9;?]*[ -/]*[@-~]|\u{1B}\][^\u{07}\u{1B}]*(\u{07}|\u{1B}\\)|\u{1B}[@-Z\\-_]"#, with: "", options: .regularExpression)
        var rows = clean.components(separatedBy: "\n").map { row -> String in
            let line = row.hasSuffix("\r") ? String(row.dropLast()) : row
            return line.components(separatedBy: "\r").last ?? ""
        }
        if truncated, !rows.isEmpty { rows.removeFirst() }
        while let last = rows.last, last.isEmpty { rows.removeLast() }
        return rows.suffix(maxLines).map { $0.count > maxLineChars ? String($0.prefix(maxLineChars)) + "…" : $0 }
    }

    /// nil when the file cannot be read (missing, or on another computer).
    public static func read(path: String) -> [String]? {
        guard let handle = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd() else { return nil }
        let start = size > UInt64(maxBytes) ? size - UInt64(maxBytes) : 0
        guard (try? handle.seek(toOffset: start)) != nil, let data = try? handle.readToEnd() ?? Data() else { return nil }
        return lines(data, truncated: start > 0)
    }
}
```

  Check the regex against the fixture's ANSI case by reasoning; `swiftc -parse` only checks syntax.

- [ ] **Step 4: Tracker fields.**
  - Add `background`, `outputFile` and `noticeReply` to `ExecutionGraphNode` and `MightyGraphAgent`, and copy them in the projection.
  - In the tracker:
    1. At the Agent tool with `run_in_background` (~255), set `node.background = true`.
    2. At the background tool block (~273), set `node.background = true`.
    3. In `acknowledgeTask`, parse `Output is being written to: (?<path>\S+)` from the text with `firstMatch`, and set `node.outputFile` to the path with a trailing `.` trimmed.
    4. In `settleTasks`, read `Self.tag(body, "output-file")`. When it is present for a task block, set `outputFile` to it.
    5. **noticeReply:** in `settleTasks`, count the notifications in this user message. When exactly one task block was settled with `notice == true`, remember it in a new `private var replyTarget: String?`. Otherwise set it to nil. In the notice `result` branch (~315), when `replyTarget` is set, also `update(replyTarget) { $0.noticeReply = answer }`. Clear `replyTarget` with `noticedTasks`, and also where `steeredTurn` is reset.
  - Update `backgroundWaiting`.

- [ ] **Step 5: Fixture cases + tracker tests.** In `agent-notices.json`, follow the existing case shape (look at "background shell command (kind task): unchanged" ~959). Add these cases, expecting `background`, `outputFile` and `noticeReply` on the named block:
  - (a) A bash launch result with `Output is being written to: /tmp/example/tasks/b1.output` gives `outputFile` that path and `background: true`.
  - (b) A notification with `<output-file>/tmp/example/tasks/b1-moved.output</output-file>` replaces it.
  - (c) One notification followed by a `result` with `origin.kind:"task-notification"` and text "Build finished fine." gives that block `noticeReply: "Build finished fine."`. The main output still has the `작업 알림 이후` section.
  - (d) Two notifications in one user message, then a result, give no `noticeReply` on either.
  - (e) A background Agent gives `background: true`; a foreground Agent gives `background` absent.

  If `AgentNoticeTests.swift` compares only some keys, extend its comparison to these three keys when the case declares them. Add an `ExecutionGraphTests` case: a run whose only background agent is still running reads `backgroundWaiting == 1` after the main completes.

- [ ] **Step 6: Checks.** Run `xcrun swiftc -parse` on every touched Swift file, then `node scripts/check-style-freeze.sh`.

- [ ] **Step 7: Commit.** `(mac) background blocks know their kind, a background run remembers its output file and Claude's reply to its notice, and a shared rule reads a run's last output lines`

---

### Task 2: Mac app (tail polling, views, phone blocks, copy)

**Files:**
- Create: `native/macos/Sources/MightyClaude/AppStore+BackgroundOutput.swift`
- Modify:
  - `native/macos/Sources/MightyClaude/MightyGraphView.swift` (~147 block glyph/tint, block body)
  - `native/macos/Sources/MightyClaude/MightyStructureView.swift` (~767-829 cards)
  - `native/macos/Sources/MightyClaude/BackgroundWorkViews.swift` (~27-36 rows)
  - `native/macos/Sources/MightyCore/PlanCardSupport.swift`, and `StyleWidgetPresentation.swift` only if the row model needs a `detail` line. Keep `StyleWidgetPresentation` unchanged if the style engine reads it; put strip-only data in `PlanCardSupport` instead.
  - Mac phone conversion: `native/macos/Sources/MightyClaude/MobileMightySupport.swift` and `native/macos/Sources/MightyCore/MobileRemoteModels.swift` (`MobileBlock` ~379, structure node). Find the exact files with `grep -rn "struct MobileBlock"`.
  - `locales/{ko,en,zh,ja}.json` and the synced copies.
- Test: `native/macos/Tests/MightyCoreTests/` (a mobile conversion test next to the existing mobile Mighty tests; `grep -rln "MobileBlock" native/macos/Tests`).

**Interfaces:**
- Consumes (Task 1):
  - `BackgroundOutputTail.read(path:)`
  - the `MightyGraphAgent` fields `background`, `outputFile`, `noticeReply` and `isBackgroundAgent`
- Produces:
  - `AppStore.backgroundOutput: [String: [String]]` (`@Published`, keyed by graph agent id)
  - `func backgroundOutputTick()`, called from the existing 1 s or 100 ms tick, so no new timer if one exists. `AppStore+Terminals.swift` has a `terminalTick`; use a 1 s cadence counter like the shell-busy check there.
  - `MobileBlock` and the node gain `background: Bool?`, `outputTail: [String]?` and `noticeReply: String?`. They are encoded only when set.

- [ ] **Step 1: Locale keys.** Add the five keys from Global Constraints to `locales/*.json`, with en/zh/ja translations. For example, en: "Sub-agent · waiting for reply", "Background run · reported by notice", "Sub-agent · {status}", "Background run · {status}", "Claude's reply". Run `node scripts/check-locales.js` to sync, then `--check`.
- [ ] **Step 2: Polling.** In `AppStore+BackgroundOutput.swift`, `backgroundOutputTick()` looks at every run on every Claude session's graph.
  - For each agent with `isTask`, status running and `outputFile != nil`, it calls `read(path:)` and stores the result only if it differs.
  - For an agent whose status became terminal and that is in a `polling` set, it reads once more and removes the agent from the set.
  - It drops entries for agents no longer in any graph.
  - The file read runs off the main actor (`Task.detached`), and the results are applied on the main actor.
  - A redrawn history block with an `outputFile` and no entry yet gets one read.
- [ ] **Step 3: 흐름 block.** In `MightyGraphView`, the label line under the head:
  - shows `graph.background.agentWaiting` or `agentEnded` for `isBackgroundAgent`;
  - shows `graph.background.runWaiting` or `runEnded` for `isTask`;
  - fills `{status}` from the terminal state.

  The task body shows, in order: the command (`input`, monospaced, one line, middle-truncated); `store.backgroundOutput[agent.id]` (monospaced, at most 6 lines, on a `palette` subtle background using existing tokens); and once ended, the summary (the existing output) and, if `noticeReply` is set, a `graph.background.reply` header with the reply text. Use existing font and spacing tokens (`DesignMetrics`) and do not raise the raw-spacing ratchet.
- [ ] **Step 4: 파이프라인 card.** Use the same label. A task card shows the last 2 tail lines while running; when it is expanded after it ends, it shows the same content as the 흐름 body.
- [ ] **Step 5: Strip.** Join on `BackgroundTask.toolUseId`, using `ExecutionGraphSupport.agentNodeID(runId:toolUseId:)` to find the graph agent in the session's latest run.
  - Kind text: agent → `agentWaiting`, shell → `runWaiting`, other → the current `작업` title. After the task ends, use the `…Ended` forms.
  - For shell, add the last tail line as a dim monospaced second line.
- [ ] **Step 6: Phone conversion.**
  - Set `background` from the agent.
  - Set `outputTail` from `store.backgroundOutput[id]`, at most 6 lines.
  - Set `noticeReply`, clipped like `output`.
  - Never put `outputFile` in a mobile model.
  - When the tail changes, the mobile graph delta must be sent. Check how graph changes trigger the mobile push; if the delta only fires on graph emits, call the same notify path when `backgroundOutput` changes.
  - Write a test: an agent with these fields converts to a `MobileBlock` with `background`/`outputTail`/`noticeReply`, and the encoded JSON has no `outputFile`.
- [ ] **Step 7: Checks.** `xcrun swiftc -parse` on the touched files, then check-locales, check-design-tokens and check-style-freeze.
- [ ] **Step 8: Commit.** `(mac) background sub-agents and background runs are labelled apart in 흐름, 파이프라인 and the strip, and a run shows its command, its live output and Claude's reply to its notice`

---

### Task 3: Windows Core + host

**Files:**
- Create: `native/windows/MightyClaude.Core/BackgroundOutputTail.cs`
- Modify:
  - `native/windows/MightyClaude.Core/ExecutionGraph.cs`, `MightyGraph.cs` (`BackgroundWaiting` ~107)
  - `native/windows/MightyClaude.Core/ExecutionGraphTracker.cs`:
    - task block ~370
    - regexes ~93-94
    - `AcknowledgeTask` ~604
    - `SettleTasks` ~624
    - notice section ~422
- Modify: the Windows host graph/state push (find where the web shell's Mighty state and the phone `MobileBlock` are built: `grep -rn "MobileBlock\|BackgroundWork" native/windows/MightyClaude.WinUI native/windows/MightyClaude.Core | head`). Also `MainWindow.BackgroundWork.cs` (~101-204) and `MainWindow.WebShell.Cards.cs` (~18-20, 380-387).
- Test:
  - `native/windows/MightyClaude.Core.Tests/BackgroundOutputVerification.cs`, wired in `Verification.cs` next to `PendingVerification`.
  - The agent-notice verification already loops `agent-notices.json`; extend its comparisons the same way as on the Mac.

**Interfaces:**
- Consumes: the fixture and the agent-notices cases from Task 1.
- Produces:
  - `public static class BackgroundOutputTail { MaxBytes = 8192; MaxLines = 6; MaxLineChars = 200; static IReadOnlyList<string> Lines(byte[] data, bool truncated); static IReadOnlyList<string>? Read(string path); }`
  - Graph node and agent `Background`, `OutputFile`, `NoticeReply`, plus `IsBackgroundAgent`, mirroring the Mac.
  - A host map `BackgroundOutput` (agent id → lines) that the web state and the mobile blocks read.
  - The web model gains `background?: boolean`, `outputTail?: string[]` and `noticeReply?: string` on the Mighty block/agent, and `detail` on strip rows already exists.

- [ ] **Step 1:** Write `BackgroundOutputVerification.cs` over the fixture, in the same style as `PendingVerification.cs`, with the constants check, every case, and temp-file `Read` checks. Wire it into `Verification.cs`.
- [ ] **Step 2:** Implement `BackgroundOutputTail.cs`, with the same algorithm as the Mac:
  - decode with `new UTF8Encoding(false, false)`, which gives U+FFFD;
  - the same ANSI regex via `Regex`;
  - split on `\n`, and for each row trim one trailing `\r` and take the last `\r` segment;
  - when truncated, drop the first row; drop trailing empty rows;
  - take the last 6, and cut each line at 200 chars plus `…`.

  `Read` uses `FileStream` with `FileShare.ReadWrite | FileShare.Delete`, seeks to `Math.Max(0, len - MaxBytes)`, and returns null on any IO exception.
- [ ] **Step 3:** Port the tracker changes (`background`, `outputFile` from `Output is being written to:` and `<output-file>`, `noticeReply` with the single-notification rule, `BackgroundWaiting`). Extend the agent-notice verification to compare `background`, `outputFile` and `noticeReply` when a case declares them.
- [ ] **Step 4:** Host polling. On the host's existing timer, or a 1 s `DispatcherQueueTimer`, follow the same rules as Mac Task 2 Step 2: read off the UI thread, update the map only on change, read once more when the block ends, and prune agents that are gone.
  - On change, push the changed block to the web page through the existing Mighty delta path.
  - Also send the mobile graph delta.
  - The mobile conversion sets `background`, `outputTail` and `noticeReply`, never `outputFile`. Add a Core verification for the conversion if the conversion lives in Core.
- [ ] **Step 5:** WinUI strip (`MainWindow.BackgroundWork.cs` `TaskDetail`) and the web strip card rows (`MainWindow.WebShell.Cards.cs`):
  - Change the kind text to the new keys, joining on tool use id as on the Mac.
  - For shell rows, put the last tail line in the row `detail`.
- [ ] **Step 6: Checks.** check-locales, check-design-tokens, check-style-freeze. C# is not compiled here, so re-read the touched code for compile errors: types, nullable, `using`s.
- [ ] **Step 7: Commit.** `(windows) background sub-agents and background runs are told apart, and a run's command, live output and reply to its notice reach the web window and the phone`

---

### Task 4: Windows web views

**Files:**
- Modify:
  - `native/windows/web/src/mighty/types.ts`
  - `native/windows/web/src/mighty/Block.tsx` (~164)
  - `native/windows/web/src/mighty/Structure.tsx` (~142-150)
  - `native/windows/web/src/mighty/mighty.css`
  - `native/windows/web/src/cards/Cards.tsx` (~237-252) and `cards.css`
  - `native/windows/web/src/store/models.ts` (~192-205)
  - the web locale access (use the existing `t()`; the keys come from Task 2's locale files, which the web reads from the shared `locales/`)
- Test: `native/windows/web/src/mighty/rules.test.ts` or a new `background.test.ts`, plus `cards.test.ts`.

**Interfaces:**
- Consumes: the fields `background`, `outputTail` and `noticeReply` from the Task 3 web model, and strip row `detail`.
- Produces: `export function backgroundLabel(block: { kind?: string; background?: boolean; status: string }): string | null` in `src/mighty/background.ts`. It returns the label text for a background agent or task, or null otherwise. Block, Structure and the cards all use it.

- [ ] **Step 1:** Write `background.test.ts`:
  - a running task gives `runWaiting` text;
  - a completed task gives `runEnded` with the completed word;
  - a running agent with `background: true` gives `agentWaiting`;
  - an agent without `background` gives null;
  - a steer gives null.
- [ ] **Step 2:** Implement `background.ts` using the existing locale helper and the same status words as the Mac.
- [ ] **Step 3:** In `Block.tsx`, add the label line under the head. In a task body, show the command line (monospace, ellipsis), then `outputTail` (monospace, ≤6 lines, subtle background), then, once ended, the summary and the `graph.background.reply` section when `noticeReply` is set.
- [ ] **Step 4:** In `Structure.tsx`, use the same label. A running task card shows the last 2 tail lines, and an expanded ended card shows the full body.
- [ ] **Step 5:** In `Cards.tsx`, the background strip rows show the kind text from the host, and a shell row's `detail` is a dim single monospace line. Update `cards.test.ts` if it snapshots row text.
- [ ] **Step 6: Checks.** `cd native/windows/web && npx tsc --noEmit`, then `npx vitest run src/mighty/background.test.ts src/cards/cards.test.ts` and any touched test file. Then check-design-tokens.
- [ ] **Step 7: Commit.** `(windows) the web window labels background sub-agents and background runs, and shows a run's command, live output and Claude's reply`

---

### Task 5: Phone + docs + help

**Files:**
- Create:
  - `mobile/src/lib/background-output.ts`, for the contract constants and the label rule
  - `mobile/src/__tests__/background-output.test.ts`
- Modify:
  - `mobile/src/api/types.ts` (`MobileBlock`, structure `Node`)
  - `mobile/src/lib/mighty.ts` (~29, 39)
  - `mobile/src/components/mighty-blocks.tsx`
  - `mobile/src/components/structure-view.tsx` (~468-476)
  - `docs/mobile-remote.md` (~130, 188-200, 243)
  - `docs/help/content/{ko,en,zh,ja}/` (the Mighty views page section on background work; find it with `grep -rln "작업 알림" docs/help/content/ko`)

**Interfaces:**
- Consumes: the wire fields from Tasks 2 and 3.
- Produces: `export const BACKGROUND_OUTPUT = { maxLines: 6 }` and `export function backgroundLabel(block: { kind: string; background?: boolean; status: string }): string | null` in `mobile/src/lib/background-output.ts`. The rules are the same as the web's.

- [ ] **Step 1:** Write the test. It reads `native/contracts/fixtures/background-output.json` the way `pending.test.ts` reads `pending.json`, and asserts that `maxLines` equals the constant. It also covers the label cases from Task 4 Step 1.
- [ ] **Step 2:** Implement `background-output.ts` with `t()` and the keys.
- [ ] **Step 3:** In `types.ts`, add the optional fields `background?: true`, `outputTail?: string[]` and `noticeReply?: string` to `MobileBlock` and `Node`.
- [ ] **Step 4:** In `mighty-blocks.tsx`, show the label line, and in a task block the command, the tail (monospace, ≤6 lines), then the summary and the reply section. In `structure-view.tsx`, use the same label, the last 2 tail lines on a running task card, and the full body when expanded. Use theme tokens only; phone raw spacing must stay at its budget.
- [ ] **Step 5:** In `docs/mobile-remote.md`, document the three optional fields on `MobileBlock` and `Node`:
  - the phone ignores them when absent;
  - `outputFile` is never sent;
  - `outputTail` is at most 6 lines, from the shared fixture.
- [ ] **Step 6:** In the help pages for 4 languages, add one short paragraph to the Mighty background-work section. It explains the two kinds, says a background run shows its command, its last output lines and Claude's reply, and says the strip shows the last line. Use `{{ui:graph.background.runWaiting}}`-style keys where the page already uses `{{ui:}}`.
- [ ] **Step 7: Checks.**
  - `cd mobile && npx tsc --noEmit && npx jest src/__tests__/background-output.test.ts`
  - `node scripts/check-locales.js --check`, `node scripts/check-design-tokens.js --check`, `node scripts/help/build.mjs --check`
- [ ] **Step 8: Commit.** `(phone) background sub-agents and background runs are labelled apart, and a run shows its command, live output and Claude's reply; the contract and help say how`
