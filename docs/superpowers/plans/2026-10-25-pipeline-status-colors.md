# Pipeline view: a colour for each state (columns and cards)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the Mighty view's 파이프라인 (structure / kanban), running, waiting, done, error and stopped look different at a glance on the Mac, on Windows (web shell and WinUI fallback) and on the phone:
- every step column gets its tone's `soft` colour under its cards, at the column opacity;
- every card gets its tone's `soft` colour at the lighter card opacity (so it stands out in a column of its own tone), plus a 3pt bar in its tone's `mark` colour down its leading edge;
- a step that hasn't happened yet stays on the plain background.

Spec: `docs/superpowers/specs/2026-10-25-pipeline-status-colors-design.md`.

**Architecture:**
- **Shared values.** A new top-level `pipeline` group in `native/contracts/fixtures/design-tokens.json`, next to `opacities`. Each platform mirrors it as named constants: Mac `DesignMetrics.Pipeline`, Windows Core `DesignMetrics.Pipeline`, the phone's `pipeline` in `mobile/src/theme`, and CSS variables in `native/windows/web/src/theme/tokens.ts`. Each platform's existing token parity check holds its copy to the fixture.
- **Mac rule.** One pure rule in MightyCore, `MightyStructure.Tone`:
  - `status`: a status word → `DesignTone?`, reusing `DesignTone(blockStatus:)` (the `MobileMightySupport.status` buckets), plus `done` → done and `pending` → none.
  - `step`: a step → tone; the current step is `wait` while its request waits.
  - `column`: a board column → tone; a current step takes its newest request's status tone (run while it holds no request).
  - `card`: a card in its column → tone.
- **Windows rule.** Core mirrors it as `MightyStructure.Tone` (`OfStatus`, `OfStep`, `OfColumn`, `OfCard`). The WinUI fallback calls it directly. The web shell gets the tone words from the host (`WebStructureCard.Tint`, `WebStructureColumn.Tone`) and only turns them into classes.
- **Phone rule.** `pipelineTone` and `stepTone` go in `mobile/src/lib/status-tone.ts`; `columnTone` and `cardTone` go in `mobile/src/lib/pipeline-board.ts`. `toneOf` is fixed to read `failed` as err and `cancelled`/`interrupted` as stop, like the Mac's `DesignTone(status:)`.
- **No new colours.** Only `soft` and `mark` of the existing tones, each theme's own, so dark mode follows.
- **What stays as it is:** the running marching outline, the wait outline, status pills and the done, pending, error and stopped step chips (the current chip takes its column's tone, see the revision below). The Windows web shell's waiting cards switch from the blue `.live` border to the wait border.

**Tech Stack:**
- Swift 6 / SwiftUI with Swift Testing (MightyCore, the MightyClaude app)
- C# / .NET with WinUI 3 (MightyClaude.Core, MightyClaude.Core.Tests, MightyClaude.WinUI)
- React 19 + TypeScript + vitest (`native/windows/web`)
- React Native / Expo + jest (`mobile`)
- Markdown help (`docs/help/content`)

## Global Constraints

- **Branch and commits**
  - Work on `main` in the local MightyClaude checkout. Other sessions may commit to `main`: run `git status` and `git log -3` before each commit, and stage only the files the task names.
  - One commit per task. The subject reads `(area) what the user now gets`. The last line of the message is `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Do **not** push.
- **No builds or test suites** (the user runs them only when asked, 2026-10-23 rule). Write the tests; each task lists the commands to run later under "Test commands to run when the user asks — NOT now".
- **Quick text checks you DO run:**
  - `node scripts/check-design-tokens.js --check`
  - `bash scripts/check-style-freeze.sh` (must print OK)
  - `node scripts/check-locales.js --check`
  - `node scripts/help/build.mjs --check`
- **Frozen style engine paths** must not be touched:
  - `native/macos/Sources/MightyCore/Styles/*`
  - `mobile/src/lib/styles.ts`
  - `docs/mighty-styles.md`
  - `native/macos/Tests/MightyCoreTests/Style*.swift`
  - New test files must not start with `Style`.
- **Public repo:** no personal paths, emails or hosts in code, docs, commits or fixtures.
- **No new colour tokens.** Use each tone's `soft` and `mark` from each theme's own palette; dark mode comes from the theme.
- **Raw-number ratchets must not move** (`scripts/check-design-tokens.js`):
  - Swift: no new `.padding(n)` or `spacing: n` with n ≥ 2.
  - C#: no bare numbers ≥ 2 in `new Thickness(…)`, no `Spacing = n`, no colour literals, no `FontSize`/`FontFamily` sets.
  - Phone: no new `padding*`/`margin*`/`gap` numbers.
  - Use the tokens and constants above. A count that goes down fails too (stale budget), so don't delete counted literals either.
- **No locale keys added or removed** (the help text uses no new `{{ui:…}}`).

### Revision after the Task 1 review (2026-10-25)

The review found cards blending into a column of the same tone. These values and rules replace the ones in the task code below wherever they differ:
- **Values.** `pipeline.columnTintOpacity` = **0.6**, `pipeline.cardTintOpacity` = **0.35**, `cardBarWidth` = 3. The card is lighter than its column so it stands out. The parity checks assert the exact values instead of "card > column".
- **Current column.** A `current` step's column takes the tone of its newest request's status, through the same status buckets (running → run, waiting → wait, completed/done → done, error/failed → err, stopped/cancelled/interrupted → stop, unknown → run). A current column with no request runs. done, error, stopped and pending steps are unchanged. The Mac's `MightyStructure.Tone.column` holds this rule; `step(_:waiting:)` stays as it is.
- **Head chip.** A current step's chip uses its column's tone (`heroFill`/`heroInk` of that tone) instead of the accent, keeping the `circle.fill` glyph.
- **Pills on a tinted card.** Inside the pipeline view only, a status pill on a tinted card gets a 1pt hairline in the card tone's `mark`.

---

### Task 1: Shared `pipeline` tokens + Mac (MightyCore helper, tests, parity, `MightyStructureView`)

**Files:**
- `native/contracts/fixtures/design-tokens.json`: insert after line 380 (the `  ],` that closes `opacities`), before line 381 `  "metrics": {`.
- `native/macos/Sources/MightyCore/DesignMetrics.swift`: insert before the final `}` (line 157, after `Layout` closes at 156).
- `native/macos/Sources/MightyCore/MightyStructureTone.swift`: new file.
- `native/macos/Tests/MightyCoreTests/MightyStructureToneTests.swift`: new file.
- `native/macos/Tests/MightyCoreTests/DesignTokenParityTests.swift`: insert after line 96 (end of `metricTablesListTheirConstants`).
- `native/macos/Sources/MightyClaude/MightyStructureView.swift`, these ranges:
  - lines 105–106 (the `statusBarWidth` constant)
  - 419–435 (`kanban`)
  - 449 (the `ForEach` in `columnCards`)
  - 567–578 (`card`)
  - 583–600 (`requestCard`)
  - 603–620 (`resultCard`)
  - 624–665 (`summaryCard`)
  - 669–688 (`execution`)
  - 743–807 (`nodeCard`)
  - 898–904 (`cardSurface`)
  - plus one new private struct at file scope

**Interfaces:**
- Consumes:
  - `DesignTone` (`run, wait, done, err, stop, idle`)
  - `DesignTone.init(blockStatus:)` (MightyTimeline.swift:18)
  - `MightyStructure.Step`, `Board`, `BoardColumn`, `Card`, `Item`, `Node`
  - `Palette.soft(_ tone: DesignTone) -> Color` (DashboardView.swift:8)
  - `Palette.mark(_ tone: DesignTone) -> Color` (Palette.swift:94)
  - `MightyStructureBoardTests.boards(_:style:phase:startingNew:planHistory:)`
  - `MightyStructureTests.run(_:input:status:agents:final:source:)`, `.agent(_:parent:status:kind:)`, `.style(_:)`
- Produces:
  - fixture `pipeline`: `{ "columnTintOpacity": 0.45, "cardTintOpacity": 0.85, "cardBarWidth": 3 }`
  - `public enum DesignMetrics.Pipeline { static let columnTintOpacity: Double; static let cardTintOpacity: Double; static let cardBarWidth: CGFloat; static let all: [String: Double] }`
  - `public enum MightyStructure.Tone`:
    - `static func status(_ status: String) -> DesignTone?`
    - `static func step(_ step: MightyStructure.Step, waiting: Bool = false) -> DesignTone?`
    - `static func column(_ column: MightyStructure.BoardColumn) -> DesignTone?`
    - `static func card(_ card: MightyStructure.Card, in column: MightyStructure.BoardColumn) -> DesignTone?`
    - `static func newestRequest(_ column: MightyStructure.BoardColumn) -> MightyStructure.Item?` (internal)

- [ ] **Step 1: Add the fixture group.** In `native/contracts/fixtures/design-tokens.json`, between the `  ],` closing `opacities` and `  "metrics": {`, insert:

```json
  "pipeline": {
    "columnTintOpacity": 0.45,
    "cardTintOpacity": 0.85,
    "cardBarWidth": 3
  },
```

- [ ] **Step 2: Add the Mac constants.** In `native/macos/Sources/MightyCore/DesignMetrics.swift`, before the file's last `}`, insert:

```swift

    /// The 파이프라인 view's state colours (fixture `pipeline`): a column's cards sit on its tone's
    /// soft tint at `columnTintOpacity`, every card on the same tint at the stronger
    /// `cardTintOpacity`, with a `cardBarWidth` bar in the tone's mark down its leading edge
    /// (`MightyStructure.Tone`). Windows reads the same group as `DesignMetrics.Pipeline` in
    /// MightyClaude.Core and the phone as `pipeline` in mobile/src/theme.
    public enum Pipeline {
        public static let columnTintOpacity: Double = 0.45
        public static let cardTintOpacity: Double = 0.85
        public static let cardBarWidth: CGFloat = 3

        /// Every value by its fixture name.
        public static let all: [String: Double] = [
            "columnTintOpacity": columnTintOpacity, "cardTintOpacity": cardTintOpacity, "cardBarWidth": Double(cardBarWidth),
        ]
    }
```

- [ ] **Step 3: Write the failing tests** in `native/macos/Tests/MightyCoreTests/MightyStructureToneTests.swift`:

```swift
import Foundation
import Testing
@testable import MightyCore

/// The 파이프라인 view's state colours (spec 2026-10-25): a status, a step, a column and a card
/// take one of concept D's tones, a waiting request turns its current step amber, and a step
/// still to come takes none. Windows (`MightyStructureToneVerification`) and the phone
/// (`pipeline-tone.test.ts`) check the same cases.
struct MightyStructureToneTests {
    typealias T = MightyStructureTests
    typealias Tone = MightyStructure.Tone

    @Test func statusWordsTakeTheBlockBucketsDoneItsOwnAndPendingNone() {
        let words = ["running", "starting", "queued", "waiting", "completed", "done", "error", "failed", "stopped", "cancelled", "interrupted"]
        #expect(words.map(Tone.status) == [.run, .run, .run, .wait, .done, .done, .err, .err, .stop, .stop, .stop])
        #expect(Tone.status("pending") == nil)
    }

    @Test func stepsTakeTheirToneAndTheCurrentOneWaitsWithItsRequest() {
        #expect(Tone.step(.done) == .done)
        #expect(Tone.step(.current) == .run)
        #expect(Tone.step(.current, waiting: true) == .wait)
        #expect(Tone.step(.error) == .err)
        #expect(Tone.step(.stopped) == .stop)
        #expect(Tone.step(.pending) == nil)
        #expect(Tone.step(.pending, waiting: true) == nil)
        #expect(Tone.step(.done, waiting: true) == .done)
    }

    @Test func aFreeBoardTintsItsColumnsAndCards() throws {
        let board = try #require(MightyStructureBoardTests.boards([T.run("r1", status: "waiting", agents: [T.agent("a", status: "running")])]).first)
        // 요청 done, 실행 waits with its request, 결과 still to come.
        #expect(board.columns.map(Tone.column) == [.done, .wait, nil])
        let execution = board.columns[1]
        #expect(execution.cards.map { Tone.card($0, in: execution) } == [.wait, .run])
        // The 요청 and 결과 cards stand for their step.
        #expect(Tone.card(board.columns[0].cards[0], in: board.columns[0]) == .done)
        #expect(Tone.card(board.columns[2].cards[0], in: board.columns[2]) == nil)
        let ended = MightyStructureBoardTests.boards([T.run("r1", final: "끝"), T.run("r2", status: "failed"), T.run("r3", status: "cancelled"), T.run("r4", status: "running")])
        #expect(ended.map { $0.columns.map(Tone.column) } == [[.done, .done, .done], [.done, .err, .err], [.done, .stop, .stop], [.done, .run, nil]])
        #expect(ended.map { Tone.card($0.columns[2].cards[0], in: $0.columns[2]) } == [.done, .err, .stop, nil])
    }

    @Test func aPipelineTintsItsPhasesAndEachCardByItsOwnStatus() throws {
        let style = try T.style("ouroboros")
        let waiting = try #require(MightyStructureBoardTests.boards([
            T.run("r1", input: "ooo interview 시작", agents: [T.agent("x"), T.agent("y", parent: "x", status: "failed")]),
            T.run("r2", input: "ooo seed", status: "waiting"),
        ], style: style).first)
        #expect(waiting.columns.map(Tone.column) == [.done, .done, .wait, nil, nil, nil])
        let interview = waiting.columns[1]
        #expect(interview.cards.map { Tone.card($0, in: interview) } == [.done, .done, .err])
        let running = try #require(MightyStructureBoardTests.boards([T.run("r1", input: "ooo interview 시작"), T.run("r2", input: "ooo seed", status: "running")], style: style).first)
        #expect(running.columns.map(Tone.column) == [.done, .done, .run, nil, nil, nil])
    }
}
```

- [ ] **Step 4: Write the helper** in `native/macos/Sources/MightyCore/MightyStructureTone.swift`:

```swift
import Foundation

extension MightyStructure {
    /// The 파이프라인 view's colours by state (spec 2026-10-25): every column takes its step's
    /// tone as a wash under its cards, and every card its own as a tint and a bar down its
    /// leading edge (`DesignMetrics.Pipeline`). The tones are concept D's; there is no colour of
    /// their own. A step still to come takes none and stays on the page's ground. Windows
    /// (`MightyStructure.Tone` in MightyClaude.Core) and the phone (`pipelineTone`, `stepTone`,
    /// `columnTone`, `cardTone` in mobile/src/lib) keep the same rules.
    public enum Tone {
        /// A card's status, in the block buckets (`DesignTone(blockStatus:)`): failed is an error,
        /// cancelled and interrupted are stops, anything still in motion runs; `done` is done and
        /// `pending`, a step still to come, takes none.
        public static func status(_ status: String) -> DesignTone? {
            switch status {
            case "pending": return nil
            case "done": return .done
            default: return DesignTone(blockStatus: status)
            }
        }

        /// A step's: done, error and stopped in their own colours, the current step running, or
        /// waiting while its request waits (on a permission, a question or a plan); pending none.
        public static func step(_ step: Step, waiting: Bool = false) -> DesignTone? {
            switch step {
            case .done: return .done
            case .current: return waiting ? .wait : .run
            case .pending: return nil
            case .error: return .err
            case .stopped: return .stop
            }
        }

        /// A column's: its step's, the current one waiting while the column's newest request waits.
        public static func column(_ column: BoardColumn) -> DesignTone? {
            step(column.step.step, waiting: newestRequest(column)?.status == "waiting")
        }

        /// A card's: a free board's 요청 and 결과 stand for their step and take their column's;
        /// the 실행 card, a styled board's request and every sub-agent, task and 작업 알림 card
        /// take their own status's.
        public static func card(_ card: Card, in column: BoardColumn) -> DesignTone? {
            switch card {
            case .request, .result: return self.column(column)
            case .execution(let item), .summary(let item): return status(item.status)
            case .agent(let node, _, _): return status(node.status)
            }
        }

        /// The request of the column's last request card (its newest), nil for an empty column.
        static func newestRequest(_ column: BoardColumn) -> Item? {
            for card in column.cards.reversed() {
                switch card {
                case .request(let item), .execution(let item), .result(let item), .summary(let item): return item
                case .agent: continue
                }
            }
            return nil
        }
    }
}
```

- [ ] **Step 5: Add the parity test.** In `DesignTokenParityTests.swift`, after `metricTablesListTheirConstants` (after line 96), insert:

```swift

    /// `pipeline`: the 파이프라인 view's tints and status bar, the same numbers as `DesignMetrics.Pipeline`.
    @Test func pipelineTintsMatchTheFixture() throws {
        let table = try #require(try Self.load()["pipeline"] as? [String: Double], "pipeline")
        #expect(Set(table.keys) == Set(DesignMetrics.Pipeline.all.keys), "pipeline: Mac-only \(Set(DesignMetrics.Pipeline.all.keys).subtracting(table.keys).sorted()), fixture-only \(Set(table.keys).subtracting(DesignMetrics.Pipeline.all.keys).sorted())")
        for (name, value) in table {
            #expect(DesignMetrics.Pipeline.all[name] == value, "pipeline.\(name): fixture \(value), Mac \(DesignMetrics.Pipeline.all[name].map { "\($0)" } ?? "missing")")
        }
        // A card stands out in a column of its own tone.
        #expect(DesignMetrics.Pipeline.cardTintOpacity > DesignMetrics.Pipeline.columnTintOpacity)
        #expect(DesignMetrics.Pipeline.cardBarWidth == 3)
    }
```

- [ ] **Step 6: Change the view, `MightyStructureView.swift`.**

  - **(a) The constant, lines 105–106.** Replace

```swift
    /// A sub-agent card's status bar, and the glyph column of a tool call row.
    private static let statusBarWidth: CGFloat = 3
    private static let glyphWidth: CGFloat = 14
```
with
```swift
    /// The glyph column of a tool call row. A card's status bar is `DesignMetrics.Pipeline.cardBarWidth`.
    private static let glyphWidth: CGFloat = 14
```

  - **(b) `kanban`, line 424.** Replace `columnCards(column, board: board.id, onNewest: onNewest, width: cardWidth)` with

```swift
                    columnCards(column, board: board.id, onNewest: onNewest, width: cardWidth)
                        // The step's state as a wash under its cards; none while it is still to come.
                        .background { columnTint(MightyStructure.Tone.column(column)) }
```
    Then add this method right after `kanban` (after line 435):
```swift

    /// A column's state (`MightyStructure.Tone.column`): its tone's soft tint at the column's
    /// opacity, lighter than a card's, behind its cards; nothing for a step still to come.
    @ViewBuilder private func columnTint(_ tone: DesignTone?) -> some View {
        if let tone {
            RoundedRectangle(cornerRadius: Self.cardCorner, style: .continuous)
                .fill(Palette.soft(tone).opacity(DesignMetrics.Pipeline.columnTintOpacity))
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }
```

  - **(c) `columnCards`, line 449.** Replace `ForEach(column.cards) { card($0, width: width).id($0.id) }` with `ForEach(column.cards) { card($0, column: column, width: width).id($0.id) }`.

  - **(d) `card`, lines 567–578.** Replace the whole function with

```swift
    @ViewBuilder private func card(_ card: MightyStructure.Card, column: MightyStructure.BoardColumn, width: CGFloat) -> some View {
        let tone = MightyStructure.Tone.card(card, in: column)
        switch card {
        case .request(let item): requestCard(item, width: width, tone: tone)
        case .execution(let item):
            // The column says 실행 and the bar says which request: the card needs no title.
            execution(item, width: width, titled: false, tone: tone)
                .accessibilityLabel(L("graph.block.executionOf", ["n": "\(item.ordinal)"]))
        case .result(let item): resultCard(item, tone: tone)
        case .summary(let item): summaryCard(item, width: width, tone: tone)
        case .agent(let node, _, let parent): nodeCard(node, column: width, parent: parent, tone: tone)
        }
    }
```

  - **(e) `requestCard`, line 583.** Change the signature to `private func requestCard(_ item: MightyStructure.Item, width: CGFloat, tone: DesignTone?) -> some View`, and at line 596 change `.cardSurface()` to `.cardSurface(tone: tone)`.

  - **(f) `resultCard`, line 603.** Change the signature to `private func resultCard(_ item: MightyStructure.Item, tone: DesignTone?) -> some View`, and at line 617 change `.cardSurface()` to `.cardSurface(tone: tone)`.

  - **(g) `summaryCard`, line 624.** Change the signature to `private func summaryCard(_ item: MightyStructure.Item, width: CGFloat, tone: DesignTone?) -> some View`, and at line 659 change `.cardSurface()` to `.cardSurface(tone: tone)`. Keep the `MightyGraphActivityOutline` overlay at line 661 as it is.

  - **(h) `execution`, line 669.** Change the signature to `private func execution(_ item: MightyStructure.Item, width: CGFloat, titled: Bool, tone: DesignTone?) -> some View`, and at line 684 change `.cardSurface()` to `.cardSurface(tone: tone)`. Keep the outline overlay at line 685.

  - **(i) `nodeCard`, lines 743–807.**
    - Change the signature to `private func nodeCard(_ node: MightyStructure.Node, column: CGFloat, parent: String?, tone: DesignTone?) -> some View`.
    - In its doc comment, change "its status as a bar in its colour" to "its status as a tint and a bar in its colour (`MightyStructureCardTint`, the same bar every card wears)".
    - Replace lines 795–798

```swift
        .background(Palette.panel, in: shape)
        .overlay(alignment: .leading) {
            Rectangle().fill(Palette.mark(node.status)).frame(width: Self.statusBarWidth).allowsHitTesting(false).accessibilityHidden(true)
        }
```
    with
```swift
        // One bar for every card: the sub-agent card's own is the pipeline's (no second bar).
        .background(alignment: .leading) { MightyStructureCardTint(tone: tone) }
        .background(Palette.panel, in: shape)
```
    Keep `.clipShape(shape)`, the border overlay and the `MightyGraphActivityOutline` overlay as they are.

  - **(j) `cardSurface`, lines 898–904.** Replace with

```swift
    /// A card of the structure: the diagram's white block card around its padded content,
    /// with its state's tint and bar under the content (`MightyStructureCardTint`).
    func cardSurface(tone: DesignTone?) -> some View {
        padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(alignment: .leading) { MightyStructureCardTint(tone: tone) }
            .mightyBlockCard(cornerRadius: MightyStructureView.cardCorner)
    }
```
    and add at file scope, after that `private extension View { … }` (after line 915):
```swift

/// A 파이프라인 card's state (`MightyStructure.Tone.card`): its tone's soft tint at the card's
/// opacity over the card, and a `DesignMetrics.Pipeline.cardBarWidth` bar in the tone's mark
/// down its leading edge, both under the content and clipped by the card; nothing for a step
/// still to come. The running outline, the wait outline and the pills stay as they are, so
/// colour is never the only sign.
private struct MightyStructureCardTint: View {
    let tone: DesignTone?

    var body: some View {
        if let tone {
            ZStack(alignment: .leading) {
                Palette.soft(tone).opacity(DesignMetrics.Pipeline.cardTintOpacity)
                Rectangle().fill(Palette.mark(tone)).frame(width: DesignMetrics.Pipeline.cardBarWidth)
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }
}
```

- [ ] **Step 7: Quick text checks (run now).** `node scripts/check-design-tokens.js --check` must print `✓ design-token ratchets hold.` (macOS spacing count unchanged). `bash scripts/check-style-freeze.sh` must be OK.

- [ ] **Step 8: Commit.** Run `git status` and `git log -3`, then:
```
git add native/contracts/fixtures/design-tokens.json native/macos/Sources/MightyCore/DesignMetrics.swift native/macos/Sources/MightyCore/MightyStructureTone.swift native/macos/Tests/MightyCoreTests/MightyStructureToneTests.swift native/macos/Tests/MightyCoreTests/DesignTokenParityTests.swift native/macos/Sources/MightyClaude/MightyStructureView.swift
git commit -F - <<'EOF'
(mac) the pipeline view colours each step's column and every card by its state: a soft wash per column, a stronger tint and a 3pt status bar on every 요청, 실행, 결과, sub-agent and 작업 알림 card, a waiting request's step in the wait colour, a step still to come left plain

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

**Test commands to run when the user asks — NOT now:**
- `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-pipeline --filter '^MightyCoreTests\.(MightyStructureToneTests|DesignTokenParityTests|MightyStructureBoardTests|MightyStructureTests)/'`
- App compile: `DEVELOPER_DIR=/Library/Developer/CommandLineTools swift build --package-path native/macos --scratch-path /tmp/mc-pipeline --product MightyClaude`

---

### Task 2: Phone (status-tone, board tones, theme values, structure view, jest)

**Files:**
- `mobile/src/lib/status-tone.ts`: lines 1–24 (header and `toneOf`); add after line 24.
- `mobile/src/lib/pipeline-board.ts`: imports at lines 1–11; add after `boardCurrentStep` (ends at line 191).
- `mobile/src/theme/index.ts`: add after `toneColors` (ends at line 397).
- `mobile/src/components/structure-view.tsx`:
  - imports at lines 57 and 75–87
  - `RequestCard` 252–263, `ExecutionCard` 266–279, `ResultCard` 282–294
  - `FoldedCard` 310–343, `SummaryCard` 349–403, `AgentCard` 410–446
  - `CardCell` 455–476, `renderCard` 478–480
  - `ColumnPage` 603–638
  - style `nodeBar` at 1077
- `mobile/src/__tests__/timeline.test.ts`: lines 10–19.
- `mobile/src/__tests__/pipeline-tone.test.ts`: new file.

**How the phone gets tokens:** it does not read the json at runtime (Metro would not resolve `native/contracts` outside the app). It keeps its own constants in `mobile/src/theme/index.ts`, and jest holds them to the fixture by reading `../../../native/contracts/fixtures/design-tokens.json`, as `design-spacing.test.ts` does for `metrics.spacing`. Nothing is copied.

**Interfaces:**
- Consumes:
  - `Tone` (`'run' | 'wait' | 'done' | 'err' | 'stop' | 'idle'`)
  - `StructureStep`, `StructureItem`, `StructureNode` (`@/api/types`)
  - `BoardColumn`, `BoardCard`, `Board`, `structureBoards` (`@/lib/pipeline-board`)
  - `toneColors(palette, tone)`, `Palette`, `lightPalette`, `darkPalette`
- Produces:
  - `toneOf(status)`: now `failed` → `'err'`; `cancelled`, `interrupted` → `'stop'`
  - `pipelineTone(status: string): Tone | undefined`
  - `stepTone(step: StructureStep, waiting?: boolean): Tone | undefined`
  - `columnTone(column: BoardColumn): Tone | undefined`
  - `cardTone(card: BoardCard, column: BoardColumn): Tone | undefined`
  - `pipeline = { columnTintOpacity: 0.45, cardTintOpacity: 0.85, cardBarWidth: 3 } as const`
  - `toneMark(palette: Palette, tone: Tone): string`
  - `tintOver(base: string, over: string, opacity: number): string`

- [ ] **Step 1: Write the failing tests.** In `mobile/src/__tests__/timeline.test.ts`, after `expect(toneOf('stopped')).toBe('stop');` (line 15), insert:
```ts
    // The Mac's `DesignTone(status:)` buckets: failed is an error, cancelled and interrupted are stops.
    expect(toneOf('failed')).toBe('err');
    expect(toneOf('cancelled')).toBe('stop');
    expect(toneOf('interrupted')).toBe('stop');
```
Then create `mobile/src/__tests__/pipeline-tone.test.ts`:

```ts
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { MobileStructure, StructureColumn, StructureItem, StructureNode, StructureRow, StructureStep } from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import { cardTone, columnTone, structureBoards, type Board } from '@/lib/pipeline-board';
import { pipelineTone, stepTone, type Tone } from '@/lib/status-tone';
import { darkPalette, lightPalette, pipeline, tintOver, toneColors, toneMark } from '@/theme';

/**
 * The 파이프라인 view's state colours, held to the Mac's `MightyStructureToneTests`: the same
 * tone for a status, a step, a column and a card, and the same tints, bar and tone colours as
 * the contract (`native/contracts/fixtures/design-tokens.json` `pipeline` and `derived`).
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/design-tokens.json');
const contract = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  pipeline: Record<string, number>;
  derived: Record<'light' | 'dark', { tones: Record<Tone, { soft: string; mark: string }> }>;
};

beforeAll(() => resetLanguage('en'));
afterAll(() => resetLanguage());

function item(ordinal: number, overrides: Partial<StructureItem> = {}): StructureItem {
  return { runId: `r${ordinal}`, ordinal, status: 'completed', finished: true, activity: [], tally: { agents: 0, tools: 0 }, clusters: [], ...overrides };
}

function node(id: string, status: string, depth = 0): StructureNode {
  return { id, depth, title: id, status, kind: 'agent' };
}

function row(entry: StructureItem, request: StructureStep, execution: StructureStep, result: StructureStep): StructureRow {
  return { item: entry, request, execution, result };
}

function free(rows: StructureRow[]): MobileStructure {
  return { v: 1, shape: 'free', rows };
}

function columnTones(board: Board): (Tone | undefined)[] {
  return board.columns.map((column) => columnTone(column));
}

describe('pipeline tones', () => {
  it('gives a status the block bucket, done its own and pending none', () => {
    const words = ['running', 'starting', 'queued', 'waiting', 'completed', 'done', 'error', 'failed', 'stopped', 'cancelled', 'interrupted'];
    expect(words.map((word) => pipelineTone(word))).toEqual(['run', 'run', 'run', 'wait', 'done', 'done', 'err', 'err', 'stop', 'stop', 'stop']);
    expect(pipelineTone('pending')).toBeUndefined();
  });

  it('gives a step its tone, the current one waiting while its request waits', () => {
    const steps: StructureStep[] = ['done', 'current', 'pending', 'error', 'stopped'];
    expect(steps.map((step) => stepTone(step))).toEqual(['done', 'run', undefined, 'err', 'stop']);
    expect(stepTone('current', true)).toBe('wait');
    expect(stepTone('pending', true)).toBeUndefined();
    expect(stepTone('done', true)).toBe('done');
  });

  it('tints a free board: 요청 done, 실행 by its request, 결과 none until it ends', () => {
    const waiting = item(1, { status: 'waiting', finished: false, clusters: [{ nodes: [node('a', 'running')] }] });
    const board = structureBoards(free([row(waiting, 'done', 'current', 'pending')]))[0]!;
    expect(columnTones(board)).toEqual(['done', 'wait', undefined]);
    const execution = board.columns[1]!;
    expect(execution.cards.map((card) => cardTone(card, execution))).toEqual(['wait', 'run']);
    expect(cardTone(board.columns[0]!.cards[0]!, board.columns[0]!)).toBe('done');
    expect(cardTone(board.columns[2]!.cards[0]!, board.columns[2]!)).toBeUndefined();
    const ended = structureBoards(
      free([
        row(item(1), 'done', 'done', 'done'),
        row(item(2, { status: 'error' }), 'done', 'error', 'error'),
        row(item(3, { status: 'stopped' }), 'done', 'stopped', 'stopped'),
        row(item(4, { status: 'running', finished: false }), 'done', 'current', 'pending'),
      ]),
    );
    expect(ended.map(columnTones)).toEqual([
      ['done', 'done', 'done'],
      ['done', 'err', 'err'],
      ['done', 'stop', 'stop'],
      ['done', 'run', undefined],
    ]);
  });

  it('tints a pipeline by its phases, each card by its own status', () => {
    const phases = ['goal', 'interview', 'seed', 'run', 'evaluate', 'evolve'];
    const held: Record<string, StructureItem[]> = {
      interview: [item(1, { clusters: [{ nodes: [node('x', 'completed'), node('y', 'error', 1)] }] })],
      seed: [item(2, { status: 'waiting', finished: false })],
    };
    const columns: StructureColumn[] = phases.map((id, index) => ({
      id,
      title: id,
      step: index < 2 ? 'done' : index === 2 ? 'current' : 'pending',
      items: held[id] ?? [],
    }));
    const board = structureBoards({ v: 1, shape: 'phases', columns })[0]!;
    expect(columnTones(board)).toEqual(['done', 'done', 'wait', undefined, undefined, undefined]);
    const interview = board.columns[1]!;
    expect(interview.cards.map((card) => cardTone(card, interview))).toEqual(['done', 'done', 'err']);
  });
});

describe('pipeline colours', () => {
  it('keeps the contract tints and bar (pipeline)', () => {
    expect(Object.keys(contract.pipeline).sort()).toEqual(Object.keys(pipeline).sort());
    for (const [name, value] of Object.entries(contract.pipeline)) expect(pipeline[name as keyof typeof pipeline]).toBe(value);
    expect(pipeline.cardTintOpacity).toBeGreaterThan(pipeline.columnTintOpacity);
  });

  it.each([
    ['light', lightPalette],
    ['dark', darkPalette],
  ] as const)('draws every tone in the contract soft and mark (%s)', (mode, palette) => {
    for (const tone of ['run', 'wait', 'done', 'err', 'stop', 'idle'] as const) {
      expect(toneColors(palette, tone).soft).toBe(contract.derived[mode].tones[tone].soft);
      expect(toneMark(palette, tone)).toBe(contract.derived[mode].tones[tone].mark);
    }
  });

  it('lays a tint on a surface as one solid colour', () => {
    expect(tintOver('#000000', '#FFFFFF', 0.5)).toBe('#808080');
    expect(tintOver('#ECEEF3', '#E6EDFF', 0)).toBe('#ECEEF3');
    expect(tintOver('#ECEEF3', '#E6EDFF', 1)).toBe('#E6EDFF');
  });
});
```

- [ ] **Step 2: Update `mobile/src/lib/status-tone.ts`.** Replace lines 1–24 (doc, `Tone`, `toneOf`) with the block below and keep `isSettled` after it:

```ts
import type { StructureStep } from '@/api/types';

/**
 * The five colours concept D gives to state, and the one neutral. Everything that draws a
 * status — a status glyph, a stat tile, a Mighty timeline node —
 * goes through `toneOf`, so a word the host adds later lands on `idle` instead of a
 * colour it did not earn. The buckets are the Mac's `DesignTone(status:)`: failed is an
 * error, cancelled and interrupted are stops.
 */
export type Tone = 'run' | 'wait' | 'done' | 'err' | 'stop' | 'idle';

export function toneOf(status: string): Tone {
  switch (status) {
    case 'running':
      return 'run';
    case 'waiting':
      return 'wait';
    case 'completed':
      return 'done';
    case 'error':
    case 'failed':
      return 'err';
    case 'stopped':
    case 'cancelled':
    case 'interrupted':
      return 'stop';
    default:
      return 'idle';
  }
}

/**
 * The 파이프라인 view's colour for a card's status (the Mac's `MightyStructure.Tone.status`):
 * the block buckets, where anything still in motion runs, `done` is done, and a step still to
 * come (`pending`) takes none.
 */
export function pipelineTone(status: string): Tone | undefined {
  switch (status) {
    case 'pending':
      return undefined;
    case 'waiting':
      return 'wait';
    case 'completed':
    case 'done':
      return 'done';
    case 'error':
    case 'failed':
      return 'err';
    case 'stopped':
    case 'cancelled':
    case 'interrupted':
      return 'stop';
    default:
      return 'run';
  }
}

/**
 * A step's colour (`MightyStructure.Tone.step`): done, error and stopped in their own, the
 * current step running, or waiting while its request waits; a step still to come none.
 */
export function stepTone(step: StructureStep, waiting = false): Tone | undefined {
  switch (step) {
    case 'done':
      return 'done';
    case 'current':
      return waiting ? 'wait' : 'run';
    case 'error':
      return 'err';
    case 'stopped':
      return 'stop';
    default:
      return undefined;
  }
}
```

- [ ] **Step 3: Board tones in `mobile/src/lib/pipeline-board.ts`.**
  - After `import { stepStateWord } from '@/lib/structure';` (line 11), add `import { pipelineTone, stepTone, type Tone } from '@/lib/status-tone';`.
  - After `boardCurrentStep` (after line 191), insert:

```ts

/**
 * A column's colour (the Mac's `MightyStructure.Tone.column`): its step's, the current one
 * waiting while the column's newest request waits.
 */
export function columnTone(column: BoardColumn): Tone | undefined {
  let newest: StructureItem | undefined;
  for (const card of column.cards) if (card.kind !== 'agent') newest = card.item;
  return stepTone(column.step.step, newest?.status === 'waiting');
}

/**
 * A card's colour (`MightyStructure.Tone.card`): a free board's 요청 and 결과 stand for their
 * step and take their column's; the 실행 card, a pipeline's request and every sub-agent, task
 * and 작업 알림 card take their own status's.
 */
export function cardTone(card: BoardCard, column: BoardColumn): Tone | undefined {
  switch (card.kind) {
    case 'request':
    case 'result':
      return columnTone(column);
    case 'agent':
      return pipelineTone(card.node.status);
    default:
      return pipelineTone(card.item.status);
  }
}
```

- [ ] **Step 4: Theme values in `mobile/src/theme/index.ts`.** After `toneColors` (after line 397), insert:

```ts

/**
 * The 파이프라인 view's state colours, from `native/contracts/fixtures/design-tokens.json`
 * (`pipeline`): a column's cards sit on its tone's soft tint at `columnTintOpacity`, each card
 * on the same tint at the stronger `cardTintOpacity`, with a `cardBarWidth` bar in the tone's
 * mark down its leading edge. `pipeline-tone.test.ts` holds them to the contract.
 */
export const pipeline = {
  columnTintOpacity: 0.45,
  cardTintOpacity: 0.85,
  cardBarWidth: 3,
} as const;

/**
 * A tone's mark, the bar down a 파이프라인 card's edge (the Mac's `DesignPalette.mark`): the
 * fill where it holds 3:1 on a card, the amber's ink for wait, the quiet ink for idle.
 */
export function toneMark(palette: Palette, tone: Tone): string {
  switch (tone) {
    case 'wait':
      return palette.warning;
    case 'idle':
      return palette.textFaint;
    default:
      return toneColors(palette, tone).fill;
  }
}

/** `over` laid on `base` at `opacity`, as one solid `#RRGGBB`: a tint on a surface. */
export function tintOver(base: string, over: string, opacity: number): string {
  const channel = (hex: string, at: number) => parseInt(hex.slice(1 + 2 * at, 3 + 2 * at), 16);
  let out = '#';
  for (let at = 0; at < 3; at += 1) {
    const value = Math.round(channel(base, at) * (1 - opacity) + channel(over, at) * opacity);
    out += value.toString(16).padStart(2, '0').toUpperCase();
  }
  return out;
}
```

- [ ] **Step 5: Change the view, `mobile/src/components/structure-view.tsx`.**

  - **(a) Imports.**
    - In the `@/lib/pipeline-board` import (lines 37–56), add `cardTone,` and `columnTone,` in alphabetical order.
    - Line 57 becomes `import { toneOf, type Tone } from '@/lib/status-tone';`.
    - In the `@/theme` import (lines 75–87), add `pipeline,`, `tintOver,` and `toneMark,` in alphabetical order.

  - **(b) Card helper.** After `stepColors` (after line 109), insert:

```tsx

/**
 * A 파이프라인 card's state (`cardTone`): its tone's soft tint laid on the card's own surface and
 * a bar in the tone's mark down its leading edge; nothing for a step still to come.
 */
function cardTint(palette: Palette, tone: Tone | undefined): ViewStyle | undefined {
  if (!tone) return undefined;
  return {
    backgroundColor: tintOver(palette.surfaceRaised, toneColors(palette, tone).soft, pipeline.cardTintOpacity),
    borderLeftColor: toneMark(palette, tone),
    borderLeftWidth: pipeline.cardBarWidth,
  };
}
```

  - **(c) `RequestCard` (252–263).** The signature becomes `const RequestCard = memo(function RequestCard({ item, tone }: { item: StructureItem; tone?: Tone }) {`. Add `const palette = usePalette();` and change `<View style={styles.card}>` to `<View style={[styles.card, cardTint(palette, tone)]}>`.

  - **(d) `ExecutionCard` (266–279).** Same props change (`{ item, tone }: { item: StructureItem; tone?: Tone }`) and `const palette = usePalette();`. Its root becomes `<View accessibilityLabel={t('graph.block.executionOf', { n: item.ordinal })} style={[styles.card, cardTint(palette, tone)]}>`.

  - **(e) `ResultCard` (282–294).** Props become `{ item, tone }: { item: StructureItem; tone?: Tone }`; the root becomes `<View style={[styles.card, cardTint(palette, tone)]}>`.

  - **(f) `FoldedCard` (310).** The signature becomes `function FoldedCard({ item, onOpen, tone }: { item: StructureItem; onOpen: () => void; tone?: Tone }) {`. Its `style` becomes `style={({ pressed }) => [styles.card, cardTint(palette, tone), styles.folded, pressed && styles.pressed]}`.

  - **(g) `SummaryCard` (349–403).**
    - Add `tone,` to the destructured props and `tone?: Tone;` to the prop type.
    - `if (folded) return <FoldedCard item={item} tone={tone} onOpen={() => onToggle(item)} />;`
    - The root becomes `<View style={[styles.card, cardTint(palette, tone)]}>`.

  - **(h) `AgentCard` (410–446).**
    - The signature becomes `const AgentCard = memo(function AgentCard({ node, parent, tone }: { node: StructureNode; parent?: string; tone?: Tone }) {`.
    - Replace lines 419–420 with:
```tsx
    <View
      style={[
        styles.node,
        { marginLeft: node.depth * spacing.lg },
        tone && { backgroundColor: tintOver(palette.surface, toneColors(palette, tone).soft, pipeline.cardTintOpacity) },
      ]}
    >
      {/* The sub-agent card's own bar is the pipeline's bar: one bar, in the tone's mark. */}
      <View style={[styles.nodeBar, { backgroundColor: toneMark(palette, tone ?? toneOf(node.status)) }]} />
```
    - Style at line 1077: `nodeBar: { width: 3 },` becomes `nodeBar: { width: pipeline.cardBarWidth },`.

  - **(i) `CardCell` (455–476).**
    - The signature becomes `function CardCell({ card, tone }: { card: BoardCard; tone: Tone | undefined }) {`.
    - Each body passes the tone:
      - `<RequestCard item={card.item} tone={tone} />`
      - `<ExecutionCard item={card.item} tone={tone} />`
      - `<ResultCard item={card.item} tone={tone} />`
      - `<SummaryCard item={card.item} folded={isCardFolded(card.item, unfolded)} onToggle={onToggle} tone={tone} />`
      - `<AgentCard node={card.node} parent={card.parent} tone={tone} />`
    - Delete `renderCard` (lines 478–480).

  - **(j) `ColumnPage` (603–638).** After `const styles = useStyles(makeStyles);` insert:
```tsx
  const palette = usePalette();
  // The step's state as a wash behind its cards; none while it is still to come (`columnTone`).
  const tone = columnTone(column);
  const renderItem = useCallback(
    ({ item }: { item: BoardCard }) => <CardCell card={item} tone={cardTone(item, column)} />,
    [column],
  );
```
    Then replace `<View style={{ width }}>` with
```tsx
    <View
      style={[
        { width },
        tone && { backgroundColor: tintOver(palette.background, toneColors(palette, tone).soft, pipeline.columnTintOpacity) },
      ]}
    >
```
    and `renderItem={renderCard}` with `renderItem={renderItem}`.

- [ ] **Step 6: Quick text checks (run now).** `node scripts/check-design-tokens.js --check` (the phone count must stay the same: no new padding, margin or gap numbers) and `bash scripts/check-style-freeze.sh` (OK; `mobile/src/lib/styles.ts` untouched).

- [ ] **Step 7: Commit.** Run `git status` and `git log -3`, then:
```
git add mobile/src/lib/status-tone.ts mobile/src/lib/pipeline-board.ts mobile/src/theme/index.ts mobile/src/components/structure-view.tsx mobile/src/__tests__/timeline.test.ts mobile/src/__tests__/pipeline-tone.test.ts
git commit -F - <<'EOF'
(mobile) the phone's pipeline view colours each step's page and every card by its state as the Mac does, and a failed, cancelled or interrupted status reads as an error or a stop everywhere on the phone

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

**Test commands to run when the user asks — NOT now:**
- `cd mobile && npm run typecheck && npx jest src/__tests__/pipeline-tone.test.ts src/__tests__/timeline.test.ts src/__tests__/pipeline-board.test.ts src/__tests__/design-spacing.test.ts src/__tests__/palette-contrast.test.ts`
- If `node_modules` is missing, run `npm ci` inside `mobile/` only.

---

### Task 3: Windows (Core helper + Core.Tests, token parity, web shell, WinUI fallback)

**Files:**
- `native/windows/MightyClaude.Core/DesignMetrics.cs`: insert before line 232 (`/// <summary>Dashed outlines…`).
- `native/windows/MightyClaude.Core/MightyStructureTone.cs`: new file.
- `native/windows/MightyClaude.Core.Tests/MightyStructureToneVerification.cs`: new file.
- `native/windows/MightyClaude.Core.Tests/DesignTokenVerification.cs`: add after `MetricsAndOpacitiesMatchTheFixture` (ends around line 170).
- `native/windows/MightyClaude.Core.Tests/Verification.cs`: insert after line 285 and after line 1012.
- `native/windows/MightyClaude.WinUI/MainWindow.WebShell.Mighty.cs`: lines 36–39 (records), 394–420 (`WebStructure`).
- `native/windows/web/src/mighty/types.ts`: lines 123–131 (end of `StructureCard`, and `StructureColumn`).
- `native/windows/web/src/mighty/structureTone.ts`: new file.
- `native/windows/web/src/mighty/structureTone.test.ts`: new file.
- `native/windows/web/src/mighty/Structure.tsx`: lines 9–11 (imports), 77–78, 90, 110, 142, 401.
- `native/windows/web/src/mighty/mighty.css`: lines 285–290, plus a new block at the end of the file.
- `native/windows/web/src/theme/tokens.ts`: add `percent`; insert in `tokenStyleSheet` before line 53 (the "brand marks" comment).
- `native/windows/web/src/theme/tokens.test.ts`: import line and a new `it`.
- `native/windows/MightyClaude.WinUI/MainWindow.MightyStructure.cs`:
  - lines 97–110 (`StructureColumn` and the constants)
  - 284–297 (header block in `DrawMightyStructure`) and 303 (card call)
  - 569–570 (`BuildStructureKanban`)
  - 912–947 (`StructureCardView`)
  - 974–988 (`StructureRequestCard`), 1029–1040 (`StructureExecution`), 1062–1086 (`StructureResult`), 1095–1119 (`StructureSummary`)
  - 1164–1213 (`StructureNodeCard`)
  - 1254–1259 (`StructureSurface`)

**Interfaces:**
- Consumes:
  - `DesignTone` (StatusGlyph.cs:7)
  - `StatusGlyph.Tone(string)`, `MightyTimeline.Status(string)`
  - `MightyStructure.Step`, `BoardColumn`, `Card`, `CardKind`, `Item`
  - `DesignPalette.SoftToken(DesignTone)`
  - `DesignBrushes.Brush(DesignToken, double opacity)`, `DesignBrushes.Transparent`
  - `StructureMark(DesignTone)` (WinUI)
  - test helpers `MightyStructureVerification.Run/Agent/Style`
  - web: the `@tokens/design-tokens.json` import
- Produces:
  - `public static class DesignMetrics.Pipeline { const double ColumnTintOpacity = 0.45, CardTintOpacity = 0.85, CardBarWidth = 3; }`
  - `public static class MightyStructure.Tone`:
    - `DesignTone? OfStatus(string status)`
    - `DesignTone? OfStep(Step step, bool waiting = false)`
    - `DesignTone? OfColumn(BoardColumn column)`
    - `DesignTone? OfCard(Card card, BoardColumn column)`
  - `WebStructureCard(... , string? Tint = null)`, `WebStructureColumn(..., IReadOnlyList<WebStructureCard> Cards, string? Tone)`
  - TS: `StructureCard.tint?: string | null`, `StructureColumn.tone?: string | null`
  - `tintClass(tone: string | null | undefined): string`, `edgeClass(status: string): string`
  - `percent(opacity: number): string`
  - CSS variables `--pipeline-column-tint`, `--pipeline-card-tint`, `--pipeline-card-bar`

- [ ] **Step 1: Core constants.** In `DesignMetrics.cs`, before `    /// <summary>Dashed outlines, in pt as the Mac draws them.</summary>`, insert:

```csharp
    /// <summary>
    /// The 파이프라인 view's state colours (fixture <c>pipeline</c>, M/MightyCore/DesignMetrics.swift Pipeline): a column's cards sit
    /// on its tone's soft tint at <see cref="ColumnTintOpacity"/>, each card on the same tint at the stronger
    /// <see cref="CardTintOpacity"/>, with a <see cref="CardBarWidth"/> bar in the tone's mark down its leading edge
    /// (<see cref="MightyStructure.Tone"/>).
    /// </summary>
    public static class Pipeline
    {
        public const double ColumnTintOpacity = 0.45;
        public const double CardTintOpacity = 0.85;
        public const double CardBarWidth = 3;
    }

```

- [ ] **Step 2: Write the failing Core checks** in `native/windows/MightyClaude.Core.Tests/MightyStructureToneVerification.cs`:

```csharp
using MightyClaude.Core;
using static MightyStructureVerification;
using Board = MightyClaude.Core.MightyStructure.Board;
using Step = MightyClaude.Core.MightyStructure.Step;
using Tone = MightyClaude.Core.MightyStructure.Tone;

/// <summary>
/// The 파이프라인 view's state colours against the Mac's MightyStructureToneTests: a status, a step, a column and a card take the
/// same tone, a waiting request turns its current step amber, and a step still to come takes none.
/// </summary>
internal static class MightyStructureToneVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Words(IEnumerable<DesignTone?> tones) => string.Join(",", tones.Select(tone => tone?.ToString().ToLowerInvariant() ?? "-"));
    private static IReadOnlyList<Board> Boards(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style = null) => MightyStructure.Boards(MightyStructure.Make(runs, style, null));
    private static string Columns(Board board) => Words(board.Columns.Select(Tone.OfColumn));
    private static string Cards(MightyStructure.BoardColumn column) => Words(column.Cards.Select(card => Tone.OfCard(card, column)));

    internal static Task AStatusAndAStepTakeThePipelineTone()
    {
        var statuses = Words(new[] { "running", "starting", "queued", "waiting", "completed", "done", "error", "failed", "stopped", "cancelled", "interrupted", "pending" }.Select(Tone.OfStatus));
        Check(statuses == "run,run,run,wait,done,done,err,err,stop,stop,stop,-", "a status takes the Mac's block bucket, done is done and pending none: " + statuses);
        var steps = Words([Tone.OfStep(Step.Done), Tone.OfStep(Step.Current), Tone.OfStep(Step.Current, waiting: true), Tone.OfStep(Step.Error), Tone.OfStep(Step.Stopped),
            Tone.OfStep(Step.Pending), Tone.OfStep(Step.Pending, waiting: true), Tone.OfStep(Step.Done, waiting: true)]);
        Check(steps == "done,run,wait,err,stop,-,-,done", "a step's tone, the current one waiting while its request waits: " + steps);
        return Task.CompletedTask;
    }

    internal static Task AFreeBoardTintsItsColumnsAndCards()
    {
        var board = Boards([Run("r1", status: "waiting", agents: [Agent("a", status: "running")])])[0];
        Check(Columns(board) == "done,wait,-", "a waiting request: 요청 done, 실행 waits, 결과 to come: " + Columns(board));
        Check(Cards(board.Columns[1]) == "wait,run", "the 실행 card waits, its running sub-agent runs: " + Cards(board.Columns[1]));
        Check(Cards(board.Columns[0]) == "done" && Cards(board.Columns[2]) == "-", "the 요청 and 결과 cards take their column's tone: " + Cards(board.Columns[0]) + " " + Cards(board.Columns[2]));
        var ended = string.Join("|", Boards([Run("r1", final: "끝"), Run("r2", status: "failed"), Run("r3", status: "cancelled"), Run("r4", status: "running")]).Select(Columns));
        Check(ended == "done,done,done|done,err,err|done,stop,stop|done,run,-", "a free board by how its request ended: " + ended);
        return Task.CompletedTask;
    }

    internal static Task APipelineTintsItsPhasesAndEachCardByItsOwnStatus()
    {
        var style = Style("ouroboros");
        var waiting = Boards([Run("r1", "ooo interview 시작", agents: [Agent("x"), Agent("y", "x", "failed")]), Run("r2", "ooo seed", status: "waiting")], style)[0];
        Check(Columns(waiting) == "done,done,wait,-,-,-", "the phases done, the waiting request's phase waits, the rest to come: " + Columns(waiting));
        Check(Cards(waiting.Columns[1]) == "done,done,err", "each card its own status's: " + Cards(waiting.Columns[1]));
        var running = Boards([Run("r1", "ooo interview 시작"), Run("r2", "ooo seed", status: "running")], style)[0];
        Check(Columns(running) == "done,done,run,-,-,-", "the running request's phase runs: " + Columns(running));
        return Task.CompletedTask;
    }
}
```

In `DesignTokenVerification.cs`, after `MetricsAndOpacitiesMatchTheFixture`, add:

```csharp

    internal static Task PipelineTintsMatchTheFixture()
    {
        var table = Fixture.Value.GetProperty("pipeline").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        var constants = typeof(DesignMetrics.Pipeline).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToDictionary(f => Camel(f.Name), f => (double)f.GetRawConstantValue()!);
        Check(constants.Keys.ToHashSet().SetEquals(table.Keys), $"pipeline: C# [{string.Join(", ", constants.Keys.Order())}] vs fixture [{string.Join(", ", table.Keys.Order())}]");
        foreach (var (name, value) in table) Check(Near(constants[name], value), $"pipeline.{name}: expected {value}, got {constants[name]}");
        Check(DesignMetrics.Pipeline.CardTintOpacity > DesignMetrics.Pipeline.ColumnTintOpacity, "a card's tint is stronger than its column's, so it stands out in a column of its own tone");
        return Task.CompletedTask;
    }
```

In `Verification.cs`, after line 285 insert:
```csharp
        await Test("Mighty structure tone: a status and a step take the pipeline view's tone", MightyStructureToneVerification.AStatusAndAStepTakeThePipelineTone);
        await Test("Mighty structure tone: a free board tints its columns and cards", MightyStructureToneVerification.AFreeBoardTintsItsColumnsAndCards);
        await Test("Mighty structure tone: a pipeline tints its phases and each card by its own status", MightyStructureToneVerification.APipelineTintsItsPhasesAndEachCardByItsOwnStatus);
```
and after line 1012 (`metrics and opacities match the fixture`) insert:
```csharp
        await Test("design tokens: the pipeline view's tints and status bar match the fixture", DesignTokenVerification.PipelineTintsMatchTheFixture);
```

- [ ] **Step 3: Core helper** in `native/windows/MightyClaude.Core/MightyStructureTone.cs`:

```csharp
namespace MightyClaude.Core;

public static partial class MightyStructure
{
    /// <summary>
    /// The 파이프라인 view's colours by state (M/MightyCore/MightyStructureTone.swift, spec 2026-10-25): every column takes its step's
    /// tone as a wash under its cards, and every card its own as a tint and a bar down its leading edge (<see cref="DesignMetrics.Pipeline"/>).
    /// The tones are concept D's; there is no colour of their own. A step still to come takes none. The web shell gets these words from
    /// the host (MainWindow.WebShell.Mighty.cs); the WinUI view reads them here.
    /// </summary>
    public static class Tone
    {
        /// <summary>A card's status, in the block buckets (<see cref="MightyTimeline.Status"/>): failed is an error, cancelled and interrupted are stops, anything still in motion runs; <c>done</c> is done and <c>pending</c> none.</summary>
        public static DesignTone? OfStatus(string status) => status switch
        {
            "pending" => null,
            "done" => DesignTone.Done,
            _ => StatusGlyph.Tone(MightyTimeline.Status(status)),
        };

        /// <summary>A step's: done, error and stopped in their own colours, the current step running, or waiting while its request waits; pending none.</summary>
        public static DesignTone? OfStep(Step step, bool waiting = false) => step switch
        {
            Step.Done => DesignTone.Done,
            Step.Current => waiting ? DesignTone.Wait : DesignTone.Run,
            Step.Error => DesignTone.Err,
            Step.Stopped => DesignTone.Stop,
            _ => null,
        };

        /// <summary>A column's: its step's, the current one waiting while the column's newest request waits.</summary>
        public static DesignTone? OfColumn(BoardColumn column) =>
            OfStep(column.Step.Step, waiting: column.Cards.LastOrDefault(card => card.Kind != CardKind.Agent)?.Item.Status == "waiting");

        /// <summary>A card's: a free board's 요청 and 결과 stand for their step and take their column's; the 실행 card, a styled board's request and every sub-agent, task and 작업 알림 card their own status's.</summary>
        public static DesignTone? OfCard(Card card, BoardColumn column) => card.Kind switch
        {
            CardKind.Request or CardKind.Result => OfColumn(column),
            CardKind.Agent => OfStatus(card.Node!.Status),
            _ => OfStatus(card.Item.Status),
        };
    }
}
```

- [ ] **Step 4: Host sends the words** (`MainWindow.WebShell.Mighty.cs`).
  - **Records.** Line 38 ends `..., string? ExecutionNodeId = null, string? ResultNodeId = null);`; change it to `..., string? ExecutionNodeId = null, string? ResultNodeId = null, string? Tint = null);`. Line 39 becomes:
```csharp
internal sealed record WebStructureColumn(string Id, string Title, string Step, string StepWord, int Count, IReadOnlyList<WebStructureCard> Cards, string? Tone);
```
  - **`WebStructure`.** After `static string Tone(string status) => …;` (line 386), add:
```csharp
            // The 파이프라인 view's state colours (Core MightyStructure.Tone), null for a step still to come.
            static string? ToneWord(DesignTone? tone) => tone?.ToString().ToLowerInvariant();
```
  - Line 394 becomes `WebStructureCard Card(MightyStructure.Card card, MightyStructure.BoardColumn column)`. In its `return new(...)`, change the last argument line `node?.Depth ?? 0, node?.Prompt?.Preview, item.ExecutionNodeId, MightyGraphLayout.NodeID(runs[item.RunIndex], "result"));` to
```csharp
                    node?.Depth ?? 0, node?.Prompt?.Preview, item.ExecutionNodeId, MightyGraphLayout.NodeID(runs[item.RunIndex], "result"), ToneWord(MightyStructure.Tone.OfCard(card, column)));
```
  - Lines 419–420: replace `StepWord(column.Step.Step), column.Count, column.Cards.Select(Card).ToList())).ToList(), …` with
```csharp
                    StepWord(column.Step.Step), column.Count, column.Cards.Select(card => Card(card, column)).ToList(), ToneWord(MightyStructure.Tone.OfColumn(column)))).ToList(), board.Kind == MightyStructure.BoardKind.Pipeline ? styleName : null)).ToList();
```

- [ ] **Step 5: Web types and helper.**
  - **Types.** In `types.ts`, replace the end of `StructureCard` (`  executionNodeId?: string;\n  resultNodeId?: string;\n}` at lines 124–126) with
```ts
  executionNodeId?: string;
  resultNodeId?: string;
  /** The card's state colour (Core MightyStructure.Tone.OfCard): run, wait, done, err or stop; none for a step still to come. */
  tint?: string | null;
}
```
    and line 131 with
```ts
export interface StructureColumn { id: string; title: string; step: string; stepWord: string; count: number; cards: StructureCard[]; /** The step's state colour (MightyStructure.Tone.OfColumn), none while it is to come. */ tone?: string | null }
```
  - **Helper.** Create `native/windows/web/src/mighty/structureTone.ts`:
```ts
// The 파이프라인 view's state colours on the page: the host's tone words (Core MightyStructure.Tone, M/MightyStructureView.swift) as
// classes, and each card's moving edge. The rule itself is the host's; the page only draws it (mighty.css "pipeline").

const tones = new Set(['run', 'wait', 'done', 'err', 'stop']);

/** ` tinted tint-<tone>` for a tone the host sent; nothing for a step still to come or a word this page does not know. */
export function tintClass(tone: string | null | undefined): string {
  return tone && tones.has(tone) ? ' tinted tint-' + tone : '';
}

/** A card's edge while it moves (MightyGraphActivity): the run line while it runs, the wait line while it waits, none once settled. */
export function edgeClass(status: string): string {
  return status === 'running' ? ' live' : status === 'waiting' ? ' waiting' : '';
}
```
  - **Tests.** Create `native/windows/web/src/mighty/structureTone.test.ts`:
```ts
import { describe, expect, it } from 'vitest';
import { edgeClass, tintClass } from './structureTone';

describe('pipeline state colours', () => {
  it('turns the host tone into a class, none for a step to come or an unknown word', () => {
    expect(['run', 'wait', 'done', 'err', 'stop'].map((tone) => tintClass(tone))).toEqual([
      ' tinted tint-run', ' tinted tint-wait', ' tinted tint-done', ' tinted tint-err', ' tinted tint-stop',
    ]);
    expect(tintClass(null)).toBe('');
    expect(tintClass(undefined)).toBe('');
    expect(tintClass('idle')).toBe('');
    expect(tintClass('constructor')).toBe('');
  });

  it('gives a waiting card the wait edge, not the running blue', () => {
    expect(edgeClass('running')).toBe(' live');
    expect(edgeClass('waiting')).toBe(' waiting');
    expect(edgeClass('completed')).toBe('');
    expect(edgeClass('error')).toBe('');
    expect(edgeClass('stopped')).toBe('');
  });
});
```

- [ ] **Step 6: Web tokens.**
  - **`tokens.ts`.** After `hexAlpha` (line 36), add:
```ts

/** An opacity as a CSS percentage, rounded to a tenth so 0.45 reads 45% rather than 45.00000000000001%. */
export function percent(opacity: number): string {
  return `${Math.round(opacity * 1000) / 10}%`;
}
```
    In `tokenStyleSheet`, insert before the line `    // The agents' brand marks (ProviderMark.swift), the same in both themes.`:
```ts
    // The 파이프라인 view's state washes and its cards' status bar (fixture `pipeline`).
    `--pipeline-column-tint:${percent(tokens.pipeline.columnTintOpacity)};--pipeline-card-tint:${percent(tokens.pipeline.cardTintOpacity)};--pipeline-card-bar:${tokens.pipeline.cardBarWidth}px;` +
```
  - **`tokens.test.ts`.** Change the import to `import { kebab, paletteVariables, percent, terminalPalette, themeName, tokenStyleSheet } from './tokens';` and add inside `describe('tokens', …)`:
```ts
  it('carries the pipeline tints and status bar from the fixture', () => {
    expect(percent(0.45)).toBe('45%');
    expect(percent(0.085)).toBe('8.5%');
    const sheet = tokenStyleSheet();
    expect(sheet).toContain(`--pipeline-column-tint:${percent(tokens.pipeline.columnTintOpacity)};`);
    expect(sheet).toContain(`--pipeline-card-tint:${percent(tokens.pipeline.cardTintOpacity)};`);
    expect(sheet).toContain(`--pipeline-card-bar:${tokens.pipeline.cardBarWidth}px;`);
    expect(tokens.pipeline.cardTintOpacity).toBeGreaterThan(tokens.pipeline.columnTintOpacity);
  });
```

- [ ] **Step 7: Web view and CSS.**
  - **`Structure.tsx`.**
    - After line 9 add `import { edgeClass, tintClass } from './structureTone';`.
    - Replace lines 77–78 with
```tsx
  const base = 'structure-card tone-' + card.tone + ' kind-' + card.kind + edgeClass(card.status) + tintClass(card.tint);
```
    - Line 90: `className={'structure-card kind-request' + tintClass(card.tint)}`
    - Line 110: `className={'structure-card tone-' + card.tone + ' kind-result' + tintClass(card.tint)}`
    - Line 142: `className={'structure-node tone-' + card.tone + edgeClass(card.status) + tintClass(card.tint)}`
    - Line 401: `className={'structure-column step-' + column.step + tintClass(column.tone)}`
  - **`mighty.css`.** Replace lines 285–290 (the six `.structure-node…::before` rules) with
```css
.structure-node::before { content: ''; position: absolute; left: 0; top: 0; bottom: 0; width: var(--pipeline-card-bar); background: var(--idle); }
```
    and append at the end of the file:
```css

/* ── pipeline state colours (fixture `pipeline`, Core MightyStructure.Tone, M/MightyStructureView.swift) ── */
/* A column's cards sit on its tone's soft tint; each card on a stronger one with a bar in the tone's mark down its leading edge.
   A step still to come has none. The sub-agent card's own bar is the same bar. */
.tint-run { --tint-soft: var(--run-soft); --tint-mark: var(--run); }
.tint-wait { --tint-soft: var(--wait-soft); --tint-mark: var(--wait-text); }
.tint-done { --tint-soft: var(--done-soft); --tint-mark: var(--done); }
.tint-err { --tint-soft: var(--err-soft); --tint-mark: var(--err); }
.tint-stop { --tint-soft: var(--stop-soft); --tint-mark: var(--stop); }
.structure-column.tinted > .structure-column-body { background: color-mix(in srgb, var(--tint-soft) var(--pipeline-column-tint), transparent); border-radius: 10px; }
.structure-card.tinted, .structure-node.tinted { background-image: linear-gradient(color-mix(in srgb, var(--tint-soft) var(--pipeline-card-tint), transparent), color-mix(in srgb, var(--tint-soft) var(--pipeline-card-tint), transparent)); }
.structure-card.tinted { position: relative; overflow: hidden; }
.structure-card.tinted::before { content: ''; position: absolute; left: 0; top: 0; bottom: 0; width: var(--pipeline-card-bar); background: var(--tint-mark); }
.structure-node.tinted::before { background: var(--tint-mark); }
/* A waiting card wears the wait line, as the diagram's waiting block does; only a running one the run line (.live). */
.structure-card.waiting, .structure-node.waiting { border: 2px solid var(--wait); }
.structure-card.waiting { padding: calc(var(--space-sm) - 1px) calc(var(--inset-graph-block-body-h) - 1px); }
```

- [ ] **Step 8: WinUI fallback, `MainWindow.MightyStructure.cs`.**
  - **(a) `StructureColumn` (lines 97–107).** After `internal readonly StackPanel Cards = cards;` add
```csharp
            /// <summary>The column's state as a wash under its cards (<see cref="MightyStructure.Tone.OfColumn"/>), coloured with its header.</summary>
            internal readonly Border Wash = new() { CornerRadius = new CornerRadius(StructureCardCorner), IsHitTestVisible = false };
```
  - **(b) Constants (lines 109–110).** Drop `StructureStatusBar = DesignMetrics.Stroke.Rail, ` from the declaration. The comment becomes "A chip's height, a card's and a sub-agent card's corners and a tool call's glyph column; every card's status bar is `DesignMetrics.Pipeline.CardBarWidth`".
  - **(c) `BuildStructureKanban`.** After `var place = structureColumns[column.Id] = new(column.Id, view, header, scroll, cards);` (line 570) add
```csharp
                // Under the cards, behind the column's scroll view: its state's wash (DrawMightyStructure colours it).
                Grid.SetRow(place.Wash, 1); view.Children.Insert(1, place.Wash);
                AutomationProperties.SetAccessibilityView(place.Wash, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
```
  - **(d) `DrawMightyStructure`, lines 285–289.** Replace
```csharp
                    var headerKey = (StructureStepTitle(column.Step), column.Step.Step, column.Count, language, last);
                    if (!Equals(place.HeaderKey, headerKey))
                    {
                        place.Header.Child = StructureColumnHeader(column, last); place.HeaderKey = headerKey;
```
    with
```csharp
                    var tone = MightyStructure.Tone.OfColumn(column);
                    var headerKey = (StructureStepTitle(column.Step), column.Step.Step, column.Count, language, last, tone);
                    if (!Equals(place.HeaderKey, headerKey))
                    {
                        place.Header.Child = StructureColumnHeader(column, last); place.HeaderKey = headerKey;
                        // The step's state as a wash under its cards (M/MightyStructureView.swift columnTint); none while it is to come.
                        place.Wash.Background = tone is { } wash ? owner.brushes.Brush(DesignPalette.SoftToken(wash), DesignMetrics.Pipeline.ColumnTintOpacity) : owner.brushes.Transparent;
```
    and at line 303 change the call to
```csharp
                        var (view, built) = StructureCardView(card, width, GraphRequestTitle(runs[card.Item.RunIndex], card.Item.Ordinal), language, catalog, MightyStructure.Tone.OfCard(card, column));
```
  - **(e) `StructureCardView` (912–947).**
    - Its signature gains `, DesignTone? tint`: `private (FrameworkElement View, bool Built) StructureCardView(MightyStructure.Card card, double width, string heading, string language, IReadOnlyList<ModelOption>? catalog, DesignTone? tint)`.
    - Its cases become:
```csharp
                case MightyStructure.CardKind.Request:
                    key = (heading, item.Request, item.Input, item.NodeId, Open("request", item.RunId), requestCardFolds, language, tint);
                    build = () => StructureRequestCard(item, heading, requestCardFolds, tint);
                    break;
                case MightyStructure.CardKind.Execution:
                    key = (item.Ordinal, item.Status, item.Tally, item.Message, activity, item.Finished, Open("message", item.RunId), messageFolds, item.ExecutionNodeId, item.NodeId, language, structureMotion, tint);
                    build = () => StructureExecution(item, messageFolds, card.Id, tint);
                    break;
                case MightyStructure.CardKind.Result:
                    key = (item.Status, item.Finished, item.Answer, Open("answer", item.RunId), item.ExecutionNodeId, item.NodeId, language, tint);
                    build = () => StructureResult(item, tint);
                    break;
                case MightyStructure.CardKind.Summary:
                    var line = MightyStructure.Fold.ResultLine(item);
                    key = ((heading, item.Request, item.Input, item.NodeId, Open("request", item.RunId), requestFolds, language), (item.Ordinal, item.Status, item.Tally, item.Message, activity, item.Finished, Open("message", item.RunId), messageFolds), (item.ExecutionNodeId, line, structureMotion, tint));
                    build = () => StructureSummary(item, heading, requestFolds, messageFolds, line, card.Id, tint);
                    break;
                default:
                    var node = card.Node!; var open = Open("node", node.NodeId); var nodeWidth = MightyStructure.NodeTextWidth(width, node.Depth);
                    var folds = Folds(node.Prompt, MightyStructure.PromptLines, StructurePromptSize, nodeWidth) || Folds(node.Output, MightyStructure.OutputLines, StructureOutputSize, nodeWidth) || node.Activity.Count > 1;
                    var model = node.Model is { } named ? ModelUsageFormat.ShortName(named, catalog, versioned: true) : null;
                    key = (node, card.Parent, open, folds, model, language, structureMotion, tint);
                    build = () => StructureNodeCard(node, card.Parent, open, folds, model, card.Id, tint);
                    break;
```
  - **(f) Builders.** Add a trailing `DesignTone? tint` parameter and pass it to `StructureSurface`:
    - `StructureRequestCard(MightyStructure.Item item, string heading, bool folds, DesignTone? tint)`: line 982 becomes `var card = StructureSurface(request, tint);`.
    - `StructureExecution(MightyStructure.Item item, bool folds, string marks, DesignTone? tint)`: line 1036 becomes `var card = StructureSurface(body, tint);`.
    - `StructureResult(MightyStructure.Item item, DesignTone? tint)`: line 1083 becomes `var card = StructureSurface(body, tint);`.
    - `StructureSummary(MightyStructure.Item item, string heading, bool requestFolds, bool messageFolds, string? line, string marks, DesignTone? tint)`: line 1115 becomes `var card = StructureSurface(body, tint);`.
  - **(g) `StructureNodeCard` (1164).**
    - Its signature gains `, DesignTone? tint`.
    - Replace lines 1201–1204
```csharp
            // Inside the card's edge, its left corners following the card's own.
            var bar = new Border { Width = StructureStatusBar, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(StructureNodeCorner - BlockEdge, 0, 0, StructureNodeCorner - BlockEdge), Background = b.Brush(StructureMark(StatusGlyph.Tone(node.Status))), IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var face = new Grid(); face.Children.Add(bar); face.Children.Add(body);
```
      with
```csharp
            // The sub-agent card's own bar is the pipeline's bar (StructureTinted): one bar, with the card's tint under it.
            var face = StructureTinted(body, tint ?? StatusGlyph.Tone(node.Status), StructureNodeCorner);
```
      Keep `Child = face` in the `new Border { … }` below. In the summary comment, "its status as a 3pt bar in its mark colour" becomes "its status as a tint and a `DesignMetrics.Pipeline.CardBarWidth` bar in its mark colour".
  - **(h) `StructureSurface` (1254–1259).** Replace with
```csharp
        private Border StructureSurface(UIElement content, DesignTone? tint)
        {
            var b = owner.brushes;
            var padded = new Border
            {
                Child = content,
                Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Spacing.Sm - BlockEdge, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Spacing.Sm - BlockEdge),
            };
            return new Border
            {
                Child = StructureTinted(padded, tint, StructureCardCorner), Background = b.Brush(DesignToken.Card), CornerRadius = new CornerRadius(StructureCardCorner),
                BorderThickness = new Thickness(BlockEdge), BorderBrush = b.Brush(DesignToken.Line),
            };
        }

        /// <summary>
        /// A 파이프라인 card's state (M/MightyStructureView.swift MightyStructureCardTint): the tone's soft tint at
        /// <see cref="DesignMetrics.Pipeline.CardTintOpacity"/> under the content and a <see cref="DesignMetrics.Pipeline.CardBarWidth"/>
        /// bar in its mark (<see cref="StructureMark"/>) down the leading edge, inside the card's edge with corners following the
        /// card's own; the content alone for a step still to come. The outline and the pills stay as they are.
        /// </summary>
        private UIElement StructureTinted(UIElement content, DesignTone? tint, double corner)
        {
            if (tint is not { } tone) return content;
            var b = owner.brushes; var inner = corner - BlockEdge;
            var wash = new Border { Background = b.Brush(DesignPalette.SoftToken(tone), DesignMetrics.Pipeline.CardTintOpacity), CornerRadius = new CornerRadius(inner), IsHitTestVisible = false };
            var bar = new Border { Width = DesignMetrics.Pipeline.CardBarWidth, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(inner, 0, 0, inner), Background = b.Brush(StructureMark(tone)), IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(wash, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var face = new Grid(); face.Children.Add(wash); face.Children.Add(bar); face.Children.Add(content);
            return face;
        }
```
  - The answer's `RichEditBox` is already transparent (`MarkdownView`, MainWindow.Files.cs:999), so the result card's tint shows through. The waiting outline already uses the wait colour (`MightyGraphActivity.Outline`), so nothing changes there.

- [ ] **Step 9: Quick text checks (run now).** `node scripts/check-design-tokens.js --check`: Windows colour, spacing and font counts must be unchanged (no colour literals, only expression arguments in `new Thickness(…)`, no `FontSize`). `bash scripts/check-style-freeze.sh` must be OK.

- [ ] **Step 10: Commit.** Run `git status` and `git log -3`, then:
```
git add native/windows/MightyClaude.Core/DesignMetrics.cs native/windows/MightyClaude.Core/MightyStructureTone.cs native/windows/MightyClaude.Core.Tests/MightyStructureToneVerification.cs native/windows/MightyClaude.Core.Tests/DesignTokenVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs native/windows/MightyClaude.WinUI/MainWindow.WebShell.Mighty.cs native/windows/MightyClaude.WinUI/MainWindow.MightyStructure.cs native/windows/web/src/mighty/types.ts native/windows/web/src/mighty/structureTone.ts native/windows/web/src/mighty/structureTone.test.ts native/windows/web/src/mighty/Structure.tsx native/windows/web/src/mighty/mighty.css native/windows/web/src/theme/tokens.ts native/windows/web/src/theme/tokens.test.ts
git commit -F - <<'EOF'
(windows) the pipeline view colours each step's column and every card by its state as the Mac does, in the web window and the WinUI fallback alike, and a waiting card wears the wait outline instead of the running blue

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

**Test commands to run when the user asks — NOT now:**
- `bash <windows-core-checks>/core-checks.sh "$PWD"` (the local Core checks tool outside the repo) → must end with `RESULT: BASELINE`.
- `bash scripts/check-winui-compile.sh`
- `cd native/windows/web && npm run typecheck && npm test` (if `node_modules` is missing, `npm ci` inside `native/windows/web` only).

---

### Task 4: Help text in four languages (+ mighty-mode.md check)

**Files:**
- `docs/help/content/ko/agent-pane.md`, `docs/help/content/en/agent-pane.md`, `docs/help/content/zh/agent-pane.md`, `docs/help/content/ja/agent-pane.md`: insert one list item after line 123 (the bullet about the columns' cards), before line 124 (the bullet about folded text), in the `### … {#structure}` section.
- `docs/mighty-mode.md`: **no change.** It does not describe how the pipeline view looks; line 20 only says which notice cards the view keeps.

**Interfaces:**
- Consumes: the `{#structure}` section's list.
- Produces: one bullet per language; no new `{{ui:…}}` keys, no new shots (the `agent-structure` screenshot is refreshed by the next CI capture).

- [ ] **Step 1: ko.** In `docs/help/content/ko/agent-pane.md`, before the line starting `- 긴 글은 접혀 있고`, insert:
```markdown
- 상태는 색으로도 보입니다. 칸 바탕과 카드 바탕, 카드 왼쪽의 막대가 실행 중이면 파란색, 기다리는 중(권한·질문·계획)이면 노란색, 완료면 초록색, 오류면 빨간색, 중지면 회색이고, 남은 단계의 칸은 바탕 그대로입니다. 실행 중 카드의 움직이는 테두리와 상태 글자도 그대로 보입니다.
```
- [ ] **Step 2: en.** Before `- Long text is folded;`, insert:
```markdown
- Colour shows the state too: a column's background, each card's background and the bar down its left edge are blue while running, yellow while waiting (for a permission, a question or a plan), green when completed, red on an error and grey when stopped, and a step still to come keeps the plain background. A running card's moving border and the status words stay as they are.
```
- [ ] **Step 3: zh.** Before `- 长文本默认折叠`, insert:
```markdown
- 颜色也会表示状态：列的背景、卡片的背景和卡片左侧的竖条，运行中为蓝色，等待中（权限、提问、计划）为黄色，已完成为绿色，出错为红色，已停止为灰色；尚未进行的阶段保持原来的背景。运行中卡片的流动边框和状态文字照常显示。
```
- [ ] **Step 4: ja.** Before `- 長い文章は折りたたまれていて`, insert:
```markdown
- 状態は色でもわかります。列の背景、カードの背景、カード左端のバーは、実行中は青、待機中（許可・質問・プラン）は黄、完了は緑、エラーは赤、停止はグレーで、まだ来ていないステップの列は背景のままです。実行中のカードの動く枠線や状態の文字もそのまま表示されます。
```
- [ ] **Step 5: Quick text checks (run now).**
  - `node scripts/help/build.mjs --check`
  - `node scripts/check-locales.js --check`
  - `node scripts/check-design-tokens.js --check`
  - `bash scripts/check-style-freeze.sh`
  - All must pass. Help pages must keep the same headings and shots in every language.
- [ ] **Step 6: Commit.** Run `git status` and `git log -3`, then:
```
git add docs/help/content/ko/agent-pane.md docs/help/content/en/agent-pane.md docs/help/content/zh/agent-pane.md docs/help/content/ja/agent-pane.md
git commit -F - <<'EOF'
(docs) the help's pipeline section in all four languages says what the colours mean: blue running, yellow waiting, green done, red error, grey stopped, a step still to come left plain

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

**Test commands to run when the user asks — NOT now:** none beyond the text checks above. The help screenshot `agent-structure` is refreshed by the next CI capture.

---

## Self-review against the spec

- **Scope: cards + column background, Mac, Windows and phone.**
  - Column washes: Mac `columnTint`, WinUI `StructureColumn.Wash`, web `.structure-column.tinted > .structure-column-body`, phone `ColumnPage` background.
  - Card tint and 3pt bar on 요청 / 실행 / 결과 / summary / sub-agent / task / 작업 알림 cards: Mac `MightyStructureCardTint`, WinUI `StructureTinted`, web `.tinted::before`, phone `cardTint` / `nodeBar`.
- **Status → tone table.** running and unknown → run; waiting → wait; completed and done → done; error and failed → err; stopped, cancelled and interrupted → stop; pending → none.
  - Implemented once per platform: `MightyStructure.Tone.status` (reusing `DesignTone(blockStatus:)` and the `MobileMightySupport.status` buckets), Core `Tone.OfStatus` (reusing `MightyTimeline.Status` + `StatusGlyph.Tone`), phone `pipelineTone`.
  - Tested on all three. The phone's `toneOf` now maps failed and cancelled/interrupted like the Mac.
- **Column rule.** done → done, current → run (wait if its request waits), error → err, stopped → stop, pending → none: `step` / `OfStep` / `stepTone` plus `column` / `OfColumn` / `columnTone`, with tests for both kinds of board.
  - Decision taken: "that request" is the column's newest request card.
- **Card opacity > column opacity; bar = mark; tint = soft.** These come from the fixture `pipeline` (0.45 / 0.85 / 3). Mac `DesignTokenParityTests.pipelineTintsMatchTheFixture`, Windows `DesignTokenVerification.PipelineTintsMatchTheFixture`, phone jest and web `tokens.test.ts` all assert the values and that the card tint is the stronger one.
  - The phone test also holds `toneMark` and `toneColors().soft` to the fixture's derived `mark` / `soft` in both themes, so dark mode follows each theme.
- **No double bar on the sub-agent card.** The Mac replaces its overlay bar; WinUI replaces its bar Border; web reuses `.structure-node::before`; the phone reuses `nodeBar`.
- **Outlines and pills kept.** Mac outlines and WinUI `StructureOutlined` are untouched. Web waiting cards move from the blue `.live` border to the new `.waiting` wait border, as the spec asks.
- **Tests on every platform.** Mac `MightyStructureToneTests`; Windows `MightyStructureToneVerification` registered in `Verification.cs`; phone `pipeline-tone.test.ts` + `timeline.test.ts`; web `structureTone.test.ts` + `tokens.test.ts`.
  - **Flagged deviation:** the spec asks for a web unit test of status→tone and step→tone. On Windows that rule lives in Core and the host sends the words, so the web test only covers the class mapping and the wait edge, and the Windows rule is tested in Core.Tests. This avoids a third Windows copy of the rule.
- **Docs.** One line in each language's `agent-pane.md`. `mighty-mode.md` was checked and doesn't describe the look, so it's left alone. The screenshot is left to CI.
- **Constraints.** No new colour tokens. No locale keys. Frozen style paths untouched. The ratchets shouldn't move: only named constants and expressions are added, and no counted literal is removed. No personal paths: the Windows checks tool is written as a placeholder. Nothing is built or tested now.

### Critical Files for Implementation
- native/contracts/fixtures/design-tokens.json
- native/macos/Sources/MightyClaude/MightyStructureView.swift
- native/windows/MightyClaude.WinUI/MainWindow.MightyStructure.cs
- native/windows/MightyClaude.WinUI/MainWindow.WebShell.Mighty.cs
- mobile/src/components/structure-view.tsx
