import Foundation
import Testing
@testable import MightyCore

/// A workspace's agent pane in its own worktree (``PaneWorktree``): where a new pane works, its port, how it is saved, and
/// the real git of making, merging and discarding it.
struct PaneWorktreeTests {
    private func agent(_ id: String, workspace: String = "w", parent: String? = nil, folder: String? = nil, kind: String = SessionKind.claude) -> RunSession {
        var session = RunSession(id: id, workspaceId: workspace, title: id, kind: kind)
        session.parentSessionId = parent; session.workingFolder = folder
        return session
    }

    @Test func theFirstAgentPaneStaysInTheFolderAndAFurtherOneIsAsked() {
        var workspace = Workspace(id: "w", name: "W", path: "/tmp/w")
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: []) == .sameFolder)
        // A terminal, a child and a pane already in its own worktree do not work in the workspace folder.
        let others = [agent("t", kind: SessionKind.shell), agent("c", parent: "x", folder: "/wt/c"), agent("p", folder: "/wt/p"), agent("o", workspace: "other")]
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: others) == .sameFolder)
        let open = others + [agent("a")]
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: open) == .ask)
        // A pane going away no longer counts.
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: open, closing: ["a"]) == .sameFolder)
        workspace.paneWorktreeMode = .further
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: open) == .worktree)
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: []) == .sameFolder)
        // Every pane, the first too: the workspace folder only receives merges.
        workspace.paneWorktreeMode = .every
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: []) == .worktree)
        // Nobody to ask (the phone): asking becomes the folder, a setting still opens a worktree.
        workspace.paneWorktreeMode = nil
        #expect(PaneWorktreePolicy.decideWithoutAsking(workspace: workspace, sessions: open) == .sameFolder)
        workspace.paneWorktreeMode = .further
        #expect(PaneWorktreePolicy.decideWithoutAsking(workspace: workspace, sessions: open) == .worktree)
        // A remote workspace's folder is another computer's: never a worktree here.
        workspace.remote = RemoteWorkspaceReference(connectionId: "c1", workspaceId: "h1", hostName: "host")
        #expect(PaneWorktreePolicy.decide(workspace: workspace, sessions: open) == .sameFolder)
    }

    @Test func aPaneAsksForTheSamePortAndSkipsTakenAndBusyOnes() {
        let first = PaneWorktreePorts.port(for: "pane-1", taken: [], isFree: { _ in true })
        #expect(PaneWorktreePorts.range.contains(first))
        #expect(PaneWorktreePorts.port(for: "pane-1", taken: [], isFree: { _ in true }) == first)
        let next = PaneWorktreePorts.port(for: "pane-1", taken: [first], isFree: { _ in true })
        #expect(next != first && PaneWorktreePorts.range.contains(next))
        #expect(PaneWorktreePorts.port(for: "pane-1", taken: [], isFree: { $0 != first }) == next)
    }

    @Test func aPaneWorktreeAndTheWorkspaceSettingsRoundTripAndOlderSavesLoad() throws {
        var session = agent("pane-1", folder: "/wt/pane-1")
        session.paneWorktree = PaneWorktree(worktreePath: "/wt/pane-1", branch: "mighty/pane-1", baseBranch: "main", baseCommit: String(repeating: "a", count: 40),
                                            checkout: "/repo", port: 4321, setupCommand: "npm ci", setupStatus: .done, setupExitCode: 0)
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(decoded.paneWorktree == session.paneWorktree)
        #expect(decoded.workingFolder == "/wt/pane-1")
        // A save from before has no key: no pane worktree.
        var object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        object.removeValue(forKey: "paneWorktree")
        #expect(try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object)).paneWorktree == nil)

        var workspace = Workspace(id: "w", name: "W", path: "/tmp/w")
        workspace.paneWorktreeMode = .every; workspace.worktreeSetupCommand = "npm ci"; workspace.worktreeCopyPaths = [".env"]
        workspace.keptPaneWorktrees = [KeptPaneWorktree(sessionId: "pane-1", title: "T", provider: "claude", worktree: try #require(session.paneWorktree))]
        #expect(try JSONDecoder().decode(Workspace.self, from: JSONEncoder().encode(workspace)) == workspace)
        let older = try JSONDecoder().decode(Workspace.self, from: Data(#"{"id":"w","name":"W","path":"/tmp/w","createdAt":"2026-01-01T00:00:00.000Z"}"#.utf8))
        #expect(older.paneWorktreeMode == nil && older.worktreeSetupCommand == nil && older.keptPaneWorktrees == nil)
        // A mode this build does not know reads as asking; a damaged kept record costs only itself, never the workspace.
        let odd = try JSONDecoder().decode(Workspace.self, from: Data(#"{"id":"w","name":"W","path":"/tmp/w","createdAt":"x","paneWorktreeMode":"sometimes","keptPaneWorktrees":[{"sessionId":1},{"sessionId":"pane-1","title":"T","provider":"claude","keptAt":"x","worktree":{"worktreePath":"/wt/pane-1","branch":"mighty/pane-1","baseBranch":"main","baseCommit":"a","checkout":"/repo","port":4321}}]}"#.utf8))
        #expect(odd.paneWorktreeMode == .ask && odd.keptPaneWorktrees?.map(\.sessionId) == ["pane-1"])
    }

    @Test func aSavedPaneWorktreeIsCheckedOnLoad() {
        let pane = PaneWorktree(worktreePath: "/wt/pane-1", branch: "mighty/pane-1", baseBranch: "main", baseCommit: String(repeating: "a", count: 40),
                                checkout: "/repo", port: 4321, setupCommand: " npm ci \n", setupStatus: .running)
        // A setup still running when the app quit reads as failed; the command is one clean line.
        let restored = pane.normalized(sessionId: "pane-1", restoring: true)
        #expect(restored?.setupStatus == .failed && restored?.setupCommand == "npm ci")
        #expect(pane.normalized(sessionId: "pane-1", restoring: false)?.setupStatus == .running)
        // Another pane's branch, a relative path or a port outside the range is no record of this app's.
        #expect(pane.normalized(sessionId: "pane-2", restoring: true) == nil)
        var relative = pane; relative.worktreePath = "wt/pane-1"
        #expect(relative.normalized(sessionId: "pane-1", restoring: true) == nil)
        var port = pane; port.port = 80
        #expect(port.normalized(sessionId: "pane-1", restoring: true) == nil)
        #expect(PaneWorktreeSetup.normalizedCommand("   ") == nil)
        #expect(PaneWorktreeSetup.normalizedCommand("npm ci\nnpm test") == "npm ci npm test")
    }
}

/// Real git in a temp folder: a pane worktree made, merged into the workspace branch and discarded.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct PaneWorktreeGitTests {
    private static let plenty: @Sendable (URL) -> Int64? = { _ in 50_000_000_000 }

    @Test func aPaneWorktreeIsMadeMergedByFastForwardAndThenHasNothingToMerge() async throws {
        let place = try PaneGitPlace(); defer { place.remove() }
        try await place.repository()
        let pane = try await place.makePane("pane-1")
        #expect(pane.branch == "mighty/pane-1" && pane.baseBranch == "main")
        // No TASK.md or REPORT.md: a pane worktree is no delegated child.
        #expect(!FileManager.default.fileExists(atPath: pane.worktreePath + "/" + ChildWorktree.notesFolder))
        #expect(await PaneWorktreeGit.hasUnmergedWork(pane) == false)
        #expect(await PaneWorktreeGit.merge(pane) == .refused(.nothingToMerge))

        // An uncommitted change does not go with a merge.
        try Data("new\n".utf8).write(to: URL(fileURLWithPath: pane.worktreePath).appendingPathComponent("file-0.txt"))
        #expect(await PaneWorktreeGit.hasUnmergedWork(pane) == true)
        #expect(await PaneWorktreeGit.merge(pane) == .refused(.uncommittedChanges))
        try await place.git(["commit", "-q", "-am", "pane work"], in: pane.worktreePath)
        let head = try await place.git(["rev-parse", "HEAD"], in: pane.worktreePath)
        guard case .merged(let merged, let preMerge) = await PaneWorktreeGit.merge(pane) else { Issue.record("not merged"); return }
        #expect(merged == head && preMerge == pane.baseCommit)
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(await PaneWorktreeGit.hasUnmergedWork(pane) == false)
        #expect(await PaneWorktreeGit.merge(pane) == .refused(.nothingToMerge))
    }

    @Test func aMovedWorkspaceBranchGetsOneMergeCommitAndAConflictIsRefused() async throws {
        let place = try PaneGitPlace(); defer { place.remove() }
        try await place.repository()
        let pane = try await place.makePane("pane-2")
        try Data("pane\n".utf8).write(to: URL(fileURLWithPath: pane.worktreePath).appendingPathComponent("pane.txt"))
        try await place.git(["add", "-A"], in: pane.worktreePath); try await place.git(["commit", "-q", "-m", "pane"], in: pane.worktreePath)
        try Data("main\n".utf8).write(to: place.repo.appendingPathComponent("main.txt"))
        try await place.git(["add", "-A"]); try await place.git(["commit", "-q", "-m", "main"])
        // The workspace folder dirty: refused before any write.
        try Data("dirty\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        #expect(await PaneWorktreeGit.merge(pane) == .refused(.trackedChanges))
        try await place.git(["checkout", "-q", "--", "file-0.txt"])
        guard case .merged(let commit, _) = await PaneWorktreeGit.merge(pane) else { Issue.record("not merged"); return }
        #expect(try await place.git(["rev-list", "--parents", "-n", "1", commit]).split(separator: " ").count == 3)
        #expect(FileManager.default.fileExists(atPath: place.repo.appendingPathComponent("pane.txt").path))

        // Both change the same line: refused, nothing moves.
        let other = try await place.makePane("pane-3")
        try Data("left\n".utf8).write(to: URL(fileURLWithPath: other.worktreePath).appendingPathComponent("file-0.txt"))
        try await place.git(["commit", "-q", "-am", "left"], in: other.worktreePath)
        try Data("right\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        try await place.git(["commit", "-q", "-am", "right"])
        let before = try await place.git(["rev-parse", "HEAD"])
        #expect(await PaneWorktreeGit.merge(other) == .refused(.mergeConflict))
        #expect(try await place.git(["rev-parse", "HEAD"]) == before)
    }

    @Test func discardRemovesTheWorktreeAndItsBranch() async throws {
        let place = try PaneGitPlace(); defer { place.remove() }
        try await place.repository()
        let pane = try await place.makePane("pane-4")
        #expect(await PaneWorktreeGit.discard(pane, sessionId: "pane-4", root: place.root) == .discarded)
        #expect(!FileManager.default.fileExists(atPath: pane.worktreePath))
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]).isEmpty)
        // Only the pane's own place is ever forced away.
        var misplaced = pane; misplaced.worktreePath = place.repo.path
        guard case .failed = await PaneWorktreeGit.discard(misplaced, sessionId: "pane-4", root: place.root) else { Issue.record("a misplaced worktree was removed"); return }
        #expect(FileManager.default.fileExists(atPath: place.repo.path))
    }
}

/// A temp folder holding `repo` (the workspace) and `worktrees` (the root).
struct PaneGitPlace {
    let base: URL
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
    var root: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-pane-worktree-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    func repository() async throws {
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await git(["init", "-q", "-b", "main"])
        // The app's own commits (commit-tree, commit) read the repository's identity: CI has no global one.
        try await git(["config", "user.name", "Fixture"]); try await git(["config", "user.email", "fixture@example.invalid"])
        try Data("0\n".utf8).write(to: repo.appendingPathComponent("file-0.txt"))
        try await git(["add", "-A"]); try await git(["commit", "-q", "-m", "start"])
    }

    func makePane(_ id: String) async throws -> PaneWorktree {
        let maker = ChildWorktreeMaker(root: root, freeBytes: { _ in 50_000_000_000 })
        guard case .ready(let base) = await maker.check(workspace: repo.path) else { throw MightyError("not ready") }
        guard case .created(let made) = await maker.makePane(sessionId: id, base: base) else { throw MightyError("not made") }
        // The fixture's commits in the worktree use the fixture's identity.
        return PaneWorktree(made, checkout: base.repository, port: 4100, setupCommand: nil)
    }

    @discardableResult
    func git(_ arguments: [String], in directory: String? = nil) async throws -> String {
        let executable = try #require(DelegationGit.executable)
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"
        for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
        let folder = directory ?? repo.path
        let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder] + arguments, environment: environment, cwd: URL(fileURLWithPath: folder), timeout: 120)
        #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
        return DelegationGit.line(result.stdout)
    }
}
