import AppKit
import MightyCore
import SwiftUI

/// What the structure view keeps of a pane while the pane shows its diagram or timeline,
/// so coming back finds the same board and the same cards open. Held by the pane's
/// `MightyGraphView`.
final class MightyStructureMemory: ObservableObject {
    /// Texts opened past their folded lines, by `MightyStructureView.foldKey`.
    @Published var expanded = Set<String>()
    /// The board shown (`MightyStructure.Navigation`).
    @Published var navigation = MightyStructure.Navigation()
    /// The list of boards is open.
    @Published var listOpen = false
    /// Where the navigation controls sit in SwiftUI's global space, by name ("previous",
    /// "next", "list", "badge"), and the page's scroll view ("page"), for the GUI smoke: a
    /// SwiftUI button has no native view to find it by. Not published.
    var controlFrames: [String: CGRect] = [:]
    /// Which of the board's columns follow their end (`MightyGraphFollow.ColumnWatch`).
    /// Not published: scrolling redraws nothing.
    var columns = MightyGraphFollow.ColumnWatch()
    /// The columns the system reports a scroll of right now, such as a drag of a
    /// column's scroll bar (macOS 15 and later).
    var scrollingColumns = Set<String>()
    /// The user is dragging the board's empty space (`MightyStructurePanning`), and when
    /// the last such drag ended.
    var panning = false
    var panReleasedAt: TimeInterval = 0
    /// The board's wheel and trackpad gestures, which `MightyStructurePanning` moves the
    /// columns and the page by: the system never sees them, so it reports no scroll for them.
    var wheel = MightyStructureWheel.Tracker()
    /// The column the current or last drag or wheel gesture moves; nil when it moves
    /// the page only.
    var movingColumn: String?
    /// Each column's native probe (`MightyStructureColumnScrolling`), held weakly: the
    /// page's probe and the GUI smoke reach a column's scroll view by it. A board shown
    /// before may leave its own for a moment; they are out of the window.
    let columnProbes = NSHashTable<MightyStructureColumnProbe>.weakObjects()
    /// For the GUI diagnostics: column reports judged so far, and the last one's column,
    /// origin, time and whether it was judged as the user's move.
    var reports = 0
    var lastReport: (column: String, originY: Double, at: TimeInterval, userMoving: Bool)?
    /// Column `column` is moving under the user's hand: a scroll the system reports, a
    /// drag or the moment after it (`MightyStructurePan.releaseGrace`), or a wheel or
    /// trackpad gesture still going, that moves this column.
    func userMoving(_ column: String) -> Bool {
        scrollingColumns.contains(column)
            || (movingColumn == column
                && MightyStructureWheel.userMoving(scrollPhase: false, panning: panning, panReleasedAt: panReleasedAt,
                                                   wheel: wheel, now: ProcessInfo.processInfo.systemUptime))
    }
    /// The native scroll view of column `id`.
    @MainActor func columnScroll(_ id: String) -> NSScrollView? {
        columnProbes.allObjects.lazy.filter { $0.columnID == id }.compactMap(\.scrollView).first
    }

    /// Shows board `id` of `boards` and closes the list: what a list entry does.
    func choose(_ id: String, in boards: [MightyStructure.Board]) {
        navigation.select(id, in: boards)
        listOpen = false
    }
}

/// The Mighty graph drawn as its structure, as a kanban board: one request of a free pane
/// or one pipeline of a styled pane at a time (`MightyStructure.boards`). On top, ◀ and ▶
/// step between the boards and the title opens the list of them all; a badge says a newer
/// board arrived while an older one is shown. Under it the board's steps (요청 → 실행 →
/// 결과, or the style's phases) as chips joined by lines, with the board's status; then
/// one column per step, side by side, each with its header and its cards, which scroll
/// on their own.
///
/// The cards read on their own: the request alone, its 실행 part with the latest reply, the
/// last tool calls and the status, the answer once it finished, and each sub-agent's
/// prompt, output and last tool call. A long text opens in place; the small button in a
/// card's header shows its block in the diagram, as opening a timeline row aims the
/// diagram's camera.
struct MightyStructureView: View {
    let sessionID: String
    let provider: String
    /// The pane's boards, oldest first.
    let boards: [MightyStructure.Board]
    /// The pane's style, which names a pipeline whose first request has no words.
    var styleName: String? = nil
    /// Request titles' style prefixes, as the diagram and the timeline title them.
    let styleTitles: StyleRequestTitles
    /// The newest request: a new one is the user's own send, followed on the newest board.
    let newestRunID: String?
    /// Model catalog for a sub-agent's model name, as the diagram's capsules shorten it.
    var catalog: [ModelOption] = []
    /// The pane's board, open texts and follow state, kept across view switches.
    @ObservedObject var memory: MightyStructureMemory
    /// Shows a diagram node (`MightyGraphLayout.Node.id`) in the diagram.
    let onReveal: (String) -> Void

    /// Scroll bars that always show take their width from the columns' cards.
    @ViewState private var legacyScrollers = NSScroller.preferredScrollerStyle == .legacy

    private static let chipHeight: CGFloat = 20
    /// Each column's scroll space, and the prefix of its end's id.
    private static let scrollSpace = "mighty-structure-scroll"
    private static let endID = "mighty-structure-end"
    /// A card's and a sub-agent card's corners.
    static let cardCorner: CGFloat = 10
    private static let nodeCorner: CGFloat = 8
    /// A sub-agent card's status bar, and the glyph column of a tool call row.
    private static let statusBarWidth: CGFloat = 3
    private static let glyphWidth: CGFloat = 14
    /// Type sizes: a card's texts, a sub-agent's output, its prompt.
    private static let textSize: CGFloat = 12
    private static let outputSize: CGFloat = 11
    private static let promptSize: CGFloat = 10.5
    /// The list of boards.
    private static let listWidth: CGFloat = 340
    private static let listHeight: CGFloat = 420

    var body: some View {
        let index = memory.navigation.index(in: boards)
        let board = index.map { boards[$0] }
        VStack(alignment: .leading, spacing: 0) {
            if let index, let board {
                navigationBar(index: index, board: board)
                header(board)
                Divider().overlay(Palette.border)
            }
            page(board, onNewest: index.map { $0 == boards.count - 1 } ?? true)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSScroller.preferredScrollerStyleDidChangeNotification)) { _ in
            legacyScrollers = NSScroller.preferredScrollerStyle == .legacy
        }
        .onPreferenceChange(MightyStructureControlFrames.self) { memory.controlFrames = $0 }
        .background(Palette.raised)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-\(sessionID)")
    }

    // MARK: Navigation

    /// ◀, the board's place with the list, ▶, and the badge of a newer board.
    private func navigationBar(index: Int, board: MightyStructure.Board) -> some View {
        let position = Self.position(board, index: index, count: boards.count)
        return HStack(spacing: DesignMetrics.Spacing.sm) {
            navButton("chevron.left", name: "previous", label: L("graph.structure.board.previous"), disabled: index == 0) {
                memory.navigation.step(-1, in: boards)
            }
            Button { memory.listOpen.toggle() } label: {
                HStack(spacing: DesignMetrics.Spacing.xs) {
                    Text(position).font(.system(size: Self.textSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1).monospacedDigit()
                    Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold)).foregroundStyle(Palette.ink2)
                }
                .frame(height: DesignMetrics.Layout.hitTarget)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(L("graph.structure.board.list"))
            .accessibilityLabel(L("graph.structure.board.list"))
            .accessibilityValue(position)
            .accessibilityIdentifier("mighty-structure-list-\(sessionID)")
            .background(controlMark("list"))
            .popover(isPresented: $memory.listOpen, arrowEdge: .bottom) { boardList(index) }
            navButton("chevron.right", name: "next", label: L("graph.structure.board.next"), disabled: index == boards.count - 1) {
                memory.navigation.step(1, in: boards)
            }
            if memory.navigation.hasUnseen(in: boards) { newerBadge(board.kind) }
            Spacer(minLength: 0)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.top, DesignMetrics.Spacing.sm)
    }

    /// "요청 3 / 7" or "파이프라인 2 / 3".
    static func position(_ board: MightyStructure.Board, index: Int, count: Int) -> String {
        let subs = ["n": "\(index + 1)", "count": "\(count)"]
        return board.kind == .request ? L("graph.structure.board.request", subs) : L("graph.structure.board.pipeline", subs)
    }

    private func navButton(_ icon: String, name: String, label: String, disabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon).font(.system(size: 11, weight: .semibold))
                .foregroundStyle(disabled ? Palette.ink2.opacity(0.4) : Palette.ink)
                .frame(width: DesignMetrics.Layout.hitTarget, height: DesignMetrics.Layout.hitTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .help(label)
        .accessibilityLabel(label)
        .accessibilityIdentifier("mighty-structure-\(name)-\(sessionID)")
        .background(controlMark(name))
    }

    /// A newer board arrived while an older one is shown: one click shows the newest.
    private func newerBadge(_ kind: MightyStructure.Board.Kind) -> some View {
        let text = kind == .request ? L("graph.structure.board.newRequest") : L("graph.structure.board.newPipeline")
        return Button { memory.navigation.showNewest(in: boards) } label: {
            Text(text).font(.system(size: 10, weight: .bold)).foregroundStyle(Palette.onAccent).lineLimit(1)
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xxs)
                .background(Palette.accent, in: Capsule())
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(L("graph.structure.board.showNewest"))
        .accessibilityLabel(text)
        .accessibilityHint(L("graph.structure.board.showNewest"))
        .accessibilityIdentifier("mighty-structure-newer-\(sessionID)")
        .background(controlMark("badge"))
    }

    /// Every board, newest last: its number, title, status and steps. A click shows it.
    private func boardList(_ shown: Int) -> some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Inset.listGap) {
                    ForEach(Array(boards.enumerated()), id: \.element.id) { index, board in
                        listRow(board, index: index, shown: index == shown).id(board.id)
                    }
                }
                .padding(DesignMetrics.Inset.popover)
            }
            .onAppear { proxy.scrollTo(boards[shown].id, anchor: .center) }
        }
        .frame(width: Self.listWidth).frame(maxHeight: Self.listHeight)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.board.list"))
    }

    /// One entry of the list: the board's number, status, title and steps.
    private func listRow(_ board: MightyStructure.Board, index: Int, shown: Bool) -> some View {
        let title = title(board)
        let steps = stepsSummary(board)
        let fill = shown ? Palette.track : Color.clear
        let traits: AccessibilityTraits = shown ? [.isButton, .isSelected] : .isButton
        return Button { memory.choose(board.id, in: boards) } label: {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                Text(verbatim: "\(index + 1)").font(.system(size: 11, weight: .bold)).monospacedDigit().foregroundStyle(Palette.ink2)
                Image(systemName: Self.statusIcon(board.status)).font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Palette.mark(board.status))
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text(verbatim: title).font(.system(size: Self.textSize, weight: shown ? .bold : .regular))
                        .foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.tail)
                    Text(verbatim: steps).font(.system(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xs)
            .background(fill, in: RoundedRectangle(cornerRadius: Self.nodeCorner, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Self.position(board, index: index, count: boards.count) + ", " + title)
        .accessibilityValue(MightyGraphView.statusLabel(board.status) + ", " + steps)
        .accessibilityAddTraits(traits)
    }

    /// The board's words: its first request's first line, else the style's name, else its place.
    private func title(_ board: MightyStructure.Board) -> String {
        board.headline ?? (board.kind == .pipeline ? styleName : nil)
            ?? Self.position(board, index: board.ordinal - 1, count: boards.count)
    }

    /// "요청 ✓ → 실행 ● → 결과 ○": each step and where it stands.
    private func stepsSummary(_ board: MightyStructure.Board) -> String {
        board.steps.map { stepName($0) + " " + Self.stepMark($0.step) }.joined(separator: " → ")
    }

    private static func stepMark(_ step: MightyStructure.Step) -> String {
        switch step {
        case .done: "✓"
        case .current: "●"
        case .pending: "○"
        case .error: "✕"
        case .stopped: "■"
        }
    }

    private static func statusIcon(_ status: String) -> String {
        switch status {
        case "completed": "checkmark.circle.fill"
        case "error": "exclamationmark.triangle.fill"
        case "stopped": "stop.circle.fill"
        case "waiting": "pause.circle.fill"
        default: "circle.dotted"
        }
    }

    /// Reports a navigation control's frame (`MightyStructureMemory.controlFrames`).
    private func controlMark(_ name: String) -> some View {
        GeometryReader { control in
            Color.clear.preference(key: MightyStructureControlFrames.self, value: [name: control.frame(in: .global)])
        }
    }

    // MARK: Board header

    /// The board's status and first line, then its steps as chips joined by lines that
    /// are green once the step before them is done.
    private func header(_ board: MightyStructure.Board) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                MightyGraphActivityIndicator(status: board.status, tint: Palette.run)
                MightyStatusPill(text: MightyGraphView.statusLabel(board.status), tone: DesignTone(status: board.status))
                Text(verbatim: title(board)).font(Palette.heading(13)).foregroundStyle(Palette.ink).lineLimit(1).truncationMode(.tail)
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: 0)
            }
            HStack(spacing: 0) {
                ForEach(Array(board.steps.enumerated()), id: \.element.id) { index, step in
                    chip(stepName(step), step: step.step)
                    if index < board.steps.count - 1 {
                        Rectangle().fill(step.step == .done ? Palette.done : Palette.track)
                            .frame(minWidth: DesignMetrics.Spacing.md, maxWidth: .infinity).frame(height: 2)
                            .accessibilityHidden(true)
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(L("guidedPanel.phasesAccessibility"))
        }
        .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
    }

    /// A free step's localized name, or the phase's title.
    private func stepName(_ step: MightyStructure.BoardStep) -> String {
        if let title = step.title { return title }
        switch step.id {
        case "request": return L("graph.structure.column.request")
        case "execution": return L("graph.structure.column.execution")
        default: return L("graph.structure.column.result")
        }
    }

    private func chip(_ title: String, step: MightyStructure.Step) -> some View {
        let look = Self.look(step)
        return HStack(spacing: DesignMetrics.Spacing.xs) {
            Image(systemName: look.icon).font(.system(size: 8, weight: .bold))
            Text(verbatim: title).font(.system(size: 11, weight: step == .current ? .bold : .semibold))
                .lineLimit(1).truncationMode(.tail)
        }
        .foregroundStyle(look.ink)
        .padding(.horizontal, DesignMetrics.Spacing.md).frame(height: Self.chipHeight)
        .background(look.fill, in: Capsule())
        // A long phase title is cut first; the whole title is in the help.
        .layoutPriority(1)
        .help(title)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(stepAccessibility(title, step))
    }

    /// done is green, current the accent, pending grey; an error or a stop in its own colour.
    private static func look(_ step: MightyStructure.Step) -> (fill: Color, ink: Color, icon: String) {
        switch step {
        case .done: (Palette.heroFill(.done), Palette.heroInk(.done), "checkmark")
        case .current: (Palette.accent, Palette.onAccent, "circle.fill")
        case .pending: (Palette.track, Palette.ink2, "circle")
        case .error: (Palette.heroFill(.err), Palette.heroInk(.err), "xmark")
        case .stopped: (Palette.heroFill(.stop), Palette.heroInk(.stop), "stop.fill")
        }
    }

    private func stepAccessibility(_ name: String, _ step: MightyStructure.Step) -> String {
        let state = switch step {
        case .done: L("graph.structure.step.done")
        case .current: L("graph.structure.step.current")
        case .pending: L("graph.structure.step.pending")
        case .error: L("graph.state.error")
        case .stopped: L("graph.state.stopped")
        }
        return L("graph.structure.stepAccessibility", ["name": name, "state": state])
    }

    // MARK: Kanban

    /// The board's columns side by side in a page that scrolls sideways, only when they
    /// do not fit. Each column keeps its header on top and scrolls its cards on its own;
    /// the page and the columns are dragged and wheeled as the diagram is
    /// (`MightyStructurePanning`). On the newest board each column follows its own end as
    /// it grows (`MightyGraphFollow.ColumnWatch`); an older board opens at its columns' tops.
    private func page(_ board: MightyStructure.Board?, onNewest: Bool) -> some View {
        GeometryReader { geo in
            // The pane's size, read from outside the scroll view, so the columns' size
            // never feeds back into what is measured.
            let available = geo.size.width - 2 * DesignMetrics.Inset.graphBlockBodyH
            let gap = DesignMetrics.Spacing.lg
            let count = board?.columns.count ?? 0
            let width = MightyStructure.columnWidth(available: available, count: count, gap: gap)
            // Scroll bars that always show: a column's takes its cards' width, and the
            // page's, when the columns do not fit, the columns' height.
            let scroller = legacyScrollers ? NSScroller.scrollerWidth(for: .regular, scrollerStyle: .legacy) : 0
            let overflows = CGFloat(count) * width + gap * CGFloat(max(0, count - 1)) > available + 0.5
            let height = max(0, geo.size.height - DesignMetrics.Spacing.md - (overflows ? scroller : 0))
            ScrollViewReader { proxy in
                ScrollView(.horizontal) {
                    Group {
                        if let board {
                            kanban(board, onNewest: onNewest, width: width, cardWidth: width - scroller, height: height)
                                .id(board.id)
                        } else {
                            Text(L("graph.timeline.empty")).font(.system(size: 12)).foregroundStyle(Palette.ink2)
                                .padding(.vertical, DesignMetrics.Spacing.md)
                        }
                    }
                    .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.top, DesignMetrics.Spacing.md)
                }
                // Dragging the board's empty space moves it, as in the diagram.
                .background(MightyStructurePanning(sessionID: sessionID, memory: memory))
                // The page's own frame in the same space as the controls' frames: the GUI
                // smoke maps that space onto the window through the pan probe, a native view
                // with this very frame.
                .background(controlMark("page"))
                // Another board, or a new request on the newest one: the page shows its
                // first column again; the columns follow or open at their top themselves.
                .onChange(of: board?.id) { _, id in toFirstColumn(id, proxy) }
                .onChange(of: newestRunID) { _, id in
                    if id != nil, onNewest { toFirstColumn(board?.id, proxy) }
                }
            }
        }
    }

    /// The page back at its first column, after the current update.
    private func toFirstColumn(_ boardID: String?, _ proxy: ScrollViewProxy) {
        guard let boardID else { return }
        DispatchQueue.main.async { proxy.scrollTo(boardID, anchor: .topLeading) }
    }

    /// One column per step, each with its header and its cards.
    private func kanban(_ board: MightyStructure.Board, onNewest: Bool, width: CGFloat, cardWidth: CGFloat, height: CGFloat) -> some View {
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.lg) {
            ForEach(board.columns) { column in
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    columnHeader(column)
                    columnCards(column, board: board.id, onNewest: onNewest, width: cardWidth)
                }
                .frame(width: width, height: height, alignment: .topLeading)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(L("graph.structure.board.columnAccessibility",
                                      ["name": stepName(column.step), "state": stepState(column.step.step), "count": "\(column.count)"]))
                .accessibilityIdentifier("mighty-structure-column-\(column.id)")
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-board-\(board.id)")
    }

    /// A column's cards in a scroll view of their own, under its header. Every report of
    /// where the column stands decides whether it follows its end and where it scrolls
    /// (`MightyGraphFollow.ColumnWatch`), on the newest board only.
    private func columnCards(_ column: MightyStructure.BoardColumn, board: String, onNewest: Bool, width: CGFloat) -> some View {
        let id = column.id
        let end = Self.endID + "-" + id
        return ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 0) {
                    // One board holds one request's or one pipeline's cards: drawn whole,
                    // so a follow finds every card laid out.
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                        ForEach(column.cards) { card($0, width: width).id($0.id) }
                    }
                    // The column's last card is its newest.
                    .background(GeometryReader { cards in
                        Color.clear.preference(key: MightyStructurePage.self, value: .init(newestBottom: cards.frame(in: .named(Self.scrollSpace)).maxY))
                    })
                    .padding(.bottom, DesignMetrics.Spacing.md)
                    // The column's end, under its padding, which a follow scrolls to.
                    Color.clear.frame(width: 1, height: 1).id(end)
                }
                .frame(width: width, alignment: .topLeading)
                .background(GeometryReader { content in
                    Color.clear.preference(key: MightyStructurePage.self, value: .init(content: content.frame(in: .named(Self.scrollSpace))))
                })
            }
            .coordinateSpace(name: Self.scrollSpace)
            // The page's probe finds the column's native scroll view by this one.
            .background(MightyStructureColumnScrolling(sessionID: sessionID, columnID: id, memory: memory))
            // The column's height joins the same report: a taller composer moves no
            // content, yet may hide the column's end.
            .background(GeometryReader { pane in
                Color.clear.preference(key: MightyStructurePage.self, value: .init(visible: pane.size.height))
            })
            .modifier(MightyStructureScrollRoles(memory: memory, column: id, opensAtEnd: onNewest))
            .onPreferenceChange(MightyStructurePage.self) { report in
                guard let content = report.content, let visible = report.visible else { return }
                let page = MightyGraphFollow.Page(originY: Double(-content.minY), visibleHeight: Double(visible), documentHeight: Double(content.height),
                                                  newestID: id, newestBottom: report.newestBottom.map { Double($0 - content.minY) })
                let moving = memory.userMoving(id)
                memory.reports += 1
                memory.lastReport = (id, page.originY, ProcessInfo.processInfo.systemUptime, moving)
                scroll(memory.columns.observe(board: board, newest: onNewest, column: id, page, userScrolling: moving), proxy, to: end, column: id)
            }
            .onAppear { scroll(memory.columns.appeared(board: board, newest: onNewest, column: id), proxy, to: end, column: id) }
            // A column torn down in the middle of a scroll bar's drag never reports its end.
            .onDisappear { memory.scrollingColumns.remove(id) }
            // A new request on the newest board is the user's own: every column shows its
            // end, and follows it from then on.
            .onChange(of: newestRunID) { _, run in
                guard run != nil, onNewest else { return }
                scroll(memory.columns.newRequest(board: board, column: id), proxy, to: end, column: id)
            }
        }
    }

    /// A column's end at its bottom, after the current update: a report arrives while
    /// the column is being laid out. A column's newest card is its last, so either
    /// target of the follow rule is the column's own end. The column's native clip moves:
    /// a scroll to a view inside the column may scroll the page around it too. Before
    /// the column's native view is known, its end is scrolled to.
    private func scroll(_ target: MightyGraphFollow.Scroll?, _ proxy: ScrollViewProxy, to end: String, column: String) {
        guard target != nil else { return }
        DispatchQueue.main.async {
            // A drag, a wheel or a trackpad gesture in the column is the user's: nothing
            // scrolls under it, even a scroll asked for just before it began.
            guard !memory.userMoving(column) else { return }
            // Nor once the column stopped following since: a text opened in it, or the
            // user's move, after the report that asked for this scroll.
            guard memory.columns.following(column) else { return }
            // Laid out first: a board just shown has its columns' native views and ends
            // where the report saw them.
            memory.columnProbes.allObjects.lazy.compactMap(\.window).first?.layoutIfNeeded()
            guard let scroll = memory.columnScroll(column) else { proxy.scrollTo(end, anchor: .bottom); return }
            let clip = scroll.contentView
            let range = MightyStructurePanProbe.originRange(clip)
            let bottom = CGPoint(x: clip.bounds.origin.x, y: clip.isFlipped ? range.maximum.y : range.minimum.y)
            guard abs(bottom.y - clip.bounds.origin.y) > 0.5 else { return }
            clip.scroll(to: bottom)
            scroll.reflectScrolledClipView(clip)
        }
    }

    /// The column of the board shown whose cards hold the text `key` (`foldKey`).
    private func column(holding key: String) -> String? {
        guard let index = memory.navigation.index(in: boards) else { return nil }
        return boards[index].columns.first { $0.cards.contains { Self.foldKeys($0).contains(key) } }?.id
    }

    /// The keys of the texts a card opens in place (`foldKey`).
    private static func foldKeys(_ card: MightyStructure.Card) -> [String] {
        switch card {
        case .request(let item): [foldKey("request", item.runID)]
        case .execution(let item): [foldKey("message", item.runID)]
        case .result(let item): [foldKey("answer", item.runID)]
        case .summary(let item): [foldKey("request", item.runID), foldKey("message", item.runID)]
        case .agent(let node, _, _): [foldKey("node", node.nodeID)]
        }
    }

    private func stepState(_ step: MightyStructure.Step) -> String {
        switch step {
        case .done: L("graph.structure.step.done")
        case .current: L("graph.structure.step.current")
        case .pending: L("graph.structure.step.pending")
        case .error: L("graph.state.error")
        case .stopped: L("graph.state.stopped")
        }
    }

    /// The step's name in its state's ink, and how many cards it holds.
    private func columnHeader(_ column: MightyStructure.BoardColumn) -> some View {
        let look = Self.look(column.step.step)
        return HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: look.icon).font(.system(size: 8, weight: .bold))
                .foregroundStyle(column.step.step == .pending ? Palette.ink2 : look.fill)
            Text(verbatim: stepName(column.step)).font(.system(size: Self.textSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
            Text("\(column.count)").font(.system(size: 10, weight: .semibold)).monospacedDigit().foregroundStyle(Palette.ink2)
                .padding(.horizontal, DesignMetrics.Spacing.sm).background(Palette.track, in: Capsule())
            Spacer(minLength: 0)
        }
        .frame(height: Self.chipHeight)
        .accessibilityHidden(true)
    }

    @ViewBuilder private func card(_ card: MightyStructure.Card, width: CGFloat) -> some View {
        switch card {
        case .request(let item): requestCard(item, width: width)
        case .execution(let item):
            // The column says 실행 and the bar says which request: the card needs no title.
            execution(item, width: width, titled: false)
                .accessibilityLabel(L("graph.block.executionOf", ["n": "\(item.ordinal)"]))
        case .result(let item): resultCard(item)
        case .summary(let item): summaryCard(item, width: width)
        case .agent(let node, _, let parent): nodeCard(node, column: width, parent: parent)
        }
    }

    // MARK: Cards

    /// A free board's 요청: "Request N · Claude" and what was asked.
    private func requestCard(_ item: MightyStructure.Item, width: CGFloat) -> some View {
        // The column says 요청 and the bar above says which one: the card is what was asked.
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            if let request = item.request {
                foldableText(request, key: Self.foldKey("request", item.runID), lines: MightyStructure.requestLines, size: Self.textSize,
                             width: Self.cardTextWidth(width) - DesignMetrics.Layout.hitTarget - DesignMetrics.Spacing.sm,
                             name: L("graph.structure.column.request"))
            } else {
                Text(heading(item)).font(.system(size: Self.textSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
            }
            Spacer(minLength: 0)
            revealButton(item.nodeID)
        }
        .cardSurface()
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.contentAccessibility", ["name": heading(item), "text": item.input]))
        .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
    }

    /// A free board's 결과: the answer, how the request ended, or that it is still to come.
    private func resultCard(_ item: MightyStructure.Item) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                resultTitle(item)
                Spacer(minLength: 0)
                revealButton(item.finished ? MightyGraphBlockSize.nodeID(runID: item.runID, suffix: "result") : item.executionNodeID)
            }
            // A reply without an answer is in 실행 already; 결과 says how the request ended.
            outcome(item)
        }
        .cardSurface()
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-result-\(item.runID)")
    }

    /// A styled board's request: what was asked, its 실행 part (status, tally, latest
    /// reply, tool calls) and how it ended, on one line.
    private func summaryCard(_ item: MightyStructure.Item, width: CGFloat) -> some View {
        let line = MightyStructure.Fold.resultLine(item)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(heading(item)).font(.system(size: Self.textSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                Spacer(minLength: DesignMetrics.Spacing.xs)
                revealButton(item.nodeID)
            }
            if let request = item.request {
                foldableText(request, key: Self.foldKey("request", item.runID), lines: MightyStructure.requestLines, size: Self.textSize,
                             width: Self.cardTextWidth(width), name: L("graph.structure.column.request"))
            }
            Divider().overlay(Palette.border)
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Label(L("graph.block.executionOf", ["n": "\(item.ordinal)"]), systemImage: MightyGraphView.executionIcon)
                    .font(.system(size: Self.outputSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                MightyGraphActivityIndicator(status: item.status, tint: Palette.run)
                MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                tally(item)
                Spacer(minLength: 0)
                revealButton(item.executionNodeID)
            }
            progress(item, width: Self.cardTextWidth(width))
            if item.finished {
                HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.sm) {
                    resultTitle(item)
                    Text(verbatim: line ?? MightyGraphView.statusLabel(item.status))
                        .font(.system(size: Self.outputSize)).foregroundStyle(line == nil ? Palette.text(item.status) : Palette.ink2)
                        .lineLimit(1).truncationMode(.tail)
                        .help(line ?? "")
                    Spacer(minLength: 0)
                    revealButton(MightyGraphBlockSize.nodeID(runID: item.runID, suffix: "result"))
                }
            }
        }
        .cardSurface()
        // Running, the card wears the diagram's marching border, so the board shows what moves.
        .overlay { MightyGraphActivityOutline(tone: DesignTone(blockStatus: item.status), cornerRadius: Self.cardCorner).allowsHitTesting(false) }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.contentAccessibility", ["name": heading(item), "text": item.input]))
        .accessibilityIdentifier("mighty-structure-request-\(item.runID)")
    }

    /// The 실행 part: the main agent's status, tally, latest reply and tool calls, as the
    /// diagram's 실행 block holds them. `titled` names it.
    private func execution(_ item: MightyStructure.Item, width: CGFloat, titled: Bool) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                if titled {
                    Label(L("graph.block.executionOf", ["n": "\(item.ordinal)"]), systemImage: MightyGraphView.executionIcon)
                        .font(.system(size: Self.outputSize, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1)
                }
                MightyGraphActivityIndicator(status: item.status, tint: Palette.run)
                MightyStatusPill(text: MightyGraphView.statusLabel(item.status), tone: DesignTone(status: item.status))
                tally(item)
                Spacer(minLength: 0)
                revealButton(item.executionNodeID)
            }
            progress(item, width: Self.cardTextWidth(width))
        }
        .cardSurface()
        .overlay { MightyGraphActivityOutline(tone: DesignTone(blockStatus: item.status), cornerRadius: Self.cardCorner).allowsHitTesting(false) }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-structure-execution-\(item.runID)")
    }

    /// The width a card's text has in a column of `width`.
    private static func cardTextWidth(_ width: CGFloat) -> CGFloat { width - 2 * DesignMetrics.Inset.graphBlockBodyH }

    private func tally(_ item: MightyStructure.Item) -> some View {
        Text(L("graph.structure.tally", ["agents": "\(item.tally.agents)", "tools": "\(item.tally.tools)"]))
            .font(.system(size: Self.outputSize, weight: .semibold)).foregroundStyle(Palette.ink2).lineLimit(1)
    }

    /// The main block's latest reply and its last tool calls.
    @ViewBuilder private func progress(_ item: MightyStructure.Item, width: CGFloat) -> some View {
        if let message = item.message {
            foldableText(message, key: Self.foldKey("message", item.runID), lines: MightyStructure.messageLines, size: Self.textSize,
                         width: width, name: L("graph.structure.latest"))
        } else if !item.finished && item.activity.isEmpty {
            Text(L("graph.block.waitingResponse")).font(.system(size: Self.textSize)).foregroundStyle(Palette.ink2)
        }
        if !item.activity.isEmpty {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                ForEach(item.activity) { activityRow($0) }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(L("graph.structure.activity"))
        }
    }

    /// The result card's header words, in the outcome's colour once there is one.
    private func resultTitle(_ item: MightyStructure.Item) -> some View {
        let look: (title: String, icon: String) = switch item.status {
        case "error": (L("graph.block.resultError"), "exclamationmark.triangle")
        case "stopped": (L("graph.block.resultStopped"), "stop.circle")
        default: (L("graph.block.result"), "checkmark.seal")
        }
        return Label(look.title, systemImage: look.icon)
            .font(.system(size: Self.outputSize, weight: .bold)).lineLimit(1)
            .foregroundStyle(item.finished ? Palette.text(item.status) : Palette.ink2)
    }

    /// The answer as the transcript renders a reply; without one, its state (the last
    /// reply of a request that failed or stopped is in its 실행 part).
    @ViewBuilder private func outcome(_ item: MightyStructure.Item) -> some View {
        if let answer = item.answer {
            foldableMarkdown(answer, key: Self.foldKey("answer", item.runID), name: L("graph.block.result"))
        } else {
            Text(item.finished ? MightyGraphView.statusLabel(item.status) : L("graph.structure.resultPending"))
                .font(.system(size: Self.textSize)).foregroundStyle(Palette.ink2)
        }
    }

    /// A sub-agent or task as a small card: its status as a bar in its colour, its kind's
    /// glyph and ink as the diagram gives them (`MightyGraphView.agentPresentation`), its
    /// title, kind and model; the block it runs under, when it is nested (indented by its
    /// depth too); then what it was asked, its latest output and its last tool call.
    /// Opened, it shows them whole and its last few tool calls.
    private func nodeCard(_ node: MightyStructure.Node, column: CGFloat, parent: String?) -> some View {
        let task = node.kind == "task"
        let key = Self.foldKey("node", node.nodeID)
        let open = memory.expanded.contains(key)
        // Folds are judged at the width the card's text really has at its depth.
        let textWidth = MightyStructure.nodeTextWidth(column: column, depth: node.depth)
        let folds = node.prompt?.folds(lines: MightyStructure.promptLines,
                                       charactersPerLine: MightyStructure.charactersPerLine(width: textWidth, fontSize: Self.promptSize)) == true
            || node.output?.folds(lines: MightyStructure.outputLines,
                                  charactersPerLine: MightyStructure.charactersPerLine(width: textWidth, fontSize: Self.outputSize)) == true
            || node.activity.count > 1
        let model = node.model.map { ModelUsageFormat.shortName($0, catalog: catalog, versioned: true) }
        let meta = ([MightyTimelineText.kind(node.kind)] + [model].compactMap { $0 }).joined(separator: " · ")
        let shape = RoundedRectangle(cornerRadius: Self.nodeCorner, style: .continuous)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: task ? "terminal" : "person.crop.square.filled.and.at.rectangle")
                    .font(.system(size: 9, weight: .semibold)).foregroundStyle(task ? Palette.taskText : Palette.agentText)
                Text(verbatim: node.title).font(.system(size: Self.outputSize, weight: .semibold)).foregroundStyle(Palette.ink).lineLimit(1)
                    .layoutPriority(1)
                Text(meta).font(.system(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                Spacer(minLength: 0)
                MightyGraphActivityIndicator(status: node.status, tint: Palette.run)
                revealButton(node.nodeID)
            }
            if let parent {
                Label(L("graph.structure.underParent", ["title": parent]), systemImage: "arrow.turn.down.right")
                    .font(.system(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.tail)
            }
            if let prompt = node.prompt {
                let shown = open ? prompt.text : prompt.preview
                Text(verbatim: shown)
                    .font(.system(size: Self.promptSize)).foregroundStyle(Palette.ink2)
                    .lineLimit(open ? nil : MightyStructure.promptLines)
                    .contentText(name: L("graph.structure.prompt"), value: shown)
            }
            if let output = node.output {
                let shown = open ? output.text : output.preview
                Text(verbatim: shown)
                    .font(.system(size: Self.outputSize)).foregroundStyle(Palette.ink)
                    .lineLimit(open ? nil : MightyStructure.outputLines)
                    .contentText(name: L("graph.structure.output"), value: shown)
            }
            ForEach(open ? node.activity : Array(node.activity.suffix(1))) { activityRow($0) }
            if folds { foldToggle(key, open: open) }
        }
        // `MightyStructure.nodeTextWidth` takes this padding and the indent off.
        .padding(.leading, DesignMetrics.Spacing.md).padding(.trailing, DesignMetrics.Spacing.xs)
        .padding(.vertical, DesignMetrics.Spacing.xs)
        .background(Palette.panel, in: shape)
        .overlay(alignment: .leading) {
            Rectangle().fill(Palette.mark(node.status)).frame(width: Self.statusBarWidth).allowsHitTesting(false).accessibilityHidden(true)
        }
        .clipShape(shape)
        .overlay { shape.strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
        .overlay { MightyGraphActivityOutline(tone: DesignTone(blockStatus: node.status), cornerRadius: Self.nodeCorner).allowsHitTesting(false) }
        .padding(.leading, CGFloat(min(node.depth, MightyStructure.maximumIndent)) * DesignMetrics.Spacing.lg)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("graph.structure.nodeAccessibility", ["title": node.title, "status": MightyGraphView.statusLabel(node.status)]))
        .accessibilityValue(node.output?.preview ?? node.prompt?.preview ?? "")
        .accessibilityIdentifier("mighty-structure-node-\(node.nodeID)")
    }

    /// One tool call with its kind's glyph, as a diagram block's activity row draws it.
    private func activityRow(_ line: MightyStructure.ActivityLine) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: AgentActivityRow.symbol(line.kind))
                .font(.system(size: 10, weight: .medium)).frame(width: Self.glyphWidth)
                .foregroundStyle(line.state == "error" ? Palette.errText : line.state == "running" ? Palette.accent : Palette.ink2)
            Text(verbatim: line.text)
                .font(.system(size: Self.outputSize, design: AgentActivityRow.monospacedKinds.contains(line.kind) ? .monospaced : .default))
                .foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.tail)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .help(line.text)
        .accessibilityElement(children: .combine)
    }

    // MARK: Texts shown in place

    /// The key a text's open state is kept under: what it is and whose.
    static func foldKey(_ part: String, _ id: String) -> String { part + ":" + id }

    /// Plain text: its first lines folded, the whole of it (up to its cap) opened. The
    /// toggle shows whenever the folded lines may cut it at `width`.
    private func foldableText(_ excerpt: MightyStructure.Excerpt, key: String, lines: Int, size: CGFloat, width: CGFloat, name: String) -> some View {
        let open = memory.expanded.contains(key)
        let shown = open ? excerpt.text : excerpt.preview
        let folds = excerpt.folds(lines: lines, charactersPerLine: MightyStructure.charactersPerLine(width: width, fontSize: size))
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Text(verbatim: shown)
                .font(.system(size: size)).foregroundStyle(Palette.ink)
                .lineLimit(open ? nil : lines)
                .contentText(name: name, value: shown)
            if folds { foldToggle(key, open: open) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// Markdown as the transcript renders a reply. Folded, its preview: whole lines with
    /// any open code fence closed, so nothing is cut mid-line.
    private func foldableMarkdown(_ excerpt: MightyStructure.Excerpt, key: String, name: String) -> some View {
        let open = memory.expanded.contains(key)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            AgentMarkdownView(source: open ? excerpt.text : excerpt.preview, compact: true)
                .foregroundStyle(Palette.ink)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(name)
            if excerpt.cut { foldToggle(key, open: open) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func foldToggle(_ key: String, open: Bool) -> some View {
        Button {
            // A text opened or folded is being read: its column follows nothing until the
            // reader comes back to its end. Stopped before the text changes size, so no
            // report of the new size can follow the column to its end first. (The
            // diagram's blocks keep their size as they open, so there the camera only
            // re-judges where it is.)
            if let column = column(holding: key) { memory.columns.stop(column) }
            if !memory.expanded.insert(key).inserted { memory.expanded.remove(key) }
        } label: {
            Text(open ? L("graph.structure.less") : L("graph.structure.more"))
                .font(.system(size: Self.outputSize, weight: .bold)).foregroundStyle(Palette.accent)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(.isToggle)
        .accessibilityValue(open ? L("graph.structure.expanded") : L("graph.structure.collapsed"))
        .accessibilityIdentifier("mighty-structure-more-\(key)")
    }

    /// The small header button that shows the block in the diagram.
    private func revealButton(_ nodeID: String) -> some View {
        Button { onReveal(nodeID) } label: {
            Image(systemName: "arrow.up.right.square").font(.system(size: 11, weight: .medium)).foregroundStyle(Palette.ink2)
                .frame(width: DesignMetrics.Layout.hitTarget, height: DesignMetrics.Layout.hitTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(L("graph.structure.reveal"))
        .accessibilityLabel(L("graph.structure.reveal"))
        .accessibilityIdentifier("mighty-structure-reveal-\(nodeID)")
    }

    private func heading(_ item: MightyStructure.Item) -> String {
        StyleChrome.requestTitle(prefix: styleTitles.prefix(item.input), ordinal: item.ordinal, providerLabel: ProviderOptions.label(provider))
    }
}

private extension View {
    /// A card of the structure: the diagram's white block card around its padded content.
    func cardSurface() -> some View {
        padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .mightyBlockCard(cornerRadius: MightyStructureView.cardCorner)
    }

    /// A text shown in place: wrapped to its column, selectable, and read out as what it is.
    func contentText(name: String, value: String) -> some View {
        multilineTextAlignment(.leading)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .textSelection(.enabled)
            .accessibilityLabel(name)
            .accessibilityValue(value)
    }
}

/// Where a column of the board stands in its scroll view's space: its cards' frame,
/// its last card's bottom and the column's height, merged into one report so the follow
/// rule never judges one against a stale other.
private struct MightyStructurePage: PreferenceKey {
    struct Value: Equatable {
        var content: CGRect?
        var newestBottom: CGFloat?
        var visible: CGFloat?
    }
    static let defaultValue = Value()
    static func reduce(value: inout Value, nextValue: () -> Value) {
        let next = nextValue()
        if let content = next.content { value.content = content }
        if let newestBottom = next.newestBottom { value.newestBottom = newestBottom }
        if let visible = next.visible { value.visible = visible }
    }
}

/// Where each navigation control sits in the window's SwiftUI space, by name
/// (`MightyStructureMemory.controlFrames`).
private struct MightyStructureControlFrames: PreferenceKey {
    static let defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, next in next }
    }
}

/// A column of the newest board opens at its end, and one of an older board at its top;
/// a column shorter than its height sits at its top. From macOS 15 only the opening is
/// anchored, so a column never keeps its end in place by itself as it grows, which is the
/// follow rule's to decide, and a scroll the user is making is known, so it is never
/// fought. macOS 14 takes the one anchor at the top; the follow rule shows the end.
private struct MightyStructureScrollRoles: ViewModifier {
    let memory: MightyStructureMemory
    let column: String
    let opensAtEnd: Bool
    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) {
            content
                .defaultScrollAnchor(opensAtEnd ? .bottom : .top, for: .initialOffset)
                .defaultScrollAnchor(.top, for: .alignment)
                .onScrollPhaseChange { _, phase in
                    switch phase {
                    case .tracking, .interacting, .decelerating: memory.scrollingColumns.insert(column)
                    default: memory.scrollingColumns.remove(column)
                    }
                }
        } else {
            content.defaultScrollAnchor(.top)
        }
    }
}
