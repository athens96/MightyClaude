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
            SessionContextRing(usage: usage)
                .frame(width: 32, height: 32).contentShape(Circle())
                .accessibilityElement(children: .ignore)
        }
        .buttonStyle(.plain)
        .disabled(store.hasModal && store.sessionInfoSessionID != sessionID)
        .accessibilityLabel("세션 정보 · 컨텍스트 사용량")
        .accessibilityValue(SessionUsagePresentation.percent(usage))
        .accessibilityIdentifier("context-\(sessionID)")
        .help("컨텍스트 \(SessionUsagePresentation.percent(usage)) · 세션 정보")
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
    private var tint: Color { (percent ?? 0) >= 95 ? .orange : Palette.accent }

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
                .lineLimit(1)
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
                    Text("닫힌 실행 창입니다").font(.system(size: 13, weight: .medium))
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
        let catalog = store.providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog
        let selectedModel = catalog.models.first { $0.value == session.model }?.displayName ?? (session.model == "default" ? "CLI 기본값" : session.model)
        return VStack(alignment: .leading, spacing: sectionSpacing) {
            HStack(spacing: 9) {
                Image(systemName: Palette.symbol(session.provider)).font(.system(size: 20)).foregroundStyle(Palette.accent)
                VStack(alignment: .leading, spacing: 3) {
                    Text(session.title).font(.system(size: 14, weight: .semibold)).lineLimit(1).help(session.title)
                    Text(ProviderOptions.label(session.provider)).font(.system(size: 11)).foregroundStyle(.secondary)
                        .accessibilityIdentifier("session-info-provider-\(sessionID)")
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
                            Text("컨텍스트 사용량").font(.system(size: 12, weight: .semibold))
                            Text(SessionUsagePresentation.context(usage)).font(.system(size: 11)).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                    .background(Palette.accent.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel("컨텍스트 사용량 \(SessionUsagePresentation.percent(usage)) · \(SessionUsagePresentation.context(usage))")
                    .accessibilityIdentifier("session-info-context-\(sessionID)")

                    row(usage?.model == nil ? "선택 모델" : "사용 모델", usage?.model ?? selectedModel, key: "model")
                    if let timing = session.runTiming, timing.isValid {
                        HStack {
                            Text("최근 요청 시간").foregroundStyle(.secondary)
                            Spacer()
                            AgentElapsedView(timing: timing)
                        }.font(.system(size: 11))
                    }
                    if let workspace {
                        row("워크스페이스", workspace.name, key: "workspace")
                        if let remote = workspace.remote { row("실행 컴퓨터", remote.hostName, key: "host") }
                        Text(workspace.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                            .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .accessibilityIdentifier("session-info-path-\(sessionID)")
                    }
                    Divider()
                    if let usage, SessionUsagePresentation.hasTokens(usage) {
                        Text("토큰 · \(SessionUsagePresentation.scope(usage.tokenScope))").font(.system(size: 11, weight: .semibold))
                        if let count = usage.inputTokens { tokenRow("입력", count, key: "input") }
                        if let count = usage.outputTokens { tokenRow("출력", count, key: "output") }
                        if let count = usage.cacheReadTokens { tokenRow("캐시 읽기", count, key: "cache-read") }
                        if let count = usage.cacheWriteTokens { tokenRow("캐시 쓰기", count, key: "cache-write") }
                        if let count = usage.reasoningTokens { tokenRow("사고", count, key: "reasoning") }
                        if let count = usage.totalTokens { tokenRow("전체", count, key: "total") }
                        if usage.cacheReadTokens != nil || usage.cacheWriteTokens != nil || usage.reasoningTokens != nil {
                            Text("캐시는 입력에, 사고 토큰은 출력에 포함됩니다.")
                                .font(.system(size: 10)).foregroundStyle(.tertiary)
                        }
                    }
                    if let cost = usage?.costUSD, cost.isFinite, cost >= 0 {
                        row("비용 (USD) · \(SessionUsagePresentation.scope(usage?.costScope))", cost.formatted(.currency(code: "USD").precision(.fractionLength(0...4))), key: "cost")
                    }
                    if usage.map({ !SessionUsagePresentation.hasTokens($0) && $0.costUSD == nil && $0.contextUsedTokens == nil && $0.contextWindowTokens == nil }) ?? true {
                        Text("아직 CLI가 사용량을 보고하지 않았습니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    Button { showsIdentifiers.toggle() } label: {
                        HStack(spacing: 5) {
                            Image(systemName: showsIdentifiers ? "chevron.down" : "chevron.right").font(.system(size: 8, weight: .semibold))
                            Text("세션 ID").font(.system(size: 11))
                            Spacer(minLength: 0)
                        }.contentShape(Rectangle()).accessibilityElement(children: .ignore)
                    }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
                    .accessibilityLabel("세션 ID").accessibilityValue(showsIdentifiers ? "펼침" : "접힘")
                    .accessibilityIdentifier("session-info-identifiers-\(sessionID)")
                    if showsIdentifiers {
                        row("MightyClaude 세션 ID", session.id, key: "identity", monospaced: true)
                        if let id = usage?.providerSessionId ?? session.resumeId { row("CLI 세션 ID", id, key: "cli-identity", monospaced: true) }
                    }
                    if let usage {
                        VStack(alignment: .leading, spacing: 3) {
                            Text("\(ProviderOptions.label(session.provider)) CLI 보고값").help(usage.source)
                            if let date = AgentRunTiming.parseTimestamp(usage.updatedAt) { Text("마지막 수신 \(date.formatted(date: .omitted, time: .standard))") }
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
        if let used = usage?.contextUsedTokens, let limit = usage?.contextWindowTokens { return "\(tokens(used)) / \(tokens(limit)) 토큰" }
        if let used = usage?.contextUsedTokens { return "\(tokens(used)) 토큰 · 한도 미측정" }
        if let limit = usage?.contextWindowTokens { return "한도 \(tokens(limit)) 토큰 · 사용량 미측정" }
        return "아직 컨텍스트 사용량을 받지 못했습니다."
    }
    static func scope(_ value: String?) -> String {
        switch value { case "run": "이번 실행"; case "session": "이 대화 누적"; case "response": "최근 응답"; default: "보고된 범위" }
    }
    static func hasTokens(_ usage: SessionUsage) -> Bool {
        [usage.inputTokens, usage.outputTokens, usage.cacheReadTokens, usage.cacheWriteTokens, usage.reasoningTokens, usage.totalTokens].contains { $0 != nil }
    }
}
