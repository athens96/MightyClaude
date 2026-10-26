import AppKit
import MightyCore

/// The two ways to add a workspace besides opening an existing folder
/// (docs/superpowers/specs/2026-10-26-workspace-create-design.md).
enum WorkspaceCreateSheet: String, Identifiable {
    case newFolder, fromGit
    var id: String { rawValue }
}

extension AppStore {
    func showWorkspaceCreate(_ kind: WorkspaceCreateSheet) {
        guard isLoaded, !hasModal else { return }
        workspaceCreate = kind
    }

    /// Where a new folder or a clone goes unless the user picks another place: the last place
    /// used, else the folder that holds the open workspace on this Mac, else the home folder.
    var createLocationDefault: String {
        let active = activeWorkspace.flatMap { $0.remote == nil ? $0.path : nil }
        return WorkspaceCreate.defaultLocation(last: snapshot.lastCreateLocation, activeWorkspacePath: active,
                                               home: FileManager.default.homeDirectoryForCurrentUser.path)
    }

    /// The system folder picker for the location; nil when the user cancels.
    func chooseCreateLocation(current: String) -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        if !current.isEmpty { panel.directoryURL = URL(fileURLWithPath: current, isDirectory: true) }
        panel.prompt = L("workspace.create.chooseLocation")
        return panel.runModal() == .OK ? panel.url?.path : nil
    }

    /// Saved when a new folder or a clone succeeds, so the next one starts there.
    func rememberCreateLocation(_ path: String) {
        if snapshot.lastCreateLocation != path { snapshot.lastCreateLocation = path }
    }
}
