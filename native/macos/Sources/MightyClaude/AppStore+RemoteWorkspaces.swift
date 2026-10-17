import Foundation
import MightyCore

/// Tailscale remote workspaces on the Mac (docs/remote-workspaces.md): the
/// Settings → 원격 연결 actions, and what a remote workspace's panes may and may not do.
extension AppStore {
    func isRemoteSession(_ session: RunSession) -> Bool { isRemoteWorkspace(session.workspaceId) }
    func isRemoteSession(_ id: String) -> Bool { snapshot.sessions.first { $0.id == id }.map(isRemoteSession) ?? false }
    func isRemoteWorkspace(_ id: String) -> Bool { snapshot.workspaces.first { $0.id == id }?.remote != nil }

    func remoteConnection(for workspace: Workspace) -> RemoteConnectionInfo? {
        guard let reference = workspace.remote else { return nil }
        return remoteConnections.first { $0.id == reference.connectionId }
    }

    /// The host's CLI as its last answer described it. Before this launch has
    /// heard from the host, a stand-in that blocks nothing: the send connects
    /// first and the host's real answer is checked then.
    func remoteProviderRuntime(_ provider: String, reference: RemoteWorkspaceReference) -> ProviderRuntime {
        if let known = remoteConnections.first(where: { $0.id == reference.connectionId })?.runtime?.providers?.first(where: { $0.id == provider }) { return known }
        var fallback = ProviderOptions.fallbackRuntime(provider)
        fallback.available = true
        fallback.detail = L("remote.workspace.runtimeUnknown", ["host": reference.hostName])
        fallback.capabilities.permissionModes = ProviderOptions.permissionModes(provider: provider)
        return fallback
    }

    /// Why a remote pane cannot send now: its connection was removed, or the
    /// host said its CLI is missing or lacks the chosen mode.
    func remoteRunBlockedReason(_ session: RunSession, workspace: Workspace, checkRuntime: Bool) -> String? {
        guard let reference = workspace.remote else { return nil }
        guard let connection = remoteConnections.first(where: { $0.id == reference.connectionId }) else { return L("remote.workspace.connectionGone", ["host": reference.hostName]) }
        guard session.kind != "shell", checkRuntime, connection.status == .connected,
              let provider = connection.runtime?.providers?.first(where: { $0.id == session.provider }) else { return nil }
        if !provider.available { return L("remote.host.error.cliMissing", ["name": ProviderOptions.label(session.provider)]) }
        if !provider.capabilities.permissionModes.contains(session.settings.permissionMode) { return L("remote.workspace.modeUnsupported", ["host": reference.hostName]) }
        return nil
    }

    /// A send from a remote pane connects first when the connection is not up
    /// (the first send after launch reads the key from the Keychain).
    func connectForRemoteRun(_ workspace: Workspace) async throws {
        guard let reference = workspace.remote else { return }
        let info = try await remoteClient.ensureConnected(id: reference.connectionId)
        store(info)
        guard info.workspaces.contains(where: { $0.id == reference.workspaceId }) else { throw MightyError(L("remote.workspace.error.notShared")) }
    }

    private func store(_ info: RemoteConnectionInfo) {
        if let index = remoteConnections.firstIndex(where: { $0.id == info.id }) { remoteConnections[index] = info } else { remoteConnections.append(info) }
    }

    /// Reads both sides again; Settings → 원격 연결 calls it while it is open.
    func reloadRemoteState() async {
        remoteHostState = await remoteHost.current()
        remoteConnections = await remoteClient.list()
    }

    /// Opens Settings on its 원격 연결 page.
    func openRemoteSettings() {
        UserDefaults.standard.set(SettingsPane.remote.rawValue, forKey: "settingsPane")
        showSettings = true
    }

    private func remoteAction(_ operation: @escaping () async throws -> Void) {
        guard !remoteBusy else { return }
        remoteBusy = true; remoteError = nil
        Task {
            do { try await operation() } catch { remoteError = error.localizedDescription }
            await reloadRemoteState()
            remoteBusy = false
        }
    }

    func startSharing(workspaceIds: [String], port: Int) {
        remoteAction { [self] in
            // The host reads its workspaces from the saved state.
            try await flush()
            remoteHostState = try await remoteHost.start(workspaceIds: workspaceIds, port: port)
        }
    }
    func stopSharing() { remoteAction { [self] in remoteHostState = await remoteHost.stop() } }
    func connectRemote(name: String, address: String, key: String) { remoteAction { [self] in store(try await remoteClient.connect(name: name, address: address, key: key)) } }
    func refreshRemote(_ id: String) { remoteAction { [self] in store(try await remoteClient.refresh(id: id)) } }
    func disconnectRemote(_ id: String) { remoteAction { [self] in await remoteClient.disconnect(id: id) } }
    func removeRemote(_ id: String) { remoteAction { [self] in await remoteClient.remove(id: id) } }

    /// Lists a shared workspace of a connected computer in the sidebar and opens it.
    func openRemoteWorkspace(connectionId: String, hostWorkspaceId: String) {
        remoteAction { [self] in
            let workspace = try await remoteClient.openWorkspace(connectionId: connectionId, hostWorkspaceId: hostWorkspaceId)
            showSettings = false
            addWorkspace(workspace)
            try await flush()
        }
    }
}
