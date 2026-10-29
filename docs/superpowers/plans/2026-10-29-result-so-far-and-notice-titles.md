# Result So Far and Notice Titles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
- A request whose result is in, but whose background work still runs, titles its result "현재까지의 결과 · 백그라운드 작업 {count}개 기다리는 중". With no work left it stays "최종 결과".
- Every answer that today becomes an "알림 이후" section (or a "위임 알림 이후" section) opens its own notice run instead.
- Every notice run is titled with the work it reports on, e.g. "백그라운드 작업 완료 · 코드 검토 에이전트".
- This applies on Mac, Windows (web window and WinUI fallback) and the phone.

**Architecture:**
- **Result title:** one pure rule per platform picks it from the run status and the existing waiting count (`backgroundWaiting` / `BackgroundWaiting`). Every view calls that rule.
- **Tracker:** the graph tracker remembers what opened each notice run (`Opener`: work, message, delegation) and re-titles the run's main node as copies and lines join it. A notification-origin answer with no run to go to opens one; it never extends the request's result.
- **Projection:** the run projection carries the main title into a new optional `MightyGraphRun.noticeTitle`.
- **Phone wire:** it gains `backgroundWaiting?` on runs and structure items. The notice title rides in the existing `title` fields.

**Tech Stack:** Swift (MightyCore, MightyClaude), C# (Core, WinUI host), React/TS (`native/windows/web`), Expo RN (`mobile/`).

**Spec:** `docs/superpowers/specs/2026-10-29-result-so-far-and-notice-titles-design.md`

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
- **Frozen engine:**
  - Do not touch `native/macos/Sources/MightyCore/Styles/**` (frozen engine v7).
  - `RunSession.withoutNoticeRuns` keeps feeding the frozen engine without notice runs. Message and delegation runs are notice runs too.
- **Commits:**
  - Subjects are `(area) what the user now gets`.
  - Each message ends with exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - `git config user.email` is `11283764+athens96@users.noreply.github.com`.
  - Never commit `.ouroboros_eval_artifact.md`.
- **Locale keys:** add them to `locales/{ko,en,zh,ja}.json`, then copy the files byte for byte to `native/macos/Sources/MightyCore/Resources/Locales/` and `mobile/src/locales/`. Run `node scripts/check-locales.js`, which rewrites `docs/i18n.md`.
  - No hard-coded Korean.
  - `scripts/korean-literal-budget.json` only goes down.

  | Key | ko | en | zh | ja |
  | --- | --- | --- | --- | --- |
  | `graph.block.resultSoFar` | 현재까지의 결과 · 백그라운드 작업 {count}개 기다리는 중 | Result so far · waiting on {count} background tasks | 目前的结果 · 正在等待 {count} 个后台任务 | これまでの結果 · バックグラウンド作業 {count} 件を待機中 |
  | `graph.background.messageRun` | 하위 에이전트 메시지 | Sub-agent message | 子代理消息 | サブエージェントのメッセージ |
  | `graph.background.noticeRunTitle` | {kind} · {name} | {kind} · {name} | {kind} · {name} | {kind} · {name} |
  | `graph.background.noticeRunMore` | {first} 외 {count}개 | {first} and {count} more | {first} 及另外 {count} 项 | {first} ほか {count} 件 |

  - Remove `graph.result.afterNotice` once no source uses it (end of Task 2).
- **Result title rule:**
  - error/failed → `graph.block.resultError`
  - stopped/cancelled/interrupted → `graph.block.resultStopped`
  - otherwise `waiting > 0` → `graph.block.resultSoFar` with `{count}`
  - else `graph.block.result`
  - `waiting` is always the existing `backgroundWaiting` count; never count anew.
- **Notice title rule:**
  - The title is `{kind} · {name}`.
  - `kind`:
    - work → `graph.background.noticeRun`
    - message → `graph.background.messageRun`
    - delegation → `graph.block.delegationNotice`
  - A run holding copies is always work. Its names are the copies' titles, or the input when a title is empty.
  - Without copies, the names are the opener's: a known sender's block title, or none.
  - Each name keeps its first line only. A name longer than 32 characters becomes its first 31 characters plus "…".
  - Several names: `noticeRunMore` with `{first}` and `{count}` = names − 1. Equal names are counted, not merged.
  - No names: the kind alone.
- **Unchanged:**
  - The user's 다음 실행 steer path (`graph.result.afterSteer`, 중간 요청 이후), `withNoticeSection` and `noticeSectionMark`.
  - The 위임 알림 steer block and its label in the original run, and delegation delivery.
  - The 0.4.46 copy rules (`copiedIn`, `mergeTurn`).
- **Saved data:** nothing is migrated. Old results keep their "알림 이후" text. Old notice runs without `noticeTitle` read "백그라운드 작업 완료".

---

### Task 1: Contracts + Mac core + Mac app + mighty-mode doc

**Files:**
- **Contracts:**
  - `native/contracts/fixtures/agent-notices.json`
  - `native/contracts/graph-vectors.json` (vec-cs-5 notice main title)
- **Locales:** the four `locales/*.json` files and their copies (see Global Constraints).
- **Mac core (MightyCore):**
  - `BackgroundNoticeRuns.swift`
  - `MightyGraph.swift`: `MightyGraphRun.noticeTitle`, `MightyGraphSupport.resultTitle`, and the `recordGraph` main-node branch ~509
  - `ExecutionGraphTracker.swift`: `steer` ~96, the notice `result` branch ~330-357, `settleTasks` ~654, `copy` ~758, `openNoticeRun` ~800, `clearNoticeTurn` ~813, `settleHandbacks` ~821
  - `MightyStructure.swift` (`Item.noticeTitle`, `Item.backgroundWaiting`, set ~441)
  - `MightyTimeline.swift` (`Group.noticeTitle`)
  - `Remote/MobileRemoteSupport.swift`: `blocks` ~288, `runs` ~323, `digest` ~355
  - `Remote/MobileRemoteModels.swift`: `MobileMightyRun.backgroundWaiting`
  - `Remote/MobileStructure.swift`: `Item.backgroundWaiting` and the notice `title` ~176/270
- **Mac app (MightyClaude):**
  - `MightyGraphView.swift`: ~605, 635, 1039, 1108, 1172
  - `MightyStructureView.swift`: ~745-756, 954
- **Docs:** `docs/mighty-mode.md`, lines ~10, 17, 21-22 and 29.
- **Tests:**
  - `native/macos/Tests/MightyCoreTests/AgentNoticeTests.swift`
  - `BackgroundNoticeRunsTests.swift`
  - `ExecutionGraphTests.swift` (delegation case next to `aSteerAfterTheTurnFollowsInTheResult`)
  - `MobileRemoteExtensionTests.swift`
  - `MobileRemoteStructureTests.swift`

**Interfaces (produces):**
```swift
extension BackgroundNoticeRuns {
    /// What opened a notice run; it names the run's kind while the run holds no copied block.
    public enum Opener: String, Sendable { case work, message, delegation }
    public static let nameLimit = 32
    /// "백그라운드 작업 완료" — what was `title`; renamed so `title(kind:names:)` reads plainly.
    public static var genericTitle: String { L("graph.background.noticeRun") }
    public static func kind(_ opener: Opener) -> String
    /// "<kind> · <name>", "<kind> · <first> 외 <n>개", or "<kind>" alone.
    public static func title(kind: String, names: [String]) -> String
    /// A notice run's heading: its own title, or the generic one (older saves).
    public static func heading(_ run: MightyGraphRun) -> String
}
extension MightyGraphSupport {
    public static func resultTitle(status: String, waiting: Int) -> String
}
// MightyGraphRun.noticeTitle: String?   (Codable key "noticeTitle", trailing init parameter defaulting to nil)
// MightyStructure.Item.noticeTitle: String? = nil, backgroundWaiting: Int = 0
// MightyTimeline.Group.noticeTitle: String? = nil
// MobileMightyRun.backgroundWaiting: Int?   MobileStructure.Item.backgroundWaiting: Int?
```

- [ ] **Step 1: Locales.** Add the four keys (see Global Constraints), copy the files and run `node scripts/check-locales.js`. It fails on unused keys until Step 4 uses them; that is expected mid-task.

- [ ] **Step 2: Fixture.** Edit `agent-notices.json`.
  - **Comment:** document the new keys:
    - `noticeRunTitles`: every notice run's main title, in n order, read in ko.
    - `noticeRunOutputs`: every notice run's main output, null for none.
    - `noticeTitles`: the pure title rule.
    - `resultTitles`: the pure result rule.
  - **Remove** `mainNoticeSections` from "mid-run peer message, then hand-back, then notification". Change that case's expectations:
    ```json
    "noticeRuns": 2, "noticeRunChildren": [0, 1],
    "noticeRunOutput": "Noted the progress.", "noticeRunInput": "Halfway: two of five pages read.",
    "noticeRunTitles": ["하위 에이전트 메시지 · page-reader", "백그라운드 작업 완료 · page-reader"]
    ```
  - **"a hand-back from a sender this session does not know is ignored":** change its expectations:
    ```json
    "noticeRuns": 1, "noticeRunChildren": [0], "noticeRunOutput": "That report is not from my agent.",
    "noticeRunTitles": ["하위 에이전트 메시지"]
    ```
  - **Existing cases:** add `noticeRunTitles`:
    - "notice run: one command notification, then its reply" → `["백그라운드 작업 완료 · Lint the code"]`
    - "notice run: two notifications in one message, then one reply" → `["백그라운드 작업 완료 · Build the app 외 1개"]`
    - "background agent: hand-back by agent ID, then its notification" → `["백그라운드 작업 완료 · docs-surveyor"]`
  - **New stream** "a notification for work this run never started opens a 백그라운드 작업 완료 run". Its events:
    - A background Bash `toolu_FAKEcmd0901` titled "Watch the logs". Its launch acknowledgement names task `c9d0e1f2a`.
    - Assistant text "Watching.", then the root `result`.
    - A root `user` message with origin `task-notification`, holding `<task-notification><task-id>0f1e2d3c4</task-id><status>completed</status><summary>Background command "Old build" completed (exit code 0)</summary></task-notification>`.
    - Assistant text "The old build finished.", then a `result` with origin `task-notification` and result "The old build finished.".

    Its expectations:
    ```json
    "agentKind": "task", "agentState": "running", "noticeBlocks": 0, "mainOutputContains": ["Watching."],
    "noticeRuns": 1, "noticeRunChildren": [0], "noticeRunOutput": "The old build finished.",
    "noticeRunInput": "Background command \"Old build\" completed (exit code 0)", "noticeRunTitles": ["백그라운드 작업 완료"]
    ```
  - **New stream** "a running agent's message after the turn opens a 하위 에이전트 메시지 run". Its events:
    - Launch a background agent `toolu_FAKEagent0902` named "scout" (agentId `b2c3d4e5f6a7b8c9d`).
    - Assistant text "Scouting.", then `result`.
    - A peer `user` with `<agent-message from="b2c3d4e5f6a7b8c9d">Found the config.</agent-message>`.
    - Assistant text "Good.", then a `result` with origin `peer`.

    Its expectations:
    ```json
    "agentKind": "agent", "agentState": "running", "noticeBlocks": 0, "agentLog": ["Found the config."],
    "noticeRuns": 1, "noticeRunChildren": [0], "noticeRunOutput": "Good.", "noticeRunInput": "Found the config.",
    "noticeRunTitles": ["하위 에이전트 메시지 · scout"]
    ```
  - **New top-level sections:**
    ```json
    "noticeTitles": [
      { "name": "no names", "kind": "graph.background.noticeRun", "names": [], "expect": "백그라운드 작업 완료" },
      { "name": "one", "kind": "graph.background.noticeRun", "names": ["코드 검토 에이전트"], "expect": "백그라운드 작업 완료 · 코드 검토 에이전트" },
      { "name": "three", "kind": "graph.background.noticeRun", "names": ["Build", "Test", "Build"], "expect": "백그라운드 작업 완료 · Build 외 2개" },
      { "name": "long first line", "kind": "graph.background.messageRun", "names": ["abcdefghijklmnopqrstuvwxyz0123456789\nsecond"], "expect": "하위 에이전트 메시지 · abcdefghijklmnopqrstuvwxyz01234…" }
    ],
    "resultTitles": [
      { "name": "waiting", "status": "completed", "waiting": 2, "expect": { "key": "graph.block.resultSoFar", "count": 2 } },
      { "name": "settled", "status": "completed", "waiting": 0, "expect": { "key": "graph.block.result" } },
      { "name": "failed", "status": "error", "waiting": 1, "expect": { "key": "graph.block.resultError" } },
      { "name": "stopped", "status": "stopped", "waiting": 1, "expect": { "key": "graph.block.resultStopped" } }
    ]
    ```
  - **`graph-vectors.json`:** in vec-cs-5 (`background-task-run-in-background`), the `vec-cs-5:notice:1` main `title` becomes `"백그라운드 작업 완료 · Build"`.

- [ ] **Step 3: Tests.** In `AgentNoticeTests.streamsSettleTheBlockAndOpenNoticeRuns`:
  - Delete the `mainNoticeSections` loop.
  - Replace the fixed-title check with:
  ```swift
  // No answer to a notice extends the request's result any more.
  #expect(main.output?.contains(MightyGraphSupport.noticeSectionMark) != true, "\(name): notice section in the request's result")
  if let titles = expect["noticeRunTitles"] as? [String] { #expect(runs.map { $0.main?.title ?? "" } == titles, "\(name): titles") }
  if let outputs = expect["noticeRunOutputs"] as? [Any] {
      #expect(runs.map { $0.main?.output } == outputs.map { $0 as? String }, "\(name): outputs")
  }
  // in the per-run loop, instead of `notice.title == L("graph.background.noticeRun")`:
  let kinds = [BackgroundNoticeRuns.Opener.work, .message, .delegation].map(BackgroundNoticeRuns.kind)
  #expect(kinds.contains { notice.title == $0 || notice.title.hasPrefix($0 + " · ") } && notice.parentId == nil, "\(name): \(run.id) main")
  ```
  - Add two tests in the same file:
  ```swift
  @Test func noticeTitlesFollowTheFixture() throws {
      try LocaleOverride.$language.withValue(.ko) { () throws in
          for item in try #require(try Self.load()["noticeTitles"] as? [[String: Any]]) {
              let title = BackgroundNoticeRuns.title(kind: L(item["kind"] as? String ?? ""), names: item["names"] as? [String] ?? [])
              #expect(title == item["expect"] as? String, "\(item["name"] ?? "")")
          }
      }
  }
  @Test func resultTitlesFollowTheFixture() throws {
      try LocaleOverride.$language.withValue(.ko) { () throws in
          for item in try #require(try Self.load()["resultTitles"] as? [[String: Any]]) {
              let expect = try #require(item["expect"] as? [String: Any])
              let wanted = L(expect["key"] as? String ?? "", (expect["count"] as? Int).map { ["count": "\($0)"] } ?? [:])
              #expect(MightyGraphSupport.resultTitle(status: item["status"] as? String ?? "", waiting: item["waiting"] as? Int ?? 0) == wanted, "\(item["name"] ?? "")")
          }
      }
  }
  ```
  - `BackgroundNoticeRunsTests`:
    - `heading` falls back to `genericTitle` for a run without `noticeTitle`.
    - `noticeTitle` round-trips through Codable, and JSON without it decodes to nil.
    - `recordGraph` copies a notice main node's title into `noticeTitle` and leaves a request run's nil.
    - A request run with one running task reads `resultTitle(status:waiting:)` = 현재까지의 결과(1), and 최종 결과 once the task ends.
  - `ExecutionGraphTests`, next to `aSteerAfterTheTurnFollowsInTheResult`:
  ```swift
  /// A delegation notice handed in after the request's result opens a 위임 알림 notice run with the answer;
  /// the 위임 알림 block stays in the request and takes the reply; the request's result is untouched.
  @Test func aDelegationNoticeAfterTheTurnOpensItsOwnRun() throws {
      try LocaleOverride.$language.withValue(.ko) { () throws in
          var nodes: [ExecutionGraphNode] = []
          let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "deleg-after", graph: { nodes.append($0) }, graphInput: "Split the work")
          try send(assistant([["type": "text", "text": "Delegated."]], id: "main-1"), to: parser)
          try send(["type": "result", "subtype": "success", "is_error": false, "result": "Delegated."], to: parser)
          let text = "[Mighty Claude notice n-1] Your child c-1 reported revision 1. Read the report with child_status; merge it with merge when it is right."
          parser.steer(id: "n-1", text: text)
          try send(assistant([["type": "text", "text": "Merged the child."]], id: "main-2"), to: parser)
          try send(["type": "result", "subtype": "success", "is_error": false, "result": "Merged the child."], to: parser)
          let mainID = ExecutionGraphSupport.mainNodeID(runId: "deleg-after")
          #expect(try latest(nodes, mainID).output == "Delegated.")
          let notice = try latest(nodes, ExecutionGraphSupport.mainNodeID(runId: BackgroundNoticeRuns.runID(original: "deleg-after", index: 1)))
          #expect(notice.title == L("graph.block.delegationNotice")); #expect(notice.input == text)
          #expect(notice.output == "Merged the child."); #expect(notice.state == "completed")
          let steer = try latest(nodes, ExecutionGraphSupport.identifier("deleg-after", "steer:n-1"))
          #expect(steer.output == "Merged the child.")
      }
  }
  ```
  - Mobile tests:
    - `MobileMightySupport.runs` sets `backgroundWaiting` only when it is > 0.
    - A notice run's `title` and its `main` block title are `heading(run)`.
    - `digest` changes when the waiting count changes.
    - The structure `Item` carries both fields (`backgroundWaiting` and the notice `title`).
  - `aSteerAfterTheTurnFollowsInTheResult` must pass unchanged.

- [ ] **Step 4: Rules.**
  - In `BackgroundNoticeRuns.swift`, rename `title` to `genericTitle` and update every caller. Then add:
  ```swift
  public static func kind(_ opener: Opener) -> String {
      switch opener {
      case .work: L("graph.background.noticeRun")
      case .message: L("graph.background.messageRun")
      case .delegation: L("graph.block.delegationNotice")
      }
  }
  public static func title(kind: String, names: [String]) -> String {
      let lines = names.map { ($0.components(separatedBy: .newlines).first ?? "").trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
      guard let first = lines.first else { return kind }
      let short = first.count > nameLimit ? String(first.prefix(nameLimit - 1)) + "…" : first
      let name = lines.count == 1 ? short : L("graph.background.noticeRunMore", ["first": short, "count": "\(lines.count - 1)"])
      return L("graph.background.noticeRunTitle", ["kind": kind, "name": name])
  }
  public static func heading(_ run: MightyGraphRun) -> String {
      guard let title = run.noticeTitle?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty else { return genericTitle }
      return title
  }
  ```
  - In `MightyGraph.swift`, add `resultTitle`:
  ```swift
  /// The result card's title in every view and on the phone: how the request ended, or the result so far while
  /// background work it started still runs (`MightyGraphRun.backgroundWaiting`).
  public static func resultTitle(status: String, waiting: Int) -> String {
      switch status {
      case "error", "failed": L("graph.block.resultError")
      case "stopped", "cancelled", "interrupted": L("graph.block.resultStopped")
      default: waiting > 0 ? L("graph.block.resultSoFar", ["count": "\(waiting)"]) : L("graph.block.result")
      }
  }
  ```
  - Also in `MightyGraph.swift`:
    - Add `noticeTitle` to `MightyGraphRun`: the property, `CodingKeys`, and a trailing init parameter.
    - In `recordGraph`'s `if runs[index].noticeRun == true` branch, add `if !node.title.isEmpty { runs[index].noticeTitle = node.title }`.
    - In `normalized(_:restoring:budget:)`, cap it with the run-title budget the file already uses for strings.

- [ ] **Step 5: Tracker.** Make these changes in `ExecutionGraphTracker.swift`.
  - Add state:
  ```swift
  /// What opened each notice run and what it names while it holds no copy (`BackgroundNoticeRuns.title(kind:names:)`).
  private var noticeOpeners: [String: (opener: BackgroundNoticeRuns.Opener, names: [String], lines: [String])] = [:]
  /// Lines of root messages this turn no run took yet (in or before the request's own turn): a notice answer opens a run naming them.
  private var turnLines: [String] = []
  ```
  - `openNoticeRun(running:opener:names:lines:)`, with defaults `.work`, `[]`, `[]`:
    - Store `noticeOpeners[run]`.
    - Create the main node with `title: BackgroundNoticeRuns.title(kind: BackgroundNoticeRuns.kind(opener), names: names)` and `input: lines.joined(separator: "\n")`.
  - Replace the "request lines" tail of `copy(_:into:)` with `describe(run)`:
  ```swift
  /// A notice run's title and request lines: its opener's lines, then each copied block and how it ended. A run
  /// holding copies is named after them; one without is named by what opened it.
  private func describe(_ run: String) {
      let copies = (noticeCopies[run] ?? []).compactMap { nodes[$0] }
      let opened = noticeOpeners[run] ?? (opener: .work, names: [], lines: [])
      func name(_ copy: ExecutionGraphNode) -> String { copy.title.isEmpty ? copy.input ?? "" : copy.title }
      let lines = opened.lines + copies.map { copy in
          name(copy) + " · " + L(copy.state == "completed" ? "graph.block.taskNotice.completed" : copy.state == "error" ? "graph.block.taskNotice.failed" : "graph.block.taskNotice.stopped")
      }
      let title = copies.isEmpty ? BackgroundNoticeRuns.title(kind: BackgroundNoticeRuns.kind(opened.opener), names: opened.names)
                                 : BackgroundNoticeRuns.title(kind: BackgroundNoticeRuns.kind(.work), names: copies.map(name))
      update(ExecutionGraphSupport.mainNodeID(runId: run)) { $0.title = title; $0.input = lines.joined(separator: "\n") }
  }
  /// A root message about no block a notice run copies: a running agent's message, a message from a sender this run
  /// does not know, a notification for work this run never started, a delegation notice after the request's result.
  /// It joins the turn's open notice run, or opens that turn's run when it opens a turn; otherwise its line waits in
  /// `turnLines` for the turn's answer.
  private func joinTurn(_ opener: BackgroundNoticeRuns.Opener, name: String?, line: String?) {
      guard provider == "claude" else { return }
      let line = line?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty
      if let run = noticeTurn ?? mergeTurn {
          var opened = noticeOpeners[run] ?? (opener: opener, names: [], lines: [])
          if let name { opened.names.append(name) }
          if let line { opened.lines.append(line) }
          noticeOpeners[run] = opened; describe(run)
      } else if turnEnded, steeredTurn == nil {
          noticeTurn = openNoticeRun(running: true, opener: opener, names: name.map { [$0] } ?? [], lines: turnLines + (line.map { [$0] } ?? []))
          turnLines = []
      } else if let line, turnLines.count < 16 { turnLines.append(line) }
  }
  ```
  If `nilIfEmpty` is not in MightyCore, write the guard inline.
  - `steer(id:text:)`:
  ```swift
  guard addSteer(id: id, text: text), turnEnded, steeredTurn == nil else { return }
  // A delegation notice's answer is its own 위임 알림 run's; the block stays under the request and takes the reply.
  if MightyGraphSupport.isDelegationNotice(text) { joinTurn(.delegation, name: nil, line: text) }
  else { steeredTurn = L("graph.block.steer") }
  ```
  - `settleTasks`: the unmatched `guard … else` now does `if notice { joinTurn(.work, name: nil, line: Self.tag(body, "summary") ?? Self.tag(body, "status")) }; continue`.
  - `settleHandbacks`:
    - An unknown sender with a non-empty body: `if notice { joinTurn(.message, name: nil, line: message.body) }; continue`.
    - A known sender's running message: after the log append, `if notice { joinTurn(.message, name: node.title.isEmpty ? nil : node.title, line: message.body) }`.
  - Notice `result` branch:
    - Capture `let absorbed = absorbedRun, lines = turnLines` before `clearNoticeTurn()`.
    - Keep the `run` and `merged` paths.
    - Replace the `withNoticeSection(… afterNotice …)` line with:
  ```swift
  // A notice answer no notice run took never extends the request's result: it is the answer of the run its notices
  // were taken into, or of a run of its own.
  guard !answer.isEmpty else { return }
  if let absorbed { update(ExecutionGraphSupport.mainNodeID(runId: absorbed)) { $0.output = answer }; return }
  let peer = (value["origin"] as? [String: Any])?["kind"] as? String == "peer"
  guard let opened = openNoticeRun(running: false, opener: peer ? .message : .work, lines: lines) else { return }
  update(ExecutionGraphSupport.mainNodeID(runId: opened)) { $0.output = answer }
  if failed { setNoticeState(opened, "error") }
  ```
  - `clearNoticeTurn()` also empties `turnLines`. The request's own root `result` (`absorbedRun = nil`) empties `turnLines` too.
  - Leave `afterSteer` alone. It now serves user steers only.

- [ ] **Step 6: Views and phone conversion (Mac).**
  - **Result titles:** every result title becomes `MightyGraphSupport.resultTitle(status: run.status, waiting: run.backgroundWaiting)`:
    - `MightyGraphView` `.result` ~635, keeping the icon choice by status
    - `timelineResult` ~1172, using its `run`
    - `MightyStructureView.resultTitle` ~745, using `item.status` and `item.backgroundWaiting`
    - the `foldableMarkdown` name ~756
  - **Notice headings:** every notice heading becomes `BackgroundNoticeRuns.heading(run)` or the carried `noticeTitle ?? genericTitle`:
    - `MightyGraphView` ~605, 1039 and 1108, through `MightyTimeline.Group.noticeTitle`
    - `MightyStructureView` ~954, through `MightyStructure.Item.noticeTitle`, which the builder ~441 sets with `backgroundWaiting: run.backgroundWaiting`
  - **Phone conversion:**
    - `blocks`: the main title is `heading(run)`.
    - `runs`: a notice run gets `title: heading(run)`, and `mobile.backgroundWaiting = run.backgroundWaiting > 0 ? run.backgroundWaiting : nil`.
    - `digest`: `hasher.combine(run.backgroundWaiting); hasher.combine(run.noticeTitle)`.
    - `MobileStructure`: `let name = source.noticeRun ? source.noticeTitle : title(source.runID)`, and `backgroundWaiting: item.backgroundWaiting > 0 ? item.backgroundWaiting : nil`.
  - Grep afterwards: `git grep -n "BackgroundNoticeRuns.title\b\|graph.result.afterNotice" -- native/macos` shows no source hits.

- [ ] **Step 7: Doc.** In `docs/mighty-mode.md`:
  - Line 10: replace the "알림 이후" sentences with the new-run rule and the `현재까지의 결과` title.
  - Line 17: the main block title is now `{종류} · {이름}`, with the rule.
  - Line 21: delegation answers now open a 위임 알림 run, and the steer block stays.
  - Line 29: 위임 알림 이후 → its own run. 중간 요청 이후 is unchanged.

- [ ] **Step 8: Check and commit.**
  - Run check-locales, check-design-tokens, check-style-freeze, and `git diff --stat -- native/macos/Sources/MightyCore/Styles` (expect empty).
  - Commit. Subject: `(mac) a result waiting on background work reads 현재까지의 결과 with the count, and each answer to a notice opens its own diagram named after the work`

### Task 2: Windows Core + WinUI + web

**Files:**
- **Core** (`native/windows/MightyClaude.Core`):
  - `BackgroundNoticeRuns.cs` (`Opener`, `Kind`, `Title(kind, names)`, `Heading(run)`, `GenericTitle`)
  - `MightyGraph.cs`: `NoticeTitle`, the bounds copy ~300, the clone ~67
  - `ExecutionGraphTracker.cs`: `Steer` ~177, the result branch ~438-462, `SettleTasks`, `SettleHandbacks`, `Copy`, `OpenNoticeRun` ~781, `ClearNoticeTurn`, and `BuildRunLocked` ~1019, which sets `NoticeTitle = notice ? main.Title : null`
  - `MightyGraphViewModel.cs`: `MightyGraphBlockModel.ResultTitle(string runStatus, int waiting = 0)` ~290, with the result block ~374 passing `MightyGraphSupport.BackgroundWaiting(run)`, and the notice title ~330
  - `MightyTimeline.cs` ~75, `MightyStructure.cs` (`Item` `NoticeTitle`, `BackgroundWaiting`)
  - `Remote/MobileMighty.cs`: the `MobileMightyRun` record gains `int? BackgroundWaiting = null`; main title ~120, `Runs` ~143, `Digest` ~201
  - `Remote/MobileStructure.cs`: `Item` gains `int? BackgroundWaiting`, plus the notice title ~85
- **WinUI:**
  - `MainWindow.MightyTimeline.cs` ~196, 510
  - `MainWindow.MightyStructure.cs` ~1066-1073, 1149
  - `MainWindow.WebShell.Mighty.cs`:
    - `WebTimelineGroup` and `WebStructureCard` gain `string ResultTitle`
    - caption ~396
- **Web:**
  - `native/windows/web/src/mighty/Timeline.tsx` ~142
  - `Structure.tsx` ~180-184
  - the matching types in `src/mighty/types.ts`
  - `src/store/models.ts` (`noticeTitle?: string` on `MightyGraphRun`)
- **Tests:**
  - `MightyClaude.Core.Tests/AgentNoticeVerification.cs` (same fixture keys, drop `mainNoticeSections` ~119)
  - `BackgroundNoticeRunsVerification.cs` (~116, 197 title asserts)
  - `MightyTimelineVerification.cs` ~79
  - `SessionHistoryVerification.cs` (delegation case next to `ASteerAfterTheTurnFollowsInTheResult` ~135, registered in `Verification.cs` ~1089)
  - web `src/mighty/notice.test.ts` or a new `src/mighty/resultTitle.test.ts`

**Interfaces:** the C# mirror of Task 1 with the same names in PascalCase. `ResultTitle(status, waiting)` takes the place of `resultTitle(status:waiting:)`. The web reads `group.resultTitle` and `card.resultTitle`, which the host fills from `ResultTitle(run.Status, BackgroundWaiting(run))`.

- [ ] **Step 1: Core tests.**
  - Port the Task 1 test changes:
    - `AgentNoticeVerification` reads `noticeRunTitles`, `noticeRunOutputs`, `noticeTitles` and `resultTitles`.
    - It asserts that no stream's request output contains `MightyGraphSupport.NoticeSectionMark`.
    - It accepts any of the three kind prefixes for a notice main title.
  - Add the delegation case to `SessionHistoryVerification` with the same steps and expectations as the Mac test.
  - Update the fixed-title asserts in `BackgroundNoticeRunsVerification` and `MightyTimelineVerification` to the named titles the fixtures now produce.
  - `ASteerAfterTheTurnFollowsInTheResult` stays green.
  ```csharp
  foreach (var item in Fixture.GetProperty("noticeTitles").EnumerateArray())
  {
      var names = item.GetProperty("names").EnumerateArray().Select(n => n.GetString() ?? "").ToList();
      var title = BackgroundNoticeRuns.Title(Locale.Get(item.GetProperty("kind").GetString()!), names);
      Check(title == item.GetProperty("expect").GetString(), $"notice title: {item.GetProperty("name").GetString()}");
  }
  ```
- [ ] **Step 2: Core.**
  - Port `BackgroundNoticeRuns`, `NoticeTitle`, `ResultTitle(status, waiting)` and the tracker changes from Task 1 Steps 4-5. Keep the Mac's comments, rules and order.
  - The C# `Title`:
  ```csharp
  public static string Title(string kind, IReadOnlyList<string> names)
  {
      var lines = names.Select(n => n.Split('\n')[0].Trim()).Where(n => n.Length > 0).ToList();
      if (lines.Count == 0) return kind;
      var first = new System.Globalization.StringInfo(lines[0]);
      var shortName = first.LengthInTextElements > NameLimit ? first.SubstringByTextElements(0, NameLimit - 1) + "…" : lines[0];
      var name = lines.Count == 1 ? shortName : Locale.Get("graph.background.noticeRunMore", new Dictionary<string, string> { ["first"] = shortName, ["count"] = (lines.Count - 1).ToString(CultureInfo.InvariantCulture) });
      return Locale.Get("graph.background.noticeRunTitle", new Dictionary<string, string> { ["kind"] = kind, ["name"] = name });
  }
  ```
  - `ResultTitle`:
  ```csharp
  public static string ResultTitle(string runStatus, int waiting = 0) => runStatus switch
  {
      "error" or "failed" => Locale.Get("graph.block.resultError"),
      "stopped" or "cancelled" or "interrupted" => Locale.Get("graph.block.resultStopped"),
      _ when waiting > 0 => Locale.Get("graph.block.resultSoFar", new Dictionary<string, string> { ["count"] = waiting.ToString(CultureInfo.InvariantCulture) }),
      _ => Locale.Get("graph.block.result"),
  };
  ```
  - The notice title sites use `BackgroundNoticeRuns.Heading(run)` or the carried `NoticeTitle`:
    - `MightyGraphViewModel` ~330
    - `MightyTimeline` ~75
    - `MobileMighty` ~120
  - Phone wire: notice runs and items carry `Title = Heading(run)`. `BackgroundWaiting` is set only when > 0. The digest mixes in both fields.
- [ ] **Step 3: Host and web.**
  - In WinUI, every result title calls `ResultTitle(run.Status, MightyGraphSupport.BackgroundWaiting(run))`, and every notice title calls `Heading(run)`. These are the fallback views and the web copies:
    - `WebTimelineGroup.ResultTitle`
    - `WebStructureCard.ResultTitle`, set for result cards
    - the caption ~396
  - Web `Timeline.tsx`:
  ```tsx
  <span className="timeline-result-title">{group.resultTitle ?? t('graph.block.result')}</span>
  ```
  - Web `Structure.tsx` `ResultTitle`: keep the error and stopped branches. Otherwise use `card.resultTitle ?? t('graph.block.result')`.
  - Vitest: a group or card with `resultTitle` shows it, and one without shows 최종 결과.
- [ ] **Step 4: Remove the key.**
  - `git grep -n "graph.result.afterNotice" -- native mobile/src ':!*.json'` must be empty.
  - Remove the key from the four locales and their copies, then run `node scripts/check-locales.js`. It regenerates `docs/i18n.md`.
  - Run web `npx tsc --noEmit -p .` if cheap.
- [ ] **Step 5: Commit.** Subject: `(windows) a result waiting on background work reads 현재까지의 결과 with the count, and each answer to a notice opens its own diagram named after the work, in the web window and on the phone`

### Task 3: Phone + mobile-remote doc

**Files:**
- `mobile/src/api/types.ts`: `MobileMightyRun` and `StructureItem` gain `backgroundWaiting?: number`. Each `title` doc notes that it holds a notice run's heading.
- `mobile/src/lib/mighty.ts`: `parseRun` ~287, `runHeading` ~119, a new `resultTitle`
- `mobile/src/components/mighty-blocks.tsx`: `ResultCard` ~290-305
- `mobile/src/lib/structure.ts`: the parse ~140-170, `itemHeading` ~392, `resultHeading` ~399
- `mobile/src/lib/pipeline-board.ts`: `Board.noticeTitle`, set ~105; `boardPosition` ~266 and `boardTitle` ~278
- Tests: `mobile/src/__tests__/mighty.test.ts`, `structure.test.ts`, `pipeline-board.test.ts`
- Doc: `docs/mobile-remote.md`, around ~194-199 (the run fields) and ~281 (알림 실행)

**Interfaces (consumes):** Task 1's wire: `backgroundWaiting?` on runs and items, and the notice heading in `title`.

- [ ] **Step 1: Tests first.**
  ```ts
  it('reads a positive waiting count and nothing else', () => {
    const run = (backgroundWaiting: unknown) => normalizeMighty({ style: 'cli', runs: [{ id: 'r1', input: 'x', status: 'completed', backgroundWaiting, blocks: [] }] })!.runs[0]!;
    expect(run(2).backgroundWaiting).toBe(2);
    for (const bad of [0, -1, 1.5, '2', null]) expect(run(bad).backgroundWaiting).toBeUndefined();
  });
  it('titles the result so far while background work waits', () => {
    expect(resultTitle('completed', 2)).toBe(t('graph.block.resultSoFar', { count: 2 }));
    expect(resultTitle('completed', undefined)).toBe(t('graph.block.result'));
    expect(resultTitle('error', 2)).toBe(t('graph.block.resultError'));
  });
  it('names a notice run by its title, else the generic one', () => {
    expect(runHeading(run({ id: 'r1:notice:1', noticeRun: true, title: '백그라운드 작업 완료 · lint' }), 1)).toBe('백그라운드 작업 완료 · lint');
    expect(runHeading(run({ id: 'r1:notice:1', noticeRun: true }), 1)).toBe(t('graph.background.noticeRun'));
  });
  ```
  Add the same checks for `itemHeading`, `resultHeading` (with `backgroundWaiting`) and `boardTitle`/`boardPosition` (with `noticeTitle`) in the structure and pipeline tests.
- [ ] **Step 2: Code.**
  ```ts
  /** The result card's title: how the run ended, or the result so far while background work it started still runs (the Mac's `MightyGraphSupport.resultTitle`). */
  export function resultTitle(status: string, waiting: number | undefined): string {
    if (status === 'error') return t('graph.block.resultError');
    if (status === 'stopped') return t('graph.block.resultStopped');
    return waiting !== undefined && waiting > 0 ? t('graph.block.resultSoFar', { count: waiting }) : t('graph.block.result');
  }
  // parseRun and the structure parse:
  const waiting = raw.backgroundWaiting;
  if (typeof waiting === 'number' && Number.isInteger(waiting) && waiting > 0) run.backgroundWaiting = Math.min(waiting, 999);
  // runHeading / itemHeading:
  if (run.noticeRun) return run.title?.trim() || t('graph.background.noticeRun');
  ```
  - `ResultCard`: a completed run with `backgroundWaiting` shows `resultTitle(run.status, run.backgroundWaiting)` without the "✓". The other cases keep today's `phone.blocks.finalResult` heading.
  - `resultHeading(item)` becomes `resultTitle(item.status, item.backgroundWaiting)`.
  - The pipeline board copies `item.title` into `noticeTitle` for notice boards. `boardPosition` and `boardTitle` return `board.noticeTitle ?? t('graph.background.noticeRun')`.
  - Use theme tokens only; no new raw spacing.
- [ ] **Step 3: Doc.** In `docs/mobile-remote.md`:
  - `backgroundWaiting` (optional, positive integer) on runs and structure items, and its meaning.
  - A notice run's `title` and `main` block title carry its heading.
  - Add the old/new host and phone table from the spec §5.
- [ ] **Step 4: Check and commit.**
  - Run `npm run -s typecheck` if cheap, then check-locales and check-design-tokens.
  - Commit. Subject: `(phone) the phone titles a result still waiting on background work 현재까지의 결과 with the count, and names each notice diagram after its work`

### Task 4: Help in four languages + final sweep

**Files:** `docs/help/content/{ko,en,zh,ja}/`:
- `agent-pane.md`: the result card paragraph, ko line ~143
- `approvals.md`: the `#background` section, ko lines ~73-74
- `delegation.md`: the `#notices` section, the bullet about a running Claude parent (ko ~134)

- [ ] **Step 1:** Rewrite the wording in all four languages, describing it in prose. Use `{{ui:…}}` tokens only for keys without placeholders.
  - **agent-pane.md:** while the request's background work still runs, the result card reads "현재까지의 결과 · 백그라운드 작업 N개 기다리는 중". When the last one ends it becomes {{ui:graph.block.result}}. Every answer Claude gives to a notice is a new diagram, never an addition to the request's result.
  - **approvals.md:**
    - The diagram is titled after the finished work, e.g. "{{ui:graph.background.noticeRun}} · 코드 검토 에이전트", or "… 외 N개" for several.
    - A message a sub-agent sends while it is still running gets its own {{ui:graph.background.messageRun}} diagram with Claude's answer.
  - **delegation.md:** a notice that reaches a running parent after its answer still shows the orange {{ui:graph.block.delegationNotice}} block. Claude's answer to it is its own {{ui:graph.block.delegationNotice}} diagram.
- [ ] **Step 2: Sweep.**
  - `git grep -n "알림 이후\|afterNotice" -- docs native mobile/src locales`: only `afterSteer` (중간 요청 이후), old-save notes and the 0.4.46 spec and plan may remain.
  - Run `node scripts/help/build.mjs --check`, check-locales, check-design-tokens and check-style-freeze.
- [ ] **Step 3: Commit.** Subject: `(help) the help says a result waiting on background work is the result so far, and that each answer to a notice is its own named diagram`
