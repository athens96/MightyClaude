import Foundation

/// A delegated child's own checkout (macOS only): the flat branch
/// `mighty/<session id>` at its parent's head, in a git worktree under the
/// worktree root, outside the workspace checkout. TASK.md and REPORT.md sit in
/// ``notesFolder`` inside it. That folder ignores itself, so neither file is
/// ever committed or counts as a change.
public struct ChildWorktree: Sendable, Equatable {
    public static let notesFolder = ".mighty-delegation"

    public var sessionId: String
    public var worktreePath: String
    /// Where the child works: the worktree, or the same subfolder of it when
    /// the workspace is a subfolder of its repository.
    public var workingFolder: String
    public var branch: String
    /// The parent checkout's branch (short name) and its head when the child was made.
    public var parentBranch: String
    public var baseCommit: String

    public var taskFile: String { Self.taskFile(worktreePath: worktreePath) }
    public var reportFile: String { Self.reportFile(worktreePath: worktreePath) }

    public static func taskFile(worktreePath: String) -> String { notesPath(worktreePath, "TASK.md") }
    public static func reportFile(worktreePath: String) -> String { notesPath(worktreePath, "REPORT.md") }
    private static func notesPath(_ worktreePath: String, _ name: String) -> String {
        URL(fileURLWithPath: worktreePath).appendingPathComponent(notesFolder).appendingPathComponent(name).path
    }
}

/// The parent side of a child about to be made, read from the workspace.
public struct ChildWorktreeBase: Sendable, Equatable {
    /// The top folder of the workspace's checkout.
    public var repository: String
    /// The workspace's folder inside it: "" or "sub/folder/".
    public var prefix: String
    public var parentBranch: String
    public var baseCommit: String
}

public enum ChildWorktreeCheck: Sendable, Equatable {
    case ready(ChildWorktreeBase)
    /// Refused before anything was made.
    case refused(DelegationReasonCode)
}

public enum ChildWorktreeOutcome: Sendable, Equatable {
    case created(ChildWorktree)
    /// Refused before anything was made.
    case refused(DelegationReasonCode)
    /// Git or the file system failed partway. Whatever was left (a branch, a
    /// folder) stays until a human discards the child; the app never removes
    /// it on its own.
    case failed(String)
}

/// Makes child worktrees under one root: `~/.mightyclaude/worktrees` in the
/// app, a temp folder in tests and smoke runs. A workspace that is not in a
/// git checkout, whose branch has no commit yet, whose HEAD is detached, or
/// whose root volume has under 10 GB free is refused before anything is made.
public struct ChildWorktreeMaker: Sendable {
    public static let minimumFreeBytes: Int64 = 10_000_000_000
    public static var defaultRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".mightyclaude", isDirectory: true).appendingPathComponent("worktrees", isDirectory: true)
    }

    public let root: URL
    private let freeBytes: @Sendable (URL) -> Int64?

    /// `freeBytes` reads the free space of the volume holding a path; nil
    /// (unknown) counts as too little.
    public init(root: URL = ChildWorktreeMaker.defaultRoot, freeBytes: @escaping @Sendable (URL) -> Int64? = { ChildWorktreeMaker.availableBytes(near: $0) }) {
        self.root = root.standardizedFileURL; self.freeBytes = freeBytes
    }

    public func worktreePath(for sessionId: String) -> String { root.appendingPathComponent(sessionId, isDirectory: true).path }

    /// Checks the workspace and the free space, and makes nothing. Quick, so a
    /// delegate call can answer with its refusal.
    public func check(workspace: String) async -> ChildWorktreeCheck {
        guard workspace.hasPrefix("/"), !workspace.contains("\0"),
              let place = await DelegationGit.run(["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--show-prefix"], in: workspace), place.exitCode == 0 else { return .refused(.notGit) }
        let lines = String(decoding: place.stdout, as: UTF8.self).split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        guard lines.count >= 2, lines[0] == "true", lines[1].hasPrefix("/") else { return .refused(.notGit) }
        guard let head = await DelegationGit.run(["symbolic-ref", "-q", "HEAD"], in: workspace) else { return .refused(.notGit) }
        let ref = DelegationGit.line(head.stdout)
        guard head.exitCode == 0, ref.hasPrefix("refs/heads/"), ref.count > 11 else { return .refused(head.exitCode == 0 || head.exitCode == 1 ? .detachedHead : .notGit) }
        guard let commit = await DelegationGit.run(["rev-parse", "-q", "--verify", "HEAD^{commit}"], in: workspace) else { return .refused(.notGit) }
        let sha = DelegationGit.line(commit.stdout)
        guard commit.exitCode == 0, DelegationGit.isObjectName(sha) else { return .refused(.unbornBranch) }
        guard let free = freeBytes(root), free >= Self.minimumFreeBytes else { return .refused(.lowDisk) }
        return .ready(ChildWorktreeBase(repository: lines[1], prefix: lines.count > 2 ? lines[2] : "", parentBranch: String(ref.dropFirst(11)), baseCommit: sha))
    }

    /// Makes the branch `mighty/<session id>` at the recorded base commit and
    /// its worktree, then TASK.md with `task` and an empty REPORT.md. Never
    /// replaces an existing branch, folder or file.
    public func make(sessionId: String, base: ChildWorktreeBase, task: String, timeout: TimeInterval = 300) async -> ChildWorktreeOutcome {
        guard Self.isSafeSessionId(sessionId) else { return .failed("The session id cannot name a branch and a folder.") }
        let worktree = worktreePath(for: sessionId), branch = ChildRecord.branchName(for: sessionId)
        do { try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true) } catch { return .failed(error.localizedDescription) }
        guard let added = await DelegationGit.run(["worktree", "add", "-b", branch, worktree, base.baseCommit], in: base.repository, timeout: timeout) else { return .failed("Git did not finish.") }
        guard added.exitCode == 0 else { return .failed(DelegationGit.message(added)) }
        let notes = URL(fileURLWithPath: worktree).appendingPathComponent(ChildWorktree.notesFolder, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: notes, withIntermediateDirectories: false)
            try Data("*\n".utf8).write(to: notes.appendingPathComponent(".gitignore"), options: .withoutOverwriting)
            try Data(task.utf8).write(to: URL(fileURLWithPath: ChildWorktree.taskFile(worktreePath: worktree)), options: .withoutOverwriting)
            try Data().write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: worktree)), options: .withoutOverwriting)
        } catch { return .failed(error.localizedDescription) }
        let folder = base.prefix.isEmpty ? worktree : URL(fileURLWithPath: worktree).appendingPathComponent(base.prefix, isDirectory: true).path
        return .created(ChildWorktree(sessionId: sessionId, worktreePath: worktree, workingFolder: folder, branch: branch, parentBranch: base.parentBranch, baseCommit: base.baseCommit))
    }

    /// ``check(workspace:)`` and then ``make(sessionId:base:task:timeout:)``.
    public func create(sessionId: String, workspace: String, task: String) async -> ChildWorktreeOutcome {
        switch await check(workspace: workspace) {
        case .refused(let reason): return .refused(reason)
        case .ready(let base): return await make(sessionId: sessionId, base: base, task: task)
        }
    }

    /// Letters, digits, `-`, `_` and inner dots, at most 128: one folder name
    /// and a valid branch name part.
    public static func isSafeSessionId(_ id: String) -> Bool {
        let bytes = Array(id.utf8), dot = UInt8(ascii: "."), dash = UInt8(ascii: "-")
        let letters = UInt8(ascii: "a") ... UInt8(ascii: "z"), digits = UInt8(ascii: "0") ... UInt8(ascii: "9")
        guard let first = bytes.first, let last = bytes.last, bytes.count <= 128, first != dot, first != dash, last != dot,
              !id.contains(".."), !id.hasSuffix(".lock") else { return false }
        return bytes.allSatisfy { letters.contains($0 | 0x20) || digits.contains($0) || $0 == dash || $0 == dot || $0 == UInt8(ascii: "_") }
    }

    /// Free bytes for important use on the volume holding `url`, or its
    /// nearest existing folder.
    public static func availableBytes(near url: URL) -> Int64? {
        var place = url.standardizedFileURL
        while !FileManager.default.fileExists(atPath: place.path) {
            let parent = place.deletingLastPathComponent()
            guard parent.path != place.path else { return nil }
            place = parent
        }
        guard let values = try? place.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey]) else { return nil }
        if let important = values.volumeAvailableCapacityForImportantUsage, important > 0 { return important }
        return values.volumeAvailableCapacity.map { Int64($0) }
    }
}

/// Git for delegation: the user's own settings, but no hooks, fsmonitor, auto
/// gc, prompts or inherited GIT_* variables.
enum DelegationGit {
    static let executable: URL? = ["/Library/Developer/CommandLineTools/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git"]
        .first(where: { FileManager.default.isExecutableFile(atPath: $0) }).map { URL(fileURLWithPath: $0) }

    /// nil when git is missing, could not start or did not finish in time.
    static func run(_ arguments: [String], in directory: String, timeout: TimeInterval = 10) async -> ProcessResult? {
        guard let executable else { return nil }
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_OPTIONAL_LOCKS"] = "0"; environment["GIT_TERMINAL_PROMPT"] = "0"; environment["LC_ALL"] = "C"
        let settings = ["-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c", "gc.auto=0", "-C", directory]
        return try? await ProcessCapture.run(executable: executable, arguments: settings + arguments, environment: environment,
                                             cwd: URL(fileURLWithPath: directory), timeout: timeout, maximumBytes: 1_048_576)
    }

    static func line(_ data: Data) -> String { String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) }

    static func isObjectName(_ value: String) -> Bool { (value.count == 40 || value.count == 64) && value.allSatisfy(\.isHexDigit) }

    static func message(_ result: ProcessResult) -> String {
        let text = line(result.stderr)
        return text.isEmpty ? "Git exited with \(result.exitCode)." : String(text.prefix(500))
    }
}
