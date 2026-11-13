import Foundation

// The rest of a pane worktree's life (macOS; the rules are the ones Windows and the phone follow, docs/devtool-features.md
// §12): what its agent is told, how far it is from its base, committing and undoing, the files copied into it, the
// worktrees a workspace still has on disk, and the pull request. Every git step goes through ``DelegationGit``.

/// What a pane worktree's agent is told once, with its first request: where it works, its PORT, and that it commits its
/// work and leaves merging to the person.
public enum PaneWorktreeNote {
    public static func text(_ pane: PaneWorktree) -> String {
        "[Mighty Code] This pane works in its own git worktree (\(pane.runFolder)) on the branch \(pane.branch), made from \(pane.baseBranch) so that the other panes of this workspace keep working undisturbed. Dev servers you start here use PORT=\(pane.port) (already set in the environment); start them on that port. When you are done, commit your work on \(pane.branch), leaving no uncommitted changes and no new files outside a commit. The person who sent this merges your branch into \(pane.baseBranch) from the app. Do not merge, push, rebase or switch branches."
    }

    /// Whether a request of the pane carries the note: the first one that is no slash command (a command must stay first).
    public static func applies(_ pane: PaneWorktree, input: String) -> Bool {
        pane.noteGiven != true && !input.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("/")
    }

    /// `input` with the note under it.
    public static func attach(_ pane: PaneWorktree, to input: String) -> String { input + "\n\n" + text(pane) }

    /// The request that asks the pane's agent to bring its base branch in and resolve the conflict (the merge refused it).
    public static func conflictPrompt(_ pane: PaneWorktree) -> String {
        "[Mighty Code] Merging your branch \(pane.branch) into \(pane.baseBranch) from the app was refused: the two conflict. In this worktree, bring \(pane.baseBranch) into your branch with `git merge \(pane.baseBranch)`, resolve every conflict so that the changes on both sides are kept (ask when a choice is unclear), run the checks that apply, and commit the merge on \(pane.branch), leaving no uncommitted changes. The person merges your branch from the app again afterwards. Do not push, rebase or switch branches."
    }
}

/// How far a pane worktree is from its base: commits it has that the base lacks, commits the base moved ahead by since,
/// and files changed in the worktree (tracked or new, ignored ones aside).
public struct PaneWorktreeStatus: Sendable, Equatable {
    public var ahead: Int
    public var behind: Int
    public var changedFiles: Int
    public init(ahead: Int, behind: Int, changedFiles: Int) { self.ahead = ahead; self.behind = behind; self.changedFiles = changedFiles }
}

/// One file a pane worktree changed against the point it shares with its base.
public struct PaneWorktreeChangedFile: Sendable, Equatable, Identifiable, Hashable {
    /// Relative to the worktree's top folder.
    public var path: String
    /// `A`dded, `M`odified, `D`eleted, `R`enamed, `?` new and not committed.
    public var status: String
    public var id: String { path }
    public init(path: String, status: String) { self.path = path; self.status = status }
}

/// How an undo of a pane's merge went.
public enum PaneWorktreeUndoOutcome: Sendable, Equatable {
    case undone
    case refused(Refusal)
    case failed(String)

    public enum Refusal: String, Sendable, Equatable {
        /// No merge from the pane is on record.
        case notMerged
        /// The workspace folder no longer has the base branch checked out.
        case branchNotCheckedOut
        /// The base branch moved on since the merge: undoing would throw that work away.
        case baseMoved
        /// The workspace folder has changes to tracked files.
        case trackedChanges
    }
}

/// How committing a pane worktree's changes went ("커밋하고 병합").
public enum PaneWorktreeCommitOutcome: Sendable, Equatable {
    /// Committed; `leftOut` are new files not taken (secret-looking or over 5 MB): they still block the merge.
    case committed(leftOut: [String])
    case nothing(leftOut: [String])
    /// An unresolved conflict is in the worktree.
    case unmerged
    case failed(String)
}

extension PaneWorktreeGit {
    /// Commits ahead of the base, commits the base moved ahead, and changed files; nil when git could not tell.
    public static func status(_ pane: PaneWorktree) async -> PaneWorktreeStatus? {
        guard ChildMerge.isFolder(pane.worktreePath) else { return nil }
        let base = "refs/heads/" + pane.baseBranch, branch = "refs/heads/" + pane.branch
        guard let counts = await DelegationGit.run(["rev-list", "--left-right", "--count", base + "..." + branch], in: pane.checkout), counts.exitCode == 0,
              let changed = await DelegationGit.run(["status", "--porcelain", "--untracked-files=normal"], in: pane.worktreePath), changed.exitCode == 0 else { return nil }
        let numbers = DelegationGit.line(counts.stdout).split(whereSeparator: { $0 == "\t" || $0 == " " }).compactMap { Int($0) }
        guard numbers.count == 2 else { return nil }
        let files = String(decoding: changed.stdout, as: UTF8.self).split(separator: "\n").count
        return PaneWorktreeStatus(ahead: numbers[1], behind: numbers[0], changedFiles: files)
    }

    /// The files the pane changed against the commit it shares with its base: its commits and what is not committed yet.
    public static func changedFiles(_ pane: PaneWorktree) async -> [PaneWorktreeChangedFile]? {
        guard let since = await mergeBase(pane),
              let diff = await DelegationGit.run(["diff", "--name-status", "-z", "--no-renames", since], in: pane.worktreePath, timeout: 30), diff.exitCode == 0,
              let others = await DelegationGit.run(["ls-files", "-z", "--others", "--exclude-standard"], in: pane.worktreePath, timeout: 30), others.exitCode == 0 else { return nil }
        var files: [PaneWorktreeChangedFile] = []
        let fields = diff.stdout.split(separator: 0, omittingEmptySubsequences: true).map { String(decoding: $0, as: UTF8.self) }
        var index = 0
        while index + 1 < fields.count {
            files.append(PaneWorktreeChangedFile(path: fields[index + 1], status: String(fields[index].prefix(1))))
            index += 2
        }
        for path in others.stdout.split(separator: 0).map({ String(decoding: $0, as: UTF8.self) }) where !files.contains(where: { $0.path == path }) {
            files.append(PaneWorktreeChangedFile(path: path, status: "?"))
        }
        return files.sorted { $0.path < $1.path }
    }

    /// The diff of `path` against the commit the pane shares with its base, as the change review draws it.
    public static func fileChanges(_ pane: PaneWorktree, path: String) async -> FileChangeSet? {
        guard let since = await mergeBase(pane) else { return nil }
        let root = URL(fileURLWithPath: pane.worktreePath, isDirectory: true)
        let current = FileChanges.current(path, root: root) ?? ""
        let shown = await DelegationGit.run(["show", since + ":" + path], in: pane.worktreePath, timeout: 30)
        let created = shown?.exitCode != 0
        let old = created ? "" : (FilePreviewClassifier.decodeText(shown?.stdout ?? Data(), sample: false)?.text ?? "")
        return FileDiff.compute(old: old, new: current).map { FileChangeSet(base: "head", diff: $0, headSha: String(since.prefix(8)), created: created) }
    }

    static func mergeBase(_ pane: PaneWorktree) async -> String? {
        guard let found = await DelegationGit.run(["merge-base", "refs/heads/" + pane.baseBranch, "HEAD"], in: pane.worktreePath), found.exitCode == 0 else { return nil }
        let commit = DelegationGit.line(found.stdout)
        return DelegationGit.isObjectName(commit) ? commit : nil
    }

    /// Commits everything the worktree changed, new files too, except new files that look like secrets or are over 5 MB
    /// (``ChildAutoCommit/looksSecret(_:)``): one commit on the pane's branch, no hooks, no signing prompt.
    public static func commitAll(_ pane: PaneWorktree) async -> PaneWorktreeCommitOutcome {
        let folder = pane.worktreePath
        guard ChildMerge.isFolder(folder) else { return .failed(L("paneWorktree.refused.worktreeMissing")) }
        guard let unmerged = await DelegationGit.run(["ls-files", "-u"], in: folder, timeout: 60), unmerged.exitCode == 0,
              let others = await DelegationGit.run(["ls-files", "-z", "--others", "--exclude-standard"], in: folder, timeout: 60), others.exitCode == 0 else {
            return .failed(L("delegation.card.error.gitUnfinished"))
        }
        if !unmerged.stdout.isEmpty { return .unmerged }
        let new = others.stdout.split(separator: 0).map { String(decoding: $0, as: UTF8.self) }
        let leftOut = new.filter { ChildAutoCommit.looksSecret($0) || ChildAutoCommit.size(of: URL(fileURLWithPath: folder).appendingPathComponent($0).path) > ChildAutoCommit.maximumNewFileBytes }
        let taken = new.filter { !leftOut.contains($0) }
        func write(_ arguments: [String], input: Data? = nil) async -> String? {
            guard let result = await DelegationGit.run(arguments, in: folder, timeout: 300, input: input) else { return L("delegation.card.error.gitUnfinished") }
            return result.exitCode == 0 ? nil : DelegationGit.message(result)
        }
        if let why = await write(["add", "-u"]) { return .failed(why) }
        if !taken.isEmpty, let why = await write(["--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul"], input: Data(taken.joined(separator: "\0").utf8)) {
            return .failed(why)
        }
        guard let staged = await DelegationGit.run(["diff", "--cached", "--quiet"], in: folder, timeout: 60), staged.exitCode <= 1 else { return .failed(L("delegation.card.error.gitUnfinished")) }
        if staged.exitCode == 0 { return .nothing(leftOut: leftOut) }
        if let why = await write(["-c", "commit.gpgsign=false", "commit", "-q", "--no-verify", "-m", "mighty: commit \(pane.branch) for its merge"]) { return .failed(why) }
        return .committed(leftOut: leftOut)
    }

    /// Puts the base branch back where it was before the pane's last merge. The first rule that applies answers, and every
    /// refusal comes before any git write: a merge on record, the base branch checked out in the workspace folder with its
    /// head still at the merged commit, no changes to tracked files there. Then, as a delegated child's card undo does, the
    /// index and files go back by `git read-tree -m -u` (never `reset --hard`: untracked files stay) and the branch moves
    /// back by compare-and-swap. The pane's branch keeps its commits, so it can be merged again.
    public static func undoMerge(_ pane: PaneWorktree) async -> PaneWorktreeUndoOutcome {
        guard let merged = pane.mergedCommit?.lowercased(), let preMerge = pane.preMergeCommit?.lowercased(),
              DelegationGit.isObjectName(merged), DelegationGit.isObjectName(preMerge) else { return .refused(.notMerged) }
        guard DelegationGit.executable != nil else { return .failed(L("delegation.card.error.gitMissing")) }
        guard let head = await ChildMerge.checkedOutHead(of: pane.baseBranch, in: pane.checkout) else { return .refused(.branchNotCheckedOut) }
        guard head == merged else { return .refused(.baseMoved) }
        switch await ChildMerge.hasTrackedChanges(pane.checkout) {
        case nil: return .failed(L("delegation.card.error.parentChangesUnread"))
        case true?: return .refused(.trackedChanges)
        case false?: break
        }
        let reason = "mighty: undo the merge of \(pane.branch)"
        _ = await DelegationGit.run(["update-index", "-q", "--refresh"], in: pane.checkout, timeout: 60)
        guard let files = await DelegationGit.run(["read-tree", "-m", "-u", merged, preMerge], in: pane.checkout, timeout: 60), files.exitCode == 0 else {
            return .failed(L("delegation.card.error.filesNotRestored"))
        }
        guard let moved = await DelegationGit.run(["update-ref", "-m", reason, "refs/heads/" + pane.baseBranch, preMerge, merged], in: pane.checkout), moved.exitCode == 0 else {
            _ = await DelegationGit.run(["read-tree", "-m", "-u", preMerge, merged], in: pane.checkout, timeout: 60)
            return .failed(L("delegation.card.error.parentMovedDuringUndo"))
        }
        return .undone
    }

    /// The pane worktrees `checkout` has under `root`: every `git worktree` whose folder is `<root>/<id>` and whose branch
    /// is `mighty/<id>`. Delegated children live there too; the caller leaves their ids out.
    public static func worktrees(checkout: String, root: URL) async -> [PaneWorktreeOnDisk]? {
        guard let listed = await DelegationGit.run(["worktree", "list", "--porcelain"], in: checkout, timeout: 30), listed.exitCode == 0 else { return nil }
        return PaneWorktreeInventory.parse(String(decoding: listed.stdout, as: UTF8.self), root: root)
    }
}

/// A pane worktree found on disk.
public struct PaneWorktreeOnDisk: Sendable, Equatable {
    public var sessionId: String
    public var worktreePath: String
    public var branch: String
    public var head: String
}

/// The rows of a workspace's worktree list: the open panes' worktrees, the ones kept after their pane closed, and any
/// other pane worktree git still has under the root (made by an earlier run whose record is gone).
public enum PaneWorktreeInventory {
    public enum Origin: Sendable, Equatable {
        /// A pane that is open now has it.
        case open
        /// Kept when its pane closed with work in it (``KeptPaneWorktree``).
        case kept
        /// On disk with no record: its base is the checkout's branch now.
        case found
    }

    public struct Row: Sendable, Equatable, Identifiable {
        public var sessionId: String
        public var title: String
        public var provider: String
        public var worktree: PaneWorktree
        public var origin: Origin
        public var id: String { sessionId }
    }

    /// `git worktree list --porcelain` read for `<root>/<id>` folders on `mighty/<id>` branches.
    public static func parse(_ text: String, root: URL) -> [PaneWorktreeOnDisk] {
        let rootPath = root.standardizedFileURL.path
        var found: [PaneWorktreeOnDisk] = []
        for block in text.components(separatedBy: "\n\n") {
            var path: String?, head: String?, branch: String?
            for line in block.split(separator: "\n") {
                if line.hasPrefix("worktree ") { path = String(line.dropFirst(9)) }
                else if line.hasPrefix("HEAD ") { head = String(line.dropFirst(5)) }
                else if line.hasPrefix("branch refs/heads/") { branch = String(line.dropFirst(18)) }
            }
            guard let path, let branch, let head else { continue }
            let folder = URL(fileURLWithPath: path).standardizedFileURL
            let id = folder.lastPathComponent
            guard folder.deletingLastPathComponent().path == rootPath, CoreValidation.identifier(id), branch == ChildRecord.branchName(for: id) else { continue }
            found.append(PaneWorktreeOnDisk(sessionId: id, worktreePath: folder.path, branch: branch, head: head))
        }
        return found
    }

    /// The list for one workspace: open panes first, then kept ones, then those found on disk. `excluded` are delegated
    /// children's ids (their worktrees share the root); `baseBranch` is the checkout's branch now, the base a found one gets.
    public static func rows(open: [RunSession], kept: [KeptPaneWorktree], onDisk: [PaneWorktreeOnDisk], excluded: Set<String>,
                            checkout: String, baseBranch: String?, port: (String) -> Int) -> [Row] {
        var rows: [Row] = []
        for session in open { if let pane = session.paneWorktree { rows.append(Row(sessionId: session.id, title: session.title, provider: session.provider, worktree: pane, origin: .open)) } }
        for record in kept where !rows.contains(where: { $0.sessionId == record.sessionId }) {
            rows.append(Row(sessionId: record.sessionId, title: record.title, provider: record.provider, worktree: record.worktree, origin: .kept))
        }
        if let baseBranch {
            for disk in onDisk where !excluded.contains(disk.sessionId) && !rows.contains(where: { $0.sessionId == disk.sessionId }) {
                let pane = PaneWorktree(worktreePath: disk.worktreePath, branch: disk.branch, baseBranch: baseBranch, baseCommit: disk.head, checkout: checkout,
                                        port: port(disk.sessionId), workingFolder: disk.worktreePath)
                rows.append(Row(sessionId: disk.sessionId, title: disk.branch, provider: "claude", worktree: pane, origin: .found))
            }
        }
        return rows
    }
}

/// Files the workspace folder has but git leaves out of a worktree (`.env`, `.env.local`, …), copied into a new pane
/// worktree before its setup command. Only plain files and folders inside the workspace folder are copied, never a link
/// and nothing a link leads to, never over a file the worktree already has, and at most 50 MB in 2,000 files.
public enum PaneWorktreeCopy {
    public static let maximumBytes: Int64 = 50_000_000
    public static let maximumFiles = 2_000
    public static let maximumEntries = 32

    /// The list as saved: relative paths without `..`, a leading `/` or `~`, one per entry, at most 32.
    public static func normalizedPaths(_ raw: [String]?) -> [String]? {
        guard let raw else { return nil }
        var seen = Set<String>()
        let paths = raw.compactMap { entry -> String? in
            var path = entry.trimmingCharacters(in: .whitespacesAndNewlines)
            while path.hasPrefix("./") { path.removeFirst(2) }
            while path.hasSuffix("/") { path.removeLast() }
            guard !path.isEmpty, path.count <= 512, !path.hasPrefix("/"), !path.hasPrefix("~"), !path.contains("\0"),
                  !path.split(separator: "/").contains(where: { $0 == ".." || $0 == "." || $0 == ".git" }), seen.insert(path).inserted else { return nil }
            return path
        }
        return paths.isEmpty ? nil : Array(paths.prefix(maximumEntries))
    }

    /// One line of the settings field (`.env, .env.local` or one per line) as entries.
    public static func entries(_ text: String) -> [String] {
        text.split(whereSeparator: { $0 == "," || $0 == "\n" }).map(String.init)
    }

    public struct Result: Sendable, Equatable {
        public var copied: [String] = []
        /// Entries not copied, with why (a short word: `missing`, `link`, `outside`, `exists`, `limit`, `failed`).
        public var skipped: [String: String] = [:]
    }

    /// Copies `paths` from `source` (the workspace folder) into `destination` (the pane's run folder).
    public static func copy(_ paths: [String], from source: URL, to destination: URL) -> Result {
        var result = Result()
        let fileManager = FileManager.default
        let sourceRoot = source.resolvingSymlinksInPath().standardizedFileURL.path
        var bytes: Int64 = 0, files = 0
        for path in normalizedPaths(paths) ?? [] {
            let from = source.appendingPathComponent(path)
            guard let attributes = try? fileManager.attributesOfItem(atPath: from.path) else { result.skipped[path] = "missing"; continue }
            let type = attributes[.type] as? FileAttributeType
            guard type != .typeSymbolicLink else { result.skipped[path] = "link"; continue }
            // A folder on the way may be a link out of the workspace: the real place must still be inside it.
            let real = from.resolvingSymlinksInPath().standardizedFileURL.path
            guard real.hasPrefix(sourceRoot + "/") else { result.skipped[path] = "outside"; continue }
            let to = destination.appendingPathComponent(path)
            guard !fileManager.fileExists(atPath: to.path) else { result.skipped[path] = "exists"; continue }
            // Everything the entry holds, measured first: links inside are left out.
            var items: [(URL, URL)] = []
            if type == .typeDirectory {
                guard let walker = fileManager.enumerator(at: from, includingPropertiesForKeys: [.isSymbolicLinkKey, .isRegularFileKey, .fileSizeKey]) else { result.skipped[path] = "failed"; continue }
                for case let item as URL in walker {
                    let values = try? item.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey, .fileSizeKey])
                    guard values?.isSymbolicLink != true, values?.isRegularFile == true else { continue }
                    let relative = String(item.standardizedFileURL.path.dropFirst(from.standardizedFileURL.path.count))
                    items.append((item, to.appendingPathComponent(relative)))
                    bytes += Int64(values?.fileSize ?? 0); files += 1
                }
            } else if type == .typeRegular {
                items.append((from, to))
                bytes += (attributes[.size] as? NSNumber)?.int64Value ?? 0; files += 1
            } else { result.skipped[path] = "failed"; continue }
            guard bytes <= maximumBytes, files <= maximumFiles else { result.skipped[path] = "limit"; break }
            do {
                for (item, target) in items {
                    try fileManager.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try fileManager.copyItem(at: item, to: target)
                }
                if type == .typeDirectory, items.isEmpty { try fileManager.createDirectory(at: to, withIntermediateDirectories: true) }
                result.copied.append(path)
            } catch { result.skipped[path] = "failed" }
        }
        return result
    }
}

/// Pushing a pane's branch and the page where its pull request is made.
public enum PaneWorktreePR {
    /// The `owner/repo` of a GitHub remote URL (https, ssh or scp-like), nil for any other host.
    public static func gitHubRepository(_ remote: String) -> String? {
        var rest = remote.trimmingCharacters(in: .whitespacesAndNewlines)
        for prefix in ["https://github.com/", "http://github.com/", "ssh://git@github.com/", "git@github.com:", "git://github.com/"] where rest.hasPrefix(prefix) {
            rest = String(rest.dropFirst(prefix.count))
            if rest.hasSuffix(".git") { rest.removeLast(4) }
            while rest.hasSuffix("/") { rest.removeLast() }
            let parts = rest.split(separator: "/")
            guard parts.count == 2, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber || "-_.".contains($0) } }) else { return nil }
            return parts.joined(separator: "/")
        }
        return nil
    }

    /// GitHub's page for a pull request of `branch` into `base`.
    public static func compareURL(repository: String, base: String, branch: String) -> URL? {
        let allowed = CharacterSet.urlPathAllowed.subtracting(CharacterSet(charactersIn: "?#"))
        guard let base = base.addingPercentEncoding(withAllowedCharacters: allowed), let branch = branch.addingPercentEncoding(withAllowedCharacters: allowed) else { return nil }
        return URL(string: "https://github.com/\(repository)/compare/\(base)...\(branch)?expand=1")
    }

    /// The `origin` remote of the checkout, nil without one.
    public static func origin(_ pane: PaneWorktree) async -> String? {
        guard let found = await DelegationGit.run(["remote", "get-url", "origin"], in: pane.checkout), found.exitCode == 0 else { return nil }
        let url = DelegationGit.line(found.stdout)
        return url.isEmpty ? nil : url
    }

    /// Pushes the pane's branch to `origin` under the same name. Never forced: a remote branch that moved refuses it.
    /// nil when it went; git's words otherwise.
    public static func push(_ pane: PaneWorktree) async -> String? {
        let ref = "refs/heads/" + pane.branch
        guard let pushed = await DelegationGit.run(["push", "--porcelain", "origin", ref + ":" + ref], in: pane.checkout, timeout: 180) else { return L("delegation.card.error.gitUnfinished") }
        return pushed.exitCode == 0 ? nil : DelegationGit.message(pushed)
    }
}
