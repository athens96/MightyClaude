import AppKit
import MightyCore
import SwiftUI

/// Settings → 원격 연결 (Tailscale): share this Mac's workspaces with another
/// Mighty Claude, and connect to one that shares (docs/remote-workspaces.md).
struct RemoteSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var selected = Set<String>()
    @ViewState private var port = "\(RemoteProtocol.defaultPort)"
    @ViewState private var name = ""
    @ViewState private var address = ""
    @ViewState private var key = ""
    @ViewState private var showsKey = false
    @ViewState private var tailscale: [String] = []

    private var host: RemoteHostState { store.remoteHostState }
    private var localWorkspaces: [Workspace] { store.snapshot.workspaces.filter { $0.remote == nil } }

    var body: some View {
        Group {
            Text(L("settings.remote.intro")).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            SettingsGroup(L("settings.remote.shareTitle")) { sharing }
            SettingsGroup(L("settings.remote.connectTitle")) { connecting }
            if let message = store.remoteError {
                Label(message, systemImage: "exclamationmark.triangle").font(.ui(size: 11)).foregroundStyle(Palette.errText)
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("settings-remote-error")
            }
            if !store.remoteConnections.isEmpty {
                SettingsGroup(L("settings.remote.connectionsTitle")) {
                    ForEach(store.remoteConnections) { connection in connectionRow(connection) }
                }
            }
            SettingsGroup(L("settings.remote.scopeTitle")) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    ForEach(["settings.remote.scope.runs", "settings.remote.scope.notMirrored", "settings.remote.scope.outOfScope", "settings.remote.scope.key"], id: \.self) { item in
                        Text(L(item)).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .accessibilityIdentifier("settings-remote-scope")
            }
        }
        // While this page is open, read both sides now and then: runs start
        // and end on the host, and Tailscale may come up or go away.
        .task {
            await store.reloadRemoteState()
            // What is shared, or else the workspace in front.
            selected = Set(host.enabled ? host.workspaceIds : [store.activeWorkspace].compactMap { $0?.remote == nil ? $0?.id : nil })
            while !Task.isCancelled {
                tailscale = TailscaleAddresses.current()
                await store.reloadRemoteState()
                try? await Task.sleep(for: .seconds(3))
            }
        }
    }

    // MARK: 이 컴퓨터 공유

    @ViewBuilder private var sharing: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Circle().fill(Palette.mark(host.enabled ? "completed" : tailscale.isEmpty ? "error" : "idle")).frame(width: 8, height: 8)
            Text(host.enabled ? L("settings.remote.sharingOn", ["count": "\(host.workspaceIds.count)"]) : tailscale.isEmpty ? L("settings.remote.tailscaleOff") : L("settings.remote.sharingOff"))
                .font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Spacer()
            if host.enabled, host.activeRuns > 0 {
                Text(L("settings.remote.activeRuns", ["count": "\(host.activeRuns)"])).font(.ui(size: 11)).foregroundStyle(Palette.waitText)
            }
        }
        .accessibilityIdentifier("settings-remote-share-status")
        if host.enabled {
            ForEach(host.addresses, id: \.self) { value in
                LabeledContent(L("settings.remote.addressLabel")) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(value).font(.ui(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1)
                        copyButton(value, identifier: "settings-remote-copy-address")
                    }
                }
            }
            if let value = host.key {
                LabeledContent(L("settings.remote.keyLabel")) {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Text(showsKey ? value : String(repeating: "•", count: 16)).font(.ui(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                        Button(showsKey ? L("settings.remote.hideKeyButton") : L("settings.remote.showKeyButton")) { showsKey.toggle() }.controlSize(.small)
                        copyButton(value, identifier: "settings-remote-copy-key", secret: true)
                    }
                }
            }
            Toggle(isOn: Binding(get: { host.allowsFullAccess }, set: { store.setRemoteFullAccess($0) })) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text(L("settings.remote.allowFullAccess"))
                    Text(L("settings.remote.allowFullAccessDescription"))
                        .font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            .accessibilityIdentifier("settings-remote-full-access")
            Text(L("settings.remote.sharedListTitle")).font(.ui(size: 11, weight: .medium)).foregroundStyle(.secondary)
            ForEach(localWorkspaces.filter { host.workspaceIds.contains($0.id) }) { workspace in
                Label(workspace.name, systemImage: "folder").font(.ui(size: 12)).help(workspace.path)
            }
            HStack {
                Spacer()
                Button(role: .destructive) { store.stopSharing(); showsKey = false } label: { Label(L("settings.remote.stopSharing"), systemImage: "stop.circle") }
                    .disabled(store.remoteBusy).accessibilityIdentifier("settings-remote-stop-sharing")
            }
            Text(L("settings.remote.stopSharingNote")).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        } else {
            if localWorkspaces.isEmpty {
                Text(L("settings.remote.noLocalWorkspaces")).font(.ui(size: 11)).foregroundStyle(.secondary)
            } else {
                Text(L("settings.remote.pickWorkspaces")).font(.ui(size: 11, weight: .medium)).foregroundStyle(.secondary)
                ForEach(localWorkspaces) { workspace in
                    Toggle(isOn: Binding(get: { selected.contains(workspace.id) }, set: { if $0 { selected.insert(workspace.id) } else { selected.remove(workspace.id) } })) {
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                            Text(workspace.name).font(.ui(size: 12))
                            Text(workspace.path).font(.ui(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                        }
                    }
                    .accessibilityIdentifier("settings-remote-share-\(workspace.id)")
                }
            }
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(L("settings.remote.portLabel")).font(.ui(size: 12))
                TextField("\(RemoteProtocol.defaultPort)", text: $port).textFieldStyle(.roundedBorder).font(.ui(size: 11, design: .monospaced)).frame(width: 80)
                    .accessibilityIdentifier("settings-remote-port")
                Spacer()
                Button { store.startSharing(workspaceIds: localWorkspaces.map(\.id).filter(selected.contains), port: Int(port) ?? RemoteProtocol.defaultPort) } label: {
                    Label(L("settings.remote.startSharing"), systemImage: "play.circle")
                }
                .disabled(store.remoteBusy || selected.isDisjoint(with: localWorkspaces.map(\.id)) || tailscale.isEmpty)
                .accessibilityIdentifier("settings-remote-start-sharing")
            }
            Text(L("settings.remote.shareNote")).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: 다른 컴퓨터 연결

    @ViewBuilder private var connecting: some View {
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Text(L("settings.remote.addressField")).font(.ui(size: 12)).frame(width: 90, alignment: .leading)
            TextField(L("settings.remote.addressPlaceholder"), text: $address).textFieldStyle(.roundedBorder).font(.ui(size: 11, design: .monospaced))
                .accessibilityIdentifier("settings-remote-address")
        }
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Text(L("settings.remote.keyField")).font(.ui(size: 12)).frame(width: 90, alignment: .leading)
            SecureField(L("settings.remote.keyPlaceholder"), text: $key).textFieldStyle(.roundedBorder).font(.ui(size: 11, design: .monospaced))
                .accessibilityIdentifier("settings-remote-key")
        }
        HStack(spacing: DesignMetrics.Spacing.sm) {
            Text(L("settings.remote.nameField")).font(.ui(size: 12)).frame(width: 90, alignment: .leading)
            TextField(L("settings.remote.namePlaceholder"), text: $name).textFieldStyle(.roundedBorder).font(.ui(size: 12))
                .accessibilityIdentifier("settings-remote-name")
            Button { store.connectRemote(name: name, address: address, key: key); key = "" } label: { Label(L("settings.remote.connectButton"), systemImage: "link") }
                .disabled(store.remoteBusy || address.trimmingCharacters(in: .whitespaces).isEmpty || key.trimmingCharacters(in: .whitespaces).isEmpty)
                .keyboardShortcut(.defaultAction)
                .accessibilityIdentifier("settings-remote-connect")
        }
        Text(L("settings.remote.connectNote")).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    private func connectionRow(_ connection: RemoteConnectionInfo) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Circle().fill(Palette.mark(connection.status == .connected ? "completed" : connection.status == .disconnected ? "error" : "idle")).frame(width: 8, height: 8)
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    Text(connection.name).font(.ui(size: 12, weight: .medium))
                    Text(connection.address).font(.ui(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                }
                Spacer(minLength: DesignMetrics.Spacing.md)
                Button(L("settings.remote.refreshButton")) { store.refreshRemote(connection.id) }.controlSize(.small).disabled(store.remoteBusy)
                    .accessibilityIdentifier("settings-remote-refresh-\(connection.id)")
                if connection.status == .connected {
                    Button(L("settings.remote.disconnectButton")) { store.disconnectRemote(connection.id) }.controlSize(.small).disabled(store.remoteBusy)
                }
                Button(role: .destructive) { store.removeRemote(connection.id) } label: { Image(systemName: "trash") }
                    .controlSize(.small).disabled(store.remoteBusy).help(L("settings.remote.removeHelp"))
                    .accessibilityLabel(L("settings.remote.removeHelp"))
            }
            if let detail = connection.detail { Text(detail).font(.ui(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            if connection.status == .connected {
                if connection.workspaces.isEmpty { Text(L("settings.remote.noShared")).font(.ui(size: 11)).foregroundStyle(.secondary) }
                ForEach(connection.workspaces) { shared in
                    let opened = store.snapshot.workspaces.contains { $0.remote?.connectionId == connection.id && $0.remote?.workspaceId == shared.id }
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        Image(systemName: "desktopcomputer").foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                            Text(shared.name).font(.ui(size: 12))
                            Text(shared.path).font(.ui(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                        }
                        Spacer()
                        Button(opened ? L("settings.remote.openedButton") : L("settings.remote.openButton")) { store.openRemoteWorkspace(connectionId: connection.id, hostWorkspaceId: shared.id) }
                            .controlSize(.small).disabled(store.remoteBusy)
                            .accessibilityIdentifier("settings-remote-open-\(shared.id)")
                    }
                    .padding(.leading, DesignMetrics.Spacing.xl)
                }
            }
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
        .accessibilityIdentifier("settings-remote-connection-\(connection.id)")
    }

    /// A `secret` (the key) is marked concealed and transient, so clipboard
    /// managers neither show nor keep it, and is cleared after a minute if it
    /// is still what the clipboard holds.
    private func copyButton(_ value: String, identifier: String, secret: Bool = false) -> some View {
        Button {
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            guard secret else { pasteboard.setString(value, forType: .string); return }
            let item = NSPasteboardItem()
            item.setString(value, forType: .string)
            item.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
            item.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.TransientType"))
            pasteboard.writeObjects([item])
            let written = pasteboard.changeCount
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(60))
                if pasteboard.changeCount == written, pasteboard.string(forType: .string) == value { pasteboard.clearContents() }
            }
        } label: { Label(L("settings.remote.copyButton"), systemImage: "doc.on.doc") }
            .controlSize(.small).accessibilityIdentifier(identifier)
    }
}
