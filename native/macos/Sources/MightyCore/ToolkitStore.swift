import CryptoKit
import Foundation

// MARK: - Approval

/// Approval bound to the entry's canonical content hash.  For repoScript the
/// resolved commit SHA (always 40-hex) is stored alongside the content hash.
public struct ToolkitApproval: Sendable, Equatable {
    public let contentHash: String
    public let resolvedCommit: String?

    public init(contentHash: String, resolvedCommit: String? = nil) {
        self.contentHash = contentHash
        self.resolvedCommit = resolvedCommit
    }
}

// MARK: - Executor

/// Injected into approve() so tests never hit the network or the real CLIs.
public protocol ToolkitCommandExecutor: Sendable {
    func run(_ argv: [String]) throws -> String
}

// MARK: - Error

public struct ToolkitStoreError: Error, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
}

// MARK: - ToolkitStore

/// Manages the user's toolkit list next to workspace-state.json.
///
/// `list()` always returns bundled entries; if toolkit.json exists but is
/// unreadable or structurally invalid the error is surfaced through the second
/// return value and the file is never modified.
public actor ToolkitStore {
    private let fileURL: URL

    // Internal state set once on first access.
    private var loaded = false
    private var userEntries: [ToolkitEntry] = []
    private var approvals: [String: ToolkitApproval] = [:]
    private var fileError: (any Error)?
    // Raw entry dicts (including unknown keys like a stale `platforms`) for
    // entries that were loaded from disk and have not been mutated this session.
    private var rawEntryDicts: [String: [String: Any]] = [:]
    // IDs of entries added, removed, edited, approved or imported this session.
    // Those entries are written in canonical form; untouched entries use rawEntryDicts.
    private var touchedEntryIds: Set<String> = []

    public init(directory: URL) {
        self.fileURL = directory.appendingPathComponent("toolkit.json")
    }

    // MARK: - List

    /// Bundled entries are always first.  If toolkit.json is invalid, user
    /// entries are empty and the error is non-nil; the file is left untouched.
    /// Other-OS entries (e.g. winget on macOS) are kept in storage but omitted
    /// from the returned list.
    public func list() -> (entries: [ToolkitEntry], error: (any Error)?) {
        doLoad()
        let visible = userEntries.filter { $0.install.platforms.contains(.macOS) }
        return (ToolkitBundled.entries + visible, fileError)
    }

    // MARK: - Approval

    /// Returns the stored approval if and only if it still matches the entry's
    /// current canonical hash.  Returns nil for bundled entries and any entry
    /// whose content changed since approval.
    public func approval(for entry: ToolkitEntry) -> ToolkitApproval? {
        doLoad()
        guard entry.source == .user, let stored = approvals[entry.entryId] else { return nil }
        return stored.contentHash == Self.canonicalHash(entry) ? stored : nil
    }

    /// Approves a user entry.  For a repoScript with a tag ref the executor
    /// resolves it to a full 40-hex commit SHA via `git ls-remote`.
    public func approve(entryId: String, executor: any ToolkitCommandExecutor) throws {
        try requireLoaded()
        guard let entry = userEntries.first(where: { $0.entryId == entryId }) else {
            throw ToolkitStoreError("Entry not found: \(entryId)")
        }
        let hash = Self.canonicalHash(entry)
        var resolvedCommit: String? = nil
        if case .repoScript(let url, let ref, _) = entry.install {
            if Self.is40HexSHA(ref) {
                resolvedCommit = ref
            } else {
                let output = try executor.run(["git", "ls-remote", url, ref])
                resolvedCommit = try Self.parseSHA(from: output, tag: ref)
            }
        }
        approvals[entryId] = ToolkitApproval(contentHash: hash, resolvedCommit: resolvedCommit)
        rawEntryDicts.removeValue(forKey: entryId)
        touchedEntryIds.insert(entryId)
        try persist()
    }

    // MARK: - Mutate

    /// Adds or replaces a user entry.  The new entry is always unapproved.
    public func addEntry(_ entry: ToolkitEntry) throws {
        try requireLoaded()
        userEntries.removeAll { $0.entryId == entry.entryId }
        approvals.removeValue(forKey: entry.entryId)
        rawEntryDicts.removeValue(forKey: entry.entryId)
        touchedEntryIds.insert(entry.entryId)
        var e = entry; e.source = .user
        userEntries.append(e)
        try persist()
    }

    /// Removes a user entry.  No command is executed and no file other than
    /// toolkit.json is touched.
    public func removeEntry(id: String) throws {
        try requireLoaded()
        userEntries.removeAll { $0.entryId == id }
        approvals.removeValue(forKey: id)
        rawEntryDicts.removeValue(forKey: id)
        touchedEntryIds.insert(id)
        try persist()
    }

    // MARK: - Export / Import

    /// Serialises user entries as a JSON array with no approval data.
    public func exportData() throws -> Data {
        try requireLoaded()
        let objects = userEntries.map { Self.entryToObject($0) }
        return try JSONSerialization.data(withJSONObject: objects, options: [.sortedKeys])
    }

    /// Adds every entry from a JSON array as unapproved.  An existing id is
    /// replaced and becomes unapproved.
    public func importData(_ data: Data) throws {
        try requireLoaded()
        guard let array = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] else {
            throw ToolkitStoreError("Import data must be a JSON array")
        }
        for rawObject in array {
            var stripped = rawObject
            stripped.removeValue(forKey: "approval")
            let entry = try ToolkitEntryDecoder.decode(stripped)
            userEntries.removeAll { $0.entryId == entry.entryId }
            approvals.removeValue(forKey: entry.entryId)
            rawEntryDicts.removeValue(forKey: entry.entryId)
            touchedEntryIds.insert(entry.entryId)
            userEntries.append(entry)
        }
        try persist()
    }

    // MARK: - Internal

    private func doLoad() {
        guard !loaded else { return }
        loaded = true
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return }
        guard let data = try? Data(contentsOf: fileURL),
              let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              object["version"] as? Int == 1 else {
            fileError = ToolkitStoreError("toolkit.json을 읽을 수 없습니다. 원본 파일은 변경하지 않았습니다.")
            return
        }
        for rawEntry in (object["entries"] as? [[String: Any]]) ?? [] {
            // Store the complete raw dict (including unknown keys like a stale
            // `platforms` and the existing approval) before any stripping.
            if let entryId = rawEntry["id"] as? String, !entryId.isEmpty {
                rawEntryDicts[entryId] = rawEntry
            }
            var raw = rawEntry
            let rawApproval = raw.removeValue(forKey: "approval") as? [String: Any]
            guard let entry = try? ToolkitEntryDecoder.decode(raw) else { continue }
            userEntries.append(entry)
            if let a = rawApproval, let hash = a["contentHash"] as? String {
                approvals[entry.entryId] = ToolkitApproval(
                    contentHash: hash, resolvedCommit: a["resolvedCommit"] as? String)
            }
        }
    }

    private func requireLoaded() throws {
        doLoad()
        if let err = fileError { throw err }
    }

    private func persist() throws {
        var objects: [[String: Any]] = []
        for entry in userEntries {
            if !touchedEntryIds.contains(entry.entryId),
               let rawDict = rawEntryDicts[entry.entryId] {
                // Untouched entry: emit original dict verbatim (preserves unknown
                // keys such as a stale `platforms` and the stored approval).
                objects.append(rawDict)
            } else {
                // Touched entry: emit canonical form with current approval.
                var obj = Self.entryToObject(entry)
                if let approval = approvals[entry.entryId] {
                    var a: [String: Any] = ["contentHash": approval.contentHash]
                    if let sha = approval.resolvedCommit { a["resolvedCommit"] = sha }
                    obj["approval"] = a
                }
                objects.append(obj)
            }
        }
        let fileObject: [String: Any] = ["version": 1, "entries": objects]
        // Canonical file form: sorted keys, 2-space indentation, LF, trailing newline.
        var payload = try JSONSerialization.data(
            withJSONObject: fileObject, options: [.sortedKeys, .prettyPrinted])
        if payload.last != UInt8(ascii: "\n") { payload.append(UInt8(ascii: "\n")) }
        let dir = fileURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let tmp = dir.appendingPathComponent("toolkit-" + UUID().uuidString + ".tmp")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try payload.write(to: tmp, options: .atomic)
        _ = try FileManager.default.replaceItemAt(fileURL, withItemAt: tmp)
    }

    // MARK: - Static helpers (nonisolated)

    nonisolated static func canonicalHash(_ entry: ToolkitEntry) -> String {
        let obj = entryToObject(entry)
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]) else { return "" }
        return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    /// The exact UTF-8 bytes that are fed into the SHA-256 hash: sorted keys,
    /// no insignificant whitespace, approval and unknown keys excluded.
    /// Matches BuildCanonicalJson on Windows.
    nonisolated public static func canonicalJson(_ entry: ToolkitEntry) -> String {
        let obj = entryToObject(entry)
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]),
              let json = String(data: data, encoding: .utf8) else { return "" }
        return json
    }

    nonisolated static func entryToObject(_ entry: ToolkitEntry) -> [String: Any] {
        ["id": entry.entryId, "displayName": entry.displayName,
         "install": installToObject(entry.install)]
    }

    private nonisolated static func installToObject(_ spec: ToolkitInstallSpec) -> [String: Any] {
        switch spec {
        case .plugin(let source, let pluginID):
            return ["kind": "plugin", "source": source, "pluginID": pluginID]
        case .mcp(let name, let executable, let args):
            return ["kind": "mcp", "name": name, "executable": executable, "args": args]
        case .skill(let url):
            return ["kind": "skill", "url": url]
        case .package(let manager, let name, let executable):
            var obj: [String: Any] = ["kind": "package", "manager": manager.rawValue, "name": name]
            if let exe = executable { obj["executable"] = exe }
            return obj
        case .repoScript(let url, let ref, let scriptPath):
            return ["kind": "repoScript", "url": url, "ref": ref, "scriptPath": scriptPath]
        }
    }

    nonisolated static func is40HexSHA(_ s: String) -> Bool {
        s.count == 40 && s.unicodeScalars.allSatisfy {
            ($0.value >= 0x30 && $0.value <= 0x39) ||
            ($0.value >= 0x61 && $0.value <= 0x66)
        }
    }

    private nonisolated static func parseSHA(from output: String, tag: String) throws -> String {
        for line in output.split(separator: "\n") {
            let sha = String(line.split(separator: "\t").first ?? Substring()).trimmingCharacters(in: .whitespaces)
            if is40HexSHA(sha) { return sha }
        }
        throw ToolkitStoreError("tag '\(tag)'를 커밋 SHA로 해석할 수 없습니다.")
    }
}
