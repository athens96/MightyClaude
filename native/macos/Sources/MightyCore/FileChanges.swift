import CryptoKit
import Foundation

/// What a request changed in one file (the source preview's 변경사항 view, the
/// phone's `changes`; Windows `MightyClaude.Core/FileChanges.cs`). `base` is
/// `run` (the file as it was before the run first changed it) or `head` (git's
/// HEAD, an untracked file read as empty). `runId` names the run of a `run`
/// base, `headSha` the short commit of a `head` base (nil for a repository with
/// no commit yet), `created` a file the run made or git does not know.
public struct FileChangeSet: Codable, Equatable, Sendable {
    public var base: String
    public var hunks: [FileDiffHunk]
    public var added: Int
    public var removed: Int
    public var truncated: Bool
    public var unchanged: Bool
    public var runId: String?
    public var headSha: String?
    public var created: Bool?

    init(base: String, diff: FileDiffResult, runId: String? = nil, headSha: String? = nil, created: Bool) {
        self.base = base; hunks = diff.hunks; added = diff.added; removed = diff.removed
        truncated = diff.truncated; unchanged = diff.unchanged
        self.runId = runId; self.headSha = headSha; self.created = created ? true : nil
    }
}

/// A file's content before a run first changed it; `created` when the run made
/// it (the content is then "").
public struct FileOriginal: Equatable, Sendable {
    public let runId: String
    public let path: String
    public let text: String
    public let created: Bool
}

/// The contents files had before a Claude run first changed them (Windows
/// `FileOriginalStore`), from the run's Edit, Write and MultiEdit results
/// (`FileEdit`). Kept apart from the state file, under
/// `<data>/file-originals/<sha(session)>/<sha(run)>/`: `run.json` (`Manifest`)
/// and one `<n>.txt` per file (UTF-8), each written atomically.
///
/// A path is the real path relative to the run's folder (its `root`,
/// recorded: an original is never read under another root). Only a run's
/// first edit of a path counts: an original that is unknown (no text, no patch
/// that fits the file), over 1 MiB, or past the run's 64 files or 16 MiB is
/// recorded as refused, and the path's later edits in that run never pose as
/// its first. An original rebuilt from a patch holds only while each later
/// edit of the path in the run confirms it: the CLI may write the next edit
/// before this one's result is read, and undoing a patch on that file keeps
/// the next edit in. So the file the run's last edit left is remembered (its
/// hash, in this process), each later edit's patch undone on the file now must
/// give it (or its `originalFile` must be it), and an edit that does not drops
/// the original as refused. Each pane keeps the originals of its newest 20 runs, and the
/// whole store at most 512 MiB, the oldest runs going first. Only runs the
/// pane's graph keeps are kept (`retain`), a closed pane's go (`forget`), and a
/// start drops whatever the saved state no longer names (`prune`).
public final class FileOriginalStore: @unchecked Sendable {
    public static let maximumOriginalBytes = 1_048_576
    public static let maximumFilesPerRun = 64
    public static let maximumBytesPerRun = 16 * 1_048_576
    public static let maximumBytes = 512 * 1_048_576
    public static let maximumRunsPerPane = 20
    public static let maximumRefusedPerRun = 1_024
    public static let folderName = "file-originals"

    struct Entry: Codable, Equatable { var path: String; var blob: String; var bytes: Int; var created: Bool }
    struct Manifest: Codable, Equatable {
        var version: Int; var sessionId: String; var runId: String; var sequence: Int64; var files: [Entry]
        /// The real path of the run's folder its paths are relative to.
        var root: String?
        /// Paths the run edited whose original is not kept.
        var refused: [String]?
    }
    private struct Indexed { let sessionId: String; let runId: String; let sequence: Int64; let bytes: Int; let files: Int }

    public let directory: URL
    private let budget: Int
    private let lock = NSLock()
    /// Paths of a run already settled — kept or refused — in this process.
    private var settled = Set<String>()
    /// For an original rebuilt from a patch: the hash of the file as the run's last confirmed edit of it left it.
    private var following: [String: String] = [:]
    /// Every run folder's manifest summary (by folder path), read once from disk and kept up to date.
    private var index: [String: Indexed]?
    private var lastSequence: Int64 = 0

    /// `budget`: the store's whole size (`maximumBytes`; tests pass a smaller one).
    public init(directory: URL, budget: Int = FileOriginalStore.maximumBytes) { self.directory = directory; self.budget = budget }

    static func hash(_ value: String) -> String {
        String(SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined().prefix(32))
    }
    private func sessionFolder(_ sessionId: String) -> URL { directory.appendingPathComponent(Self.hash(sessionId), isDirectory: true) }
    private func runFolder(_ sessionId: String, _ runId: String) -> URL { sessionFolder(sessionId).appendingPathComponent(Self.hash(runId), isDirectory: true) }
    private static func key(_ sessionId: String, _ runId: String, _ path: String) -> String { sessionId + "\u{1F}" + runId + "\u{1F}" + path }
    /// A background notice run's id (`BackgroundNoticeRuns.runID`) stands for the run it came from.
    public static func sourceRun(_ runId: String) -> String {
        guard let marker = runId.range(of: BackgroundNoticeRuns.marker), marker.lowerBound > runId.startIndex else { return runId }
        return String(runId[..<marker.lowerBound])
    }

    private static func manifest(in folder: URL) -> Manifest? {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent("run.json")),
              let value = try? JSONDecoder().decode(Manifest.self, from: data),
              value.version == 1, CoreValidation.identifier(value.sessionId), CoreValidation.identifier(value.runId) else { return nil }
        return value
    }

    private static func subfolders(_ folder: URL) -> [URL] {
        ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey])) ?? [])
            .filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
    }

    private static func summary(_ manifest: Manifest) -> Indexed {
        Indexed(sessionId: manifest.sessionId, runId: manifest.runId, sequence: manifest.sequence, bytes: manifest.files.reduce(0) { $0 + $1.bytes }, files: manifest.files.count)
    }

    /// Under `lock`: the run folders' summaries, read from disk the first time.
    private func loadedIndex() -> [String: Indexed] {
        if let index { return index }
        var value: [String: Indexed] = [:]
        for session in Self.subfolders(directory) {
            for run in Self.subfolders(session) {
                guard let manifest = Self.manifest(in: run) else { continue }
                value[run.path] = Self.summary(manifest); lastSequence = max(lastSequence, manifest.sequence)
            }
        }
        index = value
        return value
    }

    /// Records what the run's edit says about `filePath` before it (the
    /// convenience form: text, or nil for a created file).
    @discardableResult
    public func capture(sessionId: String, runId: String, workspaceRoot: URL, filePath: String, original: String?) -> Bool {
        capture(sessionId: sessionId, runId: runId, workspaceRoot: workspaceRoot, filePath: filePath, edit: original.map { FileEdit(original: $0) } ?? FileEdit(created: true))
    }

    /// Records what an edit of the run says about `filePath` before it. The
    /// path may be absolute or relative to `workspaceRoot`; one that does not
    /// resolve to an existing file inside it is ignored. A patch is undone
    /// against the file as it is now (`FilePatch.reverseApply`). True when an
    /// original was kept; an edit whose original cannot be kept settles the
    /// path as refused.
    @discardableResult
    public func capture(sessionId: String, runId: String, workspaceRoot: URL, filePath: String, edit: FileEdit) -> Bool {
        guard CoreValidation.identifier(sessionId), CoreValidation.identifier(runId),
              let resolved = ReferenceLinkSupport.resolve(filePath, root: workspaceRoot) else { return false }
        let realRoot = WorkspaceFiles.realRoot(workspaceRoot)
        let prefix = realRoot.path.hasSuffix("/") ? realRoot.path : realRoot.path + "/"
        guard resolved.path.hasPrefix(prefix) else { return false }
        let relative = String(resolved.path.dropFirst(prefix.count))
        lock.lock(); defer { lock.unlock() }
        let key = Self.key(sessionId, runId, relative)
        guard settled.insert(key).inserted else {
            if let expected = following[key] {
                if let next = Self.follow(expected, edit: edit, relative: relative, root: realRoot) { following[key] = next }
                else { following[key] = nil; withdraw(sessionId: sessionId, runId: runId, relative: relative) }
            }
            return false
        }
        let folder = runFolder(sessionId, runId)
        var manifest = Self.manifest(in: folder) ?? Manifest(version: 1, sessionId: sessionId, runId: runId, sequence: nextSequence(), files: [], root: realRoot.path, refused: [])
        guard !manifest.files.contains(where: { $0.path == relative }), !(manifest.refused ?? []).contains(relative),
              manifest.root == realRoot.path else { return false }
        // The files are read without a byte order mark, so an original is kept without one too.
        let text: String?
        var after: String?
        if let original = edit.original { text = FilePatch.withoutByteOrderMark(original) }
        else if edit.created { text = "" }
        else if let patch = edit.patch, let current = FileChanges.current(relative, root: realRoot),
                let before = FilePatch.reverseApply(current, hunks: patch) { text = before; after = Self.hash(current) }
        else { text = nil }
        let bytes = text.map { Data($0.utf8) }
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            guard let bytes, bytes.count <= Self.maximumOriginalBytes, manifest.files.count < Self.maximumFilesPerRun,
                  manifest.files.reduce(0, { $0 + $1.bytes }) + bytes.count <= Self.maximumBytesPerRun else {
                var refused = manifest.refused ?? []
                if refused.count < Self.maximumRefusedPerRun { refused.append(relative); manifest.refused = refused; try save(manifest, in: folder) }
                return false
            }
            // A withdrawn original leaves a gap in the numbers: the next blob takes the first free one.
            var number = manifest.files.count
            while manifest.files.contains(where: { $0.blob == "\(number).txt" }) { number += 1 }
            let blob = "\(number).txt"
            try bytes.write(to: folder.appendingPathComponent(blob), options: .atomic)
            manifest.files.append(Entry(path: relative, blob: blob, bytes: bytes.count, created: edit.original == nil && edit.created))
            try save(manifest, in: folder)
            if let after { following[key] = after }
            evict(keeping: folder.path)
            return true
        } catch { return false }
    }

    /// The hash of the file after a later edit of a path whose original was
    /// rebuilt from a patch, when the edit confirms the file the run's last
    /// edit left (`expected`); nil when it does not or cannot.
    private static func follow(_ expected: String, edit: FileEdit, relative: String, root: URL) -> String? {
        guard let patch = edit.patch else { return nil }
        if let given = edit.original {
            let before = FilePatch.withoutByteOrderMark(given)
            guard hash(before) == expected, let then = FilePatch.reverseApply(before, hunks: FilePatch.inverted(patch)) else { return nil }
            return hash(then)
        }
        guard let current = FileChanges.current(relative, root: root), let undone = FilePatch.reverseApply(current, hunks: patch),
              hash(undone) == expected else { return nil }
        return hash(current)
    }

    /// Under `lock`: drops a kept original of the run, settling its path as refused.
    private func withdraw(sessionId: String, runId: String, relative: String) {
        let folder = runFolder(sessionId, runId)
        guard var manifest = Self.manifest(in: folder), let entry = manifest.files.first(where: { $0.path == relative }) else { return }
        if !entry.blob.contains("/"), !entry.blob.contains("..") { try? FileManager.default.removeItem(at: folder.appendingPathComponent(entry.blob)) }
        manifest.files.removeAll { $0 == entry }
        var refused = manifest.refused ?? []
        if refused.count < Self.maximumRefusedPerRun { refused.append(relative) }
        manifest.refused = refused
        try? save(manifest, in: folder)
    }

    /// Under `lock`.
    private func save(_ manifest: Manifest, in folder: URL) throws {
        try JSONEncoder().encode(manifest).write(to: folder.appendingPathComponent("run.json"), options: .atomic)
        var value = loadedIndex(); value[folder.path] = Self.summary(manifest); index = value
    }

    /// Under `lock`: each pane keeps the originals of its newest
    /// `maximumRunsPerPane` runs and the store at most its budget; the oldest
    /// runs go first, never `keeping` (the run just written).
    private func evict(keeping: String?) {
        let all = loadedIndex()
        let panes = Dictionary(grouping: all.filter { $0.value.files > 0 }, by: { $0.value.sessionId })
        for (_, runs) in panes {
            for old in runs.sorted(by: { $0.value.sequence > $1.value.sequence }).dropFirst(Self.maximumRunsPerPane) where old.key != keeping { drop(old.key) }
        }
        var total = loadedIndex().values.reduce(0) { $0 + $1.bytes }
        for old in loadedIndex().sorted(by: { $0.value.sequence < $1.value.sequence }) where old.key != keeping {
            guard total > budget else { break }
            total -= old.value.bytes; drop(old.key)
        }
    }

    /// Under `lock`: removes one run folder, its summary and what this process settled for it.
    private func drop(_ folder: String) {
        var value = loadedIndex()
        if let gone = value.removeValue(forKey: folder) {
            let prefix = Self.key(gone.sessionId, gone.runId, "")
            settled = settled.filter { !$0.hasPrefix(prefix) }
            following = following.filter { !$0.key.hasPrefix(prefix) }
        }
        index = value
        try? FileManager.default.removeItem(atPath: folder)
    }

    /// A sequence later than every run's so far, also across restarts (the
    /// clock's 100 ns ticks since 1970, never going back).
    private func nextSequence() -> Int64 {
        _ = loadedIndex()
        lastSequence = max(lastSequence + 1, Int64(Date().timeIntervalSince1970 * 10_000_000))
        return lastSequence
    }

    /// The original of `relativePath` that `runId` (a notice run: the run it
    /// came from) captured, or with no run the one of the session's newest run
    /// that edited the path; nil when that run refused it, it was captured
    /// under another root than `root` (when given), or there is none.
    public func find(sessionId: String, runId: String?, relativePath: String, root: URL? = nil) -> FileOriginal? {
        let runId = runId.map(Self.sourceRun)
        guard CoreValidation.identifier(sessionId), runId.map(CoreValidation.identifier) ?? true else { return nil }
        let realRoot = root.map { WorkspaceFiles.realRoot($0).path }
        lock.lock(); defer { lock.unlock() }
        let folders: [URL] = runId.map { [runFolder(sessionId, $0)] }
            ?? loadedIndex().filter { $0.value.sessionId == sessionId }.sorted { $0.value.sequence > $1.value.sequence }.map { URL(fileURLWithPath: $0.key, isDirectory: true) }
        for folder in folders {
            guard let manifest = Self.manifest(in: folder), manifest.sessionId == sessionId, runId == nil || manifest.runId == runId else { continue }
            if (manifest.refused ?? []).contains(relativePath) { return nil }
            guard let entry = manifest.files.first(where: { $0.path == relativePath }) else { continue }
            if let realRoot, manifest.root != realRoot { return nil }
            guard !entry.blob.contains("/"), !entry.blob.contains("..") else { return nil }
            guard let data = try? Data(contentsOf: folder.appendingPathComponent(entry.blob)), data.count <= Self.maximumOriginalBytes else { return nil }
            return FileOriginal(runId: manifest.runId, path: entry.path, text: String(decoding: data, as: UTF8.self), created: entry.created)
        }
        return nil
    }

    /// The paths the run's originals were kept for, the files it edited, in the order they were recorded.
    public func files(sessionId: String, runId: String) -> [String] {
        let runId = Self.sourceRun(runId)
        guard CoreValidation.identifier(sessionId), CoreValidation.identifier(runId) else { return [] }
        lock.lock(); defer { lock.unlock() }
        guard let manifest = Self.manifest(in: runFolder(sessionId, runId)), manifest.sessionId == sessionId, manifest.runId == runId else { return [] }
        return manifest.files.map(\.path)
    }

    /// The run ids the store holds for a session, newest first.
    public func runs(sessionId: String) -> [String] {
        guard CoreValidation.identifier(sessionId) else { return [] }
        lock.lock(); defer { lock.unlock() }
        return loadedIndex().values.filter { $0.sessionId == sessionId }.sorted { $0.sequence > $1.sequence }.map(\.runId)
    }

    /// Drops the session's runs that are not in `keep` (a notice run keeps the run it came from).
    public func retain(sessionId: String, keep: Set<String>) {
        guard CoreValidation.identifier(sessionId) else { return }
        let kept = Set(keep.map(Self.sourceRun))
        lock.lock(); defer { lock.unlock() }
        for (folder, run) in loadedIndex() where run.sessionId == sessionId && !kept.contains(run.runId) { drop(folder) }
        // A folder with no readable manifest is no run.
        for folder in Self.subfolders(sessionFolder(sessionId)) where loadedIndex()[folder.path] == nil { try? FileManager.default.removeItem(at: folder) }
    }

    /// Drops everything a pane's runs captured.
    public func forget(sessionId: String) {
        guard CoreValidation.identifier(sessionId) else { return }
        lock.lock(); defer { lock.unlock() }
        index = loadedIndex().filter { $0.value.sessionId != sessionId }
        try? FileManager.default.removeItem(at: sessionFolder(sessionId))
        settled = settled.filter { !$0.hasPrefix(sessionId + "\u{1F}") }
        following = following.filter { !$0.key.hasPrefix(sessionId + "\u{1F}") }
    }

    /// Keeps only the sessions named, each with only the runs named for it (a
    /// notice run keeps the run it came from); everything else in the folder
    /// goes, temporary files left by a crash included, and then the budget holds.
    public func prune(sessions: [String: Set<String>]) {
        lock.lock(); defer { lock.unlock() }
        if FileManager.default.fileExists(atPath: directory.path) {
            var known: [String: String] = [:]
            for id in sessions.keys where CoreValidation.identifier(id) { known[Self.hash(id)] = id }
            for item in (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [] {
                guard let id = known[item.lastPathComponent], (try? item.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else {
                    try? FileManager.default.removeItem(at: item); continue
                }
                let runs = Set((sessions[id] ?? []).map(Self.sourceRun))
                for run in Self.subfolders(item) {
                    if let manifest = Self.manifest(in: run), manifest.sessionId == id, runs.contains(manifest.runId) { continue }
                    try? FileManager.default.removeItem(at: run)
                }
            }
            // Atomic writes leave their temporary files next to the target if the app dies mid-write.
            if let walker = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: nil) {
                for case let file as URL in walker where file.lastPathComponent.hasSuffix(".tmp") || file.lastPathComponent.hasPrefix(".dat.nosync") {
                    try? FileManager.default.removeItem(at: file)
                }
            }
        }
        index = nil; settled = []; following = [:]
        evict(keeping: nil)
    }

    /// The run id the originals are kept under for `runId` as a pane's graph
    /// names it: a graph run (on the Mac its id is the request's entry id)
    /// stands for the process's run, its `sourceRunID`; an id no graph run of
    /// the pane has stays as it is.
    public static func originalsRun(_ runId: String, in session: RunSession?) -> String {
        guard let graph = session?.graphRuns?.first(where: { $0.id == runId }) else { return runId }
        return graph.sourceRunID ?? graph.id
    }

    /// The runs a pane's graph keeps: its runs' ids and the ids they were made from.
    public static func keptRuns(_ session: RunSession) -> Set<String> {
        Set((session.graphRuns ?? []).flatMap { [$0.id, $0.sourceRunID].compactMap { $0 } }.filter(CoreValidation.identifier))
    }

    /// The kept runs of every pane; a pane id that appears twice keeps the runs of both.
    public static func keptRuns(_ sessions: [RunSession]) -> [String: Set<String>] {
        sessions.reduce(into: [:]) { kept, session in kept[session.id, default: []].formUnion(keptRuns(session)) }
    }
}

/// A file's content at git's HEAD (Windows `GitHeadFile`), read the way
/// `WorkspaceGitInfo` reads status — the same git, no hooks, no fsmonitor, no
/// optional locks, no GIT_* from the app's environment, output bounded, 2
/// seconds at most per command. Only reads: `rev-parse` and
/// `cat-file blob HEAD:./path`, which applies no filters.
public enum GitHeadFile {
    /// `untracked`: HEAD has no such file (or there is no commit yet), so everything reads as added.
    public struct Content: Equatable, Sendable { public let data: Data; public let shortSha: String?; public let untracked: Bool }

    private static func run(_ executable: String, root: String, _ arguments: [String], maximumBytes: Int) async -> ProcessResult? {
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_OPTIONAL_LOCKS"] = "0"; environment["GIT_TERMINAL_PROMPT"] = "0"; environment["LC_ALL"] = "C"
        let args = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c", "gc.auto=0", "-C", root] + arguments
        // ProcessCapture's limit counts stdout and stderr together; past it the read fails.
        return try? await ProcessCapture.run(executable: URL(fileURLWithPath: executable), arguments: args, environment: environment,
                                             cwd: URL(fileURLWithPath: root), timeout: 2, maximumBytes: maximumBytes + 65_536)
    }

    /// git's own words (LC_ALL=C) for a path HEAD does not have; any other failure is no base at all.
    public static func notInHead(_ error: String) -> Bool {
        error.contains("does not exist in 'HEAD'") || error.contains("exists on disk, but not in 'HEAD'")
    }

    /// HEAD's copy of `relativePath` (relative to the real `root`, "/"
    /// separated): nil when the folder is in no repository, git is missing or
    /// fails for any reason but the path not being in HEAD, or the copy is over `maximumBytes`.
    public static func read(root: URL, relativePath: String, maximumBytes: Int = FileDiff.maximumSideBytes) async -> Content? {
        let path = root.path
        guard path.hasPrefix("/"), !path.contains("\0"), !relativePath.isEmpty, !relativePath.contains("\0"),
              !relativePath.split(separator: "/", omittingEmptySubsequences: false).contains(where: { $0.isEmpty || $0 == "." || $0 == ".." }) else { return nil }
        guard let git = WorkspaceGitInfo.gitExecutable() else { return nil }
        guard let inside = await run(git, root: path, ["rev-parse", "--is-inside-work-tree"], maximumBytes: 64), inside.exitCode == 0,
              String(decoding: inside.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) == "true" else { return nil }
        guard let head = await run(git, root: path, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], maximumBytes: 256) else { return nil }
        let sha = String(decoding: head.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        // A repository with no commit yet: nothing is in HEAD.
        guard head.exitCode == 0, sha.range(of: "^[0-9a-f]{40,64}$", options: .regularExpression) != nil else { return Content(data: Data(), shortSha: nil, untracked: true) }
        guard let blob = await run(git, root: path, ["cat-file", "blob", "HEAD:./" + relativePath], maximumBytes: maximumBytes), blob.stdout.count <= maximumBytes else { return nil }
        if blob.exitCode == 0 { return Content(data: blob.stdout, shortSha: String(sha.prefix(7)), untracked: false) }
        return notInHead(String(decoding: blob.stderr, as: UTF8.self)) ? Content(data: Data(), shortSha: String(sha.prefix(7)), untracked: true) : nil
    }
}

/// `read`: what a request changed in a file, against the run's own original
/// when the store has one, else against git's HEAD (Windows `FileChanges.ReadAsync`).
public enum FileChanges {
    /// The change set of `relativePath` under `workspaceRoot` (for a delegated
    /// child, its worktree folder), nil when there is no diff to show (the path
    /// leaves the folder or is no regular file, a side is binary, undecodable
    /// or over the caps, or there is no run original and no git base). With
    /// `runId` (a notice run: the run it came from) only that run's original
    /// counts; without it the newest run of `sessionId` that edited the file.
    /// An original captured under another folder never counts.
    public static func read(store: FileOriginalStore?, workspaceRoot: URL, relativePath: String, sessionId: String? = nil, runId: String? = nil) async -> FileChangeSet? {
        guard let real = WorkspaceFiles.resolve(relativePath, root: workspaceRoot) else { return nil }
        let base = WorkspaceFiles.realRoot(workspaceRoot)
        let prefix = base.path.hasSuffix("/") ? base.path : base.path + "/"
        guard real.path.hasPrefix(prefix), let current = current(relativePath, root: workspaceRoot) else { return nil }
        let relative = String(real.path.dropFirst(prefix.count))
        if let store, let sessionId, let original = store.find(sessionId: sessionId, runId: runId, relativePath: relative, root: workspaceRoot) {
            return FileDiff.compute(old: original.text, new: current).map { FileChangeSet(base: "run", diff: $0, runId: original.runId, created: original.created) }
        }
        guard let head = await GitHeadFile.read(root: base, relativePath: relative),
              let decoded = FilePreviewClassifier.decodeText(head.data, sample: false) else { return nil }
        return FileDiff.compute(old: decoded.text, new: current).map { FileChangeSet(base: "head", diff: $0, headSha: head.shortSha, created: head.untracked) }
    }

    /// The file as it is now, through the files pane's open
    /// (`WorkspaceFiles.openFile`): nil when over 1 MiB, binary or undecodable.
    static func current(_ relativePath: String, root: URL) -> String? {
        guard let file = try? WorkspaceFiles.openFile(relativePath, root: root), file.size <= Int64(FileDiff.maximumSideBytes) else { return nil }
        let data: Data
        do {
            try file.handle.seek(toOffset: 0)
            data = try file.handle.read(upToCount: FileDiff.maximumSideBytes + 1) ?? Data()
        } catch { return nil }
        guard data.count <= FileDiff.maximumSideBytes else { return nil }
        return FilePreviewClassifier.decodeText(data, sample: false)?.text
    }
}
