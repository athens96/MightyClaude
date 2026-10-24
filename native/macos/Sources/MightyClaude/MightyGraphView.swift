import AppKit
import MightyCore
import SwiftUI

struct MightyGraphView: View {
    let sessionID: String
    let provider: String
    let runs: [MightyGraphRun]
    let draft: String
    let running: Bool
    var blockSizes: [String: MightyGraphBlockSize] = [:]
    var onSaveBlockSize: (String, MightyGraphBlockSize?) -> Void = { _, _ in }
    /// Local project folder for resolving file references; nil disables links.
    var workspaceRoot: URL? = nil
    /// Request-block titles, icons and colours come from every style this pane
    /// may run, not only the one it is in: a pane keeps blocks it made under an
    /// earlier style (§1.10). Empty for a pane that runs no style at all.
    var styleTitles: StyleRequestTitles = StyleRequestTitles()
    /// The pane's own style, for the header alone.
    var styleName: String? = nil
    var styleSource: StyleSource? = nil
    var stylePhase: String? = nil
    /// The pane's own style and phase, which the structure view sorts requests by;
    /// nil for a pane that runs no style.
    var styleEvaluator: StyleEvaluator? = nil
    var guidedPhase: StylePhase? = nil
    /// The user chose to start anew: `guidedPhase` is where the next request starts.
    var guidedStartingNew = false
    /// Model catalog for short-name resolution in all usage capsules and activity suffixes.
    var catalog: [ModelOption] = []
    var graphResultSize: MightyGraphBlockSize? = nil
    var onSaveResultSize: (MightyGraphBlockSize?) -> Void = { _ in }
    /// The pending plan block's saved size, one per pane (`RunSession.graphPlanSize`).
    var graphPlanSize: MightyGraphBlockSize? = nil
    var onSavePlanSize: (MightyGraphBlockSize?) -> Void = { _ in }
    /// Shows a background execution's dashboard where the workspace opens
    /// web pages; false when nothing was shown (or the pane has closed).
    var onOpenURL: @MainActor (URL) async -> Bool = { _ in false }
    /// Runs before this index were loaded from the CLI's session record and
    /// are drawn above the pane's own; nil `history` hides the block that
    /// loads more of them. A pane with nothing on screen loads them when it
    /// appears.
    var retainedStart = 0
    var history: SessionHistoryState? = nil
    var onLoadOlder: () -> Void = {}
    /// "Diagram | Timeline | Structure", saved per pane (`RunSession.graphViewMode`).
    var viewMode: MightyGraphViewMode = .diagram
    var onViewMode: (MightyGraphViewMode) -> Void = { _ in }
    /// The pane's pending plan (`PlanCardSupport.pendingPlan`) and its answered
    /// plans. The cards come from the pane: the canvas hosts its own views, so
    /// they bring the store with them. The plan block holds the plan alone (its
    /// answers are in the composer); the closure's action, when there is one,
    /// is its 창에 맞춤.
    var planRequest: ToolPermissionRequest? = nil
    var planHistory: [PlanRecord] = []
    var planCard: (ToolPermissionRequest, (() -> Void)?) -> AnyView = { _, _ in AnyView(EmptyView()) }
    var planRecordCard: (PlanRecord, Bool, @escaping () -> Void) -> AnyView = { _, _, _ in AnyView(EmptyView()) }
    let onFocus: () -> Void
    @ViewState private var resized: [String: MightyGraphBlockSize] = [:]
    /// The structure view's model and what it was built from. A reference, so
    /// keeping it never redraws the view.
    @ViewState private var structureCache = StructureCache()
    private final class StructureCache {
        var signature: MightyStructure.Signature?
        /// The shape's boards (`MightyStructure.boards`), one shown at a time.
        var boards: [MightyStructure.Board] = []
        /// The cards, each built again only when its own request moved.
        var items = MightyStructure.ItemCache()
        /// The structure's board and open texts, kept while the pane shows another view.
        let memory = MightyStructureMemory()
    }
    /// The newest result's size only while its drag is in progress; the saved
    /// pane-wide size is the truth before and after.
    @ViewState private var liveResultSize: MightyGraphBlockSize?
    /// The plan block's size only while its drag is in progress, as `liveResultSize`.
    @ViewState private var livePlanSize: MightyGraphBlockSize?
    /// Result cards' measured answer heights, by node id: the newest card is
    /// no taller than its content (`MightyGraphLayout.resultSize`).
    @ViewState private var resultHeights: [String: CGFloat] = [:]
    /// A height measured while the newest result was being dragged, applied
    /// on release. A reference, so keeping it never redraws the diagram.
    @ViewState private var measuredDuringDrag = MeasuredHeight()
    private final class MeasuredHeight { var value: (nodeID: String, height: CGFloat)? }
    /// Whether the camera follows the newest block (`MightyGraphFollow`), kept
    /// across view switches and shared with the camera probe.
    @ViewState private var follow = MightyGraphFollowBox()
    @ViewState private var reference: MightyGraphReference?
    @ViewState private var referenceOnLeft = false
    // Remembered across sessions and panes. Height 0 means "as tall as the graph".
    @AppStorage("mighty.referenceBubble.width") private var bubbleWidth: Double = MightyGraphReferenceBubble.defaultWidth
    @AppStorage("mighty.referenceBubble.height") private var bubbleHeight: Double = 0
    @ViewState private var expanded = Set<String>()
    @ViewState private var zoom: CGFloat = 1
    @ViewState private var scrollTarget: MightyGraphScrollTarget?
    @ViewState private var selectedNodeID: String?
    /// Counts history trims, so a second trim admits a second re-aim.
    @ViewState private var trimSequence = 0
    @StateObject private var resultFiles = MightyGraphResultFilesModel()
    @StateObject private var ouroboros = MightyGraphOuroborosModel()
    @ViewState private var canvasViewport: CGSize?
    /// Timeline rows opened to show what their diagram card holds.
    @ViewState private var timelineOpen = Set<String>()
    /// Requests whose fold differs from the default: the newest open, the rest folded.
    @ViewState private var timelineFlipped = Set<String>()
    /// Result cards showing their whole answer.
    @ViewState private var timelineFull = Set<String>()
    /// The timeline's first visible item, which is how scrolling to its top is seen.
    @ViewState private var timelineTop: String?
    private static let timelineHistoryID = "timeline-history"

    /// Kept out of the view body: long concatenations of conditionals are
    /// slow for older type checkers.
    static func headerSummary(runs: Int, agents: Int, tasks: Int, steers: Int, compactions: Int, questions: Int = 0, tokens: GraphTokenUsage) -> String {
        var parts = [L("graph.header.requests", ["n": "\(runs)"]), L("graph.header.agents", ["n": "\(agents)"])]
        if questions > 0 { parts.append(L("graph.header.questions", ["n": "\(questions)"])) }
        if tasks > 0 { parts.append(L("graph.header.tasks", ["n": "\(tasks)"])) }
        if steers > 0 { parts.append(L("graph.header.steers", ["n": "\(steers)"])) }
        if compactions > 0 { parts.append(L("graph.header.compactions", ["n": "\(compactions)"])) }
        if !tokens.isEmpty { parts.append(tokens.summary) }
        return parts.joined(separator: " · ")
    }

    /// Title, symbol and tint for a child block by kind.
    static func agentPresentation(_ agent: MightyGraphAgent) -> (title: String, icon: String, tint: Color) {
        // The title comes from the core so the phone's Mighty view names the
        // same block the same way; only the symbol and tint are Mac-only.
        let title = MightyGraphSupport.blockTitle(agent)
        if agent.isSteer { return (title, "text.bubble", Palette.steerText) }
        if agent.isCompact { return (title, "arrow.down.right.and.arrow.up.left", Palette.compactText) }
        if agent.isQuestion { return (title, "questionmark.bubble.fill", Palette.questionText) }
        if agent.isTask { return (title, "terminal", Palette.taskText) }
        return (title, "person.crop.square.filled.and.at.rectangle", Palette.agentText)
    }

    // The newest result takes the pane-wide saved size, so a drag in progress
    // has to stand in for it or the card would only change on release.
    private var layout: MightyGraphLayout { layout(executionLinks) }
    private func layout(_ links: [OuroborosExecutionLink]) -> MightyGraphLayout {
        .make(runs: runs, draft: draft, running: running, expanded: expanded, blockSizes: blockSizes.merging(resized) { _, new in new }, resultFilesRunID: resultFiles.selectedRunID, viewport: canvasViewport, zoom: zoom, sharedResultSize: liveResultSize ?? graphResultSize,
              resultContentHeight: latestResultContentHeight,
              executions: links.map { MightyGraphLayout.Execution(runID: $0.runID, key: $0.key) },
              galleries: MightyGraphImages.galleries(runs: runs, root: workspaceRoot, fixed: olderCount),
              retainedStart: pinnedStart, history: history != nil,
              planRunID: planRunID, planRecords: PlanCardSupport.diagramRecords(planHistory, runs: runs), planSize: livePlanSize ?? graphPlanSize,
              splitsExecution: true)
    }
    /// The diagram request the pending plan card goes under, nil when it is docked.
    private var planRunID: String? { PlanCardSupport.diagramPlanRunID(planRequest, showsDiagram: viewMode == .diagram, runs: runs) }
    private var planNodeID: String? { planRunID.map { MightyGraphBlockSize.nodeID(runID: $0, suffix: MightyGraphLayout.planSuffix) } }
    private var olderCount: Int { min(max(0, retainedStart), runs.count) }
    /// The newest result card's content — its header strip and its measured
    /// answer — once known. A drag in progress shows exactly the dragged size.
    private var latestResultContentHeight: CGFloat? {
        guard liveResultSize == nil, let index = runs.indices.last(where: { MightyGraphLayout.finished(runs[$0]) }) else { return nil }
        // The card's placeholder line when it has no answer (`transcriptCard`).
        if !runs[index].resultEntries.contains(where: { $0.kind != "user" }) { return 0 }
        return resultHeights[MightyGraphLayout.nodeID(runs[index], suffix: "result")].map { Self.blockHeaderHeight + $0 }
    }
    private static let blockHeaderHeight = DesignMetrics.Layout.blockHead
    /// A timeline request's chevron column, and how far its request and tally stand in under it: past the
    /// chevron and the header line's `Spacing.md`, so they line up with the title.
    private static let timelineChevronWidth: CGFloat = 11
    private static let timelineRequestIndent = timelineChevronWidth + DesignMetrics.Spacing.md
    /// The pane's own requests as the reveal rule sees them.
    private var ownRunProgress: [MightyGraphCamera.ResultReveal.RunProgress] {
        ownRuns.map { .init(id: $0.id, finished: MightyGraphLayout.finished($0)) }
    }
    /// The run that keeps its place while loaded history grows above it and
    /// trimmed runs move into it.
    private var pinnedStart: Int { history?.pinnedRunID.flatMap { id in runs.firstIndex { $0.id == id } } ?? olderCount }
    /// The pane's own requests, without those read back from the record.
    private var ownRuns: ArraySlice<MightyGraphRun> { runs.dropFirst(olderCount) }
    /// A block of a run read from the session record: sized on screen only,
    /// never saved into the pane's profile.
    private func isRecordNode(_ id: String) -> Bool {
        runs.prefix(olderCount).contains { id.hasPrefix(MightyGraphBlockSize.nodeID(runID: $0.id, suffix: "")) }
    }

    /// Background executions the pane's agents started; they outlive the
    /// request that started them, so their blocks stay live after it settled.
    /// Requests read back from the session record are history: their
    /// executions are not polled again.
    private var executionLinks: [OuroborosExecutionLink] { OuroborosExecutionLinks.extract(from: Array(runs.dropFirst(olderCount))) }
    private static func executionNodeID(_ link: OuroborosExecutionLink) -> String {
        MightyGraphBlockSize.nodeID(runID: link.runID, suffix: MightyGraphLayout.executionSuffix + link.key)
    }

    var body: some View {
        let links = executionLinks
        let linksByKey = Dictionary(links.map { ($0.key, $0) }, uniquingKeysWith: { first, _ in first })
        let graph = layout(links)
        let ouroborosRequest = MightyGraphOuroborosModel.Request(links: links, listKeys: Set(links.filter { expanded.contains(Self.executionNodeID($0)) }.map(\.key)))
        // Totals are the pane's own requests; those read back from the
        // record are counted apart.
        let own = ownRuns
        let agentCount = own.reduce(0) { $0 + $1.agents.filter { !$0.isTask && !$0.isSteer && !$0.isCompact && !$0.isQuestion }.count }
        let questionCount = own.reduce(0) { $0 + $1.agents.filter(\.isQuestion).count }
        let taskCount = own.reduce(0) { $0 + $1.agents.filter(\.isTask).count }
        // Only steers a person sent; a delegation or task notice is not counted.
        let steerCount = own.reduce(0) { $0 + $1.agents.filter { $0.isSteer && !$0.isNotice }.count }
        let compactCount = own.reduce(0) { $0 + $1.agents.filter(\.isCompact).count }
        let tokens = own.reduce(GraphTokenUsage()) { $0 + ($1.totalUsage ?? GraphTokenUsage()) }
        let summary = Self.headerSummary(runs: own.count, agents: agentCount, tasks: taskCount, steers: steerCount, compactions: compactCount, questions: questionCount, tokens: tokens)
            + (olderCount > 0 ? " · " + L("graph.history.headerLoaded", ["count": "\(olderCount)"]) : "")
        // Hoisted out of the canvas call: older type checkers spend a long time
        // on optional maps and conditionals written inline in an argument list.
        let overlayView: AnyView = reference.map { AnyView(referenceOverlay($0)) } ?? AnyView(EmptyView())
        let overlayLayout: MightyOverlayLayout? = reference == nil ? nil
            : MightyOverlayLayout(onLeft: referenceOnLeft, storedWidth: bubbleWidth, storedHeight: bubbleHeight)
        let fallbackNodeID = followTarget(graph)
        // Before anything aimed it, the camera follows: the newest block right above the composer.
        let target = scrollTarget ?? MightyGraphScrollTarget(token: "initial:" + sessionID, nodeID: fallbackNodeID, alignTop: false, follows: true)
        let live = MightyGraphLayout.liveNodeIDs(runs)
        VStack(spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.md) {
                MightyViewSwitch(sessionID: sessionID, mode: viewMode) { mode in
                    onFocus()
                    onViewMode(mode)
                }
                Text(StyleChrome.graphHeader(styleName: styleName, phaseTitle: stylePhase))
                    .font(.ui(size: 12, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(1).fixedSize()
                if let styleSource, let badge = StyleChrome.sourceBadge(styleSource) { SourceBadge(text: badge) }
                Text(summary)
                    .font(.ui(size: 10)).foregroundStyle(Palette.ink2).lineLimit(1)
                    .help(tokens.isEmpty ? "" : L("graph.header.totalHelp", ["detail": tokens.detail]))
                Spacer(minLength: DesignMetrics.Spacing.md)
                if viewMode == .diagram {
                    Group {
                        Button { zoomOut() } label: { Image(systemName: "minus.magnifyingglass") }
                            .disabled(zoom <= 0.5).help(L("graph.zoom.out")).accessibilityIdentifier("mighty-zoom-out-\(sessionID)")
                        Button { zoom = 1 } label: { Text("\(Int((zoom * 100).rounded()))%").monospacedDigit().frame(width: 38) }
                            .help(L("graph.zoom.reset")).accessibilityIdentifier("mighty-zoom-reset-\(sessionID)")
                        Button { zoom = min(1.5, zoom + 0.1) } label: { Image(systemName: "plus.magnifyingglass") }
                            .disabled(zoom >= 1.5).help(L("graph.zoom.in")).accessibilityIdentifier("mighty-zoom-in-\(sessionID)")
                    }
                    .foregroundStyle(Palette.ink2)
                }
            }
            .buttonStyle(.plain).padding(.horizontal, DesignMetrics.Inset.graphBarH).padding(.vertical, DesignMetrics.Inset.graphBarV)
            Divider().overlay(Palette.border)
            if viewMode == .timeline {
                timeline(MightyTimeline.groups(runs))
            } else if viewMode == .structure {
                MightyStructureView(sessionID: sessionID, provider: provider,
                                    boards: structureBoards(), styleName: styleName,
                                    styleTitles: styleTitles, newestRunID: runs.last?.id, catalog: catalog, memory: structureCache.memory) { nodeID in
                    // The diagram opens with its camera on the request or block whose button was clicked.
                    onFocus()
                    publish(.reaim(nodeID: nodeID, alignTop: false))
                    onViewMode(.diagram)
                }
            } else {
            MightyGraphCanvas(graph: graph, zoom: zoom, sessionID: sessionID,
                              scrollTarget: target,
                              defaultNodeID: fallbackNodeID, follow: follow,
                              diagnosticActions: MightyGraphDiagnosticActions(toggleExpanded: toggleExpanded, zoomOut: zoomOut),
                              selection: $selectedNodeID, edges: { graphEdges(graph, live: live, visible: $0) },
                              overlay: overlayView,
                              overlayLayout: overlayLayout,
                              onOverlayResize: { size, _ in
                                  // .zero is the corner's double click: back to the default size.
                                  if size == .zero { bubbleWidth = MightyGraphReferenceBubble.defaultWidth; bubbleHeight = 0 }
                                  else { bubbleWidth = Double(size.width); bubbleHeight = Double(size.height) }
                              },
                              onResize: { resize(graph, $0, $1, $2, $3) }, onResetSize: resetSize,
                              onStranded: { reaim(graph, after: $0) }, newestRunID: runs.last?.id,
                              onReachTop: { if history?.phase == .idle { onLoadOlder() } },
                              card: { card($0, executions: linksByKey) })
                // The canvas changing height — the composer growing as a draft is typed,
                // shrinking after a send, the pane resizing — never re-aims the camera: a
                // following camera keeps its block above the composer, any other stays.
                .background(GeometryReader { geo in
                    Color.clear
                        .onAppear { canvasViewport = geo.size }
                        .onChange(of: geo.size) { _, new in canvasViewport = new }
                })
                // A plan waiting for an answer is what the user has to read next.
                .onChange(of: planNodeID) { _, id in
                    guard let id else { return }
                    scrollTarget = MightyGraphScrollTarget(token: "plan:" + id + ":" + (planRequest?.id ?? ""), nodeID: id, alignTop: true)
                }
                // A block the user opened or folded never moves the camera; it follows on
                // only if the newest block is still in view.
                .onChange(of: expanded) { _, _ in follow.watch.userChangedLayout() }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-graph-\(sessionID)")
        // Result files are looked for in the pane's own requests only.
        .task(id: MightyGraphResultFilesModel.Request(sessionID: sessionID, runs: Array(ownRuns), root: workspaceRoot)) {
            await resultFiles.load(.init(sessionID: sessionID, runs: Array(ownRuns), root: workspaceRoot))
        }
        // A resumed pane (or one switched to this view) with nothing on screen
        // yet shows its session's latest requests.
        .onAppear {
            if runs.isEmpty, history?.phase == .idle { onLoadOlder() }
            // A plan already waiting when the diagram is shown is what to read first.
            if let id = planNodeID {
                scrollTarget = MightyGraphScrollTarget(token: "plan:" + id + ":" + (planRequest?.id ?? ""), nodeID: id, alignTop: true)
            }
        }
        // The whole id list, not just the last one: dropping the oldest
        // runs moves every surviving card up without touching the last
        // id, and nothing else would re-aim the camera. Watched in every view,
        // so a send from the 구조 view or the timeline is followed in the diagram.
        .onChange(of: runs.map(\.id)) { previous, current in
            // A new request is the user's own send: follow it, its 실행 block and
            // everything after it right above the composer.
            if let last = current.last, previous.last != last {
                publishFollow()
                return
            }
            // A following camera keeps its block in place through a trim itself.
            guard !follow.watch.following else { return }
            publish(MightyGraphCamera.trimAnchor(previousRunIDs: previous, runIDs: current, selectedNodeID: selectedNodeID,
                                                 layoutNodeIDs: Set(layout.nodes.map(\.id))))
        }
        // Back to the diagram, a following camera shows the newest block again.
        .onChange(of: viewMode) { _, mode in
            if mode == .diagram, follow.watch.following { publishFollow() }
        }
        // Cancelled when the graph leaves the screen or the pane closes.
        .task(id: ouroborosRequest) { await ouroboros.run(ouroborosRequest) }
    }

    /// The structure view's boards, rebuilt only when something they draw changed, and
    /// then only the cards of the requests that moved.
    private func structureBoards() -> [MightyStructure.Board] {
        let pending = planRequest?.runId
        let signature = MightyStructure.Signature(runs: runs, style: styleEvaluator, currentPhase: guidedPhase, startingNew: guidedStartingNew,
                                                  planHistory: planHistory, pendingPlanRunID: pending)
        if structureCache.signature != signature {
            let items = structureCache.items.items(runs, signature: signature)
            structureCache.boards = MightyStructure.boards(MightyStructure.make(runs: runs, style: styleEvaluator, currentPhase: guidedPhase,
                                                                                startingNew: guidedStartingNew, planHistory: planHistory,
                                                                                pendingPlanRunID: pending, items: items))
            structureCache.signature = signature
        }
        return structureCache.boards
    }

    /// Every camera re-aim goes through one token sequence, so a second one
    /// lands even when it picks the block the first one did.
    private func publish(_ anchor: MightyGraphCamera.Anchor) {
        guard case .reaim(let nodeID, let alignTop) = anchor else { return }
        // Where it lands decides whether the camera follows from there.
        follow.watch.stop()
        trimSequence += 1
        scrollTarget = MightyGraphScrollTarget(token: MightyGraphCamera.trimToken(sequence: trimSequence, nodeID: nodeID), nodeID: nodeID, alignTop: alignTop)
    }

    /// Follow from now on, the newest block right above the composer.
    private func publishFollow() {
        follow.watch.start()
        trimSequence += 1
        scrollTarget = MightyGraphScrollTarget(token: "follow:\(trimSequence)", nodeID: followTarget(layout), alignTop: false, follows: true)
    }

    /// A result card's answer measured a new height.
    /// Only the newest card's height is kept; a drag in progress shows its own size.
    private func resultMeasured(_ nodeID: String, _ height: CGFloat) {
        guard height.isFinite else { return }
        if liveResultSize != nil { measuredDuringDrag.value = (nodeID, height); return }
        guard abs((resultHeights[nodeID] ?? -1) - height) > 0.5 else { return }
        resultHeights = [nodeID: height]
    }

    /// The layout moved every card off screen while the camera stood still —
    /// a trim, a collapsed card, anything else that shortens the document.
    private func reaim(_ graph: MightyGraphLayout, after loss: MightyGraphCamera.StrandedWatch.Loss) {
        // A request that arrived since the detection aims the camera itself, and a
        // following camera keeps its own block on screen.
        guard runs.last?.id == loss.newestRunID, !follow.watch.following else { return }
        let nodeIDs = Set(graph.nodes.map(\.id))
        // Back to the card they were reading, not somewhere they never were.
        if nodeIDs.contains(loss.lookedAtNodeID) { publish(.reaim(nodeID: loss.lookedAtNodeID, alignTop: false)); return }
        let anchor = MightyGraphCamera.reaimAnchor(newestRunID: loss.newestRunID, selectedNodeID: selectedNodeID, layoutNodeIDs: nodeIDs)
        // The net fires once per loss, so holding here would leave the blank.
        let fallback = followTarget(graph)
        if case .hold = anchor, nodeIDs.contains(fallback) { publish(.reaim(nodeID: fallback, alignTop: true)); return }
        publish(anchor)
    }

    /// Rendered inside the canvas's native overlay view, which is sized to
    /// `MightyGraphReferenceBubble.frame` from the canvas's own bounds.
    private func referenceOverlay(_ reference: MightyGraphReference) -> some View {
        MightyGraphReferenceBubble(sessionID: sessionID, reference: reference, onLeft: referenceOnLeft,
                                   onFlip: { referenceOnLeft.toggle() }, onClose: { self.reference = nil })
    }

    /// A clicked path resolves inside the workspace only. Unresolved paths
    /// still open the bubble so the user sees why nothing was shown.
    private func openReference(_ path: String, line: Int?) {
        onFocus()
        reference = MightyGraphReference(path: path, line: line, url: ReferenceLinkSupport.resolve(path, root: workspaceRoot))
    }

    /// `graph` is the layout the drag started on: its viewport limit and
    /// window fit are what the newest result card is kept within.
    private func resize(_ graph: MightyGraphLayout, _ id: String, _ size: CGSize, _ edges: ResizeEdges, _ phase: MightyGraphLayout.ResizePhase) {
        guard let value = MightyGraphBlockSize(width: size.width, height: size.height).normalized(nodeID: id) else { return }
        follow.watch.userChangedLayout()
        resized[id] = value
        if isRecordNode(id) { return }
        if id == planNodeID {
            // The pane's one plan size, kept and saved by the newest result's rules.
            let drag = MightyGraphLayout.resultDrag(dragged: size, edges: edges, phase: phase, saved: graphPlanSize,
                                                    limit: graph.planLimit, windowFit: graph.planWindowFit)
            livePlanSize = drag.live
            if phase != .live, let save = drag.save { onSavePlanSize(save) }
        } else if id == MightyGraphLayout.latestResultID(runs: runs) {
            // The saved size is the newest result's maximum, not what it shows.
            let drag = MightyGraphLayout.resultDrag(dragged: size, edges: edges, phase: phase, saved: graphResultSize,
                                                    limit: graph.resultLimit, windowFit: graph.resultWindowFit)
            liveResultSize = drag.live
            guard phase != .live else { return }
            if let save = drag.save { onSaveResultSize(save) }
            if let measured = measuredDuringDrag.value {
                measuredDuringDrag.value = nil
                resultMeasured(measured.nodeID, measured.height)
            }
        } else if phase != .live { onSaveBlockSize(id, value) }
    }

    private func resetSize(_ id: String) {
        resized.removeValue(forKey: id)
        expanded.remove(id)
        if id == planNodeID { fitPlanToWindow() }
        else if !isRecordNode(id) { onSaveBlockSize(id, nil) }
    }

    /// The plan block's 창에 맞춤: its saved size cleared, back to the window fit.
    private func fitPlanToWindow() {
        livePlanSize = nil
        onSavePlanSize(nil)
    }

    /// A block header's 펼치기/접기: back to its default size, opened or folded.
    private func toggleExpanded(_ id: String) {
        resized.removeValue(forKey: id)
        onSaveBlockSize(id, nil)
        if !expanded.insert(id).inserted { expanded.remove(id) }
    }
    private func zoomOut() { zoom = max(0.5, zoom - 0.1) }

    /// The block a following camera keeps right above the composer.
    private func followTarget(_ graph: MightyGraphLayout) -> String {
        MightyGraphFollow.targetID(graph, runs: runs) ?? MightyGraphCamera.pendingNodeID
    }

    /// Concept D's connectors: quiet ink lines, run blue (and a little heavier) into a
    /// block that is running.
    private func graphEdges(_ graph: MightyGraphLayout, live: Set<String>, visible: CGRect) -> some View {
        let split = graph.routes(in: visible, into: live)
        return ZStack {
            Self.edgePath(split.other)
                .stroke(Palette.ink2.opacity(0.45), style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
            Self.edgePath(split.into)
                .stroke(Palette.run, style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
        }
        // Routed in node coordinates, drawn in the container's, which starts at
        // the diagram's leading edge rather than at x = 0, and at its top
        // rather than at y = 0 once older requests sit above.
        .offset(x: MightyGraphCamera.drawnX(nodeX: 0, originX: graph.originX), y: -graph.originY)
        .allowsHitTesting(false).accessibilityHidden(true)
    }

    private static func edgePath(_ routes: [[CGPoint]]) -> Path {
        Path { path in
            for points in routes {
                guard let start = points.first, let end = points.last else { continue }
                path.move(to: start)
                for point in points.dropFirst() { path.addLine(to: point) }
                path.move(to: CGPoint(x: end.x - 4, y: end.y - 6))
                path.addLine(to: end)
                path.addLine(to: CGPoint(x: end.x + 4, y: end.y - 6))
            }
        }
    }

    /// `executions` is this render's execution links by key, found once
    /// rather than for every card.
    @ViewBuilder private func card(_ node: MightyGraphLayout.Node, executions: [String: OuroborosExecutionLink]) -> some View {
        switch node.content {
        case .draft:
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                HStack {
                    Label(runs.isEmpty ? L("graph.block.firstRequest") : L("graph.block.nextRequest"), systemImage: "square.and.pencil").font(.ui(size: 12, weight: .semibold))
                    Spacer()
                    Text(draft.isEmpty ? L("graph.state.draftIdle") : L("graph.state.draftTyping")).font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                }
                // A native selectable view, not Text(...).textSelection: the
                // canvas monitor hands first responder back to itself for every
                // click that is not on an NSTextView, which left a SwiftUI
                // selection here drawn but impossible to copy.
                MightyGraphDraftPreview(draft: draft, width: max(1, node.frame.width - 2 * DesignMetrics.Spacing.lg), identifier: "mighty-draft-\(sessionID)")
                Spacer(minLength: 0)
            }
            .padding(DesignMetrics.Spacing.lg).background(Palette.panel, in: RoundedRectangle(cornerRadius: 12))
            .overlay { RoundedRectangle(cornerRadius: 12).stroke(Palette.accent.opacity(0.6), style: StrokeStyle(lineWidth: 1.5, dash: [5, 4])) }
            .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(node.id)")
        case .request(let index):
            // The request alone: what was asked. What the agent did is the 실행 block below.
            let run = runs[index]
            let title = StyleChrome.requestTitle(prefix: styleTitles.prefix(run.input), ordinal: index + 1,
                                                 providerLabel: ProviderOptions.label(provider))
            let icon = styleTitles.icon(run.input)?.rawValue ?? StyleIcon.requestDefault.rawValue
            transcriptCard(node, title: title, titleProvider: provider, icon: icon, status: run.status,
                           input: run.input, entries: [], tint: Palette.tint(styleTitles.tint(run.input)),
                           fromRecord: index < olderCount, inputOnly: true)
        case .work(let index):
            // The main agent's own work for the request: its replies, tool calls, status and tokens.
            let run = runs[index]
            let runID = run.sourceRunID ?? run.id
            let runChildBlocks = GraphChildBlocks.map(responseRecords: run.responseRecords, agents: run.agents, runId: runID)
            transcriptCard(node, title: L("graph.block.executionOf", ["n": "\(index + 1)"]), icon: Self.executionIcon, status: run.status,
                           input: "", entries: run.rootEntries, tint: Palette.agentText, usage: run.usage,
                           records: run.responseRecords ?? [], nodeModelLabel: run.nodeModelLabel, childBlocks: runChildBlocks,
                           fromRecord: index < olderCount)
        case .agent(let runIndex, let agentIndex):
            let run = runs[runIndex]
            let agent = run.agents[agentIndex]
            let look = Self.agentPresentation(agent)
            let runID = run.sourceRunID ?? run.id
            let agentChildBlocks = GraphChildBlocks.map(responseRecords: agent.responseRecords, agents: run.agents, runId: runID)
            transcriptCard(node, title: look.title, icon: look.icon, status: agent.status, input: agent.shownInput, entries: agent.entries, tint: look.tint, usage: agent.usage,
                           records: agent.responseRecords ?? [], childBlocks: agentChildBlocks)
        case .result(let index):
            let run = runs[index]
            let failed = ["error", "failed"].contains(run.status)
            let stopped = ["stopped", "cancelled", "interrupted"].contains(run.status)
            let status = failed ? "error" : stopped ? "stopped" : "completed"
            transcriptCard(node, title: failed ? L("graph.block.resultError") : stopped ? L("graph.block.resultStopped") : L("graph.block.result"), icon: failed ? "exclamationmark.triangle" : stopped ? "stop.circle" : "checkmark.seal",
                           status: status, input: "", entries: run.resultEntries, tint: Palette.text(status),
                           usage: run.totalUsage, usageLabel: L("graph.block.totalUsageLabel"), resultFilesRunID: run.status == "completed" ? run.id : nil,
                           headerFill: Palette.heroFill(DesignTone(status: status)),
                           onContentHeight: node.id == MightyGraphLayout.latestResultID(runs: runs) ? { resultMeasured(node.id, $0) } : nil)
        case .resultFiles(let index):
            MightyGraphResultFilesView(nodeID: node.id, files: resultFiles.files(for: runs[index].id),
                                       onOpen: { openReference($0.path, line: $0.line) }, onClose: { resultFiles.close() })
        case .images(let index, let step):
            MightyGraphImagesCard(nodeID: node.id, items: MightyGraphImages.items(runs[index], step: step, root: workspaceRoot),
                                  expanded: expanded.contains(node.id),
                                  onToggle: { if !expanded.insert(node.id).inserted { expanded.remove(node.id) } },
                                  onOpen: onFocus)
        case .history:
            historyCard(width: node.frame.width, height: node.frame.height)
        case .plan:
            // The plan alone on the block card, its page scrolling inside;
            // its answers wait in the composer.
            if let planRequest {
                planCard(planRequest, graphPlanSize == nil ? nil : fitPlanToWindow)
                    .padding(DesignMetrics.Spacing.md)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                    .mightyBlockCard()
            }
        case .planRecord(_, let recordID):
            if let record = planHistory.last(where: { $0.id == recordID }) {
                // Folding or opening drops a dragged size, as the other blocks' size control does.
                planRecordCard(record, expanded.contains(node.id)) {
                    resized.removeValue(forKey: node.id)
                    if !isRecordNode(node.id) { onSaveBlockSize(node.id, nil) }
                    if !expanded.insert(node.id).inserted { expanded.remove(node.id) }
                }
            }
        case .execution(_, let key):
            if let link = executions[key] {
                MightyGraphOuroborosCard(nodeID: node.id, link: link, snapshot: ouroboros.snapshots[key], expanded: expanded.contains(node.id),
                                         opening: ouroboros.opening.contains(key), openFailed: ouroboros.openFailed.contains(key),
                                         onToggle: { if !expanded.insert(node.id).inserted { expanded.remove(node.id) } },
                                         onOpenDashboard: {
                                             onFocus()
                                             ouroboros.openDashboard(link) { await onOpenURL($0) }
                                         })
            }
        }
    }

    /// The 실행 block's glyph, in the diagram and on its timeline row.
    static let executionIcon = "gearshape.2"

    /// `headerFill` paints the header as a strip (the result card's outcome colour) with
    /// white words on it; the other blocks keep a plain header on the white card.
    /// `inputOnly` is the request block: its input fills the card, and it has no status of
    /// its own — the 실행 block under it runs and settles.
    private func transcriptCard(_ node: MightyGraphLayout.Node, title: String, titleProvider: String? = nil, icon: String, status: String, input: String, entries: [LogEntry], tint: Color,
                                usage: GraphTokenUsage? = nil, usageLabel: String = L("graph.block.blockUsageLabel"), resultFilesRunID: String? = nil,
                                records: [GraphResponseRecord] = [], nodeModelLabel: String? = nil,
                                childBlocks: [String: GraphChildBlock] = [:], fromRecord: Bool = false, headerFill: Color? = nil,
                                inputOnly: Bool = false, onContentHeight: ((CGFloat) -> Void)? = nil) -> some View {
        let content = entries.filter { $0.kind != "user" }
        let onStrip = headerFill != nil
        let quiet = onStrip ? Palette.onStatus : Palette.ink2
        return VStack(spacing: 0) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: icon).foregroundStyle(onStrip ? Palette.onStatus : tint)
                // A request block's title ends with its agent's name; its mark goes before it.
                (titleProvider.map { ProviderBadgeIcon.labelled(title, provider: $0, font: .ui(ofSize: 12, weight: .bold)) } ?? Text(title))
                    .font(.ui(size: 12, weight: .bold)).lineLimit(1).help(title)
                    .foregroundStyle(onStrip ? Palette.onStatus : Palette.ink)
                if fromRecord {
                    // Read back from the CLI's own session record, not kept by the app.
                    Image(systemName: "clock.arrow.circlepath").font(.ui(size: 10)).foregroundStyle(quiet)
                        .help(L("graph.history.tag"))
                        .accessibilityLabel(L("graph.history.tag"))
                        .accessibilityIdentifier("mighty-record-\(node.id)")
                }
                Spacer(minLength: 3)
                if selectedNodeID == node.id { blockScrollLabel(node.id, ink: onStrip ? Palette.onStatus : Palette.accent) }
                if !onStrip && !inputOnly {
                    MightyGraphActivityIndicator(status: status, tint: Palette.run)
                    MightyStatusPill(text: Self.statusLabel(status), tone: DesignTone(blockStatus: status))
                }
                if let capsuleText = ModelUsageFormat.blockCapsule(usage: usage, records: records, nodeModelLabel: nodeModelLabel, catalog: catalog, versioned: true) {
                    let helpText = records.isEmpty ? (usage.map { usageLabel + " · " + $0.detail } ?? "") : ModelUsageFormat.blockCapsuleHelp(records: records, catalog: catalog, versioned: true)
                    Text(capsuleText).font(.ui(size: 10, design: .monospaced)).foregroundStyle(quiet).lineLimit(1)
                        .padding(.horizontal, 6).padding(.vertical, 2)
                        .background(onStrip ? Color.black.opacity(0.18) : Palette.raised, in: Capsule())
                        .help(helpText)
                        .accessibilityLabel(helpText)
                        .accessibilityIdentifier("mighty-tokens-\(node.id)")
                }
                if let resultFilesRunID, !resultFiles.files(for: resultFilesRunID).isEmpty {
                    Button { resultFiles.toggle(resultFilesRunID) } label: {
                        HStack(spacing: 3) {
                            Image(systemName: resultFiles.selectedRunID == resultFilesRunID ? "doc.on.doc.fill" : "doc.on.doc")
                            Text("\(resultFiles.files(for: resultFilesRunID).count)").font(.ui(size: 10)).monospacedDigit()
                        }
                        .foregroundStyle(onStrip ? Palette.onStatus : resultFiles.selectedRunID == resultFilesRunID ? Palette.accent : Palette.ink2)
                    }
                    .buttonStyle(.plain).help(resultFiles.selectedRunID == resultFilesRunID ? L("graph.resultFiles.closeButton") : L("graph.resultFiles.openButton"))
                    .accessibilityLabel(L("graph.resultFiles.countLabel", ["count": "\(resultFiles.files(for: resultFilesRunID).count)"]))
                    .accessibilityIdentifier("mighty-result-files-toggle-\(node.id)")
                }
                let isLatestResult = node.id == MightyGraphLayout.latestResultID(runs: runs)
                // Core owns the rule; the header only reads the same answer.
                let isFittedResult = node.id == MightyGraphLayout.fittedResultID(runs: runs, viewport: canvasViewport, sharedResultSize: graphResultSize)
                let hasSharedSize = isLatestResult && graphResultSize != nil
                if hasSharedSize {
                    Button(L("graph.result.fitToWindow")) {
                        resized.removeValue(forKey: node.id)
                        onSaveResultSize(nil)
                    }
                        .buttonStyle(.plain)
                        .font(.ui(size: 10, weight: onStrip ? .bold : .regular))
                        .foregroundStyle(onStrip ? Palette.onStatus : Palette.accent)
                        .accessibilityIdentifier("mighty-fit-result-\(node.id)")
                }
                if !isFittedResult {
                    Button { toggleExpanded(node.id) } label: { Image(systemName: expanded.contains(node.id) ? "rectangle.compress.vertical" : "rectangle.expand.vertical") }
                        .buttonStyle(.plain).foregroundStyle(quiet).help(expanded.contains(node.id) ? L("graph.block.collapse") : L("graph.block.expand"))
                        .accessibilityLabel(expanded.contains(node.id) ? L("graph.block.collapse") : L("graph.block.expand"))
                        .accessibilityIdentifier("mighty-expand-\(node.id)")
                }
            }.padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).frame(height: Self.blockHeaderHeight)
            .background(headerFill ?? Color.clear)
            if !onStrip { Divider().overlay(Palette.border) }
            if inputOnly {
                // The whole body is the request; a long one scrolls in place.
                let room = node.frame.height - Self.blockHeaderHeight - 1 - 2 * DesignMetrics.Inset.graphBlockBodyV
                VStack(spacing: 0) {
                    if !input.isEmpty {
                        MightyGraphInputPreview(input: input, width: node.frame.width - 2 * DesignMetrics.Inset.graphBlockBodyH, identifier: "mighty-request-\(node.id)",
                                                maximumHeight: max(14, room))
                    }
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Inset.graphBlockBodyV)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .background(tint.opacity(0.055))
            } else if !input.isEmpty {
                MightyGraphInputPreview(input: input, width: node.frame.width - 2 * DesignMetrics.Inset.graphBlockBodyH, identifier: "mighty-request-\(node.id)")
                    .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Inset.graphBlockBodyV)
                    .background(tint.opacity(0.055))
                Divider().overlay(Palette.border)
            }
            if inputOnly {
                EmptyView()
            } else if content.isEmpty {
                Text(MightyGraphLayout.terminal(status) ? L("graph.block.noResponse") : L("graph.block.waitingResponse"))
                    .font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(DesignMetrics.Inset.graphBlockBodyH)
            } else {
                blockTranscript(id: "graph-\(sessionID)-\(node.id)", status: status, entries: content, records: records, childBlocks: childBlocks,
                                onContentHeight: onContentHeight)
            }
        }
        .mightyBlockCard()
        .overlay { if !inputOnly { MightyGraphActivityOutline(tone: DesignTone(blockStatus: status)) } }
        .accessibilityElement(children: .contain).accessibilityIdentifier("mighty-node-\(node.id)")
    }

    /// A block's own records as the diagram's card shows them; the timeline opens the
    /// same view under a row.
    /// `inTimeline`: the transcript sits in the timeline's scroll view and hands the
    /// wheel on to the timeline at its edges.
    private func blockTranscript(id: String, status: String, entries: [LogEntry], records: [GraphResponseRecord], childBlocks: [String: GraphChildBlock], inTimeline: Bool = false,
                                 onContentHeight: ((CGFloat) -> Void)? = nil) -> some View {
        AgentTranscriptView(sessionId: id, provider: provider,
            running: !MightyGraphLayout.terminal(status), entries: entries, onFocus: onFocus,
            onReference: workspaceRoot == nil ? nil : { path, line in openReference(path, line: line) },
            records: records, childBlocks: childBlocks, catalog: catalog, clearsCornerHandle: true, imageRoot: workspaceRoot,
            passesScrollAtEdges: inTimeline, onContentHeight: onContentHeight)
    }

    /// The top of the diagram: loads the previous requests from the session
    /// record, shows that it is doing so, or that the record begins here.
    private func historyCard(width: CGFloat, height: CGFloat) -> some View {
        let phase = history?.phase ?? .idle
        let loaded = olderCount
        return HStack(spacing: 6) {
            switch phase {
            case .loading:
                ProgressView().controlSize(.small).scaleEffect(0.7)
                Text(L("graph.history.loading"))
            case .start:
                Image(systemName: "flag")
                Text(L("graph.history.start"))
            case .unavailable:
                Text(L(loaded > 0 ? "graph.history.start" : "graph.history.none"))
            case .limit:
                Text(L("graph.history.limit"))
            case .failed:
                Button { onLoadOlder() } label: { Label(L("graph.history.failed"), systemImage: "arrow.clockwise") }
                    .buttonStyle(.plain).foregroundStyle(Palette.accent)
                    .accessibilityIdentifier("mighty-history-retry-\(sessionID)")
            case .idle:
                Button { onLoadOlder() } label: { Label(L("graph.history.load"), systemImage: "arrow.up.circle") }
                    .buttonStyle(.plain).foregroundStyle(Palette.accent)
                    .accessibilityIdentifier("mighty-history-load-\(sessionID)")
            }
            if loaded > 0 {
                Text("· " + L("graph.history.loaded", ["count": "\(loaded)"]))
            }
        }
        .font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1).minimumScaleFactor(0.8)
        .padding(.horizontal, DesignMetrics.Spacing.lg)
        .frame(width: width, height: height)
        .background(Palette.panel, in: Capsule())
        .overlay { Capsule().stroke(Palette.border, style: StrokeStyle(lineWidth: 1, dash: [4, 3])) }
        .help(L("graph.history.help"))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-history-\(sessionID)")
    }

    private func blockScrollLabel(_ id: String, ink: Color) -> some View {
        Text(L("graph.block.scrolling")).font(.ui(size: 9, weight: .medium)).foregroundStyle(ink)
            .lineLimit(1).fixedSize().accessibilityIdentifier("mighty-block-scroll-" + id)
    }

    /// A status word as the diagram's pills say it; the structure view says it the same way.
    static func statusLabel(_ status: String) -> String {
        switch status {
        case "completed": L("graph.state.completed")
        case "error", "failed": L("graph.state.error")
        case "stopped", "cancelled", "interrupted": L("graph.state.stopped")
        case "waiting": L("graph.state.waiting")
        case "starting", "queued": L("graph.state.starting")
        default: L("graph.state.running")
        }
    }

    // MARK: Timeline

    /// The requests as a timeline, read from the same runs the diagram draws. Scrolling
    /// up to its top loads older requests from the session record, as panning the
    /// diagram to its top does; a clicked row opens what that block's card holds, and
    /// file references in it open the same bubble.
    private func timeline(_ groups: [MightyTimeline.Group]) -> some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                    if history != nil {
                        historyCard(width: MightyGraphLayout.historyWidth, height: MightyGraphLayout.historyHeight)
                            .frame(maxWidth: .infinity)
                            .id(Self.timelineHistoryID)
                    }
                    if groups.isEmpty {
                        Text(L("graph.timeline.empty")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, DesignMetrics.Spacing.md)
                            .id("timeline-empty")
                    }
                    ForEach(groups) { group in
                        timelineGroup(group).id(group.id)
                    }
                }
                .scrollTargetLayout()
                .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Spacing.md)
            }
            .scrollPosition(id: $timelineTop)
            .defaultScrollAnchor(.bottom)
            // Only the user's own move up onto the top loads more, as in the diagram;
            // following the newest request moves down, never onto it.
            .onChange(of: timelineTop) { old, new in
                guard new == Self.timelineHistoryID, let old, old != new, history?.phase == .idle else { return }
                onLoadOlder()
            }
            .onChange(of: runs.last?.id) { _, last in
                guard let last else { return }
                proxy.scrollTo(last, anchor: .bottom)
            }
            // Once, when a request of this pane finishes: its result card, the
            // last thing in its group, goes to the bottom, above the composer.
            .onChange(of: ownRunProgress) { previous, current in
                guard let run = MightyGraphCamera.ResultReveal.finishedRunID(previous: previous, current: current) else { return }
                proxy.scrollTo(run, anchor: .bottom)
            }
        }
        .background(Palette.raised)
        .overlay {
            if let reference {
                GeometryReader { geo in
                    let frame = MightyGraphReferenceBubble.frame(onLeft: referenceOnLeft, canvas: geo.size, storedWidth: bubbleWidth, storedHeight: bubbleHeight)
                    referenceOverlay(reference)
                        .frame(width: frame.width, height: frame.height)
                        .position(x: frame.midX, y: frame.midY)
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("mighty-timeline-\(sessionID)")
    }

    private func timelineGroup(_ group: MightyTimeline.Group) -> some View {
        let run = runs[group.runIndex]
        let open = (group.runIndex == runs.count - 1) != timelineFlipped.contains(group.id)
        return VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            timelineHeader(group, run: run, open: open)
            if open {
                VStack(alignment: .leading, spacing: MightyTimelineMarker.rowGap) {
                    ForEach(Array(group.rows.enumerated()), id: \.element.id) { index, row in
                        timelineRow(row, index: index, group: group, run: run)
                    }
                }
            }
            if let result = group.result {
                timelineResult(result, group: group, run: run).padding(.leading, MightyTimelineMarker.width + DesignMetrics.Spacing.md)
            }
        }
    }

    /// "Request N · Claude", its status, the request itself and the block and finished counts.
    private func timelineHeader(_ group: MightyTimeline.Group, run: MightyGraphRun, open: Bool) -> some View {
        let title = StyleChrome.requestTitle(prefix: styleTitles.prefix(run.input), ordinal: group.ordinal,
                                             providerLabel: ProviderOptions.label(provider))
        let prompt = group.input.split(whereSeparator: \.isNewline).joined(separator: " ").trimmingCharacters(in: .whitespaces)
        return Button {
            if !timelineFlipped.insert(group.id).inserted { timelineFlipped.remove(group.id) }
        } label: {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                HStack(spacing: DesignMetrics.Spacing.md) {
                    Image(systemName: open ? "chevron.down" : "chevron.right")
                        .font(.ui(size: 10, weight: .bold)).foregroundStyle(Palette.ink2).frame(width: Self.timelineChevronWidth)
                    ProviderBadgeIcon.labelled(title, provider: provider, font: Palette.headingNSFont(16))
                        .font(Palette.heading(16)).foregroundStyle(Palette.ink).lineLimit(1)
                    if group.runIndex < olderCount {
                        Image(systemName: "clock.arrow.circlepath").font(.ui(size: 11)).foregroundStyle(Palette.ink2)
                            .help(L("graph.history.tag")).accessibilityLabel(L("graph.history.tag"))
                    }
                    Spacer(minLength: DesignMetrics.Spacing.sm)
                    MightyStatusPill(text: Self.statusLabel(group.status), tone: DesignTone(status: group.status), height: 20)
                }
                Group {
                    if !prompt.isEmpty {
                        Text(prompt).font(.transcript(size: 12)).foregroundStyle(Palette.ink2).lineLimit(2)
                            .multilineTextAlignment(.leading)
                    }
                    Text(L("phone.blocks.tally", ["total": "\(group.tally.total)", "settled": "\(group.tally.settled)"]))
                        .font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink2)
                }
                .padding(.leading, Self.timelineRequestIndent)
            }
            .padding(.horizontal, 2)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(.isHeader)
        .accessibilityHint(open ? L("phone.blocks.collapse") : L("phone.blocks.expand"))
        .accessibilityIdentifier("mighty-timeline-request-\(run.id)")
    }

    private func timelineRow(_ row: MightyTimeline.Row, index: Int, group: MightyTimeline.Group, run: MightyGraphRun) -> some View {
        let look = timelineLook(row, run: run)
        let open = timelineOpen.contains(row.nodeID)
        // The request's own block is titled by the Mac's locale; the phone payload's
        // title (`MobileMightySupport.blocks`) stays as the phone is sent it.
        let request = row.agentIndex == nil && !row.isExecution
        let title = request ? L("graph.timeline.requestOrdinal", ["n": "\(group.ordinal)"]) : row.block.title
        // The request has no status of its own: its 실행 row runs and settles.
        return MightyTimelineRowCard(row: row, title: title, icon: look.icon, tint: look.tint, meta: timelineMeta(row, run: run),
                                     status: request ? nil : Self.statusLabel(row.block.status), open: open, onToggle: {
            onFocus()
            if timelineOpen.insert(row.nodeID).inserted {
                // Back in the diagram, the camera stands on the block that was opened.
                publish(.reaim(nodeID: row.nodeID, alignTop: false))
            } else {
                timelineOpen.remove(row.nodeID)
            }
        }) {
            timelineDetail(row, run: run, tint: look.tint)
        }
        .padding(.leading, MightyTimelineMarker.width + DesignMetrics.Spacing.md)
        .background(alignment: .topLeading) {
            MightyTimelineMarker(node: row.node, above: index == 0 ? nil : .some(MightyTimeline.railAbove(group.rows, at: index)),
                                 last: index == group.rows.count - 1, icon: look.icon)
        }
    }

    /// The glyph and ink the diagram gives the block.
    private func timelineLook(_ row: MightyTimeline.Row, run: MightyGraphRun) -> (icon: String, tint: Color) {
        if row.isExecution { return (Self.executionIcon, Palette.agentText) }
        guard let agentIndex = row.agentIndex else {
            return (styleTitles.icon(run.input)?.rawValue ?? StyleIcon.requestDefault.rawValue, Palette.tint(styleTitles.tint(run.input)))
        }
        let look = Self.agentPresentation(run.agents[agentIndex])
        return (look.icon, look.tint)
    }

    /// After the kind: the block's usage capsule as the diagram shows it, then how long
    /// its records span once it settled. Only figures the block has.
    private func timelineMeta(_ row: MightyTimeline.Row, run: MightyGraphRun) -> [String] {
        let capsule: String? = if let agentIndex = row.agentIndex {
            ModelUsageFormat.blockCapsule(usage: run.agents[agentIndex].usage, records: run.agents[agentIndex].responseRecords ?? [], nodeModelLabel: nil, catalog: catalog, versioned: true)
        } else if row.isExecution {
            ModelUsageFormat.blockCapsule(usage: run.usage, records: run.responseRecords ?? [], nodeModelLabel: run.nodeModelLabel, catalog: catalog, versioned: true)
        } else {
            nil
        }
        return [capsule, MightyTimelineText.duration(row.block.durationMs)].compactMap { $0 }
    }

    /// What the block's diagram card holds: what it was asked, and its records. The
    /// request holds its input alone and its 실행 block the records alone.
    @ViewBuilder private func timelineDetail(_ row: MightyTimeline.Row, run: MightyGraphRun, tint: Color) -> some View {
        let agent = row.agentIndex.map { run.agents[$0] }
        let request = agent == nil && !row.isExecution
        let input = agent?.shownInput ?? (row.isExecution ? "" : run.input)
        let status = agent?.status ?? run.status
        let entries = request ? [] : (agent?.entries ?? run.rootEntries).filter { $0.kind != "user" }
        let runID = run.sourceRunID ?? run.id
        let records = (agent?.responseRecords ?? run.responseRecords) ?? []
        let childBlocks = GraphChildBlocks.map(responseRecords: agent?.responseRecords ?? run.responseRecords, agents: run.agents, runId: runID)
        VStack(alignment: .leading, spacing: 0) {
            if !input.isEmpty {
                Text(input).font(.transcript(size: 11)).foregroundStyle(Palette.ink).lineLimit(6)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, DesignMetrics.Inset.graphBlockBodyH).padding(.vertical, DesignMetrics.Inset.graphBlockBodyV)
                    .background(tint.opacity(0.055))
                if !request { Divider().overlay(Palette.border) }
            }
            if request {
                EmptyView()
            } else if entries.isEmpty {
                Text(L("phone.blocks.nothing")).font(.ui(size: 12)).foregroundStyle(Palette.ink2)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(DesignMetrics.Inset.graphBlockBodyH)
            } else {
                blockTranscript(id: "timeline-\(sessionID)-\(row.nodeID)", status: status, entries: entries, records: records, childBlocks: childBlocks, inTimeline: true)
                    .frame(height: 260)
            }
        }
        .accessibilityIdentifier("mighty-timeline-detail-\(row.nodeID)")
    }

    private func timelineResult(_ result: MightyTimeline.Result, group: MightyTimeline.Group, run: MightyGraphRun) -> some View {
        let title = switch result.tone {
        case .err: L("graph.block.resultError")
        case .stop: L("graph.block.resultStopped")
        default: L("graph.block.result")
        }
        let tokens = run.totalUsage.map { GraphTokenUsage.compact($0.total) }
        let caption = ([L("graph.timeline.requestOrdinal", ["n": "\(group.ordinal)"])] + [tokens].compactMap { $0 }).joined(separator: " · ")
        let files = run.status == "completed" ? resultFiles.files(for: run.id) : []
        return MightyTimelineResultCard(result: result, title: title, caption: caption, full: timelineFull.contains(result.nodeID), onToggleFull: {
            if !timelineFull.insert(result.nodeID).inserted { timelineFull.remove(result.nodeID) }
        }) {
            if !files.isEmpty {
                Menu {
                    ForEach(files) { file in
                        Button(file.path + (file.line.map { ":\($0)" } ?? "")) { openReference(file.path, line: file.line) }
                    }
                } label: {
                    HStack(spacing: 3) {
                        Image(systemName: "doc.on.doc")
                        Text("\(files.count)").font(.ui(size: 10)).monospacedDigit()
                    }
                    .foregroundStyle(Palette.heroInk(result.tone))
                }
                .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
                .help(L("graph.resultFiles.openButton"))
                .accessibilityLabel(L("graph.resultFiles.countLabel", ["count": "\(files.count)"]))
                .accessibilityIdentifier("mighty-timeline-result-files-\(result.nodeID)")
            }
        }
    }
}


/// Only cards close to the viewport own an NSTextView. Offscreen transparent
/// anchors keep node identity stable across long histories.
private struct MightyGraphCanvas<Card: View, Edges: View>: View {
    let graph: MightyGraphLayout
    let zoom: CGFloat
    let sessionID: String
    let scrollTarget: MightyGraphScrollTarget?
    /// Aimed at when the target's block no longer exists, so a camera is never
    /// committed from the document origin after a block disappeared. It is also
    /// the block a following camera keeps above the composer.
    let defaultNodeID: String
    let follow: MightyGraphFollowBox
    var diagnosticActions = MightyGraphDiagnosticActions()
    @Binding var selection: String?
    /// The connecting lines for the part of the diagram near the viewport.
    let edges: (CGRect) -> Edges
    var overlay: AnyView = AnyView(EmptyView())
    var overlayLayout: MightyOverlayLayout? = nil
    var onOverlayResize: (CGSize, Bool) -> Void = { _, _ in }
    let onResize: (String, CGSize, ResizeEdges, MightyGraphLayout.ResizePhase) -> Void
    let onResetSize: (String) -> Void
    var onStranded: (MightyGraphCamera.StrandedWatch.Loss) -> Void = { _ in }
    var newestRunID: String? = nil
    /// Scrolling up past the top of the diagram asks for older requests.
    var onReachTop: () -> Void = {}
    let card: (MightyGraphLayout.Node) -> Card
    @ViewState private var cameraOffset: CGPoint?
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        GeometryReader { viewport in
            let requested = graph.nodes.first(where: { $0.id == scrollTarget?.nodeID })
            let target = requested ?? graph.nodes.first(where: { $0.id == defaultNodeID })
            let targetFrame = target?.frame
            // Top alignment belongs to the block that was asked for. The
            // fallback is the followed block, so it is followed.
            let alignTop = requested != nil && (scrollTarget?.alignTop ?? false)
            let alignBottom = requested != nil && (scrollTarget?.alignBottom ?? false)
            let follows = requested == nil || (scrollTarget?.follows ?? false)
            let initialOffset = targetFrame.map { MightyGraphFollow.cameraOffset(for: $0, viewport: viewport.size, zoom: zoom, alignTop: alignTop, alignBottom: alignBottom, follows: follows) } ?? .zero
            let displayedOffset = cameraOffset ?? initialOffset
            let panBinding = Binding<CGPoint>(get: { cameraOffset ?? initialOffset }, set: { cameraOffset = $0 })
            let visible = CGRect(x: floor(-displayedOffset.x / zoom / 64) * 64 - 192,
                                 y: floor(-displayedOffset.y / zoom / 64) * 64 - 192,
                                 width: viewport.size.width / zoom + 448,
                                 height: viewport.size.height / zoom + 448)
            let diagram = ZStack(alignment: .topLeading) {
                ZStack(alignment: .topLeading) {
                    edges(visible)
                    ForEach(graph.nodes) { node in
                        ZStack {
                            if visible.intersects(node.frame) {
                                card(node)
                                    .overlay {
                                        if selection == node.id, node.content != .history {
                                            RoundedRectangle(cornerRadius: 12).stroke(Palette.accent, lineWidth: 2)
                                                .allowsHitTesting(false).accessibilityIdentifier("mighty-selected-" + node.id)
                                        }
                                    }
                            } else { Color.clear }
                        }
                        .frame(width: node.frame.width, height: node.frame.height)
                        .overlay(alignment: .bottomTrailing) {
                            if node.isResizable {
                                Image(systemName: "arrow.up.left.and.arrow.down.right")
                                    .font(.ui(size: 10, weight: .semibold))
                                    .foregroundStyle(selection == node.id ? Palette.accent : Palette.ink2)
                                    .frame(width: 22, height: 22)
                                    .background(Palette.panel.opacity(0.95), in: RoundedRectangle(cornerRadius: 5))
                                    .padding(2)
                                    .help(L("graph.block.resizeHelp"))
                                    .accessibilityLabel(L("graph.block.resizeAccessibility"))
                                    .accessibilityIdentifier("mighty-resize-" + node.id)
                                    .contextMenu {
                                        Button(L("graph.block.resetSize")) { onResetSize(node.id) }
                                    }
                            }
                        }
                        .id(node.id)
                        .position(x: MightyGraphCamera.drawnX(nodeX: node.frame.midX, originX: graph.originX), y: node.frame.midY - graph.originY)
                    }
                }
                // The container covers the whole diagram, whose leading edge is
                // negative once a tree is wider than a request card; putting
                // that edge back into the offset leaves the camera, the probe's
                // hit testing and the culling rect all in node coordinates.
                .frame(width: graph.size.width, height: graph.size.height, alignment: .topLeading)
                .scaleEffect(zoom, anchor: .topLeading)
                .offset(x: MightyGraphCamera.drawnOffsetX(cameraX: displayedOffset.x, originX: graph.originX, zoom: zoom),
                        y: displayedOffset.y + graph.originY * zoom)
            }
            .frame(width: viewport.size.width, height: viewport.size.height, alignment: .topLeading)
            .clipped()
            .background(MightyGraphDotGrid(offset: displayedOffset, zoom: zoom, dark: colorScheme == .dark))

            MightyGraphInteraction(sessionID: sessionID, nodes: graph.nodes, zoom: zoom,
                viewportSize: viewport.size, targetToken: scrollTarget?.token,
                targetFrame: targetFrame,
                alignTop: alignTop, alignBottom: alignBottom, follows: follows, followTargetID: defaultNodeID, follow: follow,
                drawnNodeIDs: Set(graph.nodes.lazy.filter { visible.intersects($0.frame) }.map(\.id)), diagnosticActions: diagnosticActions, selection: $selection, panOffset: panBinding, onResize: onResize, onStranded: onStranded, newestRunID: newestRunID,
                onUserPan: { old, new in
                    // Only the user's own move towards the top, once the top
                    // shows: re-aims and zoom never load more.
                    guard new.y > old.y, let top = graph.nodes.first(where: { $0.content == .history })?.frame.minY,
                          MightyGraphCamera.showsTop(camera: new, zoom: zoom, top: top) else { return }
                    onReachTop()
                },
                overlay: AnyView(overlay.environment(\.colorScheme, colorScheme)), overlayLayout: overlayLayout, onOverlayResize: onOverlayResize,
                content: diagram.environment(\.colorScheme, colorScheme))
                .frame(width: viewport.size.width, height: viewport.size.height)
            .accessibilityIdentifier("mighty-graph-canvas-\(sessionID)")
            .onChange(of: zoom) { old, new in
                guard old > 0 else { return }
                let center = CGPoint(x: viewport.size.width / 2, y: viewport.size.height / 2)
                let previous = cameraOffset ?? targetFrame.map { MightyGraphFollow.cameraOffset(for: $0, viewport: viewport.size, zoom: old, alignTop: alignTop, alignBottom: alignBottom, follows: follows) } ?? .zero
                cameraOffset = CGPoint(x: center.x - (center.x - previous.x) * new / old,
                                       y: center.y - (center.y - previous.y) * new / old)
            }
        }
    }
}


/// Concept D's page under the diagram, dotted in the line colour every 18pt of the
/// diagram. The dots are laid from the camera's offset, so they travel with a pan.
/// One dot is drawn into a step-sized tile and the page is filled with it as a tiled
/// pattern, so a pan or zoom frame costs the same however many dots show.
private struct MightyGraphDotGrid: View {
    let offset: CGPoint
    let zoom: CGFloat
    let dark: Bool

    var body: some View {
        let palette = dark ? DesignTokens.dark : DesignTokens.light
        Canvas { context, size in
            let step = 18 * zoom
            guard step >= 6 else { return }
            let radius = max(0.6, zoom)
            let ink = Self.color(palette.line)
            // The tile's dot sits at its centre, so the tile is anchored half a step
            // before the offset to keep a dot on every offset + n·step.
            let tile = Image(size: CGSize(width: step, height: step)) { tile in
                tile.fill(Path(ellipseIn: CGRect(x: step / 2 - radius, y: step / 2 - radius, width: radius * 2, height: radius * 2)), with: .color(ink))
            }
            context.fill(Path(CGRect(origin: .zero, size: size)),
                         with: .tiledImage(tile, origin: CGPoint(x: offset.x - step / 2, y: offset.y - step / 2)))
        }
        .background(Self.color(palette.page))
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private static func color(_ value: DesignColor) -> Color {
        Color(.sRGB, red: Double(value.red) / 255, green: Double(value.green) / 255, blue: Double(value.blue) / 255)
    }
}

/// A short request occupies its natural height; long requests stay selectable
/// and scroll within the pinned request area, separate from agent output.
private struct MightyGraphInputPreview: View {
    let input: String
    let width: CGFloat
    let identifier: String
    /// The most it takes before it scrolls: a pinned strip over a transcript, or the
    /// whole body of a request block that holds its input alone.
    var maximumHeight: CGFloat = 64
    @ViewState private var measuredHeight: CGFloat = 14
    var body: some View {
        // The native view scrolls within the pinned height on its own, and its
        // selection survives the canvas monitor moving first responder away.
        // The identifier goes on the text view itself, so the accessibility
        // lookups in MightyGraphDiagnostics land on the element that carries
        // the request text as its value.
        MightyGraphSelectableText(text: input, width: max(1, width - 16), fontSize: 11, identifier: identifier,
                                  accessibilityLabel: L("graph.block.promptAccessibility"), onHeight: { height in
            if height.isFinite, height > 0, abs(measuredHeight - height) > 0.5 { measuredHeight = ceil(height) }
        })
        .frame(width: width, height: min(maximumHeight, max(14, measuredHeight)))
    }
}

/// The next-request draft. Four lines at most, but four lines of *this* text:
/// the preview asks the layout manager how tall its own first four fragments
/// came out, so a Hangul or emoji fallback is reserved for as it is drawn
/// rather than estimated from the Latin system font.
private struct MightyGraphDraftPreview: View {
    let draft: String
    let width: CGFloat
    let identifier: String
    @ViewState private var measuredHeight: CGFloat = 16
    var body: some View {
        MightyGraphSelectableText(text: draft.isEmpty ? L("graph.block.draftHint") : draft,
                                  width: width, fontSize: 12, secondary: draft.isEmpty, maximumLines: 4,
                                  identifier: identifier,
                                  accessibilityLabel: draft.isEmpty ? L("graph.block.draftAccessibility") : nil,
                                  onHeight: { height in
            if height.isFinite, height > 0, abs(measuredHeight - height) > 0.5 { measuredHeight = ceil(height) }
        })
        .frame(maxWidth: .infinity, minHeight: measuredHeight, maxHeight: measuredHeight, alignment: .leading)
    }
}



private struct MightyGraphScrollTarget {
    let token: String
    let nodeID: String
    let alignTop: Bool
    /// A revealed result: its bottom right above the composer.
    var alignBottom = false
    /// The followed block's bottom right above the composer, on the centreline,
    /// and the camera follows from there (`MightyGraphFollow`).
    var follows = false
}
