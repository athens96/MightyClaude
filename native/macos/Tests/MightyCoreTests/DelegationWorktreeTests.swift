import Foundation
import Testing
@testable import MightyCore

/// Real git repositories in a temp folder; the worktree root is a temp folder too.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationWorktreeTests {
    private static let plenty: @Sendable (URL) -> Int64? = { _ in 50_000_000_000 }

    @Test func createsTheFlatBranchAndWorktreeWithIgnoredNotes() async throws {
        let place = try Place(); defer { place.remove() }
        try await place.repository(commits: 2)
        let head = try await place.git(["rev-parse", "HEAD"])
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: Self.plenty)

        let outcome = await maker.create(sessionId: "child-1", workspace: place.repo.path, task: "Fix the parser.\n")
        guard case .created(let child) = outcome else { Issue.record("not created: \(outcome)"); return }
        #expect(child.parentBranch == "main"); #expect(child.baseCommit == head)
        #expect(child.branch == "mighty/child-1")
        #expect(child.worktreePath == place.root.appendingPathComponent("child-1").path)
        #expect(child.workingFolder == child.worktreePath)
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/child-1"]) == head)
        #expect(try await place.git(["symbolic-ref", "HEAD"], in: child.worktreePath) == "refs/heads/mighty/child-1")
        let list = try await place.git(["worktree", "list", "--porcelain"])
        #expect(list.components(separatedBy: "worktree ").count == 3); #expect(list.contains("branch refs/heads/mighty/child-1"))

        #expect(child.taskFile == child.worktreePath + "/.mighty-delegation/TASK.md")
        #expect(try String(contentsOfFile: child.taskFile, encoding: .utf8) == "Fix the parser.\n")
        #expect(try Data(contentsOf: URL(fileURLWithPath: child.reportFile)).isEmpty)
        // Both notes are ignored: no change in the child, and a commit of everything leaves them out.
        let ignored = try await place.git(["check-ignore", ".mighty-delegation/TASK.md", ".mighty-delegation/REPORT.md"], in: child.worktreePath)
        #expect(ignored.split(separator: "\n") == [".mighty-delegation/TASK.md", ".mighty-delegation/REPORT.md"])
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"], in: child.worktreePath).isEmpty)
        try Data("REPORT: done\n".utf8).write(to: URL(fileURLWithPath: child.reportFile))
        try Data("work\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("work.txt"))
        try await place.git(["add", "-A"], in: child.worktreePath)
        try await place.git(["commit", "-q", "-m", "child work"], in: child.worktreePath)
        let committed = try await place.git(["ls-tree", "-r", "--name-only", "mighty/child-1"])
        #expect(committed.split(separator: "\n").sorted() == ["file-0.txt", "file-1.txt", "work.txt"])

        // The parent checkout is untouched.
        #expect(try await place.git(["symbolic-ref", "--short", "HEAD"]) == "main")
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
    }

    @Test func aSubfolderWorkspaceWorksInTheSameSubfolder() async throws {
        let place = try Place(); defer { place.remove() }
        try await place.repository(commits: 1, folder: "app/src")
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: Self.plenty)
        let outcome = await maker.create(sessionId: "child-sub", workspace: place.repo.appendingPathComponent("app/src").path, task: "t")
        guard case .created(let child) = outcome else { Issue.record("not created: \(outcome)"); return }
        #expect(child.workingFolder == child.worktreePath + "/app/src")
        #expect(FileManager.default.fileExists(atPath: child.workingFolder + "/file-0.txt"))
        #expect(FileManager.default.fileExists(atPath: child.worktreePath + "/.mighty-delegation/TASK.md"))
    }

    @Test func refusesOutsideGitWithoutCreatingAnything() async throws {
        let place = try Place(); defer { place.remove() }
        try FileManager.default.createDirectory(at: place.repo, withIntermediateDirectories: true)
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: Self.plenty)
        #expect(await maker.create(sessionId: "c", workspace: place.repo.path, task: "t") == .refused(.notGit))
        #expect(await maker.create(sessionId: "c", workspace: place.repo.appendingPathComponent("missing").path, task: "t") == .refused(.notGit))
        #expect(await maker.create(sessionId: "c", workspace: "relative/folder", task: "t") == .refused(.notGit))
        #expect(!FileManager.default.fileExists(atPath: place.root.path))
    }

    @Test func refusesAnUnbornBranchWithoutCreatingAnything() async throws {
        let place = try Place(); defer { place.remove() }
        try await place.repository(commits: 0)
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: Self.plenty)
        #expect(await maker.create(sessionId: "c", workspace: place.repo.path, task: "t") == .refused(.unbornBranch))
        try await place.expectNothingCreated()
    }

    @Test func refusesADetachedHeadWithoutCreatingAnything() async throws {
        let place = try Place(); defer { place.remove() }
        try await place.repository(commits: 2)
        try await place.git(["checkout", "-q", "--detach", "HEAD~1"])
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: Self.plenty)
        #expect(await maker.create(sessionId: "c", workspace: place.repo.path, task: "t") == .refused(.detachedHead))
        try await place.expectNothingCreated()
    }

    @Test func refusesUnderTenGigabytesFreeOnTheRootVolume() async throws {
        let place = try Place(); defer { place.remove() }
        try await place.repository(commits: 1)
        let asked = Asked()
        let low = ChildWorktreeMaker(root: place.root, freeBytes: { asked.add($0); return ChildWorktreeMaker.minimumFreeBytes - 1 })
        #expect(await low.create(sessionId: "c", workspace: place.repo.path, task: "t") == .refused(.lowDisk))
        #expect(asked.urls.map(\.path) == [place.root.path])
        let unknown = ChildWorktreeMaker(root: place.root, freeBytes: { _ in nil })
        #expect(await unknown.create(sessionId: "c", workspace: place.repo.path, task: "t") == .refused(.lowDisk))
        try await place.expectNothingCreated()

        let enough = ChildWorktreeMaker(root: place.root, freeBytes: { _ in ChildWorktreeMaker.minimumFreeBytes })
        guard case .created = await enough.create(sessionId: "c", workspace: place.repo.path, task: "t") else { Issue.record("10 GB free was refused"); return }
    }

    @Test func neverReplacesAnExistingBranchOrMakesAnUnsafeName() async throws {
        let place = try Place(); defer { place.remove() }
        try await place.repository(commits: 2)
        try await place.git(["branch", "mighty/taken", "HEAD~1"])
        let old = try await place.git(["rev-parse", "mighty/taken"])
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: Self.plenty)
        guard case .failed = await maker.create(sessionId: "taken", workspace: place.repo.path, task: "t") else { Issue.record("an existing branch was reused"); return }
        #expect(try await place.git(["rev-parse", "mighty/taken"]) == old)
        #expect(try await place.git(["worktree", "list", "--porcelain"]).components(separatedBy: "worktree ").count == 2)

        for unsafe in ["", "../escape", "a/b", ".hidden", "-flag", "x.lock", "a..b", "sp ace", "한글"] {
            #expect(!ChildWorktreeMaker.isSafeSessionId(unsafe), "\(unsafe)")
            guard case .failed = await maker.create(sessionId: unsafe, workspace: place.repo.path, task: "t") else { Issue.record("made \(unsafe)"); continue }
        }
        #expect(ChildWorktreeMaker.isSafeSessionId(UUID().uuidString))
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]) == "refs/heads/mighty/taken")
        #expect(try FileManager.default.contentsOfDirectory(atPath: place.root.path).isEmpty)
    }

    @Test func defaultRootAndFreeSpaceReader() throws {
        #expect(ChildWorktreeMaker.defaultRoot.path.hasSuffix("/.mightyclaude/worktrees"))
        #expect(ChildWorktreeMaker.defaultRoot.path.hasPrefix(FileManager.default.homeDirectoryForCurrentUser.path))
        let missing = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-missing-\(UUID().uuidString)/a/b")
        let free = try #require(ChildWorktreeMaker.availableBytes(near: missing))
        #expect(free > 0)
        #expect(!FileManager.default.fileExists(atPath: missing.path))
    }
}

private final class Asked: @unchecked Sendable {
    private let lock = NSLock(); private var seen: [URL] = []
    func add(_ url: URL) { lock.lock(); seen.append(url); lock.unlock() }
    var urls: [URL] { lock.lock(); defer { lock.unlock() }; return seen }
}

/// A temp folder holding `repo` (the workspace) and `worktrees` (the root).
private struct Place {
    let base: URL
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
    var root: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-worktree-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// `git init -b main` with `commits` commits, each adding one file in `folder`.
    func repository(commits: Int, folder: String = "") async throws {
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await git(["init", "-q", "-b", "main"])
        let files = folder.isEmpty ? repo : repo.appendingPathComponent(folder, isDirectory: true)
        try FileManager.default.createDirectory(at: files, withIntermediateDirectories: true)
        for index in 0 ..< commits {
            try Data("\(index)\n".utf8).write(to: files.appendingPathComponent("file-\(index).txt"))
            try await git(["add", "-A"]); try await git(["commit", "-q", "-m", "commit \(index)"])
        }
    }

    /// No root folder, no `mighty/*` branch and no second worktree.
    func expectNothingCreated() async throws {
        #expect(!FileManager.default.fileExists(atPath: root.path))
        #expect(try await git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]).isEmpty)
        #expect(try await git(["worktree", "list", "--porcelain"]).components(separatedBy: "worktree ").count == 2)
    }

    /// Fixture git, kept away from the user's and the system's settings.
    @discardableResult
    func git(_ arguments: [String], in directory: String? = nil) async throws -> String {
        let executable = try #require(DelegationGit.executable)
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"
        for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
        let folder = directory ?? repo.path
        // Generous: the first git launch on a cold CI runner can take seconds.
        let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder] + arguments, environment: environment, cwd: URL(fileURLWithPath: folder), timeout: 120)
        #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
        return DelegationGit.line(result.stdout)
    }
}
