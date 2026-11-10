import Foundation

/// Moves what an app from before the rename to Mighty Code kept under its old
/// identifiers (docs/rename-identifiers.md, macOS only): the data folder
/// `Application Support/MightyClaude Native`, the child worktrees under
/// `~/.mightyclaude`, and the settings of the bundle id
/// `dev.mightyclaude.native`. It runs at launch before anything reads them, and
/// only for the default profile. Each step is done once: a folder moves only
/// while the new one is not there yet, and the settings are copied only once.
public enum RenameMigration {
    public static let legacyBundleIdentifier = "dev.mightyclaude.native"
    public static let legacyDataFolder = "MightyClaude Native"
    public static let legacyHomeFolder = ".mightyclaude"
    /// Set once the old settings were copied, so a setting cleared later is not brought back.
    public static let defaultsMarker = "rename.settingsCopiedFromMightyClaude"

    public struct Report: Equatable, Sendable {
        public var movedDataFolder = false
        public var movedWorktrees = false
        public var copiedSettings = 0
        /// The child worktrees now under the new folder, whose git links are repaired after launch (``repairWorktrees(_:)``).
        public var worktrees: [URL] = []
    }

    /// Runs every step. `support` is the user's Application Support folder,
    /// `home` the home folder, `legacySettings` the old bundle id's settings
    /// (nil when there are none).
    @discardableResult
    public static func run(support: URL, home: URL, defaults: UserDefaults, legacySettings: [String: Any]?) -> Report {
        var report = Report()
        let files = FileManager.default
        let data = support.appendingPathComponent("MightyCode", isDirectory: true)
        report.movedDataFolder = move(support.appendingPathComponent(legacyDataFolder, isDirectory: true), to: data)

        let worktreeRoot = home.appendingPathComponent(".mightycode/worktrees", isDirectory: true)
        let oldHome = home.appendingPathComponent(legacyHomeFolder, isDirectory: true)
        if move(oldHome, to: home.appendingPathComponent(".mightycode", isDirectory: true)) {
            report.movedWorktrees = true
            // The records name each child's worktree by its full path.
            for name in ["delegation-state.json", "workspace-state.json"] {
                rewritePaths(in: data.appendingPathComponent(name), from: oldHome.path, to: home.appendingPathComponent(".mightycode").path)
            }
            report.worktrees = ((try? files.contentsOfDirectory(at: worktreeRoot, includingPropertiesForKeys: nil)) ?? []).filter { isFolder($0) }
            moveClaudeProjects(home: home)
        }

        if defaults.object(forKey: defaultsMarker) == nil {
            for (key, value) in legacySettings ?? [:] where defaults.object(forKey: key) == nil {
                defaults.set(value, forKey: key)
                report.copiedSettings += 1
            }
            defaults.set(true, forKey: defaultsMarker)
        }
        return report
    }

    /// Moves `old` to `new` when `old` is a folder and `new` is not there yet. Whether it moved.
    static func move(_ old: URL, to new: URL) -> Bool {
        let files = FileManager.default
        guard isFolder(old), !files.fileExists(atPath: new.path) else { return false }
        do {
            try files.createDirectory(at: new.deletingLastPathComponent(), withIntermediateDirectories: true)
            try files.moveItem(at: old, to: new)
            return true
        } catch {
            return false
        }
    }

    /// Replaces the folder path `old` with `new` in a JSON file, written plainly or with its slashes escaped as JSONEncoder writes them.
    static func rewritePaths(in file: URL, from old: String, to new: String) {
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return }
        let escaped = { (path: String) in path.replacingOccurrences(of: "/", with: "\\/") }
        let updated = text.replacingOccurrences(of: old + "/", with: new + "/").replacingOccurrences(of: escaped(old) + "\\/", with: escaped(new) + "\\/")
        guard updated != text else { return }
        try? Data(updated.utf8).write(to: file, options: .atomic)
    }

    /// Claude keeps a child pane's conversations in a folder named after the
    /// pane's working folder (`~/.claude/projects/-Users-me--mightyclaude-worktrees-<id>`);
    /// it follows the worktree so the child resumes its conversation.
    static func moveClaudeProjects(home: URL) {
        let projects = home.appendingPathComponent(".claude/projects", isDirectory: true)
        let marker = "--mightyclaude-worktrees-"
        for folder in (try? FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil)) ?? [] where folder.lastPathComponent.contains(marker) {
            _ = move(folder, to: projects.appendingPathComponent(folder.lastPathComponent.replacingOccurrences(of: marker, with: "--mightycode-worktrees-"), isDirectory: true))
        }
    }

    /// Points each moved worktree's parent repository at its new place
    /// (`git worktree repair`, run inside the worktree). Off the main thread:
    /// it starts git once per worktree.
    public static func repairWorktrees(_ worktrees: [URL]) async {
        for worktree in worktrees { _ = await DelegationGit.run(["worktree", "repair"], in: worktree.path) }
    }

    static func isFolder(_ url: URL) -> Bool {
        var folder: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &folder) && folder.boolValue
    }
}
