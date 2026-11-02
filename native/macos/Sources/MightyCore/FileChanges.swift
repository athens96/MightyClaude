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
/// `FileOriginalStore`), from the `tool_use_result` of the run's Edit, Write
/// and MultiEdit results. Kept apart from the state file, under
/// `<data>/file-originals/<sha(session)>/<sha(run)>/`: `run.json` (`Manifest`)
/// and one `<n>.txt` per file (UTF-8), each written atomically. A path is the
/// workspace-relative real path (inside the run's folder). Only a run's first
/// original of a path counts, at most 1 MiB each, 64 files and 16 MiB per run;
/// one past a cap is refused, and later originals of that path in that run are
/// refused too. Only runs the pane's graph keeps are kept (`retain`), a closed
/// pane's go (`forget`), and a start drops whatever the saved state no longer
/// names (`prune`).
public final class FileOriginalStore: @unchecked Sendable {
    public static let maximumOriginalBytes = 1_048_576
    public static let maximumFilesPerRun = 64
    public static let maximumBytesPerRun = 16 * 1_048_576
    public static let folderName = "file-originals"

    struct Entry: Codable, Equatable { var path: String; var blob: String; var bytes: Int; var created: Bool }
    struct Manifest: Codable, Equatable { var version: Int; var sessionId: String; var runId: String; var sequence: Int64; var files: [Entry] }

    public let directory: URL
    private let lock = NSLock()
    /// Paths of a run that were refused or already taken: a later original of them never counts.
    private var settled = Set<String>()
    private var lastSequence: Int64 = 0

    public init(directory: URL) { self.directory = directory }

    static func hash(_ value: String) -> String {
        String(SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined().prefix(32))
    }
    private func sessionFolder(_ sessionId: String) -> URL { directory.appendingPathComponent(Self.hash(sessionId), isDirectory: true) }
    private func runFolder(_ sessionId: String, _ runId: String) -> URL { sessionFolder(sessionId).appendingPathComponent(Self.hash(runId), isDirectory: true) }

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

    /// Records `filePath`'s content before the run changed it (nil: the run
    /// created it). The path may be absolute or relative to `workspaceRoot`;
    /// one that does not resolve to an existing file inside it is refused.
    /// True when this original was kept.
    @discardableResult
    public func capture(sessionId: String, runId: String, workspaceRoot: URL, filePath: String, original: String?) -> Bool {
        guard CoreValidation.identifier(sessionId), CoreValidation.identifier(runId),
              let resolved = ReferenceLinkSupport.resolve(filePath, root: workspaceRoot) else { return false }
        let base = workspaceRoot.standardizedFileURL.resolvingSymlinksInPath().path
        let prefix = base.hasSuffix("/") ? base : base + "/"
        guard resolved.path.hasPrefix(prefix) else { return false }
        let relative = String(resolved.path.dropFirst(prefix.count))
        lock.lock(); defer { lock.unlock() }
        guard settled.insert(sessionId + "\u{1F}" + runId + "\u{1F}" + relative).inserted else { return false }
        let bytes = Data((original ?? "").utf8)
        guard bytes.count <= Self.maximumOriginalBytes else { return false }
        let folder = runFolder(sessionId, runId)
        var manifest = Self.manifest(in: folder) ?? Manifest(version: 1, sessionId: sessionId, runId: runId, sequence: nextSequence(), files: [])
        guard !manifest.files.contains(where: { $0.path == relative }),
              manifest.files.count < Self.maximumFilesPerRun,
              manifest.files.reduce(0, { $0 + $1.bytes }) + bytes.count <= Self.maximumBytesPerRun else { return false }
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let blob = "\(manifest.files.count).txt"
            try bytes.write(to: folder.appendingPathComponent(blob), options: .atomic)
            manifest.files.append(Entry(path: relative, blob: blob, bytes: bytes.count, created: original == nil))
            try JSONEncoder().encode(manifest).write(to: folder.appendingPathComponent("run.json"), options: .atomic)
            return true
        } catch { return false }
    }

    /// A sequence later than every run's so far, also across restarts (the
    /// clock's 100 ns ticks since 1970, never going back).
    private func nextSequence() -> Int64 {
        lastSequence = max(lastSequence + 1, Int64(Date().timeIntervalSince1970 * 10_000_000))
        return lastSequence
    }

    /// The original of `relativePath` that `runId` captured, or with no run the
    /// newest run of the session that captured it; nil when there is none.
    public func find(sessionId: String, runId: String?, relativePath: String) -> FileOriginal? {
        guard CoreValidation.identifier(sessionId), runId.map(CoreValidation.identifier) ?? true else { return nil }
        lock.lock(); defer { lock.unlock() }
        let folders = runId.map { [runFolder(sessionId, $0)] } ?? Self.subfolders(sessionFolder(sessionId))
        let candidates = folders.compactMap(Self.manifest(in:))
            .filter { $0.sessionId == sessionId && (runId == nil || $0.runId == runId) }
            .sorted { $0.sequence > $1.sequence }
        for manifest in candidates {
            guard let entry = manifest.files.first(where: { $0.path == relativePath }) else { continue }
            guard !entry.blob.contains("/"), !entry.blob.contains("..") else { return nil }
            let url = runFolder(sessionId, manifest.runId).appendingPathComponent(entry.blob)
            guard let data = try? Data(contentsOf: url), data.count <= Self.maximumOriginalBytes else { return nil }
            return FileOriginal(runId: manifest.runId, path: entry.path, text: String(decoding: data, as: UTF8.self), created: entry.created)
        }
        return nil
    }

    /// The run ids the store holds for a session, newest first.
    public func runs(sessionId: String) -> [String] {
        guard CoreValidation.identifier(sessionId) else { return [] }
        lock.lock(); defer { lock.unlock() }
        return Self.subfolders(sessionFolder(sessionId)).compactMap(Self.manifest(in:)).sorted { $0.sequence > $1.sequence }.map(\.runId)
    }

    /// Drops the session's runs that are not in `keep`.
    public func retain(sessionId: String, keep: Set<String>) {
        guard CoreValidation.identifier(sessionId) else { return }
        lock.lock(); defer { lock.unlock() }
        for folder in Self.subfolders(sessionFolder(sessionId)) {
            if let manifest = Self.manifest(in: folder), keep.contains(manifest.runId) { continue }
            try? FileManager.default.removeItem(at: folder)
        }
    }

    /// Drops everything a pane's runs captured.
    public func forget(sessionId: String) {
        guard CoreValidation.identifier(sessionId) else { return }
        lock.lock(); defer { lock.unlock() }
        try? FileManager.default.removeItem(at: sessionFolder(sessionId))
        settled = settled.filter { !$0.hasPrefix(sessionId + "\u{1F}") }
    }

    /// Keeps only the sessions named, each with only the runs named for it;
    /// everything else in the folder goes.
    public func prune(sessions: [String: Set<String>]) {
        lock.lock(); defer { lock.unlock() }
        guard FileManager.default.fileExists(atPath: directory.path) else { return }
        var known: [String: String] = [:]
        for id in sessions.keys where CoreValidation.identifier(id) { known[Self.hash(id)] = id }
        for item in (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [] {
            guard let id = known[item.lastPathComponent], (try? item.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else {
                try? FileManager.default.removeItem(at: item); continue
            }
            for run in Self.subfolders(item) {
                if let manifest = Self.manifest(in: run), manifest.sessionId == id, sessions[id]?.contains(manifest.runId) == true { continue }
                try? FileManager.default.removeItem(at: run)
            }
        }
    }

    /// The runs a pane's graph keeps: its runs' ids and the ids they were made from.
    public static func keptRuns(_ session: RunSession) -> Set<String> {
        Set((session.graphRuns ?? []).flatMap { [$0.id, $0.sourceRunID].compactMap { $0 } }.filter(CoreValidation.identifier))
    }
}

/// A file's content at git's HEAD (Windows `GitHeadFile`), read the way
/// `WorkspaceGitInfo` reads status: no hooks, no fsmonitor, no optional locks,
/// no GIT_* from the app's environment, output bounded, 2 seconds at most per
/// command. Only reads: `rev-parse` and `cat-file blob HEAD:./path`, which
/// applies no filters.
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

    /// HEAD's copy of `relativePath` (relative to the real `root`, "/"
    /// separated): nil when the folder is in no repository, git is missing or
    /// fails, or the copy is over `maximumBytes`.
    public static func read(root: URL, relativePath: String, maximumBytes: Int = FileDiff.maximumSideBytes) async -> Content? {
        let path = root.path
        guard path.hasPrefix("/"), !path.contains("\0"), !relativePath.isEmpty, !relativePath.contains("\0"),
              !relativePath.split(separator: "/", omittingEmptySubsequences: false).contains(where: { $0.isEmpty || $0 == "." || $0 == ".." }) else { return nil }
        let candidates = ["/Library/Developer/CommandLineTools/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git"]
        guard let git = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return nil }
        guard let inside = await run(git, root: path, ["rev-parse", "--is-inside-work-tree"], maximumBytes: 64), inside.exitCode == 0,
              String(decoding: inside.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) == "true" else { return nil }
        guard let head = await run(git, root: path, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], maximumBytes: 256) else { return nil }
        let sha = String(decoding: head.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        // A repository with no commit yet: nothing is in HEAD.
        guard head.exitCode == 0, sha.range(of: "^[0-9a-f]{40,64}$", options: .regularExpression) != nil else { return Content(data: Data(), shortSha: nil, untracked: true) }
        guard let blob = await run(git, root: path, ["cat-file", "blob", "HEAD:./" + relativePath], maximumBytes: maximumBytes), blob.stdout.count <= maximumBytes else { return nil }
        return blob.exitCode == 0 ? Content(data: blob.stdout, shortSha: String(sha.prefix(7)), untracked: false) : Content(data: Data(), shortSha: String(sha.prefix(7)), untracked: true)
    }
}

/// `read`: what a request changed in a file, against the run's own original
/// when the store has one, else against git's HEAD (Windows `FileChanges.ReadAsync`).
public enum FileChanges {
    /// The change set of `relativePath` under `workspaceRoot`, nil when there is
    /// no diff to show (the path leaves the workspace or is no regular file, a
    /// side is binary, undecodable or over the caps, or there is no run
    /// original and no git base). With `runId` only that run's original counts;
    /// without it the newest run of `sessionId` that captured the file.
    public static func read(store: FileOriginalStore?, workspaceRoot: URL, relativePath: String, sessionId: String? = nil, runId: String? = nil) async -> FileChangeSet? {
        guard let real = WorkspaceFiles.resolve(relativePath, root: workspaceRoot) else { return nil }
        let base = WorkspaceFiles.realRoot(workspaceRoot)
        let prefix = base.path.hasSuffix("/") ? base.path : base.path + "/"
        guard real.path.hasPrefix(prefix), let current = current(relativePath, root: workspaceRoot) else { return nil }
        let relative = String(real.path.dropFirst(prefix.count))
        if let store, let sessionId, let original = store.find(sessionId: sessionId, runId: runId, relativePath: relative) {
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
