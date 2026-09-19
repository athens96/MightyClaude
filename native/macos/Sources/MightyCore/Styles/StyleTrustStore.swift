import Foundation

public enum StyleApprovalState: String, Sendable, Equatable { case preApproved, pending, approved, revoked }

/// One decision, bound to the bytes when it approves and to the place when it
/// refuses (§4.2). `workspacePath` is absent for a user-registered style.
public struct StyleApprovalRecord: Codable, Sendable, Equatable {
    public var styleId: String
    public var source: StyleSource
    public var path: String
    public var workspacePath: String?
    public var hash: String
    public var state: String
    public var decidedAt: Date
    public init(styleId: String, source: StyleSource, path: String, workspacePath: String? = nil, hash: String, state: String, decidedAt: Date) {
        self.styleId = styleId; self.source = source; self.path = path; self.workspacePath = workspacePath
        self.hash = hash; self.state = state; self.decidedAt = decidedAt
    }
    var location: StyleApprovalLocation { StyleApprovalLocation(source: source, path: path, workspacePath: workspacePath) }
}

struct StyleApprovalLocation: Hashable, Sendable {
    var source: StyleSource
    var path: String
    var workspacePath: String?
}

public enum StyleTrustFailure: Error, Sendable, Equatable {
    case locked(path: String)
    case full
}

struct StyleApprovalFile: Codable {
    var version: Int
    var records: [StyleApprovalRecord]
}

/// `<데이터 폴더>/style-trust/approvals.json`, deliberately outside the folder
/// the scanner reads: a manifest called `approvals` must not be able to
/// overwrite the trust store (§4.3).
public actor StyleTrustStore {
    private let directory: URL
    private let file: URL
    private var locked = false
    private var loaded = false
    private var records: [StyleApprovalRecord] = []
    private var stamp: String?

    public init(directory: URL) {
        self.directory = directory
        self.file = directory.appendingPathComponent("approvals.json")
    }

    public var isLocked: Bool {
        _ = try? readIfNeeded()
        return locked
    }

    public var path: String { file.path }

    public func load() throws -> [StyleApprovalRecord] { try readIfNeeded() }

    @discardableResult
    private func readIfNeeded() throws -> [StyleApprovalRecord] {
        if locked { throw StyleTrustFailure.locked(path: file.path) }
        // A second run of the same profile writes this file behind our back, so
        // "already loaded" is only good while the stamp still matches (§4.3).
        if loaded, stamp == Self.stamp(file) { return records }
        loaded = true
        guard FileManager.default.fileExists(atPath: file.path) else { records = []; stamp = nil; return [] }
        // The file is the only one whose contents are themselves the authority,
        // so a foreign owner or a group-writable bit closes the store (§4.3).
        guard Self.isOwnedAndPrivate(directory), Self.isOwnedAndPrivate(file) else { return try lock() }
        guard let data = CLIAccountSupport.boundedData(file, maximumBytes: 4 * 1024 * 1024) else { return try lock() }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        guard let parsed = try? decoder.decode(StyleApprovalFile.self, from: data), parsed.version == 1 else { return try lock() }
        records = parsed.records
        stamp = Self.stamp(file)
        return records
    }

    @discardableResult
    private func lock() throws -> [StyleApprovalRecord] {
        locked = true
        records = []
        throw StyleTrustFailure.locked(path: file.path)
    }

    public func approve(_ style: RegisteredStyle) throws {
        guard style.source != .bundled else { return }
        try mutate { records in
            let location = StyleApprovalLocation(source: style.source, path: style.path, workspacePath: style.workspacePath)
            // One approval per place: old bytes that come back are asked again.
            records.removeAll { $0.location == location && $0.state == StyleApprovalState.approved.rawValue }
            try Self.makeRoom(&records)
            records.append(StyleApprovalRecord(styleId: style.id, source: style.source, path: style.path,
                                               workspacePath: style.workspacePath, hash: style.hash,
                                               state: StyleApprovalState.approved.rawValue, decidedAt: Date()))
        }
    }

    public func revoke(_ style: RegisteredStyle) throws {
        guard style.source != .bundled else { return }
        try mutate { records in
            let location = StyleApprovalLocation(source: style.source, path: style.path, workspacePath: style.workspacePath)
            records.removeAll { $0.location == location }
            try Self.makeRoom(&records)
            records.append(StyleApprovalRecord(styleId: style.id, source: style.source, path: style.path,
                                               workspacePath: style.workspacePath, hash: style.hash,
                                               state: StyleApprovalState.revoked.rawValue, decidedAt: Date()))
        }
    }

    public func allowAgain(styleId: String, path: String, workspacePath: String?) throws {
        try mutate { records in
            records.removeAll { $0.styleId == styleId && $0.path == path && $0.workspacePath == workspacePath && $0.state == StyleApprovalState.revoked.rawValue }
        }
    }

    public func forget(styleId: String, path: String) throws {
        try mutate { records in records.removeAll { $0.styleId == styleId && $0.path == path } }
    }

    /// Dropping a refusal would turn itself into a permission, so only
    /// approvals are evicted and a store of refusals fails instead (§4.3).
    private static func makeRoom(_ records: inout [StyleApprovalRecord]) throws {
        while records.count >= StyleLimits.maximumApprovalRecords {
            let approvals = records.enumerated().filter { $0.element.state == StyleApprovalState.approved.rawValue }
            guard let oldest = approvals.min(by: { $0.element.decidedAt < $1.element.decidedAt }) else { throw StyleTrustFailure.full }
            records.remove(at: oldest.offset)
        }
    }

    private func mutate(_ body: (inout [StyleApprovalRecord]) throws -> Void) throws {
        var current = try readIfNeeded()
        // An actor only serialises one process; a second run of the same
        // profile must not wipe what this one wrote (§4.3). If that second
        // run left the file unreadable, closing is the answer — overwriting it
        // would erase every refusal it holds.
        if stamp != Self.stamp(file), FileManager.default.fileExists(atPath: file.path) {
            guard Self.isOwnedAndPrivate(directory), Self.isOwnedAndPrivate(file), let merged = try? Self.reread(file) else {
                try lock()
                return
            }
            current = Self.merge(current, merged)
        }
        try body(&current)
        try write(current)
    }

    private func write(_ values: [StyleApprovalRecord]) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601; encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(StyleApprovalFile(version: 1, records: values))
        let temporary = directory.appendingPathComponent("approvals-" + UUID().uuidString + ".tmp")
        try data.write(to: temporary, options: [.atomic])
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        _ = try FileManager.default.replaceItemAt(file, withItemAt: temporary)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        records = values
        stamp = Self.stamp(file)
    }

    private static func reread(_ file: URL) throws -> [StyleApprovalRecord] {
        guard let data = CLIAccountSupport.boundedData(file, maximumBytes: 4 * 1024 * 1024) else { throw StyleTrustFailure.locked(path: file.path) }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        let parsed = try decoder.decode(StyleApprovalFile.self, from: data)
        guard parsed.version == 1 else { throw StyleTrustFailure.locked(path: file.path) }
        return parsed.records
    }

    /// Same place, same state: the newer decision wins. An approval is keyed by
    /// the place alone, so a merge cannot leave two live approvals at one file
    /// — that is §4.3's "one per place", and without it a commit that reverts a
    /// manifest to already-approved bytes runs with no prompt. A refusal keeps
    /// its hash in the key so none of them is ever dropped (§4.2).
    static func merge(_ mine: [StyleApprovalRecord], _ theirs: [StyleApprovalRecord]) -> [StyleApprovalRecord] {
        var byKey: [String: StyleApprovalRecord] = [:]
        for record in theirs + mine {
            var parts = [record.source.rawValue, record.workspacePath ?? "", record.path, record.state]
            if record.state != StyleApprovalState.approved.rawValue { parts.append(record.hash) }
            let key = parts.joined(separator: "\u{1}")
            if let existing = byKey[key], existing.decidedAt >= record.decidedAt { continue }
            byKey[key] = record
        }
        return byKey.values.sorted { $0.decidedAt < $1.decidedAt }
    }

    private static func stamp(_ file: URL) -> String? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: file.path) else { return nil }
        let modified = (attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        let size = (attributes[.size] as? NSNumber)?.intValue ?? 0
        let inode = (attributes[.systemFileNumber] as? NSNumber)?.intValue ?? 0
        return "\(modified)|\(size)|\(inode)"
    }

    static func isOwnedAndPrivate(_ url: URL) -> Bool {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path) else { return false }
        let owner = (attributes[.ownerAccountID] as? NSNumber)?.uint32Value
        let permissions = (attributes[.posixPermissions] as? NSNumber)?.int32Value ?? 0o777
        return owner == getuid() && (permissions & 0o022) == 0
    }

    public nonisolated static func state(for file: DiscoveredStyleFile, in records: [StyleApprovalRecord]) -> StyleApprovalState {
        guard file.source != .bundled else { return .preApproved }
        let location = StyleApprovalLocation(source: file.source, path: file.url.path, workspacePath: file.workspacePath)
        let here = records.filter { $0.location == location }
        if here.contains(where: { $0.state == StyleApprovalState.revoked.rawValue }) { return .revoked }
        if here.contains(where: { $0.state == StyleApprovalState.approved.rawValue && $0.hash == file.hash }) { return .approved }
        return .pending
    }
}
