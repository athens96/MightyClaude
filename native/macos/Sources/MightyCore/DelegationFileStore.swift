import Foundation

/// A stored copy of a child's TASK.md or REPORT.md (macOS only). A copy is
/// never longer than ``DelegationFileStore/maximumCopyBytes`` in UTF-8: a
/// longer file is cut at a character boundary and ends with
/// ``truncationMarker``, while the full file stays in the worktree until cleanup.
public struct DelegationCopy: Codable, Sendable, Equatable {
    public enum Kind: String, Codable, Sendable, CaseIterable { case task, report }

    public static let truncationMarker = "\n\n[truncated at 64 KiB: the full file stays in the child's worktree until cleanup]\n"

    public let childId: String
    public let kind: Kind
    /// The report revision this copy holds; 0 for TASK.md.
    public let revision: Int
    public let text: String
    /// The whole file's size in bytes.
    public let originalBytes: Int
    public let truncated: Bool

    /// The copy of a file whose bytes start with `contents`. Pass `totalBytes`
    /// when `contents` holds only the start of a longer file.
    public init(childId: String, kind: Kind, revision: Int, contents: Data, totalBytes: Int? = nil) {
        self.childId = childId; self.kind = kind; self.revision = revision
        originalBytes = max(totalBytes ?? contents.count, contents.count)
        let cap = DelegationFileStore.maximumCopyBytes
        let decoded = String(decoding: contents.prefix(cap + 1), as: UTF8.self)
        if originalBytes <= cap, decoded.utf8.count <= cap { text = decoded; truncated = false; return }
        let budget = cap - Self.truncationMarker.utf8.count
        var end = decoded.startIndex, used = 0
        for character in decoded {
            let size = character.utf8.count
            guard used + size <= budget else { break }
            used += size; end = decoded.index(after: end)
        }
        text = String(decoded[..<end]) + Self.truncationMarker; truncated = true
    }

    /// Reads at most a copy's worth of the file at `url`, so a huge file is never loaded whole.
    public static func read(childId: String, kind: Kind, revision: Int, from url: URL) throws -> DelegationCopy {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let total = try handle.seekToEnd()
        try handle.seek(toOffset: 0)
        let head = try handle.read(upToCount: DelegationFileStore.maximumCopyBytes + 1) ?? Data()
        return DelegationCopy(childId: childId, kind: kind, revision: revision, contents: head, totalBytes: Int(clamping: total))
    }
}

/// Everything the delegation side file holds. It is the authority for each
/// child's parent link and working folder; receipts travel inside their
/// notice or follow-up.
public struct DelegationFile: Codable, Sendable, Equatable {
    public static let currentVersion = 1

    public var version: Int
    public var children: [ChildRecord]
    public var notices: [Notice]
    public var followUps: [FollowUp]
    public var merges: [MergeRecord]
    public var copies: [DelegationCopy]

    public init(children: [ChildRecord] = [], notices: [Notice] = [], followUps: [FollowUp] = [], merges: [MergeRecord] = [], copies: [DelegationCopy] = []) {
        version = Self.currentVersion
        self.children = children; self.notices = notices; self.followUps = followUps; self.merges = merges; self.copies = copies
    }

    /// The stored copy of one child's TASK.md or REPORT.md.
    public func copy(childId: String, kind: DelegationCopy.Kind) -> DelegationCopy? { copies.first { $0.childId == childId && $0.kind == kind } }

    /// Stores `copy` in place of the child's earlier copy of the same kind.
    public mutating func setCopy(_ copy: DelegationCopy) {
        copies.removeAll { $0.childId == copy.childId && $0.kind == copy.kind }
        copies.append(copy)
    }
}

public enum DelegationFileError: Error, Sendable, Equatable {
    /// The file would pass ``DelegationFileStore/maximumFileBytes`` (the
    /// store_full refusal); nothing was written.
    case full
    /// The file on disk is not a regular file, is over the cap, or is not a
    /// delegation file of this version. It is left untouched.
    case unreadable
}

/// Reads and writes `delegation-state.json` beside `workspace-state.json` in
/// the profile folder. A save replaces the file atomically and is refused,
/// writing nothing, when the file would pass 4 MiB. A missing file loads as empty.
public struct DelegationFileStore: Sendable {
    public static let fileName = "delegation-state.json"
    public static let maximumFileBytes = 4 * 1024 * 1024
    public static let maximumCopyBytes = 64 * 1024

    public let directory: URL
    public var fileURL: URL { directory.appendingPathComponent(Self.fileName) }

    public init(directory: URL) { self.directory = directory }

    /// The exact bytes a save writes, for measuring what still fits.
    public static func encode(_ file: DelegationFile) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(file)
    }

    public func load() throws -> DelegationFile {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return DelegationFile() }
        let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard values.isRegularFile == true, let size = values.fileSize, size <= Self.maximumFileBytes else { throw DelegationFileError.unreadable }
        let data = try Data(contentsOf: fileURL)
        guard data.count <= Self.maximumFileBytes, let file = try? JSONDecoder().decode(DelegationFile.self, from: data), file.version == DelegationFile.currentVersion else { throw DelegationFileError.unreadable }
        return file
    }

    public func save(_ file: DelegationFile) throws {
        let data = try Self.encode(file)
        guard data.count <= Self.maximumFileBytes else { throw DelegationFileError.full }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try data.write(to: fileURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
    }
}
