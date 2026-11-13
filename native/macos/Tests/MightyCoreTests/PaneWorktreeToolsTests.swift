import Foundation
import Testing
@testable import MightyCore

/// The rules around a pane worktree (``PaneWorktreeTools``): its agent's note, discard guarded by children, kept records,
/// the copy list, the worktree list, the pull request page, and the real git of committing, merging, undoing and listing.
struct PaneWorktreeRulesTests {
    private let pane = PaneWorktree(worktreePath: "/wt/pane-1", branch: "mighty/pane-1", baseBranch: "main", baseCommit: String(repeating: "a", count: 40),
                                    checkout: "/repo", port: 4321, workingFolder: "/wt/pane-1/app")

    @Test func theFirstRequestThatIsNoCommandCarriesTheNoteOnce() {
        #expect(PaneWorktreeNote.applies(pane, input: "build it"))
        #expect(!PaneWorktreeNote.applies(pane, input: "  /compact"))
        var told = pane; told.noteGiven = true
        #expect(!PaneWorktreeNote.applies(told, input: "build it"))
        let text = PaneWorktreeNote.attach(pane, to: "build it")
        #expect(text.hasPrefix("build it\n\n[Mighty Code]"))
        for part in ["mighty/pane-1", "main", "PORT=4321", "/wt/pane-1/app", "commit your work", "Do not merge, push"] { #expect(text.contains(part)) }
        #expect(PaneWorktreeNote.conflictPrompt(pane).contains("git merge main"))
        // Every run gets the pane's folders and port; only those keys pass the runner.
        #expect(pane.environment(workspaceFolder: "/repo/app") == ["PORT": "4321", "MIGHTY_WORKSPACE_FOLDER": "/repo/app", "MIGHTY_WORKTREE": "/wt/pane-1/app"])
        #expect(Set(pane.environment(workspaceFolder: "/x").keys).isSubset(of: StartRunRequest.paneEnvironmentKeys))
    }

    @Test func childrenOnThePanesBranchBlockItsDiscard() {
        var child = RunSession(id: "child", workspaceId: "w", title: "c", kind: SessionKind.claude)
        child.parentSessionId = "pane-1"; child.workingFolder = "/wt/child"
        #expect(PaneWorktreePolicy.discardBlocker(sessionId: "pane-1", sessions: [child], children: []) != nil)
        #expect(PaneWorktreePolicy.discardBlocker(sessionId: "pane-2", sessions: [child], children: []) == nil)
        let open = DelegationChildRow(id: "c2", parentSessionId: "pane-1", state: .reported, task: nil, parentBranch: "mighty/pane-1", parentCheckout: "/wt/pane-1")
        var closed = open; closed.state = .closed
        #expect(PaneWorktreePolicy.discardBlocker(sessionId: "pane-1", sessions: [], children: [open]) != nil)
        #expect(PaneWorktreePolicy.discardBlocker(sessionId: "pane-1", sessions: [], children: [closed]) == nil)
    }

    @Test func keptRecordsAreCheckedDedupedAndBounded() throws {
        var other = pane; other.branch = "mighty/pane-2"; other.worktreePath = "/wt/pane-2"; other.workingFolder = nil
        let list = [KeptPaneWorktree(sessionId: "pane-1", title: "old", provider: "claude", worktree: pane),
                    KeptPaneWorktree(sessionId: "pane-1", title: "new", provider: "claude", worktree: pane),
                    KeptPaneWorktree(sessionId: "pane-3", title: "wrong branch", provider: "claude", worktree: other)]
        let kept = try #require(KeptPaneWorktree.normalized(list, restoring: true))
        #expect(kept.map(\.title) == ["new"])
        let many = (0..<60).map { index -> KeptPaneWorktree in
            var worktree = pane; worktree.branch = "mighty/p\(index)"; worktree.worktreePath = "/wt/p\(index)"; worktree.workingFolder = nil
            return KeptPaneWorktree(sessionId: "p\(index)", title: "\(index)", provider: "claude", worktree: worktree)
        }
        let bounded = try #require(KeptPaneWorktree.normalized(many, restoring: true))
        #expect(bounded.count == 50 && bounded.first?.sessionId == "p10" && bounded.last?.sessionId == "p59")
        // A working folder outside the worktree is dropped; an undo needs a merge on record.
        var odd = pane; odd.workingFolder = "/elsewhere"; odd.preMergeCommit = String(repeating: "b", count: 40)
        let normalized = try #require(odd.normalized(sessionId: "pane-1", restoring: true))
        #expect(normalized.workingFolder == nil && normalized.preMergeCommit == nil && normalized.runFolder == "/wt/pane-1")
    }

    @Test func theCopyListTakesOnlyPlainPathsInsideTheWorkspace() {
        #expect(PaneWorktreeCopy.normalizedPaths([" .env ", "./.env.local", "config/", "../secret", "/etc/hosts", "~/x", ".git/config", "a/./b", ".env"]) == [".env", ".env.local", "config"])
        #expect(PaneWorktreeCopy.normalizedPaths(["", "  "]) == nil)
        #expect(PaneWorktreeCopy.entries(".env, .env.local\nconfig") == [".env", " .env.local", "config"])
    }

    @Test func theCopyNeverFollowsALinkOrOverwrites() throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-copy-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let workspace = base.appendingPathComponent("workspace", isDirectory: true), worktree = base.appendingPathComponent("worktree", isDirectory: true)
        let outside = base.appendingPathComponent("outside", isDirectory: true)
        for folder in [workspace, worktree, outside, workspace.appendingPathComponent("config", isDirectory: true)] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        }
        try Data("A=1\n".utf8).write(to: workspace.appendingPathComponent(".env"))
        try Data("k\n".utf8).write(to: workspace.appendingPathComponent("config/local.json"))
        try FileManager.default.createSymbolicLink(at: workspace.appendingPathComponent("config/link"), withDestinationURL: outside)
        try Data("secret\n".utf8).write(to: outside.appendingPathComponent("s.txt"))
        try FileManager.default.createSymbolicLink(at: workspace.appendingPathComponent("linked.env"), withDestinationURL: outside.appendingPathComponent("s.txt"))
        try FileManager.default.createSymbolicLink(at: workspace.appendingPathComponent("via"), withDestinationURL: outside)
        try Data("tracked\n".utf8).write(to: workspace.appendingPathComponent("README"))
        try Data("mine\n".utf8).write(to: worktree.appendingPathComponent("README"))

        let result = PaneWorktreeCopy.copy([".env", "config", "linked.env", "via/s.txt", "README", "nothing"], from: workspace, to: worktree)
        #expect(result.copied == [".env", "config"])
        #expect(result.skipped == ["linked.env": "link", "via/s.txt": "outside", "README": "exists", "nothing": "missing"])
        #expect(try String(contentsOf: worktree.appendingPathComponent(".env"), encoding: .utf8) == "A=1\n")
        #expect(FileManager.default.fileExists(atPath: worktree.appendingPathComponent("config/local.json").path))
        // The link inside the copied folder is left out, and the worktree's own file is never overwritten.
        #expect(!FileManager.default.fileExists(atPath: worktree.appendingPathComponent("config/link").path))
        #expect(try String(contentsOf: worktree.appendingPathComponent("README"), encoding: .utf8) == "mine\n")
    }

    @Test func theWorktreeListReadsGitAndOrdersOpenKeptAndFound() {
        let root = URL(fileURLWithPath: "/root/worktrees")
        let porcelain = """
        worktree /repo
        HEAD \(String(repeating: "1", count: 40))
        branch refs/heads/main

        worktree /root/worktrees/pane-1
        HEAD \(String(repeating: "2", count: 40))
        branch refs/heads/mighty/pane-1

        worktree /root/worktrees/child-1
        HEAD \(String(repeating: "3", count: 40))
        branch refs/heads/mighty/child-1

        worktree /root/worktrees/odd
        HEAD \(String(repeating: "4", count: 40))
        branch refs/heads/feature

        worktree /root/worktrees/pane-9
        HEAD \(String(repeating: "5", count: 40))
        branch refs/heads/mighty/pane-9
        """
        let disk = PaneWorktreeInventory.parse(porcelain, root: root)
        #expect(disk.map(\.sessionId) == ["pane-1", "child-1", "pane-9"])
        var open = RunSession(id: "pane-1", workspaceId: "w", title: "Open", kind: SessionKind.claude)
        open.paneWorktree = pane
        var keptPane = pane; keptPane.branch = "mighty/pane-5"; keptPane.worktreePath = "/root/worktrees/pane-5"
        let kept = [KeptPaneWorktree(sessionId: "pane-5", title: "Kept", provider: "codex", worktree: keptPane)]
        let rows = PaneWorktreeInventory.rows(open: [open], kept: kept, onDisk: disk, excluded: ["child-1"], checkout: "/repo", baseBranch: "main", port: { _ in 4400 })
        #expect(rows.map(\.sessionId) == ["pane-1", "pane-5", "pane-9"])
        #expect(rows.map(\.origin) == [.open, .kept, .found])
        #expect(rows.last?.worktree.baseBranch == "main" && rows.last?.worktree.port == 4400)
        // Without a base branch (the checkout is detached) nothing found on disk is offered.
        #expect(PaneWorktreeInventory.rows(open: [], kept: [], onDisk: disk, excluded: [], checkout: "/repo", baseBranch: nil, port: { _ in 4400 }).isEmpty)
    }

    @Test func aPullRequestPageIsMadeOnlyForGitHub() {
        for remote in ["https://github.com/athens/app.git", "git@github.com:athens/app.git", "ssh://git@github.com/athens/app", "https://github.com/athens/app/"] {
            #expect(PaneWorktreePR.gitHubRepository(remote) == "athens/app", "\(remote)")
        }
        #expect(PaneWorktreePR.gitHubRepository("https://gitlab.com/athens/app.git") == nil)
        #expect(PaneWorktreePR.gitHubRepository("https://github.com/athens") == nil)
        #expect(PaneWorktreePR.compareURL(repository: "athens/app", base: "main", branch: "mighty/pane-1")?.absoluteString
                == "https://github.com/athens/app/compare/main...mighty/pane-1?expand=1")
    }
}

/// Real git: committing new files for a merge, the merge recording where the base was, undoing it, the status counts,
/// the changed files, listing the worktrees and pushing the branch.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct PaneWorktreeToolsGitTests {
    @Test func newFilesBlockAMergeUntilCommittedAndSecretsAreLeftOut() async throws {
        let place = try PaneGitPlace(); defer { place.remove() }
        try await place.repository()
        let pane = try await place.makePane("pane-a")
        let folder = URL(fileURLWithPath: pane.worktreePath)
        try Data("new\n".utf8).write(to: folder.appendingPathComponent("new.txt"))
        // A new file alone is a change a merge would leave behind.
        #expect(await PaneWorktreeGit.merge(pane) == .refused(.uncommittedChanges))
        #expect(await PaneWorktreeGit.status(pane) == PaneWorktreeStatus(ahead: 0, behind: 0, changedFiles: 1))
        try Data("TOKEN=1\n".utf8).write(to: folder.appendingPathComponent(".env.production"))
        #expect(await PaneWorktreeGit.commitAll(pane) == .committed(leftOut: [".env.production"]))
        // The secret-looking file stays uncommitted, so the merge still refuses.
        #expect(await PaneWorktreeGit.merge(pane) == .refused(.uncommittedChanges))
        try FileManager.default.removeItem(at: folder.appendingPathComponent(".env.production"))
        #expect(await PaneWorktreeGit.status(pane) == PaneWorktreeStatus(ahead: 1, behind: 0, changedFiles: 0))
        #expect(await PaneWorktreeGit.changedFiles(pane) == [PaneWorktreeChangedFile(path: "new.txt", status: "A")])
        let diff = try #require(await PaneWorktreeGit.fileChanges(pane, path: "new.txt"))
        #expect(diff.added == 1 && diff.created == true)
        guard case .merged(let commit, let preMerge) = await PaneWorktreeGit.merge(pane) else { Issue.record("not merged"); return }
        #expect(preMerge == pane.baseCommit)
        #expect(FileManager.default.fileExists(atPath: place.repo.appendingPathComponent("new.txt").path))

        // Undo: the base goes back, untracked files in the folder stay, the pane's commits stay for another merge.
        var merged = pane; merged.mergedCommit = commit; merged.preMergeCommit = preMerge
        try Data("scratch\n".utf8).write(to: place.repo.appendingPathComponent("scratch.txt"))
        #expect(await PaneWorktreeGit.undoMerge(merged) == .undone)
        #expect(try await place.git(["rev-parse", "HEAD"]) == preMerge)
        #expect(!FileManager.default.fileExists(atPath: place.repo.appendingPathComponent("new.txt").path))
        #expect(FileManager.default.fileExists(atPath: place.repo.appendingPathComponent("scratch.txt").path))
        #expect(await PaneWorktreeGit.status(pane)?.ahead == 1)
        // Undone already: the base is no longer at the merged commit.
        #expect(await PaneWorktreeGit.undoMerge(merged) == .refused(.baseMoved))
        #expect(await PaneWorktreeGit.undoMerge(pane) == .refused(.notMerged))
    }

    @Test func anUndoRefusesWhenTheBaseMovedOnOrTheFolderIsDirty() async throws {
        let place = try PaneGitPlace(); defer { place.remove() }
        try await place.repository()
        let pane = try await place.makePane("pane-b")
        try Data("b\n".utf8).write(to: URL(fileURLWithPath: pane.worktreePath).appendingPathComponent("b.txt"))
        _ = await PaneWorktreeGit.commitAll(pane)
        guard case .merged(let commit, let preMerge) = await PaneWorktreeGit.merge(pane) else { Issue.record("not merged"); return }
        var merged = pane; merged.mergedCommit = commit; merged.preMergeCommit = preMerge
        try Data("dirty\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        #expect(await PaneWorktreeGit.undoMerge(merged) == .refused(.trackedChanges))
        try await place.git(["checkout", "-q", "--", "file-0.txt"])
        try Data("later\n".utf8).write(to: place.repo.appendingPathComponent("later.txt"))
        try await place.git(["add", "-A"]); try await place.git(["commit", "-q", "-m", "later"])
        #expect(await PaneWorktreeGit.undoMerge(merged) == .refused(.baseMoved))
        #expect(await PaneWorktreeGit.status(pane)?.behind == 1)
    }

    @Test func theWorktreeListFindsPaneWorktreesAndThePushNeverForces() async throws {
        let place = try PaneGitPlace(); defer { place.remove() }
        try await place.repository()
        let pane = try await place.makePane("pane-c")
        let found = try #require(await PaneWorktreeGit.worktrees(checkout: pane.checkout, root: place.root))
        #expect(found.map(\.sessionId) == ["pane-c"] && found.first?.branch == "mighty/pane-c")

        // origin: a bare repository next to it.
        let bare = place.base.appendingPathComponent("origin.git").path
        try await place.git(["init", "-q", "--bare", bare], in: place.base.path)
        try await place.git(["remote", "add", "origin", bare])
        #expect(await PaneWorktreePR.origin(pane) == bare)
        try Data("c\n".utf8).write(to: URL(fileURLWithPath: pane.worktreePath).appendingPathComponent("c.txt"))
        _ = await PaneWorktreeGit.commitAll(pane)
        #expect(await PaneWorktreePR.push(pane) == nil)
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/pane-c"], in: bare) == (try await place.git(["rev-parse", "HEAD"], in: pane.worktreePath)))
        // The remote branch moved on elsewhere (a commit the pane does not have): a push is refused, never forced.
        let tree = try await place.git(["rev-parse", pane.baseCommit + "^{tree}"], in: bare)
        let elsewhere = try await place.git(["commit-tree", tree, "-p", pane.baseCommit, "-m", "elsewhere"], in: bare)
        try await place.git(["update-ref", "refs/heads/mighty/pane-c", elsewhere], in: bare)
        try Data("c2\n".utf8).write(to: URL(fileURLWithPath: pane.worktreePath).appendingPathComponent("c2.txt"))
        _ = await PaneWorktreeGit.commitAll(pane)
        #expect(await PaneWorktreePR.push(pane) != nil)
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/pane-c"], in: bare) == elsewhere)
    }
}
