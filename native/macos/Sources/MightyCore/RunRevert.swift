import Foundation

/// 요청 되돌리기: puts the files one request changed back the way they were
/// before it, from the originals `FileOriginalStore` kept for that run. What
/// a revert writes is exactly what the 변경사항 view shows as the run's base,
/// so the review shows the user what will go before they confirm.
///
/// A path is refused, never half-written, when:
/// - a later run of the same pane changed it too (`laterRun`): undoing the
///   earlier run would also undo that one's work, so the newer run goes first;
/// - the file is gone or no longer a regular file inside the folder (`missing`);
/// - the file is no longer text the diff can read (`unreadable`), over 1 MiB, binary or undecodable;
/// - it already reads as the original (`unchanged`).
/// A file the run created is moved to the Trash rather than deleted.
public enum RunRevert {
    public enum Refusal: String, Codable, Equatable, Sendable {
        case laterRun, missing, unreadable, unchanged
    }

    public enum Action: String, Codable, Equatable, Sendable {
        /// Write the original text back.
        case restore
        /// The run made the file: move it to the Trash.
        case trash
    }

    public struct Item: Equatable, Sendable, Identifiable {
        public var id: String { path }
        public let path: String
        public let action: Action
        /// Lines the revert puts back (the run removed) and takes out (the run added).
        public let restored: Int
        public let removed: Int
        public let refusal: Refusal?
        public var revertible: Bool { refusal == nil }
    }

    public enum Outcome: Equatable, Sendable {
        case reverted
        case refused(Refusal)
        case failed(String)
    }

    /// What reverting `runId` would do to each file it changed, in the order the run changed them.
    public static func plan(store: FileOriginalStore, sessionId: String, runId: String, root: URL) -> [Item] {
        let runId = FileOriginalStore.sourceRun(runId)
        let later = laterPaths(store: store, sessionId: sessionId, runId: runId)
        return store.files(sessionId: sessionId, runId: runId).compactMap { path in
            guard let original = store.find(sessionId: sessionId, runId: runId, relativePath: path, root: root) else { return nil }
            return item(path: path, original: original, root: root, touchedLater: later.contains(path))
        }
    }

    /// Reverts the given paths of `runId` (all it changed when nil). Each path
    /// is re-checked just before it is written; one refusal never stops the others.
    @discardableResult
    public static func apply(store: FileOriginalStore, sessionId: String, runId: String, root: URL, paths: [String]? = nil,
                             trash: (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }) -> [String: Outcome] {
        let runId = FileOriginalStore.sourceRun(runId)
        let later = laterPaths(store: store, sessionId: sessionId, runId: runId)
        let wanted = paths.map(Set.init)
        var outcomes: [String: Outcome] = [:]
        for path in store.files(sessionId: sessionId, runId: runId) where wanted?.contains(path) ?? true {
            guard let original = store.find(sessionId: sessionId, runId: runId, relativePath: path, root: root) else { continue }
            let planned = item(path: path, original: original, root: root, touchedLater: later.contains(path))
            if let refusal = planned.refusal { outcomes[path] = .refused(refusal); continue }
            guard let url = WorkspaceFiles.resolve(path, root: root) else { outcomes[path] = .refused(.missing); continue }
            do {
                switch planned.action {
                case .trash: try trash(url)
                case .restore: try write(original.text, to: url)
                }
                outcomes[path] = .reverted
            } catch {
                outcomes[path] = .failed(error.localizedDescription)
            }
        }
        return outcomes
    }

    private static func item(path: String, original: FileOriginal, root: URL, touchedLater: Bool) -> Item {
        let action: Action = original.created ? .trash : .restore
        guard let url = WorkspaceFiles.resolve(path, root: root),
              WorkspaceFiles.contains(realPath: url, realRoot: WorkspaceFiles.realRoot(root)),
              (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else {
            return Item(path: path, action: action, restored: 0, removed: 0, refusal: .missing)
        }
        guard let current = FileChanges.current(path, root: root) else {
            return Item(path: path, action: action, restored: 0, removed: 0, refusal: .unreadable)
        }
        let diff = FileDiff.compute(old: original.text, new: current)
        let restored = diff?.removed ?? 0, removed = diff?.added ?? 0
        if touchedLater { return Item(path: path, action: action, restored: restored, removed: removed, refusal: .laterRun) }
        if !original.created, current == original.text { return Item(path: path, action: action, restored: 0, removed: 0, refusal: .unchanged) }
        return Item(path: path, action: action, restored: restored, removed: removed, refusal: nil)
    }

    /// Paths a run of the pane newer than `runId` changed too.
    static func laterPaths(store: FileOriginalStore, sessionId: String, runId: String) -> Set<String> {
        let runs = store.runs(sessionId: sessionId)
        guard let index = runs.firstIndex(of: runId) else { return [] }
        return Set(runs[..<index].flatMap { store.files(sessionId: sessionId, runId: $0) })
    }

    /// Writes `text` in place of the file in the encoding the file is in now
    /// (its byte order mark too: the store keeps originals without one),
    /// keeping its permissions.
    static func write(_ text: String, to url: URL) throws {
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        let current = (try? Data(contentsOf: url)) ?? Data()
        let encoding = FilePreviewClassifier.decodeText(current, sample: false)?.encoding ?? .utf8
        guard let body = text.data(using: encoding.stringEncoding) else { throw CocoaError(.fileWriteInapplicableStringEncoding) }
        try (Data(encoding.byteOrderMark) + body).write(to: url, options: .atomic)
        if let permissions = attributes?[.posixPermissions] {
            try? FileManager.default.setAttributes([.posixPermissions: permissions], ofItemAtPath: url.path)
        }
    }
}
