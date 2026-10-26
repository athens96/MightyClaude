# Agent Notices in the Mighty Views — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A background sub-agent's end shows in the Mac and Windows Mighty views like a background command's does. Its block settles from its `<task-notification>`, its final report (`<agent-message>` hand-back) becomes the block's result, and Claude's reply turn shows as one 작업 알림 block.

**Architecture:** The Mac `ExecutionGraphTracker` already settles kind-`task` nodes from `<task-notification>` (`settleTasks`, `acknowledgeTask`, `addSteer`, `noticedTasks`). The plan extends the same path to kind-`agent` nodes. It adds a parser for the `<agent-message>` hand-back, and treats a `peer`-origin `result` as a notice result. A shared fixture holds real record shapes; the Windows Core mirrors the change.

**Tech Stack:** Swift (MightyCore, Swift Testing), C# (MightyClaude.Core, `Verification.cs` checks), Markdown help.

Spec: `docs/superpowers/specs/2026-10-26-agent-notices-design.md`.

## Global Constraints
- Same rules on Mac and Windows; the fixture `native/contracts/fixtures/agent-notices.json` is the contract.
- One 작업 알림 block per agent, even when both a notification and a hand-back arrive.
- Status map:
  - `completed` → completed
  - `failed` / `error` → error
  - anything else (`killed`, `stopped`, …) → stopped
- A terminal block keeps its state; a hand-back still fills an empty or summary-only result.
- Only a framed hand-back is a final report: its text has `[Subagent hand-back]` or `The report follows` (or its origin has `handback: true`). Its body is the indented report after the preamble line ending `The report follows:`, with each line's leading two spaces removed and trimmed (the whole text inside the tag when that marker is absent but the frame is there). Any other `<agent-message>` from a known agent (a mid-run message) changes no state, output or notice; it is appended to that agent's log. Unknown senders are ignored.
- Notice steers come only from main-level user messages; a nested agent's notification or report settles its block without a 작업 알림 under main. Once an agent or task has its notice block, a later notice adds nothing (not even its title to the notice-answer header).
- An agent is matched to its block in this order:
  1. `tool-use-id`
  2. `task-id` / `from` / `origin.senderTaskId` equal to the agent id, read from the launch result `agentId: <id>`
  3. `from` equal to the Agent call's `name` input
- No hard-coded Korean; reuse the existing `graph.block.taskNotice.*` and `graph.result.afterTaskNotice` keys. Add a key only if needed, in all four `locales/*.json` plus the synced copies.
- **Builds and tests:** none unless the user asks (rule of 2026-10-23). Write the tests; run only `node scripts/check-locales.js --check`, `node scripts/check-design-tokens.js --check`, `bash scripts/check-style-freeze.sh` and `node scripts/help/build.mjs --check`.
- **Repository:**
  - Work on `main` in `~/Work/MightyClaude`; do not push. Public repo: no personal paths or IDs in the fixture.
  - Never commit `.ouroboros_eval_artifact.md`.
  - `git config user.email` must be `11283764+athens96@users.noreply.github.com`.
  - Subjects are `(area) what the user now gets` and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

### Task 1: Shared fixture + Mac tracker, parser and history

**Files:**
- Create: `native/contracts/fixtures/agent-notices.json`
- Modify: `native/macos/Sources/MightyCore/ExecutionGraphTracker.swift`:
  - `acknowledgeTask` (~620-636): Agent launch results feed the alias map too.
  - `settleTasks` (~642-662): accept kind `agent`.
  - The user-message branch (~303-304): also look for `<agent-message`.
  - The `isNotificationResult` branch (~305-311).
- Modify: `native/macos/Sources/MightyCore/CLIStreamParser.swift` (`ClaudeStream.isNotificationResult` ~655-658)
- Modify, only if it re-draws history by another path: `native/macos/Sources/MightyCore/SessionHistory.swift`
- Create: `native/macos/Tests/MightyCoreTests/AgentNoticeTests.swift`

**Interfaces (produced):**
- `public enum AgentHandback { public static func parse(_ text: String) -> [(from: String, body: String)] }`, in MightyCore. Pure: it finds every `<agent-message from="…">…</agent-message>` in a text (also inside `<system-reminder>`) and returns the sender and the body, de-indented per the constraint.
- `ClaudeStream.isNotificationResult` returns true for `origin.kind` in `task-notification`, `task_notification` and `peer`. Before changing it, confirm the real origin value of a hand-back turn's `result` event in the installed CLI. The CLI is at `~/.local/share/claude/versions/<latest>`; grep it for `"peer"` near `origin` / `result`. Record the evidence in the report.

- [ ] **Step 1: Fixture.** Build `agent-notices.json` from real shapes. Look at a local record (`~/.claude/projects/*/*.jsonl`), but copy only the structure and use made-up IDs such as `a1b2c3d4e5f6a7b8c` and `toolu_FAKE…` and neutral text. It holds these cases:
  - `streams`: an ordered list of stream-json events. Each case is `{ "name": …, "events": [ … ], "expect": { "agentState": …, "agentOutput": …, "noticeBlocks": n } }`. The cases:
    1. A background Agent call with `name`, launch result text containing `agentId: <id>`, then a hand-back `user` message (`origin.kind: "peer"`, `from: "<id>"`), then a `<task-notification>` with status completed and a "report delivered as message" summary. Expect completed, output = de-indented report, 1 notice.
    2. Same, but `from` is the agent's `name`, and the hand-back sits inside a `<system-reminder>` with other text around it.
    3. Only a `<task-notification>` with status `failed` and a summary. Expect error, output = summary, 1 notice.
    4. A `<task-notification>` with status `stopped`, the restart summary ("…didn't finish before the previous session ended"), matched by `task-id` only (no `tool-use-id`). Expect stopped, 1 notice.
    5. A foreground Agent call (no `run_in_background`) whose tool result settles it. No notification, 0 notices; the behaviour is unchanged.
    6. A background shell command task (kind `task`). Its existing behaviour is unchanged (regression guard).
  - `handbacks`: raw text → expected `[ { from, body } ]` for `AgentHandback.parse`. Include two messages in one text, a missing `The report follows:` marker, and CRLF.
  - `results`: `result` events with `origin.kind` in `task-notification`, `peer`, `human` and none → expected `isNotificationResult`.
- [ ] **Step 2: Tests.** `AgentNoticeTests.swift` loads the fixture the way `DelegationModesFixtureTests` does (5 × `deleteLastPathComponent` from `#filePath`). It covers:
  - `parse` matches `handbacks`;
  - `isNotificationResult` matches `results`;
  - each `streams` case fed through an `ExecutionGraphTracker` gives the expected agent node state and output, and the expected count of steer nodes whose input starts with `MightyGraphSupport.taskNoticePrefix`.

  Construct and feed the tracker exactly as the existing tracker tests do (find them with `grep -l "ExecutionGraphTracker(" native/macos/Tests`).
- [ ] **Step 3: Implement.**
  - In `acknowledgeTask` or the tool-result branch, an Agent background launch result also stores `agentId: (?<id>[A-Za-z0-9_-]+)` → node id in `taskAliases`. The tool-result branch currently `continue`s for background agents before `acknowledgeTask`; add the alias capture there.
  - Remember `name` → node id for Agent calls, capped like `taskAliases`.
  - `settleTasks` accepts nodes of kind `agent` as well as `task`. For an agent:
    - The state map is the same; never change a terminal state.
    - Set `output` to the summary only when output is empty.
    - Add the same notice steer (`addSteer(id: "notice:" + id, …)`), which `addSteer` already de-duplicates per id.
    - Append the agent's title to `noticedTasks` (capped at 16).
  - A user message containing `<agent-message` (text content, any origin) calls a new `settleHandbacks(in:)`. For each parsed `(from, body)`:
    1. Find the node by alias id, then by name.
    2. If it is not terminal, set it to completed.
    3. Set `output = body`, replacing an empty or summary-only output.
    4. Add the same single notice steer (`"notice:" + id`), so the later notification adds nothing.
  - Make `ClaudeStream.isNotificationResult` accept `peer` (per the evidence). The tracker's notice-result branch then also appends the hand-back turn's answer under `graph.result.afterTaskNotice`.
  - History: check `SessionHistory`'s Mighty re-draw path (the code around lines 400-440 and 700-740 that sets `origin` to `task-notification` for answered tasks). If it rebuilds graphs through the tracker, a peer turn should reach `settleHandbacks` with the same result. Otherwise add the minimal mapping and say so in the report.
- [ ] **Step 4: Text checks.** `node scripts/check-locales.js --check`, `node scripts/check-design-tokens.js --check`, `bash scripts/check-style-freeze.sh`.
- [ ] **Step 5: Commit.**

```bash
git add native/contracts/fixtures/agent-notices.json native/macos/Sources/MightyCore native/macos/Tests/MightyCoreTests/AgentNoticeTests.swift
git commit -m "(mac) a background sub-agent's end shows in the Mighty views like a background command's: its block settles from its notification, its final report becomes the block's result, and Claude's reply to it is one 작업 알림 block

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Windows Core mirror

**Files:**
- Modify: `native/windows/MightyClaude.Core/ExecutionGraphTracker.cs` (the same functions as the Mac: the alias capture, `SettleTasks`, the hand-back branch, the notice-result branch)
- Modify: the C# `IsNotificationResult` equivalent (find it with `grep -rn "task-notification" native/windows/MightyClaude.Core`; it is likely in `OutputParser.cs`)
- Modify, if needed: `native/windows/MightyClaude.Core/SessionHistory.cs`
- Create: `native/windows/MightyClaude.Core/AgentHandback.cs`
- Create: `native/windows/MightyClaude.Core.Tests/AgentNoticeVerification.cs`; register it in `Verification.cs`; embed the fixture in `MightyClaude.Core.Tests.csproj` as `MightyClaude.Core.Tests.AgentNotices.json` (the same pattern as `DelegationModes.json`)

**Interfaces:**
- Consumes: the fixture and the Mac behaviour from Task 1 (read the Mac diff, `git show <Task 1 commit>`).
- Produces: `public static class AgentHandback { public static IReadOnlyList<(string From, string Body)> Parse(string text) }`, and the same tracker behaviour.

- [ ] **Step 1:** Write `AgentNoticeVerification` with the same three groups as the Mac tests (handbacks, results, streams). Construct and feed the tracker the way the existing Windows tracker checks do. Register:

```csharp
await Test("agent notices: hand-backs, notice results and background sub-agent streams follow native/contracts/fixtures/agent-notices.json", AgentNoticeVerification.TheSharedCasesHold);
```

- [ ] **Step 2:** Port the Mac change line for line, keeping the existing C# patterns (caps, `Update`, `AddSteer`, `L`/`Locale`).
- [ ] **Step 3:** Run the text checks, then commit with: `(windows) a background sub-agent's end shows in the Mighty views as on the Mac: its block settles from its notification, its final report becomes its result, and Claude's reply is one 작업 알림 block` + Co-Authored-By.

---

### Task 3: Help (four languages)

**Files:** `docs/help/content/{ko,en,zh,ja}/` — the page that explains 작업 알림 / background work (find it with `grep -rln "taskNotice\|작업 알림" docs/help/content/ko`).

- [ ] **Step 1:** Next to the existing 작업 알림 explanation, add one sentence per language. Background sub-agents behave the same way: their block turns 완료, 실패 or 중지 when they end; their final report becomes the block's result; and Claude's answer to it appears under a 작업 알림 block. Use `{{ui:…}}` keys for UI names, as the page already does.
- [ ] **Step 2:** Run `node scripts/help/build.mjs --check` and `node scripts/check-locales.js --check`, then commit with: `(docs) the help (four languages) says a background sub-agent's end shows as a 작업 알림 like a background command's` + Co-Authored-By.

## Verification when the user asks
- Mac: `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-an --filter '\.(AgentNoticeTests|ExecutionGraphTracker[A-Za-z]*Tests)/'`.
- Windows: `bash ~/Work/.mc-tools/windows-core-checks/core-checks.sh`.
- A device check: start a background sub-agent in a Claude pane and watch the Mighty view.
