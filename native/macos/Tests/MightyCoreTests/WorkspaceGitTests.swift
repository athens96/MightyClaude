import Foundation
import Testing
@testable import MightyCore

struct WorkspaceGitTests {
    @Test func branchDirtyAndLocalTracking() throws {
        let value = try #require(WorkspaceGitInfo.parse("# branch.oid 123456789abcdef\n# branch.head feature/한글\n# branch.upstream origin/main\n# branch.ab +2 -3\n? a directory/\n"))
        #expect(value.label == "feature/한글"); #expect(value.isDirty)
        #expect(value.ahead == 2); #expect(value.behind == 3)
    }
    @Test func initialDetachedAndInvalidOutput() throws {
        let initial = try #require(WorkspaceGitInfo.parse("# branch.oid (initial)\n# branch.head main\n"))
        #expect(!initial.isDirty); #expect(initial.revision == nil); #expect(initial.ahead == nil)
        let detached = try #require(WorkspaceGitInfo.parse("# branch.head (detached)\n# branch.oid abcdef123456789\n1 .M N... 100644 100644 100644 a b file\n"))
        #expect(detached.label == "HEAD · abcdef1"); #expect(detached.isDirty)
        #expect(WorkspaceGitInfo.parse("fatal: not a git repository") == nil)
        #expect(WorkspaceGitInfo.parse("# branch.head bad\u{1b}branch\n") == nil)
    }
    @Test(.enabled(if: FileManager.default.isExecutableFile(atPath: "/Library/Developer/CommandLineTools/usr/bin/git"), "Requires the local Git executable"))
    func readUnbornRepositoryWithUntrackedFiles() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-git-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let git = "/Library/Developer/CommandLineTools/usr/bin/git"
        let initialized = try await ProcessCapture.run(executable: URL(fileURLWithPath: git), arguments: ["init", "-b", "main", directory.path], cwd: directory, timeout: 3)
        #expect(initialized.exitCode == 0)
        let cleanResult = await WorkspaceGitInfo.read(path: directory.path)
        let clean = try #require(cleanResult)
        #expect(clean.branch == "main"); #expect(!clean.isDirty)
        try Data("fixture".utf8).write(to: directory.appendingPathComponent("new file.txt"))
        let dirtyResult = await WorkspaceGitInfo.read(path: directory.path)
        let dirty = try #require(dirtyResult)
        #expect(dirty.isDirty)
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent(".git/index.lock").path))
    }
}
