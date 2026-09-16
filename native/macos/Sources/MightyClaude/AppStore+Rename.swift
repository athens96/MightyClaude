import Foundation

extension AppStore {
    func beginRenameWorkspace(_ id: String) {
        guard !hasModal, let workspace = snapshot.workspaces.first(where: { $0.id == id }) else { return }
        renameTarget = RenameTarget(kind: .workspace, targetID: id, initialName: workspace.name)
    }

    func beginRenameSession(_ id: String) {
        guard !hasModal, let session = snapshot.sessions.first(where: { $0.id == id }) else { return }
        renameTarget = RenameTarget(kind: .session, targetID: id, initialName: session.title)
    }

    @discardableResult
    func renameWorkspace(_ id: String, to name: String) -> Bool {
        guard let name = Self.displayName(name), let index = snapshot.workspaces.firstIndex(where: { $0.id == id }) else { return false }
        snapshot.workspaces[index].name = name
        return true
    }

    @discardableResult
    func renameSession(_ id: String, to name: String) -> Bool {
        guard let name = Self.displayName(name), snapshot.sessions.contains(where: { $0.id == id }) else { return false }
        updateSession(id) { $0.title = name }
        return true
    }

    private static func displayName(_ value: String) -> String? {
        let name = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 120,
              !name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) else { return nil }
        return name
    }
}
