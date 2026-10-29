# Queue Run Now Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
- The queue's single **다음 실행** button goes away. Every human queued row gets **바로 실행** right before its X. The first held row gets it before its pin, delivering all N held items; the other held rows get none.
- A row that cannot run now keeps its button slot. The button is drawn disabled, and its tooltip gives the reason: the current task, held items first, or a CLI update.
- 바로 실행 runs that row now in a new worktree child pane of the pane's agent, the ⌘Enter / Ctrl+Enter path. A right-click (Control-click on the Mac) picks another agent.
- Where no worktree can be made, it runs the row now in this pane when this pane can take it: started (idle pane), or steered into a run that only waits on background work. The tooltip says which.
- Mac, Windows (web window and WinUI fallback) and the phone. The phone gets a per-row `runNow` and a new `…/queue/{itemId}/run-now` route; old phones keep `/queue/run-next`.

**Architecture:**
- **Rule:** one pure decision per platform, `QueueRunNow.decide` (Mac MightyCore) / `QueueRunNow.Decide` (Windows Core).
  - It takes the row (its held index or nil, text, attachments) and a `Pane` summary (busy, update hold, `ComposerSecondSend`, `menuOnly`, background-only wait, held count).
  - It answers `worktree | agentMenu | here | steer | held | waitsForTurn | waitsForHeld | waitsForUpdate | hidden`. The `waitsFor…` answers are the disabled button with its reason.
  - Both read `native/contracts/fixtures/queue-run-now.json`.
- **Action:** Mac `AppStore.runQueuedNow(_:itemId:agent:stopping:own:)`, Windows `PaneView.RunQueuedNow(itemId, agent)`. They reuse today's paths:
  - `worktree` → `runInNewWorktree` with a new queue origin. A failure puts the row back at the head of the queue, never in the composer.
  - `here` → move the row to the head, then the queue's own start.
  - `steer` → `steerQueuedRow` by row id.
  - `held` → today's held release.
- **Phone wire:** `queued: [{ id, text, runNow?, runNowReason? }]`.
  - `runNow` is `"worktree" | "here" | "unavailable"`, and `runNowReason` is `"turn" | "held" | "update" | "agent"`, sent with `"unavailable"` only.
  - The phone draws `"unavailable"` dimmed and shows the reason in its own language.
  - A row without `runNow` (a non-first held row, or an old host) shows no button. An unknown value reads as absent.
  - New route `POST /m1/sessions/{id}/queue/{itemId}/run-now` → `{ protocol, ok, ran }`.

**Tech Stack:** Swift (MightyCore, MightyClaude), C# (Core, WinUI host), React/TS (`native/windows/web`), Expo RN (`mobile/`).

**Spec:** `docs/superpowers/specs/2026-10-29-queue-run-now-design.md`

## Global Constraints
- **Where and how:**
  - Work on main in `~/Work/MightyClaude`. Do not push. The repo is public: no personal paths, emails or IPs.
  - The Mac governs the Windows and phone ports. When Windows or the phone disagrees with the Mac, port the Mac rule.
  - Another agent may be committing Windows code on main. Before each task, check `git status --short --branch` and stage only this task's files by path (never `git add -A`).
- **Checks:** run builds and test suites only when the user asks, and they have not.
  - Implementers write the tests below but run only quick text checks:
    - `node scripts/check-locales.js`
    - `node scripts/check-design-tokens.js`
    - `bash scripts/check-style-freeze.sh`
    - `node scripts/help/build.mjs --check` (Task 4)
  - If cheap, also run web `cd native/windows/web && npx tsc --noEmit -p .` and phone `cd mobile && npm run -s typecheck`.
- **Frozen engine:** do not touch `native/macos/Sources/MightyCore/Styles/**` (frozen engine v7).
- **Commits:**
  - Subjects are `(area) what the user now gets`.
  - Each message ends with exactly `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - `git config user.email` is `11283764+athens96@users.noreply.github.com`.
  - Never commit `.ouroboros_eval_artifact.md`.
- **Locale keys:**
  - Add keys to `locales/{ko,en,zh,ja}.json`, then copy the files byte for byte to `native/macos/Sources/MightyCore/Resources/Locales/` and `mobile/src/locales/`. Run `node scripts/check-locales.js`, which rewrites `docs/i18n.md`.
  - The check fails on unused keys, so each task adds only the keys it uses. The texts for all four languages are in the spec's 문구 tables.
  - No hard-coded Korean. `scripts/korean-literal-budget.json` only goes down.

  | Task | Adds | Changes | Removes |
  | --- | --- | --- | --- |
  | 1 | `queue.runNow`, `queue.runNowWorktreeHelpMac`, `queue.runNowHereHelp`, `queue.runNowSteerHelp`, `queue.runNowHeldHelp`, `queue.runNowRefused`, `queue.runNowUnavailableHelp`, `queue.runNowAfterHeldHelp`, `queue.runNowUpdateHelp` | `queue.heldHelp`, `queue.heldNotRemovable` | – |
  | 2 | `queue.runNowWorktreeHelp` | – | – |
  | 3 | `phone.queue.runNowLabel`, `phone.queue.runNowWorktreeHint`, `phone.session.toast.runNowWorktree`, `phone.queue.runNowAgentOnComputer` | – | – |
  | 4 | – | – | `queue.runNext`, `queue.runNextHelp` |
- **The rule:** evaluate these in order.
  1. A held row: the first one (`heldIndex == 0`) → `held`; any other → `hidden`.
  2. An idle pane (not running, not starting, not held for a CLI update) → `here`.
  3. Held for a CLI update → `waitsForUpdate`.
  4. The row has words (trimmed text not empty) and `secondSend == .worktree` → `worktree`.
  5. The row has words and `menuOnly` → `agentMenu`.
  6. `waitsOnBackgroundOnly` (local Claude, not stopping, turn ended, background work running) and the row has no attachments: `steer` when nothing is held, else `waitsForHeld`.
  7. Otherwise `waitsForTurn`.

  The phone wire value:

  | Answer | `runNow` | `runNowReason` |
  | --- | --- | --- |
  | `worktree` | `"worktree"` | – |
  | `here`, `steer`, `held` | `"here"` | – |
  | `waitsForTurn` | `"unavailable"` | `"turn"` |
  | `waitsForHeld` | `"unavailable"` | `"held"` |
  | `waitsForUpdate` | `"unavailable"` | `"update"` |
  | `agentMenu` | `"unavailable"` | `"agent"` |
  | `hidden` | – | – |
- **Tooltips:**

  | Answer | Tooltip key |
  | --- | --- |
  | `worktree` | `queue.runNowWorktreeHelpMac` (Mac) / `queue.runNowWorktreeHelp` (Windows) |
  | `agentMenu` | `queue.worktreeMenuHintMac` |
  | `here` | `queue.runNowHereHelp` |
  | `steer` | `queue.runNowSteerHelp` |
  | `held` | `queue.runNowHeldHelp` with `{count}` |
  | `waitsForTurn` | `queue.runNowUnavailableHelp` |
  | `waitsForHeld` | `queue.runNowAfterHeldHelp` |
  | `waitsForUpdate` | `queue.runNowUpdateHelp` |

  - Disabled buttons carry the tooltip on a wrapper (SwiftUI, WinUI) or use `aria-disabled` (web), because a disabled control shows no tooltip.
  - A click on a `waitsFor…` button does nothing.
- **Own (natural submit):**
  - This device's click is the user's own, as 다음 실행 was. The phone's click is only followed on the Mac and Windows.
  - A worktree child started from the phone is never selected on the Mac or Windows.
- **Unchanged:**
  - ⌘Enter / Ctrl+Enter and the composer's worktree button and menu.
  - Enter queueing, the 16-row cap, and stop/error queue rules.
  - Delegation holding and release.
  - `graph.result.afterSteer`.
  - `/queue/run-next` with `mobileRunNextQueued` / `MobileRunNext` (old phones).

---

### Task 1: Contract + Mac core + Mac app + interaction docs

**Files:**
- **Contract:** `native/contracts/fixtures/queue-run-now.json` (new)
- **Locales:** the four `locales/*.json` files and their copies (see Global Constraints).
- **Mac core (MightyCore):**
  - `QueueRunNow.swift` (new)
  - `DelegationDelivery.swift`: `DelegationQueueRows.mobileItems` ~39. Remove `offersRunNext` ~53.
  - `Remote/MobileRemoteModels.swift`: `MobileQueuedItem` ~295, plus a new `MobileRunNowReply` beside `MobileOK` ~682
  - `Remote/MobileRemoteService.swift`: the delegate protocol ~41, the new route beside `run-next` ~1131
- **Mac app (MightyClaude):**
  - `AppStore.swift`: `steerQueuedRow` ~1088, new `queueRunNow` and `runQueuedNow`
  - `AppStore+Delegation.swift`:
    - `HeldSendOrigin` stays.
    - `runInNewWorktree` ~301 gains an origin.
    - `humanChildFailedToStart` ~354
    - new `putBackQueuedRow`
    - `createPane` ~604 (`quietChildParents`)
  - `AppStore+MobileRemote.swift`: the adapter ~180, `mobileItems` call ~469, new `mobileRunQueuedNow`
  - `QueuedInputsView.swift`
  - `SessionPaneView.swift`: the call ~679, `worktreeChoiceTitle` ~1012
- **Docs:**
  - `docs/interaction.md`, lines ~49, 55, 91
  - `docs/mighty-mode.md`, lines ~28-29
  - `native/contracts/fixtures/pending.json` `scrollRule` text (wording only)
- **Tests:**
  - `native/macos/Tests/MightyCoreTests/QueueRunNowFixtureTests.swift` (new)
  - `BackgroundQueuePolicyTests.swift` ~34-36
  - `DelegationDeliveryTests.swift` ~489-510
  - `HelpCaptureDelegationTests.swift` ~155
  - `MobileRemoteTests.swift` (fake ~127, new route test)

**Interfaces (produces):**
```swift
public enum QueueRunNow: String, Sendable, CaseIterable {
    case worktree, agentMenu, here, steer, held, waitsForTurn, waitsForHeld, waitsForUpdate, hidden
    public struct Pane: Sendable, Equatable { busy, heldForUpdate, secondSend: ComposerSecondSend, menuOnly, waitsOnBackgroundOnly: Bool; held: Int }
    public static func decide(heldIndex: Int?, text: String, attachments: Int, pane: Pane) -> QueueRunNow
    public var runs: Bool { get }                                     // worktree, agentMenu, here, steer, held
    public var wire: (runNow: String, reason: String?)? { get }       // see Global Constraints
}
// MobileQueuedItem.runNow: String?, runNowReason: String?   (omitted when nil; init(id:text:runNow: = nil, runNowReason: = nil))
// DelegationQueueRows.mobileItems(held:queued:runNow: (String) -> QueueRunNow = { _ in .hidden })
// MobileRunNowReply { protocol = 1, ok = true, ran: String }
// MobileHostDelegate.mobileRunQueuedNow(sessionId:itemId:) async throws -> String   (default in an extension: 404)
// AppStore.queueRunNow(_ session: RunSession, rowId: String, stopping: Bool = false) -> QueueRunNow
// AppStore.runQueuedNow(_ id: String, itemId: String, agent: String? = nil, stopping: Bool = false, own: Bool = true) async -> QueueRunNow?
```

- [ ] **Step 1: Locales.** Add Task 1's keys and change `queue.heldHelp` and `queue.heldNotRemovable`. All four languages are in the spec. Copy the files, then run `node scripts/check-locales.js`. It fails on unused keys until Step 5; that is expected mid-task.

- [ ] **Step 2: Fixture.** Write `native/contracts/fixtures/queue-run-now.json`.
  - `_comment` names the spec, the readers, and that `otherAgentReady` stands for "`menuOnly`'s choices hold one unblocked agent".
  - `rule` repeats the Global Constraints rule and wire table.
  - Each case: `{ name, row, pane, expect, wire }`.
    - `row.heldIndex` is null for a human row.
    - `wire` is `{ "runNow": …, "runNowReason"?: … }` or null.
  - Every case lists every field, so readers never default.
  ```json
  {
    "version": 1,
    "_comment": "바로 실행 on a queued row (docs/superpowers/specs/2026-10-29-queue-run-now-design.md). Read by the Mac (QueueRunNowFixtureTests), Windows Core (QueueRunNowVerification) and the phone (queue.test.ts, `wire` only). secondSend is ComposerSecondSend.choose(kind, provider, mode, isChild, delegationOn, canHostWorktree); menuOnly is ComposerSecondSend.menuOnly with the same inputs and one unblocked other agent when otherAgentReady. delegationOn is false for a remote-workspace pane. waitsFor… answers are a disabled button whose tooltip gives the reason.",
    "rule": "held row: heldIndex 0 -> held, else hidden. Idle pane: here. Held for a CLI update: waitsForUpdate. Words + secondSend worktree: worktree. Words + menuOnly: agentMenu. waitsOnBackgroundOnly + no attachments: steer when held == 0, else waitsForHeld. Else waitsForTurn. wire: worktree -> {runNow: worktree}; here, steer, held -> {runNow: here}; waitsForTurn/waitsForHeld/waitsForUpdate/agentMenu -> {runNow: unavailable, runNowReason: turn/held/update/agent}; hidden -> null.",
    "cases": [
      { "name": "busy Claude pane in a git repo", "row": { "heldIndex": null, "text": "Fix the login bug", "attachments": 0 },
        "pane": { "busy": true, "heldForUpdate": false, "kind": "claude", "provider": "claude", "mode": "acceptEdits", "isChild": false, "delegationOn": true, "canHostWorktree": true, "otherAgentReady": true, "waitsOnBackgroundOnly": false, "held": 0 },
        "expect": "worktree", "wire": { "runNow": "worktree" } },
      { "name": "only waiting on background work, a worktree can be made: worktree wins over steer", "row": { "heldIndex": null, "text": "Fix the login bug", "attachments": 0 },
        "pane": { "busy": true, "heldForUpdate": false, "kind": "claude", "provider": "claude", "mode": "acceptEdits", "isChild": false, "delegationOn": true, "canHostWorktree": true, "otherAgentReady": true, "waitsOnBackgroundOnly": true, "held": 0 },
        "expect": "worktree", "wire": { "runNow": "worktree" } },
      { "name": "idle pane after an error, a worktree could be made", "row": { "heldIndex": null, "text": "Try again", "attachments": 0 },
        "pane": { "busy": false, "heldForUpdate": false, "kind": "claude", "provider": "claude", "mode": "acceptEdits", "isChild": false, "delegationOn": true, "canHostWorktree": true, "otherAgentReady": true, "waitsOnBackgroundOnly": false, "held": 0 },
        "expect": "here", "wire": { "runNow": "here" } },
      { "name": "not a git repo, a turn running", "row": { "heldIndex": null, "text": "Fix the login bug", "attachments": 0 },
        "pane": { "busy": true, "heldForUpdate": false, "kind": "claude", "provider": "claude", "mode": "acceptEdits", "isChild": false, "delegationOn": true, "canHostWorktree": false, "otherAgentReady": true, "waitsOnBackgroundOnly": false, "held": 0 },
        "expect": "waitsForTurn", "wire": { "runNow": "unavailable", "runNowReason": "turn" } }
    ]
  }
  ```
  - Then the rest of the spec's 테스트 list, each with all fields:
    - not a git repo + waits on background → `steer`, `{here}`
    - delegation off + waits on background → `steer`
    - child pane (`isChild: true`) + turn running → `waitsForTurn`
    - remote pane (`delegationOn: false`, `waitsOnBackgroundOnly: false`) running → `waitsForTurn`
    - remote pane idle → `here`
    - Codex `manual` + other agent ready → `agentMenu`, `{unavailable, agent}`
    - Codex `manual` + no other agent → `waitsForTurn`
    - attachments only (`text: ""`, `attachments: 1`) + worktree possible + waits on background → `waitsForTurn`
    - text + 1 attachment + worktree → `worktree`
    - `heldForUpdate: true` (busy) → `waitsForUpdate`, `{unavailable, update}`
    - human row with `held: 2` + waits on background + not a git repo → `waitsForHeld`, `{unavailable, held}`
    - human row with `held: 2` + a turn running + not a git repo → `waitsForTurn`
    - human row with `held: 2` + idle → `here`
    - first held row (`heldIndex: 0`, `held: 2`) busy → `held`, `{here}`
    - second held row (`heldIndex: 1`, `held: 2`) → `hidden`, `null`
    - first held row idle → `held`
  - Add a section to `native/contracts/README.md` after "Delegation modes": `## Queue run now (fixtures/queue-run-now.json)`, three sentences on what it holds and who reads it.

- [ ] **Step 3: Tests.**
  ```swift
  import Foundation
  import Testing
  @testable import MightyCore

  /// The file Windows Core and the phone read too: what 바로 실행 on a queued row does.
  @Suite struct QueueRunNowFixtureTests {
      static let url: URL = {
          var url = URL(fileURLWithPath: #filePath)
          for _ in 0..<5 { url.deleteLastPathComponent() }
          return url.appendingPathComponent("native/contracts/fixtures/queue-run-now.json")
      }()

      @Test func everyCaseDecidesAsTheFixtureSays() throws {
          let root = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: Self.url)) as? [String: Any])
          let cases = try #require(root["cases"] as? [[String: Any]])
          #expect(cases.count >= 18)
          for item in cases {
              let name = item["name"] as? String ?? ""
              let row = try #require(item["row"] as? [String: Any]), p = try #require(item["pane"] as? [String: Any])
              func flag(_ key: String) -> Bool { p[key] as? Bool ?? false }
              let kind = p["kind"] as? String ?? "", provider = p["provider"] as? String ?? "", mode = p["mode"] as? String ?? ""
              let second = ComposerSecondSend.choose(kind: kind, provider: provider, mode: mode, isChild: flag("isChild"), delegationOn: flag("delegationOn"), canHostWorktree: flag("canHostWorktree"))
              let choices = flag("otherAgentReady") ? [DelegationChildChoice(agent: provider == "claude" ? "codex" : "claude", mode: "acceptEdits", blocked: nil)] : []
              let menuOnly = ComposerSecondSend.menuOnly(kind: kind, provider: provider, mode: mode, isChild: flag("isChild"), delegationOn: flag("delegationOn"), canHostWorktree: flag("canHostWorktree"), choices: choices)
              let pane = QueueRunNow.Pane(busy: flag("busy"), heldForUpdate: flag("heldForUpdate"), secondSend: second, menuOnly: menuOnly,
                                          waitsOnBackgroundOnly: flag("waitsOnBackgroundOnly"), held: p["held"] as? Int ?? 0)
              let path = QueueRunNow.decide(heldIndex: row["heldIndex"] as? Int, text: row["text"] as? String ?? "", attachments: row["attachments"] as? Int ?? 0, pane: pane)
              #expect(path.rawValue == item["expect"] as? String, "\(name)")
              let wire = item["wire"] as? [String: String]
              #expect(path.wire?.runNow == wire?["runNow"] && path.wire?.reason == wire?["runNowReason"], "\(name): wire")
          }
      }

      @Test func thePhonesRowsCarryTheirRunNowAndReasonAndLeaveThemOutWhenHidden() throws {
          let held = [DelegationDeliveryItem.fixture(id: "n-1", text: "Notice 1"), DelegationDeliveryItem.fixture(id: "n-2", text: "Notice 2")]
          let queued = [QueuedInput(text: "Human row", attachments: [])]
          let items = DelegationQueueRows.mobileItems(held: held, queued: queued) { $0 == "n-1" ? .held : $0 == "n-2" ? .hidden : .waitsForHeld }
          #expect(items.map(\.runNow) == ["here", nil, "unavailable"])
          #expect(items.map(\.runNowReason) == [nil, nil, "held"])
          let json = try #require(String(data: JSONEncoder().encode(items[1]), encoding: .utf8))
          #expect(!json.contains("runNow"))
      }
  }
  ```
  - `DelegationDeliveryItem.fixture` is a stand-in. Build the held item the way `DelegationDeliveryTests.heldRowsComeFirst…` ~489 does.
  - **`offersRunNext` callers:** replace each assertion with the `QueueRunNow` equivalent.
    - `BackgroundQueuePolicyTests` ~34-36:
      - `decide(heldIndex: nil, …, pane: waits-on-background, no worktree)` is `.steer`.
      - The same busy pane without the background wait is `.waitsForTurn`.
      - The first held row (`heldIndex: 0`, `held: 1`) is `.held`.
    - `DelegationDeliveryTests` ~507-510 and `HelpCaptureDelegationTests` ~155:
      - The first held row is `.held` busy or idle, and the second is `.hidden`.
      - A human row on an idle pane is `.here`.
    - Rename `…KeepRunNextOnOffer` to `…KeepRunNowOnOffer`.
  - **`MobileRemoteTests`:**
    - The fake gains `runNowAnswer = "worktree"` and `runNowFails`.
    - Add a route test:
      - `POST /m1/sessions/s1/queue/q-1/run-now` → 200 `{"protocol":1,"ok":true,"ran":"worktree"}`.
      - A bad id → 404.
      - `runNowFails` → 409 with the fake's message.

- [ ] **Step 4: Core.**
  ```swift
  import Foundation

  /// What 바로 실행 on a queued row does (docs/superpowers/specs/2026-10-29-queue-run-now-design.md; Windows Core
  /// `QueueRunNow`). Checked against native/contracts/fixtures/queue-run-now.json.
  public enum QueueRunNow: String, Sendable, CaseIterable {
      /// A new child pane of the pane's own agent, in its own worktree beside it, runs the row now; a right-click picks another agent.
      case worktree
      /// The pane's own agent cannot have a child in its mode, another agent can: a click opens the agent menu.
      case agentMenu
      /// The row starts now as the pane's next run, the items held for the pane ahead of it.
      case here
      /// The row goes into the pane's run that only waits on background work, as a new turn (중간 요청).
      case steer
      /// The first held row: every item held for the pane goes to it now, together.
      case held
      /// Disabled: a turn runs and neither a new worktree nor a steer can take the row; it runs in its turn.
      case waitsForTurn
      /// Disabled: the row could go into the run that only waits on background work, but items held for the pane go first.
      case waitsForHeld
      /// Disabled: the pane is held for a CLI update.
      case waitsForUpdate
      /// No button: a held row after the first (the first one's button sends them all).
      case hidden

      /// The pane as 바로 실행 reads it.
      public struct Pane: Sendable, Equatable {
          /// Running, starting, or held for a CLI update.
          public var busy: Bool
          public var heldForUpdate: Bool
          /// `ComposerSecondSend.choose(…)`: `.worktree` where a new child of the pane's agent can be made now.
          public var secondSend: ComposerSecondSend
          /// `ComposerSecondSend.menuOnly(…)`.
          public var menuOnly: Bool
          /// A local Claude pane, not stopping, whose turn ended and whose background work still runs.
          public var waitsOnBackgroundOnly: Bool
          /// Items delegation holds for the pane.
          public var held: Int
          public init(busy: Bool, heldForUpdate: Bool, secondSend: ComposerSecondSend, menuOnly: Bool, waitsOnBackgroundOnly: Bool, held: Int) {
              self.busy = busy; self.heldForUpdate = heldForUpdate; self.secondSend = secondSend; self.menuOnly = menuOnly
              self.waitsOnBackgroundOnly = waitsOnBackgroundOnly; self.held = held
          }
      }

      /// Nothing a person sends joins a running turn, so a busy pane that can make no worktree and is not only waiting on
      /// background work can only say why the row waits. Held items go ahead of the pane's next request, so a human row is
      /// never steered past them. `heldIndex`: the row's place among the held rows, nil for a human row.
      public static func decide(heldIndex: Int?, text: String, attachments: Int, pane: Pane) -> QueueRunNow {
          if let heldIndex { return heldIndex == 0 ? .held : .hidden }
          if !pane.busy { return .here }
          if pane.heldForUpdate { return .waitsForUpdate }
          // A new worktree needs written words for its task; files alone wait.
          let words = !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
          if words, pane.secondSend == .worktree { return .worktree }
          if words, pane.menuOnly { return .agentMenu }
          if pane.waitsOnBackgroundOnly, attachments == 0 { return pane.held == 0 ? .steer : .waitsForHeld }
          return .waitsForTurn
      }

      /// Whether the button does something (the rest are drawn disabled, or not at all).
      public var runs: Bool { [.worktree, .agentMenu, .here, .steer, .held].contains(self) }

      /// The phone's `runNow` and `runNowReason` on a queued row. The reason is a word: the phone words it in its own
      /// language. A phone cannot pick an agent, so `agentMenu` is unavailable there. nil: no button.
      public var wire: (runNow: String, reason: String?)? {
          switch self {
          case .worktree: ("worktree", nil)
          case .here, .steer, .held: ("here", nil)
          case .waitsForTurn: ("unavailable", "turn")
          case .waitsForHeld: ("unavailable", "held")
          case .waitsForUpdate: ("unavailable", "update")
          case .agentMenu: ("unavailable", "agent")
          case .hidden: nil
          }
      }
  }
  ```
  - **`MobileQueuedItem`:** add `public var runNow: String?` and `public var runNowReason: String?`. Its init becomes `init(id: String, text: String, runNow: String? = nil, runNowReason: String? = nil)`. Synthesized Codable already omits nil.
  - **`DelegationQueueRows`:**
    - `mobileItems` gains the `runNow` closure and maps each row's `.wire` into both fields.
    - Delete `offersRunNext`. Rewrite the type's doc so it says the first held row carries 바로 실행 for them all (`QueueRunNow.held`).
  - **Models:**
    ```swift
    /// `POST /m1/sessions/{id}/queue/{itemId}/run-now`: where the row ran, "worktree" or "here".
    public struct MobileRunNowReply: Codable, Sendable, Equatable {
        public var `protocol`: Int = 1
        public var ok: Bool = true
        public var ran: String
        public init(ran: String) { self.ran = ran }
    }
    ```
  - **`MobileHostDelegate`:** add `func mobileRunQueuedNow(sessionId: String, itemId: String) async throws -> String`. Give it an extension default `throw MobileHostError.notFound(L("remote.error.queueItemNotFound"))` so the seven test fakes need no change.
  - **Route,** next to `run-next`:
  ```swift
  if method == "POST", route.count == 5, route[0] == "sessions", route[2] == "queue", route[4] == "run-now", url.query == nil {
      let id = try sessionID(route[1])
      guard CoreValidation.identifier(route[3]) else { throw Failure(404, L("remote.error.queueItemNotFound")) }
      let ran = try await perform { try await delegate.mobileRunQueuedNow(sessionId: id, itemId: route[3]) }
      return reply(200, MobileRunNowReply(ran: ran))
  }
  ```

- [ ] **Step 5: App.**
  - **`AppStore.swift`:**
    - Keep `runNextQueuedInput` and `steersRunNext(_:)`. Their doc now says "the old phones' `/queue/run-next` only".
    - Widen `steerQueuedRow` with `itemId: String? = nil`:
      - it steers that row, else the first row
      - it requires the row to have no attachments
      - it keeps its `own` handling
    - Add:
  ```swift
  /// What 바로 실행 on the row `rowId` (held or the human's) of `session` does now (``QueueRunNow``). `stopping`: the pane's stop is under way.
  func queueRunNow(_ session: RunSession, rowId: String, stopping: Bool = false) -> QueueRunNow {
      let id = session.id, holds = backgroundUpdateHolds(session), held = delegationHeld[id] ?? []
      let pane = QueueRunNow.Pane(busy: session.status == "running" || pendingRuns.contains(id) || holds, heldForUpdate: holds,
                                  secondSend: composerSecondSend(session), menuOnly: worktreeMenuOnly(session),
                                  waitsOnBackgroundOnly: session.kind == "claude" && session.provider == "claude" && !isRemoteSession(session) && !stopping
                                      && session.backgroundWork?.waitingOnBackground == true,
                                  held: held.count)
      if let index = held.firstIndex(where: { $0.id == rowId }) { return QueueRunNow.decide(heldIndex: index, text: "", attachments: 0, pane: pane) }
      guard let item = queuedInputs[id]?.first(where: { $0.id == rowId }) else { return .hidden }
      return QueueRunNow.decide(heldIndex: nil, text: item.text, attachments: item.attachments.count, pane: pane)
  }

  /// 바로 실행 on the row `itemId` of the pane `id`: what ``queueRunNow(_:rowId:stopping:)`` decides now. `agent`: the row's agent
  /// menu pick, a new child of that agent. `own`: this Mac's click, whose views move to what runs (natural submit B2); the phone's
  /// only follow, and its worktree child is not selected here. What ran, or nil when nothing did (the row stays).
  @discardableResult
  func runQueuedNow(_ id: String, itemId: String, agent: String? = nil, stopping: Bool = false, own: Bool = true) async -> QueueRunNow? {
      guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }) else { return nil }
      let path = queueRunNow(session, rowId: itemId, stopping: stopping)
      if path == .held { return await runNextHeld(id, own: own) ? .held : nil }
      guard let index = queuedInputs[id]?.firstIndex(where: { $0.id == itemId }), let item = queuedInputs[id]?[index] else { return nil }
      func take() { queuedInputs[id]?.remove(at: index); if queuedInputs[id]?.isEmpty == true { queuedInputs.removeValue(forKey: id) } }
      switch (path, agent) {
      case (.worktree, _), (.agentMenu, .some):
          guard agent == nil || offersWorktreeMenu(session) else { return nil }
          take()
          runInNewWorktree(id, input: item.text, attachments: item.attachments, from: .queue(item), agent: agent, quiet: !own)
          return .worktree
      case (.here, _):
          // At the head, the queue's own start takes it: held items go ahead of it in its run, a refused start keeps it there.
          take(); queuedInputs[id, default: []].insert(item, at: 0)
          runNextQueuedRow(id, own: own)
          return .here
      case (.steer, _):
          return await steerQueuedRow(id, itemId: itemId, own: own) ? .steer : nil
      default:
          return nil
      }
  }
  ```
  - **`AppStore+Delegation.swift`:**
    - Add the origin:
    ```swift
    /// Where a new worktree's text came from, and where it goes when no child runs it.
    enum WorktreeOrigin {
        /// The composer's ⌘Enter or worktree button: taken from the draft; a refused text queues or goes back to the composer.
        case composer(draft: String)
        /// A queued row's 바로 실행: the draft is never touched; a refused text goes back to the head of the queue, whatever agent was picked.
        case queue(QueuedInput)
    }
    ```
    - `runInNewWorktree(_:input:attachments:from:agent:quiet:)` replaces the `draft:` parameter with `from origin: WorktreeOrigin`. Both existing callers in `submit` pass `.composer(draft: originalDraft)`. Then:
      - It clears `drafts[id]` and `attachmentDrafts` only for `.composer`.
      - It inserts `id` into `quietChildParents` while `quiet` and removes it once `startHumanChild` answers.
      - On `.refused` / `.failed`:
        - `.queue(item)` → `putBackQueuedRow(id, item, reason:)`
        - otherwise today's `returnToComposer` / `runAfterNoWorktree`
      - `humanChildFailedToStart` takes the origin the same way.
    ```swift
    /// A queued row whose new worktree could not be made or started: back at the head of the queue (never the composer, and
    /// without the 16-row check: it was queued), and the transcript says why. A pane idle meanwhile, its queue not paused, runs it now.
    private func putBackQueuedRow(_ id: String, _ item: QueuedInput, reason: String) {
        guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }) else { return }
        updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L("delegation.human.queued", ["reason": reason]))); $0.logs = TranscriptRetention.trimmed($0.logs) }
        let waiting = queuedInputs[id]?.isEmpty == false
        if queuedInputs[id]?.contains(where: { $0.id == item.id }) != true { queuedInputs[id, default: []].insert(item, at: 0) }
        if !waiting, session.status != "running", !pendingRuns.contains(id) { runNextQueuedRow(id) }
    }
    ```
    - `createPane`: `let shows = pane.startedByHuman && !quietChildParents.contains(parent.id) && …`.
    - Declare `var quietChildParents = Set<String>()` in `AppStore.swift` beside `ownHeldReleases`.
  - **`runQueuedNow`:** returns nil at once for `.waitsFor…` and `.hidden`, before touching the queue. Its `default:` case covers them.
  - **`AppStore+MobileRemote.swift`:**
    - Pass `runNow: { self.queueRunNow(session, rowId: $0) }` to `mobileItems` ~469. Use the session already in scope there.
    - Add the adapter beside `mobileRunNextQueued`. It hops to the store like that one does, because it awaits.
    ```swift
    /// The phone's 바로 실행 on `itemId`: what ``queueRunNow(_:rowId:stopping:)`` decides now, as this Mac's click would, but only
    /// followed by this Mac's views, and a worktree child is not selected here. "worktree" or "here".
    func mobileRunQueuedNow(_ id: String, itemId: String) async throws -> String {
        let session = try mobileCommandSession(id)
        guard (delegationHeld[id] ?? []).contains(where: { $0.id == itemId }) || queuedInputs[id]?.contains(where: { $0.id == itemId }) == true else {
            throw MobileHostError.notFound(L("remote.error.queueItemNotFound"))
        }
        let path = queueRunNow(session, rowId: itemId)
        // A dimmed row is never sent by the phone; this answers a row that changed under it.
        guard path != .agentMenu, path.runs, let wire = path.wire?.runNow else { throw MobileHostError.conflict(L("queue.runNowRefused")) }
        if path == .here || path == .held, let reason = runBlockedReason(session, checkRuntime: false) { throw MobileHostError.conflict(reason) }
        let macError = error
        let ran = await runQueuedNow(id, itemId: itemId, own: false)
        let failure = error == macError ? nil : error
        error = macError
        guard ran != nil else { throw MobileHostError.conflict(failure ?? L("queue.runNowRefused")) }
        return wire
    }
    ```
  - **`QueuedInputsView.swift`:**
    - Replace `onRunNext` and `steersRunNext` with these inputs:
      - `runNow: [String: QueueRunNow]` (by row id)
      - `onRunNow: (String, String?) -> Void` (row, agent)
      - `agentChoices: [DelegationChildChoice]`
      - `parentProvider: String`
      - `disabled: Bool` (`store.hasModal`)
    - Delete the header button.
    - Put `runNowButton` before each human row's X, and before each held row's pin. Only the first held row's is drawn (`.hidden` draws nothing).
    - Change the row's `.accessibilityElement(children: .combine)` to `.contain`.
    - Update the struct's doc comment.
  ```swift
  /// 바로 실행 beside a row's X (or the first held row's pin): what `QueueRunNow` decided for the row, in the old 다음 실행's
  /// look. A row that cannot run now keeps its slot with the button disabled; SwiftUI shows no help on a disabled control,
  /// so the reason sits on a wrapper.
  @ViewBuilder private func runNowButton(_ itemId: String) -> some View {
      let path = runNow[itemId] ?? .hidden
      let branch = path == .worktree || path == .agentMenu
      let label = Label(L("queue.runNow"), systemImage: branch ? "arrow.triangle.branch" : "play.fill").font(.ui(size: 10, weight: .medium))
      switch path {
      case .hidden: EmptyView()
      case .waitsForTurn, .waitsForHeld, .waitsForUpdate:
          HStack(spacing: 0) {
              Button {} label: { label }.buttonStyle(.plain).foregroundStyle(Palette.ink2).disabled(true)
          }
          .contentShape(Rectangle()).help(help(path))
          .accessibilityElement(children: .combine).accessibilityHint(help(path))
          .accessibilityIdentifier("queue-run-now-\(itemId)")
      case .agentMenu:
          Menu { agentMenu(itemId) } label: { label }
              .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
              .foregroundStyle(Palette.accent).disabled(disabled).help(L("queue.worktreeMenuHintMac"))
              .accessibilityIdentifier("queue-run-now-\(itemId)")
      default:
          Button { onRunNow(itemId, nil) } label: { label }
              .buttonStyle(.plain).foregroundStyle(Palette.accent).disabled(disabled).help(help(path))
              .contextMenu { if path == .worktree { agentMenu(itemId) } }
              .accessibilityIdentifier("queue-run-now-\(itemId)")
      }
  }

  private func help(_ path: QueueRunNow) -> String {
      switch path {
      case .worktree: L("queue.runNowWorktreeHelpMac")
      case .agentMenu: L("queue.worktreeMenuHintMac")
      case .steer: L("queue.runNowSteerHelp")
      case .held: L("queue.runNowHeldHelp", ["count": "\(held.count)"])
      case .waitsForTurn: L("queue.runNowUnavailableHelp")
      case .waitsForHeld: L("queue.runNowAfterHeldHelp")
      case .waitsForUpdate: L("queue.runNowUpdateHelp")
      case .here, .hidden: L("queue.runNowHereHelp")
      }
  }

  /// The composer worktree menu's rows without the ⌘Enter mark: a pick runs the row in a new child of that agent.
  @ViewBuilder private func agentMenu(_ itemId: String) -> some View {
      Section(L("composer.worktreeMenu.title")) {
          ForEach(agentChoices) { choice in
              Button { onRunNow(itemId, choice.agent) } label: {
                  Label { Text(ProviderOptions.betaTitle(choice.agent, SessionPaneView.worktreeChoiceTitle(choice, parent: parentProvider, marksShortcut: false))) } icon: {
                      if let image = ProviderIconImage.image(provider: choice.agent, pointSize: 12) { Image(nsImage: image) }
                  }
              }
              .disabled(choice.blocked != nil || disabled)
              .accessibilityIdentifier("queue-run-now-\(choice.agent)-\(itemId)")
          }
      }
  }
  ```
  - **`SessionPaneView.swift`:**
    - `worktreeChoiceTitle(_:parent:marksShortcut: Bool = true)` uses `composer.worktreeMenu.agent` for the pane's own agent when `marksShortcut` is false.
    - The `QueuedInputsView` call ~679:
    ```swift
    QueuedInputsView(sessionID: session.id, held: held, items: queued, running: running,
                     onRemove: { store.removeQueuedInput(session.id, itemId: $0) },
                     notice: …unchanged…,
                     addedAt: store.queueAddedAt[session.id],
                     runNow: Dictionary(uniqueKeysWithValues: (held.map(\.id) + queued.map(\.id)).map { ($0, store.queueRunNow(session, rowId: $0, stopping: stopping)) }),
                     onRunNow: { row, agent in Task { await store.runQueuedNow(session.id, itemId: row, agent: agent, stopping: stopping) } },
                     agentChoices: store.worktreeChoiceChoicesIfOffered(session), parentProvider: session.provider, disabled: store.hasModal)
        .onAppear { store.refreshWorktreeReadiness(for: session) }
    ```
    - `worktreeChoiceChoicesIfOffered` is a stand-in name: pass `store.offersWorktreeMenu(session) ? store.worktreeChildChoices(session) : []`.
    - `running` keeps meaning the pane is busy. The modal moves to `disabled`.
  - Grep afterwards:
    - `git grep -n "offersRunNext\|queue-run-\\\\(\|onRunNext" -- native/macos` has no source hits.
    - `queue.runNext` is left only in locales.

- [ ] **Step 6: Docs.**
  - `docs/interaction.md`:
    - Line ~49: "다음 실행이 대기열 첫 요청을" → "바로 실행이 그 행의 요청을 (worktree를 만들 수 없고 실행이 백그라운드 작업만 기다릴 때)".
    - Line ~55: list "바로 실행(이 창에서 시작하거나 중간 요청으로)" instead of 다음 실행. Add that a worktree 바로 실행 follows the ⌘Enter child rule, and that the phone's never selects the child on the Mac or Windows.
    - Line ~91: rename the row 바로 실행. Columns:
      - Mac: "대기열 행의 바로 실행(오른쪽 클릭: 에이전트)"
      - Windows: the same
      - Phone: "`QueuedList` 행의 바로 실행"
      - What it does: here, steer, worktree, as the spec §3 says
  - `docs/mighty-mode.md`:
    - Line ~28: 오류로 멈춘 대기열은 행의 **바로 실행**으로 이어 간다.
    - Line ~29: replace "**다음 실행**도 보인다" with "그 행의 **바로 실행**이 worktree를 만들 수 없을 때 그 실행에 새 턴으로 넣는다".
  - `pending.json` `scrollRule`: "다음 실행" → "바로 실행 in this pane (started or steered)". No case changes.

- [ ] **Step 7: Check and commit.**
  - Run check-locales, check-design-tokens, check-style-freeze, and `git diff --stat -- native/macos/Sources/MightyCore/Styles` (expect empty).
  - Commit. Subject: `(mac) every queued row has 바로 실행 beside its X: a new worktree child where one can be made, otherwise this pane now, greyed out with the reason when it must wait, and the phone gets the same per row`

### Task 2: Windows Core + WinUI + web

**Files:**
- **Core** (`native/windows/MightyClaude.Core`):
  - `QueueRunNow.cs` (new)
  - `DelegationDelivery.cs`: `MobileItems` ~35. Remove `OffersRunNext` ~43.
  - `Remote/MobileDesktopRouter.cs`: `MobileQueuedItem` ~11, the route beside `run-next` ~338
  - `DelegationAppHost.cs`: `AddingChild` ~42, `shows` ~56
  - `DesktopService.Delegation.cs`: `StartHumanChildAsync` ~416 gains `bool quiet = false` and keeps quiet parent ids for `AddingChild`
- **WinUI:**
  - `MainWindow.QueuedComposer.cs`: `RenderQueuedInputs` ~178, `RunNext` ~243, `SteerFirstQueuedInput` ~266
  - `MainWindow.Delegation.cs`: `RunInNewWorktree` ~391
  - `MainWindow.WorktreeMenu.cs`: `WorktreeChoiceTitle` ~58, a row menu
  - `MainWindow.WebShell.Composer.cs`: bridges ~84, `queue` state ~564
  - `MainWindow.MobileRemote.cs`: the action switch ~469, `MobileItems` call ~659
- **Web:**
  - `native/windows/web/src/composer/Composer.tsx` ~327-353
  - `types.ts` ~39-44
  - `composer/queue.ts` (new)
  - `composer/Toolbar.tsx`: export `MenuList` and `usePopover`
  - `src/store/store.test.ts` ~232, 301
  - `native/windows/web/README.md` ~71
- **Docs:**
  - `docs/windows-parity.md` ~94
  - `docs/windows-screen-checklist.md` ~465
- **Tests:**
  - `MightyClaude.Core.Tests/QueueRunNowVerification.cs` (new, registered in `Verification.cs`, fixture embedded in the `.csproj`)
  - `ClaudePlanStyleVerification.cs` ~218
  - `DelegationDeliveryVerification.cs` ~368-372
  - web `src/composer/queue.test.ts` (new)

**Interfaces:** the C# mirror of Task 1 with PascalCase names:
- `QueueRunNowPath { Worktree, AgentMenu, Here, Steer, Held, WaitsForTurn, WaitsForHeld, WaitsForUpdate, Hidden }`
- `QueueRunNow.Pane` record and `QueueRunNow.Decide(int? heldIndex, string text, int attachments, Pane pane)`
- `QueueRunNow.Runs(path)`
- `QueueRunNow.Wire(path)` → `(string RunNow, string? Reason)?`
- `QueueRunNow.Name(path)`, which is the camelCase fixture name
- `MobileQueuedItem(string Id, string Text, string? RunNow = null, string? RunNowReason = null)`

The web gets `runNow: RunNow | null` per row (`hidden` → null) and two bridges, `queueRunNow { paneId, itemId, agent? }` and `queueRunNowMenu { paneId }`. `queueRunNext` goes away.

- [ ] **Step 1: Core tests.**
  - Embed the fixture: `<EmbeddedResource Include="../../contracts/fixtures/queue-run-now.json" LogicalName="MightyClaude.Core.Tests.QueueRunNow.json" />`.
  ```csharp
  internal static class QueueRunNowVerification
  {
      /// <summary>Every case of native/contracts/fixtures/queue-run-now.json decides as the Mac's QueueRunNowFixtureTests.</summary>
      internal static void EveryCaseDecidesAsTheFixtureSays()
      {
          const string name = "MightyClaude.Core.Tests.QueueRunNow.json";
          using var stream = typeof(QueueRunNowVerification).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded");
          var cases = JsonDocument.Parse(stream).RootElement.GetProperty("cases");
          foreach (var item in cases.EnumerateArray())
          {
              var row = item.GetProperty("row"); var p = item.GetProperty("pane"); var label = item.GetProperty("name").GetString();
              bool Flag(string key) => p.GetProperty(key).GetBoolean();
              string Text(string key) => p.GetProperty(key).GetString()!;
              var second = ComposerSecondSends.Choose(Text("kind"), Text("provider"), Text("mode"), Flag("isChild"), Flag("delegationOn"), Flag("canHostWorktree"));
              DelegationChildChoice[] choices = Flag("otherAgentReady") ? [new(Text("provider") == "claude" ? "codex" : "claude", "acceptEdits", null)] : [];
              var menuOnly = ComposerSecondSends.MenuOnly(Text("kind"), Text("provider"), Text("mode"), Flag("isChild"), Flag("delegationOn"), Flag("canHostWorktree"), choices);
              var pane = new QueueRunNow.Pane(Flag("busy"), Flag("heldForUpdate"), second, menuOnly, Flag("waitsOnBackgroundOnly"), p.GetProperty("held").GetInt32());
              var heldIndex = row.GetProperty("heldIndex") is { ValueKind: JsonValueKind.Number } index ? index.GetInt32() : (int?)null;
              var path = QueueRunNow.Decide(heldIndex, row.GetProperty("text").GetString() ?? "", row.GetProperty("attachments").GetInt32(), pane);
              Check(QueueRunNow.Name(path) == item.GetProperty("expect").GetString(), $"queue run now: {label}");
              var wire = item.GetProperty("wire");
              (string RunNow, string? Reason)? expected = wire.ValueKind == JsonValueKind.Null ? null
                  : (wire.GetProperty("runNow").GetString()!, wire.TryGetProperty("runNowReason", out var reason) ? reason.GetString() : null);
              Check(QueueRunNow.Wire(path) == expected, $"queue run now wire: {label}");
          }
      }
  }
  ```
  - Register it in `Verification.cs` next to the delegation-modes test: `"queue: 바로 실행 follows native/contracts/fixtures/queue-run-now.json"`.
  - `DelegationDeliveryVerification` ~368-372:
    - `MobileItems(held, queued, row => …)` carries `RunNow`.
    - The `OffersRunNext` checks become `Decide` checks, as in Task 1.
  - `ClaudePlanStyleVerification` ~218: the same.

- [ ] **Step 2: Core.**
  - Port `QueueRunNow` from Task 1 Step 4. Keep the Mac's comments, rules and order. `Pane` is a `sealed record`.
    ```csharp
    public static string Name(QueueRunNowPath path) => JsonNamingPolicy.CamelCase.ConvertName(path.ToString());
    public static bool Runs(QueueRunNowPath path) => path is QueueRunNowPath.Worktree or QueueRunNowPath.AgentMenu or QueueRunNowPath.Here or QueueRunNowPath.Steer or QueueRunNowPath.Held;
    public static (string RunNow, string? Reason)? Wire(QueueRunNowPath path) => path switch
    {
        QueueRunNowPath.Worktree => ("worktree", null),
        QueueRunNowPath.Here or QueueRunNowPath.Steer or QueueRunNowPath.Held => ("here", null),
        QueueRunNowPath.WaitsForTurn => ("unavailable", "turn"),
        QueueRunNowPath.WaitsForHeld => ("unavailable", "held"),
        QueueRunNowPath.WaitsForUpdate => ("unavailable", "update"),
        QueueRunNowPath.AgentMenu => ("unavailable", "agent"),
        _ => null,
    };
    ```
  - **`MobileQueuedItem`:** gains `string? RunNow = null, string? RunNowReason = null`. The router's serializer must leave nulls out, as the Mac does: `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on both properties.
  - **`DelegationQueueRows`:**
    - `MobileItems(held, queued, Func<string, QueueRunNowPath>? runNow = null)` maps `Wire(runNow?.Invoke(id) ?? Hidden)` into both fields.
    - Delete `OffersRunNext` and update the doc.
  - **Router,** beside `run-next`:
    ```csharp
    if (method == "POST" && route.Length == 6 && route[3] == "queue" && route[5] == "run-now")
    {
        var ran = await action(new(pane.Id, "queue-run-now", JsonSerializer.SerializeToElement(new { id = route[4] })), token); Changed();
        return Reply(new { protocol = 1, ok = true, ran });
    }
    ```
  - **Quiet children:**
    - `StartHumanChildAsync(..., string? agent = null, bool quiet = false)` keeps `parentId` in a `quietChildParents` set while the coordinator answers.
    - `AddingChild(state, pane, task, bool quiet = false)` computes `shows = pane.StartedByHuman && !quiet && …`.
    - Its caller passes `quietChildParents.Contains(pane.ParentSessionId)`.

- [ ] **Step 3: WinUI.**
  - In `MainWindow.QueuedComposer.cs`:
  ```csharp
  /// <summary>The pane as 바로 실행 reads it (M/AppStore.swift queueRunNow).</summary>
  private QueueRunNow.Pane RunNowPane()
  {
      var pane = Session; var holds = owner.BackgroundUpdateHolds(pane); var second = owner.service.SecondSend(pane);
      return new(pane.Status == "running" || starting || queueStarting || holds, holds, second,
          second == ComposerSecondSend.Queue && owner.service.WorktreeMenuOnly(pane, LaunchesInPlanMode),
          pane.Kind == "claude" && pane.Provider == "claude" && Workspace.Remote is null && !stopping && pane.BackgroundWork?.WaitingOnBackground == true,
          owner.service.HeldItems(id).Count);
  }

  /// <summary>What 바로 실행 on the row <paramref name="itemId"/> does now.</summary>
  internal QueueRunNowPath RunNowFor(string itemId)
  {
      var pane = RunNowPane();
      var heldIndex = owner.service.HeldItems(id).ToList().FindIndex(item => item.Id == itemId);
      if (heldIndex >= 0) return QueueRunNow.Decide(heldIndex, "", 0, pane);
      return queuedInputs.Items.FirstOrDefault(item => item.Id == itemId) is { } row ? QueueRunNow.Decide(null, row.Text, row.Attachments.Count, pane) : QueueRunNowPath.Hidden;
  }

  /// <summary>
  /// 바로 실행 on a queued or held row (M/AppStore.swift runQueuedNow): a new worktree child (of <paramref name="agent"/> when the row's
  /// menu picked one), the row at the head of the queue started now, the row steered into a run that only waits on background work,
  /// or every held item released. <paramref name="quiet"/>: the phone's, whose worktree child is not selected here. What ran, or
  /// null when nothing did (a disabled or hidden row among them).
  /// </summary>
  internal async Task<QueueRunNowPath?> RunQueuedNow(string itemId, string? agent = null, bool quiet = false)
  {
      if (!QueuePaneAlive) return null;
      var path = RunNowFor(itemId);
      if (!QueueRunNow.Runs(path)) return null;
      if (path == QueueRunNowPath.Held) return await ReleaseHeldNow() ? path : null;
      if (queuedInputs.Items.FirstOrDefault(item => item.Id == itemId) is not { } row) return null;
      if (path == QueueRunNowPath.Worktree || path == QueueRunNowPath.AgentMenu && agent is not null)
      {
          if (agent is not null && !owner.service.OffersWorktreeMenu(Session, LaunchesInPlanMode)) return null;
          queuedInputs.Remove(row.Id); RenderQueuedInputs(); RefreshComposerState();
          RunInNewWorktree(row.Text, row.Attachments, agent, queued: row, quiet: quiet);
          return QueueRunNowPath.Worktree;
      }
      if (path == QueueRunNowPath.Steer) return await SteerQueuedInput(row) ? path : null;
      if (path != QueueRunNowPath.Here) return null;
      queuedInputs.MoveToFront(row.Id);
      await StartNextQueuedInput();
      return path;
  }
  ```
    - Extract `ReleaseHeldNow()` from `RunNext()`'s held branch: the block check, `RunNextAsync`, and `NotStarted` → throw. It returns `Released || NothingHeld`. `RunNext()` keeps calling it for old phones.
    - `SteerFirstQueuedInput()` becomes `SteerQueuedInput(QueuedComposerInput? row = null)`. The steer check moves to the row:
      - `Kind == "claude"`
      - `BackgroundQueuePolicy.SteersRunNext(Session.BackgroundWork, local, stopping, 1, row.Attachments.Count > 0)`
    - Add `MoveToFront(string id)` to `QueuedInputBuffer` (Core). It returns false when the id is not queued.
  - **`RenderQueuedInputs`:**
    - Drop the header button.
    - Each row gets a fourth `Auto` column, holding `RunNowButton(item.Id, RunNowFor(item.Id))` before the remove button or the pin. `Hidden` (a held row after the first) adds nothing.
    - It uses the old run-next look: the play or branch glyph at 8, "바로 실행" at 10 medium, accent ink, `PaintPlainButton(next, b.Transparent, b.Subtle, ink: accent)`.
    - `WaitsFor…` rows:
      - The button is drawn the same way with `IsEnabled = false` and `ink2`.
      - A disabled WinUI control shows no tooltip, so the button sits in a `Border` (transparent background, so it is hit-tested) that carries the tooltip.
      - `AutomationProperties.SetHelpText` on the button carries the reason too.
    - Tooltips follow the Global Constraints table (`queue.runNowWorktreeHelp` for `worktree`).
    - Clicks and menus:
      - `worktree` rows set `ContextFlyout` to a row `MenuFlyout` filled by `FillRunNowMenu(flyout, itemId)`.
      - `agentMenu` rows open that flyout on click.
      - `here`, `steer` and `held` call `owner.Act(() => RunQueuedNow(itemId))`.
    - AutomationId is `queue-run-now-{item.Id}`.
    - Update the method's doc comment.
  - **`MainWindow.WorktreeMenu.cs`:**
    - `WorktreeChoiceTitle(choice, parent, marksShortcut = true)`.
    - `FillRunNowMenu` repeats `FillWorktreeMenu`'s rows with `marksShortcut: false`. Each item calls `RunQueuedNow(itemId, agent)`.
  - **`MainWindow.Delegation.cs` `RunInNewWorktree(text, files, agent = null, QueuedComposerInput? queued = null, bool quiet = false)`:**
    - Pass `quiet` to `StartHumanChildAsync`.
    - `FallBack` with `queued` → `view.PutBackQueuedRow(queued, reason)`:
      - it writes the `delegation.human.queued` SlashNote
      - `queuedInputs.Insert(0, queued)` (add `Insert` to the buffer if missing)
      - it renders
      - it starts `queueDrainTimer` when the pane is idle
    - Never `ReturnToComposer` for a queued row.
  - **Mobile:**
    - `MobileItems(owner.service.HeldItems(id), queuedInputs.Items, RunNowFor)` ~659.
    - Action `case "queue-run-now": return await pane.MobileRunQueuedNow(body.Text("id") ?? "", token);`.
    ```csharp
    /// <summary>The phone's 바로 실행 (M/AppStore+MobileRemote.swift mobileRunQueuedNow): "worktree" or "here", else 404 or 409.</summary>
    internal async Task<object?> MobileRunQueuedNow(string itemId, CancellationToken token)
    {
        if (!owner.service.HeldItems(id).Any(item => item.Id == itemId) && !queuedInputs.Items.Any(item => item.Id == itemId)) throw new MobileRequestException(404, Locale.Get("remote.error.queueItemNotFound"));
        var path = RunNowFor(itemId);
        // A dimmed row is never sent by the phone; this answers a row that changed under it.
        if (path == QueueRunNowPath.AgentMenu || !QueueRunNow.Runs(path) || QueueRunNow.Wire(path) is not { } wire) throw new MobileRequestException(409, Locale.Get("queue.runNowRefused"));
        if (path is QueueRunNowPath.Here or QueueRunNowPath.Held && owner.DelegatedRunBlockReason(Session) is { } blocked) throw new MobileRequestException(409, blocked);
        var ran = await RunQueuedNow(itemId, quiet: true); token.ThrowIfCancellationRequested();
        return ran is null ? throw new MobileRequestException(409, Locale.Get("queue.runNowRefused")) : wire.RunNow;
    }
    ```

- [ ] **Step 4: Web.**
  - **`MainWindow.WebShell.Composer.cs`:**
    - The `queue` state drops `runNext`.
    - `held` and `items` rows carry `runNow = RunNowFor(x.Id) is var p && p != QueueRunNowPath.Hidden ? QueueRunNow.Name(p) : null`.
    - Bridges:
    ```csharp
    bridge.Register("queueRunNow", async args => { var view = WebPane(args.Text("paneId")); await Act(() => view.RunQueuedNow(args.Text("itemId") ?? "", args.Text("agent"))); return null; });
    bridge.Register("queueRunNowMenu", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebRunNowChoices()));
    ```
    - `WebRunNowChoices()` returns `[{ agent, text, enabled }]` from `WorktreeChildChoices(Session, LaunchesInPlanMode)`. The text is `ProviderCatalog.BetaLabel(agent, WorktreeChoiceTitle(choice, Session.Provider, marksShortcut: false))`.
    - Remove the `queueRunNext` bridge and `WebRunNext`.
  - **`types.ts`:**
    - `queue: { held: QueueHeld[]; items: QueueItem[]; waitsOnBackground: string | null }`.
    - `QueueHeld` and `QueueItem` gain `runNow: RunNow | null`. The host sends null for `hidden`, so only the first held row has a button.
    - `RunNow` and `RunNowChoice` come from `./queue`.
  - **`composer/queue.ts`:**
  ```ts
  /** What 바로 실행 does on a queued row, as the host decided it (Core QueueRunNow, M/QueueRunNow.swift). */
  export type RunNow = 'worktree' | 'agentMenu' | 'here' | 'steer' | 'held' | 'waitsForTurn' | 'waitsForHeld' | 'waitsForUpdate';
  /** One row of a queued row's agent menu (`queueRunNowMenu`). */
  export interface RunNowChoice { agent: string; text: string; enabled: boolean }

  const kinds: readonly RunNow[] = ['worktree', 'agentMenu', 'here', 'steer', 'held', 'waitsForTurn', 'waitsForHeld', 'waitsForUpdate'];
  /** The host's value, or null for anything else (no button). */
  export function readRunNow(value: unknown): RunNow | null {
    return typeof value === 'string' && (kinds as readonly string[]).includes(value) ? (value as RunNow) : null;
  }
  /** The button's tooltip key and its values: which of a new worktree or this pane happens, or why the row waits. */
  export function runNowTip(runNow: RunNow, held: number): [string, Record<string, number>?] {
    switch (runNow) {
      case 'worktree': return ['queue.runNowWorktreeHelp'];
      case 'agentMenu': return ['queue.worktreeMenuHintMac'];
      case 'steer': return ['queue.runNowSteerHelp'];
      case 'held': return ['queue.runNowHeldHelp', { count: held }];
      case 'waitsForTurn': return ['queue.runNowUnavailableHelp'];
      case 'waitsForHeld': return ['queue.runNowAfterHeldHelp'];
      case 'waitsForUpdate': return ['queue.runNowUpdateHelp'];
      default: return ['queue.runNowHereHelp'];
    }
  }
  /** A row that cannot run now: drawn disabled (aria-disabled, so its title still shows) and its click does nothing. */
  export const runNowWaits = (runNow: RunNow): boolean => runNow.startsWith('waitsFor');
  /** Worktree rows draw the branch, the rest the play triangle. */
  export const runNowBranches = (runNow: RunNow): boolean => runNow === 'worktree' || runNow === 'agentMenu';
  ```
  - **`queue.test.ts`:**
    - `readRunNow` takes the eight names and refuses `'hidden'`, `'runNext'`, `''`, `null` and `1`.
    - `runNowTip('held', 3)` is `['queue.runNowHeldHelp', { count: 3 }]`, and each name maps to its key (the Global Constraints table).
    - `runNowWaits` is true for the three `waitsFor…` names only.
  - **`Composer.tsx`:**
    - Remove the header button.
    - Put a `RunNowButton` before each held row's pin and each item's ×. A null `runNow` draws nothing.
  ```tsx
  /** 바로 실행 beside a queued row's × (M/QueuedInputsView.swift runNowButton). On a worktree row a right-click, Shift+F10 or the menu key picks the child's agent. */
  function RunNowButton({ paneId, itemId, runNow, held }: { paneId: string; itemId: string; runNow: RunNow | null; held: number }) {
    const { bridge, t } = useServices();
    const [choices, setChoices] = useState<RunNowChoice[] | null>(null);
    const close = useCallback(() => setChoices(null), []);
    const menuRef = usePopover(close);
    if (runNow === null) return null;
    const openMenu = async () => { try { setChoices(await bridge.call<RunNowChoice[]>('queueRunNowMenu', { paneId })); } catch { setChoices(null); } };
    const run = (agent?: string) => { setChoices(null); void bridge.call('queueRunNow', { paneId, itemId, ...(agent ? { agent } : {}) }); };
    const branch = runNowBranches(runNow);
    const waits = runNowWaits(runNow);
    const [key, values] = runNowTip(runNow, held);
    return (
      <span className="composer-queue-run-now" ref={menuRef}>
        <button type="button" className={'composer-text-button composer-queue-run' + (waits ? ' waits' : '')} data-run-now={itemId} title={t(key, values)}
          aria-disabled={waits || undefined}
          onClick={() => { if (waits) return; if (runNow === 'agentMenu') void openMenu(); else run(); }}
          onContextMenu={branch ? (event) => { event.preventDefault(); void openMenu(); } : undefined}>
          <Icon name={branch ? 'split' : 'play'} size={9} weight={branch ? 2.2 : 1} fill={!branch} />{t('queue.runNow')}
        </button>
        {choices && (
          <div className="composer-menu" role="menu">
            <MenuList rows={[{ kind: 'header', text: t('composer.worktreeMenu.title') }, ...choices.map((c) => ({ kind: 'action' as const, text: c.text, enabled: c.enabled }))]}
              path={[]} choose={(path) => { const choice = choices[(path[0] ?? 0) - 1]; if (choice) run(choice.agent); }} />
          </div>
        )}
      </span>
    );
  }
  ```
    - Use the popover class the Toolbar menus already use (not necessarily `composer-menu`).
    - Add only layout CSS for `.composer-queue-run-now`, plus `.composer-queue-run.waits { color: var(--ink2); cursor: default; }`, with tokens and no raw spacing.
    - Not `disabled`: Chromium shows no `title` on a disabled button. `aria-disabled` keeps the reason readable and the button focusable.
  - **`store.test.ts`:** drop `runNext: false` from the two composer fixtures.
  - **`README.md`:** `queueRunNext` → `queueRunNow`, `queueRunNowMenu`.
- [ ] **Step 5: Docs, keys, checks, commit.**
  - Add `queue.runNowWorktreeHelp` (four languages), copy, and run check-locales.
  - `docs/windows-parity.md` ~94: "오류 시 보류/직접 다음 실행" → "오류 시 보류, 행마다 바로 실행(새 worktree 또는 이 창)".
  - `docs/windows-screen-checklist.md` ~465: change the line. Add one line for the row's right-click agent menu in the web window and in WinUI.
  - `git grep -n "OffersRunNext\|queueRunNext\|WebRunNext" -- native/windows` shows no source hits. `RunNext()` and `MobileRunNext` stay for old phones.
  - Run web `npx tsc --noEmit -p .` if cheap.
  - Commit. Subject: `(windows) every queued row has 바로 실행 beside its ×, in the web window and the WinUI composer: a new worktree child where one can be made (right-click picks the agent), otherwise this pane now, greyed out with the reason when it must wait`

### Task 3: Phone + mobile-remote doc

**Files:**
- `mobile/src/api/types.ts`: `QueuedItem` ~287, new `RunNowResponse` beside `OkResponse` ~786
- `mobile/src/api/client.ts`: `runNext` ~237/448 → `runQueuedNow`
- `mobile/src/lib/queue.ts` (new): `runNowOf`, `runNowReasonKey`
- `mobile/src/components/ui.tsx`: `Button` gains `accessibilityHint?` and `dimmed?`
- `mobile/src/components/queued-list.tsx`
- `mobile/app/host/[hostId]/session/[sessionId].tsx`: `runNext` ~840, `<QueuedList>` ~1342
- Tests:
  - `mobile/src/__tests__/client.test.ts` ~171, 299
  - `mobile/src/__tests__/queue.test.ts` (new, reads the fixture's `wire`)
- Docs:
  - `docs/mobile-remote.md`: ~40, ~87 (`queued`), ~132, ~143 (routes), and an old/new table
  - `native/contracts/README.md`: ~80, plus a route row

**Interfaces (consumes):** Task 1's wire, `queued[].runNow?` / `queued[].runNowReason?` and `POST …/queue/{itemId}/run-now` → `{ protocol, ok, ran }`.

- [ ] **Step 1: Tests first.**
  ```ts
  import { readFileSync } from 'fs';
  import { resolve } from 'path';
  import { runNowOf, runNowReasonKey } from '@/lib/queue';

  type Wire = { runNow: string; runNowReason?: string } | null;
  const fixture = JSON.parse(readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/queue-run-now.json'), 'utf8')) as {
    cases: { name: string; wire: Wire }[];
  };

  describe('runNowOf', () => {
    it.each(fixture.cases.map((c) => [c.name, c.wire] as const))('%s', (_name, wire) => {
      const read = runNowOf({ id: 'q', text: 'x', ...(wire ?? {}) } as never);
      if (wire === null) expect(read).toBeUndefined();
      else expect(read).toEqual({ kind: wire.runNow, ...(wire.runNowReason ? { reason: wire.runNowReason } : {}) });
    });
    it('shows no button for anything else', () => {
      for (const junk of ['steer', 'agentMenu', 'hidden', '', 1, null, {}]) expect(runNowOf({ id: 'q', text: 'x', runNow: junk } as never)).toBeUndefined();
    });
    it('reads an unknown or missing reason as the current task', () => {
      expect(runNowOf({ id: 'q', text: 'x', runNow: 'unavailable', runNowReason: 'later' } as never)).toEqual({ kind: 'unavailable', reason: 'turn' });
      expect(runNowOf({ id: 'q', text: 'x', runNow: 'unavailable' } as never)).toEqual({ kind: 'unavailable', reason: 'turn' });
    });
    it('words each reason with the shared keys', () => {
      expect(runNowReasonKey('turn')).toBe('queue.runNowUnavailableHelp');
      expect(runNowReasonKey('held')).toBe('queue.runNowAfterHeldHelp');
      expect(runNowReasonKey('update')).toBe('queue.runNowUpdateHelp');
      expect(runNowReasonKey('agent')).toBe('phone.queue.runNowAgentOnComputer');
    });
  });
  ```
  - `client.test.ts`:
    - Replace the `runNext` cases with `runQueuedNow('s1', 'q 1')`. It posts to `/m1/sessions/s1/queue/q%201/run-now` with `{}`.
    - The answer `{ protocol: 1, ok: true, ran: 'worktree' }` reads `ran: 'worktree'`. An unknown `ran` reads `undefined`.
    - The 409 row in the error table now calls `runQueuedNow`.
  - A render test, next to the existing component tests if any (otherwise skip):
    - `QueuedList` shows "바로 실행" only on rows with `runNow`.
    - It shows nothing on a row without `runNow`, as from an old host.
    - An `"unavailable"` row is dimmed, reports `accessibilityState.disabled`, never calls `onRunNow`, and calls `onExplain` with its reason's text.
- [ ] **Step 2: Code.**
  ```ts
  // types.ts
  export interface QueuedItem {
    id: string;
    text: string;
    /**
     * 바로 실행 (host 0.4.47+): "worktree" — a new worktree child of the pane's agent; "here" — this pane now; "unavailable" — a
     * dimmed button whose reason is `runNowReason`. Absent: no button (a held row after the first, or an old host).
     */
    runNow?: 'worktree' | 'here' | 'unavailable';
    /** With "unavailable": "turn" (the current task), "held" (held items first), "update" (a CLI update), "agent" (pick another agent on the computer). */
    runNowReason?: 'turn' | 'held' | 'update' | 'agent';
  }
  /** `POST /m1/sessions/{id}/queue/{itemId}/run-now`. */
  export interface RunNowResponse extends OkResponse { ran?: 'worktree' | 'here' }

  // lib/queue.ts
  export type RunNowReason = 'turn' | 'held' | 'update' | 'agent';
  export type RunNowRead = { kind: 'worktree' | 'here' } | { kind: 'unavailable'; reason: RunNowReason };
  const reasons: readonly RunNowReason[] = ['turn', 'held', 'update', 'agent'];

  /** The row's 바로 실행 as the host decided it. Any other `runNow` hides the button; an unknown reason reads as the current task. */
  export function runNowOf(item: QueuedItem): RunNowRead | undefined {
    const value: unknown = item.runNow;
    if (value === 'worktree' || value === 'here') return { kind: value };
    if (value !== 'unavailable') return undefined;
    const reason: unknown = item.runNowReason;
    return { kind: 'unavailable', reason: (reasons as readonly unknown[]).includes(reason) ? (reason as RunNowReason) : 'turn' };
  }

  /** The locale key that says why a dimmed row cannot run now (the phone's own language, not the host's). */
  export function runNowReasonKey(reason: RunNowReason): string {
    switch (reason) {
      case 'held': return 'queue.runNowAfterHeldHelp';
      case 'update': return 'queue.runNowUpdateHelp';
      case 'agent': return 'phone.queue.runNowAgentOnComputer';
      default: return 'queue.runNowUnavailableHelp';
    }
  }

  // client.ts (interface and implementation; `runNext` is removed)
  /** 바로 실행 on one queued row ("queue", host 0.4.47+). */
  runQueuedNow(sessionId: string, itemId: string, signal?: AbortSignal): Promise<RunNowResponse>;
  runQueuedNow: async (sessionId, itemId, signal) => {
    const answer = await request<RunNowResponse>('POST', `/m1/sessions/${encodeURIComponent(sessionId)}/queue/${encodeURIComponent(itemId)}/run-now`, {}, signal);
    return { ...answer, ran: answer.ran === 'worktree' || answer.ran === 'here' ? answer.ran : undefined };
  },
  ```
  - **`ui.tsx` `Button`:**
    - Add `accessibilityHint?: string` and pass it to the pressable.
    - Add `dimmed?: boolean`: it draws the `disabled` look and sets `accessibilityState.disabled`, but stays pressable. A dimmed 바로 실행 explains itself on a tap; a phone has no tooltip.
  - **`QueuedList`:**
    - Drop `canRunNext` and `onRunNext`. Add `onRunNow: (item: QueuedItem) => void` and `onExplain: (text: string) => void`.
    - Remove the header button.
    - In each row, before the remove button:
    ```tsx
    {manageable && runNow ? (
      <Button
        label={t('queue.runNow')}
        accessibilityLabel={t('phone.queue.runNowLabel', { text: item.text.slice(0, 30) })}
        accessibilityHint={
          runNow.kind === 'unavailable' ? t(runNowReasonKey(runNow.reason))
            : t(runNow.kind === 'worktree' ? 'phone.queue.runNowWorktreeHint' : 'queue.runNowHereHelp')
        }
        tone="neutral"
        compact
        dimmed={runNow.kind === 'unavailable'}
        busy={busy && runNow.kind !== 'unavailable'}
        onPress={() => (runNow.kind === 'unavailable' ? onExplain(t(runNowReasonKey(runNow.reason))) : onRunNow(item))}
      />
    ) : null}
    ```
      with `const runNow = runNowOf(item);` at the top of the row's map callback.
    - Update the component's doc: 바로 실행 per row, shown only where the host sent `runNow`; a dimmed one says why on a tap.
  - **Session screen:** replace `runNext` / `firstQueued`.
  ```ts
  const runNow = useCallback(
    (item: QueuedItem) => {
      if (!client || !sessionId) return;
      const asked = Date.now();
      // This pane runs it (here): an own request (B2), watched by its words as 다음 실행's was. A worktree child is another pane.
      if (runNowOf(item)?.kind !== 'worktree' && runNowOf(item)?.kind !== 'here') return;
      const watched = runNowOf(item)?.kind === 'here' ? newPendingRequest(snapshotRef.current, newClientId(asked), item.text, asked, false) : undefined;
      void (async () => {
        setQueueBusy(true);
        try {
          const answer = await client.runQueuedNow(sessionId, item.id);
          if (shownSession.current !== sessionId) return;
          const worktree = answer.ran === 'worktree';
          showToast(t(worktree ? 'phone.session.toast.runNowWorktree' : 'phone.session.toast.nextStarted'), 'success');
          if (watched && !worktree) {
            updatePending((previous) => [...previous, { ...watched, phase: 'started', answeredAt: Date.now() }]);
            setSettleTick((count) => count + 1);
          }
          poll.refresh();
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          setQueueBusy(false);
        }
      })();
    },
    [client, poll, sessionId, updatePending],
  );
  ```
    - Copy the dependency list from today's `runNext`.
    - `<QueuedList … onRunNow={runNow} onExplain={(text) => showToast(text, 'info')} />` no longer takes `canRunNext` or `onRunNext`. Use whichever non-error tone `showToast` has.
  - Add Task 3's four keys, copy, and run check-locales. Use theme tokens only; no new raw spacing.
- [ ] **Step 3: Docs.**
  - **`docs/mobile-remote.md`:**
    - The `queued` line ~87 becomes `queued: [{ id, text, runNow?, runNowReason? }]`. Add the spec §5 wire table: `"unavailable"` is a dimmed button, and the reason is a word the phone words itself. An unknown `runNow` means no button, and an unknown reason means `"turn"`.
    - A route row:
      - Route: `POST /m1/sessions/{id}/queue/{itemId}/run-now`
      - Body: none
      - Answer: `{ protocol, ok, ran: "worktree" | "here" }`
      - Errors:
        - no row → 404
        - not now (a dimmed or hidden row, which the phone never sends; the state changed under it) → 409 `queue.runNowRefused`
        - the pane cannot run → 409 with why
        - the start failed → 409
    - `/queue/run-next` ~143 is marked "옛 폰용. 새 폰은 부르지 않는다".
    - Lines ~40 and ~132: "다음 실행(`/queue/run-next`)" → "바로 실행(`/queue/{itemId}/run-now`, 옛 폰은 `/queue/run-next`)".
    - Add the spec §5 old/new table.
  - **`native/contracts/README.md`:** add the route row and fix ~80's 다음 실행 wording the same way.
- [ ] **Step 4: Check and commit.**
  - Run `npm run -s typecheck` if cheap, then check-locales and check-design-tokens.
  - `git grep -n "runNext\|canRunNext" -- mobile/src mobile/app` shows no hits.
  - Commit. Subject: `(phone) every queued row on the phone has 바로 실행: the host runs it in a new worktree child or in this pane, a row that must wait is dimmed and says why, and an older host shows no button`

### Task 4: Help in four languages + key removal + sweep

**Files:** `docs/help/content/{ko,en,zh,ja}/`, with ko line numbers (the other languages follow the same paragraphs):
- `agent-pane.md`: `#queue`, the 다음 실행 bullet ~24
- `delegation.md`: the table ~31, `#worktree` ~37-50, `#notices` held rows ~141-145
- `phone.md`: the queue paragraph ~54

Remove the keys from `locales/*.json` and their copies.

- [ ] **Step 1: Rewrite, in all four languages.** Describe it in prose. Use `{{ui:…}}` only for keys without placeholders, so `{{ui:queue.runNow}}` and `{{ui:queue.worktree}}` are fine.
  - **agent-pane.md ~24:**
    - Each request in the queue has a {{ui:queue.runNow}} button beside its ✕.
    - Where a new worktree can be made (as {{kbd:⌘Enter}} above), it runs that request now in a new child pane, and the running task goes on. A right-click (or Control-click on the Mac) picks another agent.
    - Where it cannot:
      - an idle pane (one stopped by an error) starts the request now
      - a Claude pane whose answer is done and which only waits on background work takes it as a {{ui:graph.block.steer}}
      - otherwise the button is greyed out and its tooltip says why: the current task must end first, items held for the pane go first, or a CLI update is under way. The request waits its turn.
    - The button's tooltip says which will happen.
    - A request with attachments only never goes to a worktree.
  - **delegation.md:**
    - The table row "지금 작업과 따로, 바로 실행" adds "대기열 행의 {{ui:queue.runNow}}".
    - In `#worktree`, add a bullet:
      - A request already in the queue can go the same way with its row's {{ui:queue.runNow}}. Its right-click picks the agent, with the {{platform:mac}} / {{platform:windows}} wording of the lines ~44-45.
      - If its child cannot be made or started, it goes back to the head of the queue and the transcript says why.
    - In `#notices` ~144, replace the {{ui:queue.runNext}} bullet: the top held row's {{ui:queue.runNow}} sends all held items now, in one run, to this pane; the other held rows have no button. A {{ui:queue.runNow}} that opens a new worktree leaves them held for this pane. While items are held, a request that could otherwise go into the running Claude stays greyed out until they are sent.
  - **phone.md ~54:** queued requests show a {{ui:queue.runNow}} button when the computer's app is new enough. It runs the request in a new worktree child pane on the computer, or in this pane, as the computer decides. A greyed-out one says why when you tap it. The phone does not pick the agent.
- [ ] **Step 2: Remove the keys.**
  - `git grep -n "queue\.runNext" -- native mobile/src mobile/app docs/help ':!*.json'` must be empty.
  - Remove `queue.runNext` and `queue.runNextHelp` from the four locales and their copies, then run `node scripts/check-locales.js`. It regenerates `docs/i18n.md`.
- [ ] **Step 3: Sweep.**
  - `git grep -n "다음 실행" -- docs/help docs/interaction.md docs/mighty-mode.md docs/mobile-remote.md native/contracts locales/ko.json` may only show:
    - the app-update "다음 실행 때 설치" wording
    - the delegation "자식의 다음 실행" wording
    - `/queue/run-next` marked as the old phones' route
  - Run `node scripts/help/build.mjs --check`, check-locales, check-design-tokens and check-style-freeze.
  - Note for the user: the `delegation-held` help picture shows 바로 실행 after the next capture run. Do not regenerate it here.
- [ ] **Step 4: Commit.** Subject: `(help) the help says each queued row's 바로 실행 runs it in a new worktree child or in this pane, and that a held row's sends every held item now`
