import AppKit
import MightyCore
import SwiftUI

enum PaneDockZone: String, Equatable {
    case center, left, right, top, bottom

    static func resolve(location: CGPoint, in size: CGSize) -> PaneDockZone {
        guard size.width > 0, size.height > 0 else { return .center }
        let x = min(1, max(0, location.x / size.width))
        let y = min(1, max(0, location.y / size.height))
        let distances: [(PaneDockZone, CGFloat)] = [(.left, x), (.right, 1 - x), (.top, y), (.bottom, 1 - y)]
        if let nearest = distances.min(by: { $0.1 < $1.1 }), nearest.1 < 0.24 { return nearest.0 }
        return .center
    }

    var placement: String { self == .center ? "tab" : rawValue }
    var title: String {
        switch self {
        case .center: return "탭으로 합치기"
        case .left: return "왼쪽으로 분할"
        case .right: return "오른쪽으로 분할"
        case .top: return "위쪽으로 분할"
        case .bottom: return "아래쪽으로 분할"
        }
    }

    func previewRect(in size: CGSize) -> CGRect {
        let frame: CGRect
        switch self {
        case .center: frame = CGRect(origin: .zero, size: size)
        case .left: frame = CGRect(x: 0, y: 0, width: size.width / 2, height: size.height)
        case .right: frame = CGRect(x: size.width / 2, y: 0, width: size.width / 2, height: size.height)
        case .top: frame = CGRect(x: 0, y: 0, width: size.width, height: size.height / 2)
        case .bottom: frame = CGRect(x: 0, y: size.height / 2, width: size.width, height: size.height / 2)
        }
        return frame.insetBy(dx: 5, dy: 5)
    }
}

private enum PaneDockMetrics {
    static let divider: CGFloat = 10
    static func minimumSize(_ node: PaneLayoutNode) -> CGSize {
        guard node.kind == "split", node.children.count == 2 else { return CGSize(width: 315, height: 290) }
        let first = minimumSize(node.children[0]), second = minimumSize(node.children[1])
        if node.axis == "vertical" { return CGSize(width: max(first.width, second.width), height: first.height + second.height + divider) }
        return CGSize(width: first.width + second.width + divider, height: max(first.height, second.height))
    }
}

struct PaneDockView: View {
    @EnvironmentObject private var store: AppStore
    let root: PaneLayoutNode
    let workspaceId: String

    var body: some View {
        GeometryReader { geometry in
            let visibleRoot = focusedGroup ?? root
            let minimum = PaneDockMetrics.minimumSize(visibleRoot)
            ScrollView([.horizontal, .vertical]) {
                PaneDockNode(node: visibleRoot, workspaceId: workspaceId)
                    .frame(width: max(minimum.width, geometry.size.width - 32), height: max(minimum.height, geometry.size.height - 32))
                    .padding(16)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("pane-layout-\(workspaceId)")
    }

    private var focusedGroup: PaneLayoutNode? {
        guard store.paneLayoutMode(workspaceId) == "focus", let id = store.snapshot.activeSessionId else { return nil }
        return root.group(containing: id)
    }
}

private struct PaneDockNode: View {
    let node: PaneLayoutNode
    let workspaceId: String

    var body: some View {
        Group {
            if node.kind == "split", node.children.count == 2 {
                PaneDockSplit(node: node, workspaceId: workspaceId)
            } else { PaneDockGroup(node: node, workspaceId: workspaceId) }
        }
    }
}

private struct PaneDockSplit: View {
    @EnvironmentObject private var store: AppStore
    let node: PaneLayoutNode
    let workspaceId: String
    @ViewState private var resizeOrigin: CGFloat?
    @ViewState private var dividerHovered = false

    private var horizontal: Bool { node.axis != "vertical" }

    var body: some View {
        GeometryReader { geometry in
            let total = max(1, (horizontal ? geometry.size.width : geometry.size.height) - PaneDockMetrics.divider)
            let firstMinimum = PaneDockMetrics.minimumSize(node.children[0])
            let secondMinimum = PaneDockMetrics.minimumSize(node.children[1])
            let lower = horizontal ? firstMinimum.width : firstMinimum.height
            let upper = total - (horizontal ? secondMinimum.width : secondMinimum.height)
            let extent = min(max(lower, total * CGFloat(node.ratio)), max(lower, upper))
            if horizontal {
                HStack(spacing: 0) {
                    child(0).frame(width: extent)
                    divider(total: total, extent: extent, lower: lower, upper: upper)
                    child(1).frame(maxWidth: .infinity)
                }
            } else {
                VStack(spacing: 0) {
                    child(0).frame(height: extent)
                    divider(total: total, extent: extent, lower: lower, upper: upper)
                    child(1).frame(maxHeight: .infinity)
                }
            }
        }
    }

    // Erase only the recursive boundary to keep SwiftUI's concrete type finite.
    private func child(_ index: Int) -> AnyView { AnyView(PaneDockNode(node: node.children[index], workspaceId: workspaceId).id(node.children[index].id)) }

    private func divider(total: CGFloat, extent: CGFloat, lower: CGFloat, upper: CGFloat) -> some View {
        Rectangle().fill(Color.clear)
            .frame(width: horizontal ? PaneDockMetrics.divider : nil, height: horizontal ? nil : PaneDockMetrics.divider)
            .overlay {
                RoundedRectangle(cornerRadius: 2)
                    .fill(dividerHovered || resizeOrigin != nil ? Palette.accent : Palette.border)
                    .frame(width: horizontal ? 3 : 30, height: horizontal ? 30 : 3)
            }
            .contentShape(Rectangle())
            .onHover { hovering in
                dividerHovered = hovering
                (hovering ? (horizontal ? NSCursor.resizeLeftRight : NSCursor.resizeUpDown) : NSCursor.arrow).set()
            }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .global)
                .onChanged { value in
                    if resizeOrigin == nil { resizeOrigin = extent }
                    let offset = horizontal ? value.translation.width : value.translation.height
                    let next = min(max(lower, (resizeOrigin ?? extent) + offset), max(lower, upper))
                    store.resizePaneSplit(node.id, ratio: Double(next / total))
                }
                .onEnded { _ in resizeOrigin = nil })
            .onTapGesture(count: 2) { store.resizePaneSplit(node.id, ratio: 0.5) }
            .accessibilityElement()
            .accessibilityLabel(horizontal ? "좌우 분할 비율" : "상하 분할 비율")
            .accessibilityValue("\(Int(node.ratio * 100))퍼센트")
            .accessibilityAdjustableAction { direction in
                switch direction {
                case .increment: store.resizePaneSplit(node.id, ratio: node.ratio + 0.05)
                case .decrement: store.resizePaneSplit(node.id, ratio: node.ratio - 0.05)
                @unknown default: break
                }
            }
            .accessibilityIdentifier("pane-divider-\(node.id)")
            .help("끌어서 크기 조절 · 두 번 클릭해 균등 분할")
    }
}

private struct PaneDockGroup: View {
    @EnvironmentObject private var store: AppStore
    let node: PaneLayoutNode
    let workspaceId: String

    private var sessions: [RunSession] {
        node.sessionIds.compactMap { id in store.snapshot.sessions.first { $0.id == id && $0.workspaceId == workspaceId } }
    }
    private var selected: RunSession? { sessions.first { $0.id == node.selectedSessionId } ?? sessions.first }

    var body: some View {
        GeometryReader { geometry in
            VStack(spacing: 0) {
                tabStrip
                if let selected {
                    if selected.kind == "browser" { BrowserPaneView(session: selected).id(selected.id) }
                    else { SessionPaneView(session: selected).id(selected.id) }
                } else { Color.clear }
            }
            .background(Palette.panel, in: RoundedRectangle(cornerRadius: 11))
            .overlay(PaneDockGroupAnchor(workspaceId: workspaceId, groupId: node.id))
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("pane-group-\(node.id)")
    }

    private var tabStrip: some View {
        HStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView(.horizontal) {
                    HStack(spacing: 3) {
                        ForEach(Array(sessions.enumerated()), id: \.element.id) { index, session in
                            PaneDockTab(session: session, groupId: node.id, selected: session.id == selected?.id, nextSessionId: sessions.indices.contains(index + 1) ? sessions[index + 1].id : nil)
                                .id(session.id)
                        }
                    }.padding(.horizontal, 5).padding(.vertical, 4)
                }
                .scrollIndicators(.hidden)
                .onChange(of: node.selectedSessionId) { _, id in if let id { proxy.scrollTo(id, anchor: .center) } }
            }
            .frame(minWidth: 0, maxWidth: .infinity)
        }
        .frame(height: 38)
        .background(Palette.subtle, in: UnevenRoundedRectangle(topLeadingRadius: 11, topTrailingRadius: 11))
        .overlay(alignment: .bottom) { Rectangle().fill(sessions.contains(where: { $0.id == store.snapshot.activeSessionId }) ? Palette.accent.opacity(0.55) : Palette.border).frame(height: 1) }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("실행 창 탭 그룹").accessibilityIdentifier("pane-tab-strip-\(node.id)")
    }

}

private struct PaneDockTab: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    let groupId: String
    let selected: Bool
    let nextSessionId: String?

    var body: some View {
        HStack(spacing: 0) {
            // The click/drag handle covers the whole tab up to the close
            // button, padding included, so the tab's visible shape is its hit area.
            HStack(spacing: 6) {
                Group {
                    if session.kind == "shell" { Image(systemName: "terminal").font(.system(size: 10)) }
                    else if session.kind == "browser" { Image(systemName: "globe").font(.system(size: 10)) }
                    else { ProviderIcon(provider: session.provider, size: 10) }
                }
                Text(session.title).font(.system(size: 11, weight: selected ? .semibold : .regular)).lineLimit(1).frame(maxWidth: 125)
                if session.status == "running" { StatusDot(status: session.status) }
            }
            .padding(.leading, 10).padding(.trailing, 6)
            .frame(minWidth: 56, minHeight: 30, maxHeight: 30, alignment: .leading)
            .allowsHitTesting(false).accessibilityHidden(true)
            .overlay { PaneDockTabHandle(store: store, sessionId: session.id, workspaceId: session.workspaceId, groupId: groupId, title: session.title, nextSessionId: nextSessionId) }
            Button { store.closeSession(session.id) } label: { Image(systemName: "xmark").font(.system(size: 8, weight: .medium)).frame(width: 20, height: 30).contentShape(Rectangle()) }
                .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("\(session.title) 탭 닫기")
        }
        .padding(.trailing, 2).frame(height: 30)
        .foregroundStyle(selected ? Color.primary : Color.secondary)
        .background(selected ? Palette.panel : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .overlay { RoundedRectangle(cornerRadius: 6).stroke(selected ? Palette.border : Color.clear) }
        .contentShape(RoundedRectangle(cornerRadius: 6))
        .help("\(session.title) · 끌어서 탭 순서 변경 또는 분할")
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("pane-tab-\(session.id)")
    }
}
