import MightyCore
import SwiftUI

/// How the sidebar, the mirrored pane's strip and the status bar word a server's state, with its dot's colour.
@MainActor enum RemoteMirrorText {
    static func state(_ link: RemoteMirrorLink?, connection: RemoteConnectionInfo?) -> (text: String, color: Color) {
        guard let connection else { return (L("remote.mirror.status.removed"), Palette.mark("idle")) }
        if link?.phase == .reconnecting { return (L("remote.mirror.status.reconnecting"), Palette.waitText) }
        switch connection.status {
        case .connected: return (L("remote.mirror.status.live"), Palette.mark("completed"))
        case .saved: return (L("remote.mirror.status.saved"), Palette.mark("idle"))
        case .disconnected: return (L("remote.mirror.status.off"), Palette.mark("idle"))
        }
    }

    /// "<host>에서 실행 중 · 연결됨", for a mirrored pane or workspace.
    static func runningOn(_ host: String, store: AppStore, connectionId: String) -> (text: String, color: Color) {
        let state = state(store.remoteMirrorLinks[connectionId], connection: store.remoteConnections.first { $0.id == connectionId })
        return (L("remote.mirror.runningOn", ["host": host, "state": state.text]), state.color)
    }
}

/// A server's header in the sidebar, over its shared workspaces: "원격 · 이름", its address, a dot for the mirror's state,
/// and connecting or disconnecting from here.
struct RemoteServerHeader: View {
    @ObservedObject var store: AppStore
    let group: RemoteServerGroup

    var body: some View {
        let state = RemoteMirrorText.state(group.link, connection: group.connection)
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Circle().fill(state.color).frame(width: 7, height: 7).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(verbatim: L("remote.mirror.server", ["name": group.name])).font(.ui(size: 10, weight: .semibold)).foregroundStyle(Palette.sidebarInk2).lineLimit(1)
                Text(verbatim: [group.address, state.text].compactMap { $0 }.joined(separator: " · "))
                    .font(.ui(size: 9.5, design: .monospaced)).foregroundStyle(Palette.sidebarInk2).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 0)
            actions
        }
        .padding(.horizontal, DesignMetrics.Inset.sidebarSectionH).padding(.top, DesignMetrics.Spacing.md).padding(.bottom, DesignMetrics.Spacing.xxs)
        .help(group.link?.error ?? group.connection?.detail ?? L("remote.mirror.serverHelp", ["name": group.name]))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(L("remote.mirror.server", ["name": group.name]) + ", " + state.text)
        .accessibilityIdentifier("sidebar-remote-server-\(group.id)")
    }

    private var actions: some View {
        Menu {
            if let connection = group.connection {
                if connection.status == .connected {
                    Button(L("remote.mirror.disconnect")) { store.disconnectRemote(connection.id) }
                } else {
                    Button(L("remote.mirror.connect")) { store.refreshRemote(connection.id) }
                }
            }
            Button(L("remote.mirror.settings")) { store.openRemoteSettings() }
        } label: {
            Image(systemName: "ellipsis").font(.ui(size: 11)).foregroundStyle(Palette.sidebarInk2).frame(width: 18, height: 14).contentShape(Rectangle())
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
        .disabled(store.remoteBusy)
        .accessibilityLabel(L("remote.mirror.server", ["name": group.name]))
        .accessibilityIdentifier("sidebar-remote-server-menu-\(group.id)")
    }
}

/// The strip over a mirrored pane: "원격 창 · <host>에서 실행 중 · 연결됨", the host pane's branch and the requests queued there.
struct MirrorPaneBanner: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let mirror: RemoteMirrorReference

    var body: some View {
        let state = RemoteMirrorText.runningOn(mirror.hostName, store: store, connectionId: mirror.connectionId)
        let extra = store.remoteMirrorCounts[sessionId] ?? RemoteMirrorCounts()
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "desktopcomputer").font(.ui(size: 11)).foregroundStyle(Palette.accent).accessibilityHidden(true)
            Text(L("remote.mirror.paneBadge")).font(.ui(size: 11, weight: .semibold)).foregroundStyle(Palette.ink)
            Circle().fill(state.color).frame(width: 6, height: 6).accessibilityHidden(true)
            Text(verbatim: state.text).font(.ui(size: 11)).foregroundStyle(Palette.ink2).lineLimit(1).truncationMode(.middle)
                .accessibilityIdentifier("pane-mirror-state-\(sessionId)")
            if let branch = extra.branch {
                Label { Text(verbatim: extra.base.map { "\(branch) ← \($0)" } ?? branch).lineLimit(1).truncationMode(.middle) } icon: { Image(systemName: "arrow.triangle.branch") }
                    .font(.ui(size: 10)).foregroundStyle(Palette.ink2)
            }
            Spacer(minLength: 0)
            if extra.queued > 0 {
                Text(verbatim: L("remote.mirror.queued", ["count": String(extra.queued)])).font(.ui(size: 10, weight: .medium)).foregroundStyle(Palette.waitText)
                    .help(extra.queuedTexts.joined(separator: "\n"))
            }
        }
        .padding(.horizontal, DesignMetrics.Spacing.md)
        .frame(height: 26)
        .background(Palette.accent.opacity(0.06))
        .overlay(alignment: .bottom) { Divider().overlay(Palette.border) }
        .help(L("remote.mirror.bannerHelp", ["host": mirror.hostName, "count": String(RemoteMirrorProtocol.maximumEntries)]))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("pane-mirror-banner-\(sessionId)")
    }
}
