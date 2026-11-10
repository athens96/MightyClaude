import Foundation
import Testing
@testable import MightyCore

/// What an app from before the rename to Mighty Code kept under its old names
/// moves once, at launch, into the new ones.
@Suite struct RenameMigrationTests {
    @Test func theOldDataFolderWorktreesConversationsAndSettingsMoveOnce() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("rename-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let support = root.appendingPathComponent("Support", isDirectory: true), home = root.appendingPathComponent("home", isDirectory: true)
        let oldData = support.appendingPathComponent("MightyClaude Native", isDirectory: true)
        let oldWorktree = home.appendingPathComponent(".mightyclaude/worktrees/child-1", isDirectory: true)
        let oldProject = home.appendingPathComponent(".claude/projects/-Users-me--mightyclaude-worktrees-child-1", isDirectory: true)
        for folder in [oldData, oldWorktree, oldProject] { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        // A plain path, and one with its slashes escaped as JSONEncoder writes them.
        let oldPath = home.path + "/.mightyclaude/worktrees/child-1"
        try Data(#"{"worktreePath":"\#(oldPath)","other":"\#(oldPath.replacingOccurrences(of: "/", with: "\\/"))"}"#.utf8)
            .write(to: oldData.appendingPathComponent("delegation-state.json"))
        let defaults = try #require(UserDefaults(suiteName: "rename-migration-\(UUID().uuidString)"))
        defaults.set("kept", forKey: "already")

        let report = RenameMigration.run(support: support, home: home, defaults: defaults, legacySettings: ["theme": "dark", "already": "old"])
        #expect(report.movedDataFolder && report.movedWorktrees && report.copiedSettings == 1)
        let data = support.appendingPathComponent("MightyCode", isDirectory: true)
        let newPath = home.path + "/.mightycode/worktrees/child-1"
        #expect(!FileManager.default.fileExists(atPath: oldData.path) && FileManager.default.fileExists(atPath: newPath))
        #expect(report.worktrees.map(\.lastPathComponent) == ["child-1"])
        let record = try String(contentsOf: data.appendingPathComponent("delegation-state.json"), encoding: .utf8)
        #expect(record == #"{"worktreePath":"\#(newPath)","other":"\#(newPath.replacingOccurrences(of: "/", with: "\\/"))"}"#)
        #expect(FileManager.default.fileExists(atPath: home.appendingPathComponent(".claude/projects/-Users-me--mightycode-worktrees-child-1").path))
        // A setting the new app already has is not overwritten.
        #expect(defaults.string(forKey: "theme") == "dark" && defaults.string(forKey: "already") == "kept")

        // Once is enough: an old folder made again is left alone while the new one is there, and a setting cleared later stays cleared.
        try FileManager.default.createDirectory(at: oldData, withIntermediateDirectories: true)
        defaults.removeObject(forKey: "theme")
        let again = RenameMigration.run(support: support, home: home, defaults: defaults, legacySettings: ["theme": "dark"])
        #expect(again == RenameMigration.Report())
        #expect(FileManager.default.fileExists(atPath: oldData.path) && defaults.object(forKey: "theme") == nil)
    }

    @Test func nothingFromBeforeTheRenameIsNothingToDo() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("rename-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let defaults = try #require(UserDefaults(suiteName: "rename-migration-\(UUID().uuidString)"))
        let report = RenameMigration.run(support: root.appendingPathComponent("Support"), home: root.appendingPathComponent("home"), defaults: defaults, legacySettings: nil)
        #expect(report == RenameMigration.Report())
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Support/MightyCode").path))
    }

    /// Links and the pairing QR code from a host from before the rename still open.
    @Test func theOldSchemesStillOpen() throws {
        let link = try #require(URL(string: "mightyclaude://open?path=/tmp/project"))
        #expect(DeepLink.parse(link)?.path == "/tmp/project")
        #expect(DeepLink.url(path: "/tmp/project")?.scheme == "mightycode")
    }
}
