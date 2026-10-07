import AppKit
import SwiftUI
import MightyCore

struct SessionContextButton: View {
    @EnvironmentObject private var store: AppStore
    let sessionID: String

    private var usage: SessionUsage? {
        guard let session = store.snapshot.sessions.first(where: { $0.id == sessionID }),
              session.sessionUsage?.provider == session.provider else { return nil }
        return session.sessionUsage
    }
    private var presented: Binding<Bool> {
        Binding(get: { store.sessionInfoSessionID == sessionID }, set: { shown in
            if shown { store.sessionInfoSessionID = sessionID }
            else if store.sessionInfoSessionID == sessionID { store.sessionInfoSessionID = nil }
        })
    }

    var body: some View {
        Button {
            store.selectSession(sessionID)
            presented.wrappedValue.toggle()
        } label: {
            // The ring and its 2pt line fill the toolbar-high button.
            SessionContextRing(usage: usage, size: ComposerToolbarMetrics.height - 2)
                .frame(width: ComposerToolbarMetrics.height, height: ComposerToolbarMetrics.height).contentShape(Circle())
                .accessibilityElement(children: .ignore)
        }
        .buttonStyle(.plain)
        .disabled(store.hasModal && store.sessionInfoSessionID != sessionID)
        .accessibilityLabel(L("composer.sessionInfo.buttonAccessibility"))
        .accessibilityValue(SessionUsagePresentation.percent(usage))
        .accessibilityIdentifier("context-\(sessionID)")
        .help(L("composer.sessionInfo.buttonHelp", ["percent": SessionUsagePresentation.percent(usage)]))
        .background(SessionInfoPopoverAnchor(store: store, sessionID: sessionID, presented: presented).id(sessionID).allowsHitTesting(false))
        .onDisappear { if store.sessionInfoSessionID == sessionID { store.sessionInfoSessionID = nil } }
    }
}

/// Explicitly repositions after content-size changes. SwiftUI's popover host
/// otherwise keeps its old top edge when this short details view shrinks.
private struct SessionInfoPopoverAnchor: NSViewRepresentable {
    let store: AppStore
    let sessionID: String
    @Binding var presented: Bool

    func makeCoordinator() -> Coordinator { Coordinator(store: store, sessionID: sessionID, presented: $presented) }
    func makeNSView(context: Context) -> AnchorView {
        let view = AnchorView()
        view.setAccessibilityElement(false)
        context.coordinator.anchor = view
        view.onWindowChange = { [weak coordinator = context.coordinator] in coordinator?.schedule() }
        return view
    }
    func updateNSView(_ view: AnchorView, context: Context) {
        context.coordinator.presented = $presented
        context.coordinator.schedule()
    }
    static func dismantleNSView(_ view: AnchorView, coordinator: Coordinator) {
        view.onWindowChange = nil
        coordinator.dispose()
    }

    final class AnchorView: NSView {
        var onWindowChange: (() -> Void)?
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); onWindowChange?() }
    }

    @MainActor
    final class Coordinator: NSObject, NSPopoverDelegate {
        let store: AppStore
        let sessionID: String
        var presented: Binding<Bool>
        weak var anchor: AnchorView?
        private let popover = NSPopover()
        private var hosting: NSHostingController<AnyView>?
        private var measuredSize: NSSize?
        private var scheduled = false
        private var disposed = false
        private var dismissPending = false

        init(store: AppStore, sessionID: String, presented: Binding<Bool>) {
            self.store = store; self.sessionID = sessionID; self.presented = presented
            super.init()
            popover.delegate = self
            popover.behavior = .transient
            popover.animates = false
        }
        func schedule() {
            guard !disposed, !scheduled else { return }
            scheduled = true
            DispatchQueue.main.async { [weak self] in
                guard let self, !self.disposed else { return }
                self.scheduled = false
                self.synchronize()
            }
        }
        private func synchronize() {
            guard !dismissPending else { return }
            let sessionExists = store.snapshot.sessions.contains { $0.id == sessionID }
            guard sessionExists, presented.wrappedValue, let anchor, anchor.window != nil else {
                if !sessionExists, presented.wrappedValue { presented.wrappedValue = false }
                if popover.isShown { popover.close() }
                hosting = nil; popover.contentViewController = nil; measuredSize = nil
                return
            }
            if hosting == nil {
                let content = SessionInfoView(sessionID: sessionID, onSizeChange: { [weak self] size in
                    guard let self, size.width.isFinite, size.height.isFinite, size.width > 0, size.height > 0 else { return }
                    if self.measuredSize != size { self.measuredSize = size; self.schedule() }
                }).environmentObject(store)
                let controller = NSHostingController(rootView: AnyView(content))
                controller.sizingOptions = []
                hosting = controller
                popover.contentViewController = controller
                popover.contentSize = NSSize(width: 370, height: 480)
            }
            let size = measuredSize ?? NSSize(width: 370, height: 480)
            let changed = abs(popover.contentSize.width - size.width) > 0.5 || abs(popover.contentSize.height - size.height) > 0.5
            if changed { popover.contentSize = size }
            if !popover.isShown || changed {
                // AppKit documents repeated show() as updating the positioning
                // view/rect, preserving the anchor without closing the popover.
                popover.show(relativeTo: anchor.bounds, of: anchor, preferredEdge: .maxY)
            }
        }
        func popoverDidClose(_ notification: Notification) {
            dismissPending = true
            hosting = nil; popover.contentViewController = nil; measuredSize = nil
            DispatchQueue.main.async { [weak self] in
                guard let self, !self.disposed, !self.popover.isShown else { return }
                if self.presented.wrappedValue { self.presented.wrappedValue = false }
                self.dismissPending = false
            }
        }
        func dispose() {
            disposed = true
            popover.delegate = nil
            popover.close(); popover.contentViewController = nil
            hosting = nil; measuredSize = nil; anchor = nil
            let previousBinding = presented
            DispatchQueue.main.async {
                if previousBinding.wrappedValue { previousBinding.wrappedValue = false }
            }
        }
    }
}

struct SessionContextRing: View {
    let usage: SessionUsage?
    var size: CGFloat = 28

    private var percent: Double? {
        guard let value = usage?.contextPercent, value.isFinite else { return nil }
        return value
    }
    private var tint: Color { (percent ?? 0) >= 95 ? Palette.waitText : Palette.accent }

    var body: some View {
        ZStack {
            Circle().stroke(Color.primary.opacity(0.12), lineWidth: 2)
            if let percent {
                Circle().trim(from: 0, to: CGFloat(min(1, max(0, percent / 100))))
                    .stroke(tint, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                    .rotationEffect(.degrees(-90))
            }
            Text(SessionUsagePresentation.percent(usage))
                .font(.system(size: size > 32 ? 13 : 8.5, weight: .semibold, design: .rounded))
                .monospacedDigit().foregroundStyle(percent == nil ? Color.secondary : Color.primary)
                .lineLimit(1).minimumScaleFactor(0.8).padding(.horizontal, 2)
        }.frame(width: size, height: size)
    }
}

/// Always reads the requested session ID, never whichever pane is active now.
struct SessionInfoView: View {
    @EnvironmentObject private var store: AppStore
    let sessionID: String
    var onSizeChange: ((CGSize) -> Void)? = nil
    // AppKit chooses the popover edge before the first content measurement.
    // Reserve its largest possible height for that initial placement, then
    // shrink to the measured content; a 1pt seed can anchor it below the screen.
    @ViewState private var contentHeight: CGFloat = 410
    @ViewState private var showsIdentifiers = false

    private let width: CGFloat = 370
    private let padding: CGFloat = 14
    private let headerHeight: CGFloat = 32
    private let sectionSpacing: CGFloat = 10
    private let maximumBodyHeight: CGFloat = 410

    var body: some View {
        Group {
            if let session = store.snapshot.sessions.first(where: { $0.id == sessionID }) { content(session) }
            else {
                VStack(spacing: 8) {
                    Image(systemName: "rectangle.slash").font(.title2).foregroundStyle(.secondary)
                    Text(L("composer.sessionInfo.closedPane")).font(.system(size: 13, weight: .medium))
                }.frame(maxWidth: .infinity).padding(24)
            }
        }
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        .preferredColorScheme(store.snapshot.theme == "light" ? .light : .dark)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("session-info-\(sessionID)")
    }

    private func content(_ session: RunSession) -> some View {
        let usage = session.sessionUsage?.provider == session.provider ? session.sessionUsage : nil
        let workspace = store.snapshot.workspaces.first { $0.id == session.workspaceId }
        let selectedModel = store.modelLabel(for: session)
        return VStack(alignment: .leading, spacing: sectionSpacing) {
            HStack(spacing: 9) {
                ProviderIcon(provider: session.provider, size: 20)
                VStack(alignment: .leading, spacing: 3) {
                    Text(session.title).font(.system(size: 14, weight: .semibold)).lineLimit(1).help(session.titleHelp)
                    HStack(spacing: 6) {
                        Text(ProviderOptions.label(session.provider)).font(.system(size: 11)).foregroundStyle(.secondary)
                            .accessibilityIdentifier("session-info-provider-\(sessionID)")
                        if ProviderOptions.isBeta(session.provider) { BetaBadge() }
                    }
                }
                Spacer(minLength: 0)
                HStack(spacing: 4) { StatusDot(status: session.status); Text(Palette.status(session.status)).font(.system(size: 10)) }
                    .foregroundStyle(.secondary)
            }.frame(height: headerHeight)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 11) {
                        SessionContextRing(usage: usage, size: 42)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(L("composer.sessionInfo.contextUsage")).font(.system(size: 12, weight: .semibold))
                            Text(SessionUsagePresentation.context(usage)).font(.system(size: 11)).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                    .background(Palette.accent.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(L("composer.sessionInfo.contextAccessibility", ["percent": SessionUsagePresentation.percent(usage), "detail": SessionUsagePresentation.context(usage)]))
                    .accessibilityIdentifier("session-info-context-\(sessionID)")

                    row(usage?.model == nil ? L("composer.sessionInfo.selectedModel") : L("composer.sessionInfo.usedModel"), usage?.model.map { ModelLabel.text($0) } ?? selectedModel, key: "model")
                    if let timing = session.runTiming, timing.isValid {
                        HStack {
                            Text(L("composer.sessionInfo.latestRequestTime")).foregroundStyle(.secondary)
                            Spacer()
                            AgentElapsedView(timing: timing)
                        }.font(.system(size: 11))
                    }
                    if let workspace {
                        row(L("composer.sessionInfo.workspace"), workspace.name, key: "workspace")
                        Text(workspace.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                            .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .accessibilityIdentifier("session-info-path-\(sessionID)")
                    }
                    Divider()
                    if let usage, SessionUsagePresentation.hasTokens(usage) {
                        Text(L("composer.sessionInfo.tokenScope", ["scope": SessionUsagePresentation.scope(usage.tokenScope)])).font(.system(size: 11, weight: .semibold))
                        if let count = usage.inputTokens { tokenRow(L("composer.sessionInfo.input"), count, key: "input") }
                        if let count = usage.outputTokens { tokenRow(L("composer.sessionInfo.output"), count, key: "output") }
                        if let count = usage.cacheReadTokens { tokenRow(L("composer.sessionInfo.cacheRead"), count, key: "cache-read") }
                        if let count = usage.cacheWriteTokens { tokenRow(L("composer.sessionInfo.cacheWrite"), count, key: "cache-write") }
                        if let count = usage.reasoningTokens { tokenRow(L("composer.sessionInfo.reasoning"), count, key: "reasoning") }
                        if let count = usage.totalTokens { tokenRow(L("composer.sessionInfo.total"), count, key: "total") }
                        if usage.cacheReadTokens != nil || usage.cacheWriteTokens != nil || usage.reasoningTokens != nil {
                            Text(L("composer.sessionInfo.note"))
                                .font(.system(size: 10)).foregroundStyle(.tertiary)
                        }
                    }
                    if let cost = usage?.costUSD, cost.isFinite, cost >= 0 {
                        row(L("composer.sessionInfo.costWithScope", ["scope": SessionUsagePresentation.scope(usage?.costScope)]), cost.formatted(.currency(code: "USD").precision(.fractionLength(0...4))), key: "cost")
                    }
                    if usage.map({ !SessionUsagePresentation.hasTokens($0) && $0.costUSD == nil && $0.contextUsedTokens == nil && $0.contextWindowTokens == nil }) ?? true {
                        Text(L("composer.sessionInfo.noUsage")).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    Button { showsIdentifiers.toggle() } label: {
                        HStack(spacing: 5) {
                            Image(systemName: showsIdentifiers ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .semibold))
                            Text(L("composer.sessionInfo.sessionId")).font(.system(size: 11))
                            Spacer(minLength: 0)
                        }.contentShape(Rectangle()).accessibilityElement(children: .ignore)
                    }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
                    .accessibilityLabel(L("composer.sessionInfo.sessionId")).accessibilityValue(showsIdentifiers ? L("accessibility.expanded") : L("accessibility.collapsed"))
                    .accessibilityIdentifier("session-info-identifiers-\(sessionID)")
                    if showsIdentifiers {
                        row(L("composer.sessionInfo.mightySessionId"), session.id, key: "identity", monospaced: true)
                        if let id = usage?.providerSessionId ?? session.resumeId { row(L("composer.sessionInfo.cliSessionId"), id, key: "cli-identity", monospaced: true) }
                    }
                    if let usage {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(L("composer.sessionInfo.reported", ["provider": ProviderOptions.label(session.provider)])).help(usage.source)
                            if let date = AgentRunTiming.parseTimestamp(usage.updatedAt) { Text(L("composer.sessionInfo.lastReceived", ["time": date.formatted(date: .omitted, time: .standard)])) }
                        }.font(.system(size: 10)).foregroundStyle(.tertiary)
                    }
                }
                // Keep a bounded line width without a greedy outer geometry
                // reader. Only the content's natural height drives the popover.
                .frame(width: width - padding * 2 - 16, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
                .background(GeometryReader { geometry in
                    Color.clear.preference(key: SessionInfoContentHeight.self, value: geometry.size.height)
                })
                .padding(.trailing, 16)
            }
            .frame(height: min(maximumBodyHeight, max(1, contentHeight)))
            .onPreferenceChange(SessionInfoContentHeight.self) { height in
                guard height.isFinite, height > 0 else { return }
                let measured = ceil(height)
                if abs(contentHeight - measured) > 0.5 { contentHeight = measured }
                // The body is measured; every other vertical dimension is an
                // explicit layout constant above, not an estimated text height.
                onSizeChange?(CGSize(width: width, height: min(maximumBodyHeight, measured) + headerHeight + sectionSpacing + padding * 2))
            }
        }.padding(padding)
    }

    private func tokenRow(_ title: String, _ count: Int, key: String) -> some View {
        row(title, SessionUsagePresentation.tokens(count), key: key, monospaced: true)
    }

    private func row(_ title: String, _ value: String, key: String, monospaced: Bool = false) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(title).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                .layoutPriority(1)
            Spacer(minLength: 0)
            Text(value).font(.system(size: 11, design: monospaced ? .monospaced : .default))
                .multilineTextAlignment(.trailing).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
        .font(.system(size: 11))
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(title) \(value)")
        .accessibilityIdentifier("session-info-\(key)-\(sessionID)")
    }
}

private struct SessionInfoContentHeight: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

enum SessionUsagePresentation {
    static func percent(_ usage: SessionUsage?) -> String {
        guard let value = usage?.contextPercent, value.isFinite else { return "—" }
        return String(format: "%.0f%%", value)
    }
    static func tokens(_ count: Int) -> String { count.formatted(.number.grouping(.automatic)) }
    static func context(_ usage: SessionUsage?) -> String {
        if let used = usage?.contextUsedTokens, let limit = usage?.contextWindowTokens { return L("composer.sessionInfo.contextTokens", ["used": tokens(used), "limit": tokens(limit)]) }
        if let used = usage?.contextUsedTokens { return L("composer.sessionInfo.contextUsedOnly", ["tokens": tokens(used)]) }
        if let limit = usage?.contextWindowTokens { return L("composer.sessionInfo.contextLimitOnly", ["tokens": tokens(limit)]) }
        return L("composer.sessionInfo.contextUnavailable")
    }
    static func scope(_ value: String?) -> String {
        switch value { case "run": L("composer.sessionInfo.scopeRun"); case "session": L("composer.sessionInfo.scopeSession"); case "response": L("composer.sessionInfo.scopeResponse"); default: L("composer.sessionInfo.scopeUnknown") }
    }
    static func hasTokens(_ usage: SessionUsage) -> Bool {
        [usage.inputTokens, usage.outputTokens, usage.cacheReadTokens, usage.cacheWriteTokens, usage.reasoningTokens, usage.totalTokens].contains { $0 != nil }
    }
}
