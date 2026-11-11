import Foundation

/// The last list each CLI gave, by provider and workspace folder, so a read that fails later (offline, signed out, a
/// CLI that changed its flags), or fails at the next launch, shows that list marked as kept instead of the built-in
/// one. `ProviderService` keeps it in memory and, with a storage folder, in `model-lists.json` there: bounded, written
/// when a list changes (or its time is `rewriteAge` old), and read tolerantly (a bad file or entry is left out, never
/// an error). Hermes is not here: its model list is already the picker its last session left (`HermesAgent`).
public struct KeptModelLists: Sendable, Equatable {
    public struct Entry: Codable, Sendable, Equatable {
        public var provider: String
        /// The workspace folder the list was read in; nil for a read outside any (the app's runtime summary).
        public var workspace: String?
        /// When the CLI gave it (ISO 8601).
        public var listedAt: String
        public var detail: String
        public var models: [ModelOption]
        public init(provider: String, workspace: String?, listedAt: String, detail: String, models: [ModelOption]) {
            self.provider = provider; self.workspace = workspace; self.listedAt = listedAt; self.detail = detail; self.models = models
        }
    }

    public static let fileName = "model-lists.json"
    public static let maximumBytes = 2 * 1_048_576
    public static let maximumEntries = 24
    /// The most rows a normalizer keeps (opencode: its default and 1 024 models).
    public static let maximumModels = 1_025
    /// A list read again unchanged is written again only once its saved time is this old.
    public static let rewriteAge: TimeInterval = 300

    public private(set) var entries: [Entry]
    public init(entries: [Entry] = []) { self.entries = entries }

    public static func url(in directory: URL?) -> URL? { directory?.appendingPathComponent(fileName) }

    /// The file's lists; a missing, oversized or unreadable file gives none, and an entry that does not check out is left out.
    public static func load(from url: URL?) -> KeptModelLists {
        guard let url, let values = try? url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]), values.isRegularFile == true,
              (values.fileSize ?? .max) <= maximumBytes, let data = try? Data(contentsOf: url), data.count <= maximumBytes,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], object["version"] as? Int == 1,
              let rows = object["lists"] as? [Any] else { return KeptModelLists() }
        var lists = KeptModelLists()
        for row in rows.prefix(maximumEntries * 4) {
            guard row is [String: Any], JSONSerialization.isValidJSONObject(row), let json = try? JSONSerialization.data(withJSONObject: row),
                  let entry = try? JSONDecoder().decode(Entry.self, from: json),
                  let valid = validated(entry), !lists.entries.contains(where: { $0.provider == valid.provider && $0.workspace == valid.workspace }) else { continue }
            lists.entries.append(valid)
        }
        // Newest first, as `remember` keeps them.
        lists.entries = lists.entries.map { ($0, date($0.listedAt) ?? .distantPast) }.sorted { $0.1 > $1.1 }.map(\.0)
        lists.trim()
        return lists
    }

    static func validated(_ entry: Entry) -> Entry? {
        guard ProviderOptions.ids.contains(entry.provider), entry.provider != "hermes", date(entry.listedAt) != nil,
              entry.workspace.map({ $0.hasPrefix("/") && $0.count <= 4_096 && !$0.contains("\0") }) ?? true else { return nil }
        var seen = Set<String>()
        let models = entry.models.prefix(maximumModels).filter { CoreValidation.model($0.value) && seen.insert($0.value).inserted }.map { model -> ModelOption in
            var bounded = model
            bounded.displayName = String(model.displayName.prefix(160)); bounded.description = String(model.description.prefix(2_400))
            bounded.resolvedModel = model.resolvedModel.flatMap { CoreValidation.model($0) ? $0 : nil }
            bounded.supportedEffortLevels = model.supportedEffortLevels.map { Array($0.prefix(16)) }
            return bounded
        }
        guard !models.isEmpty else { return nil }
        return Entry(provider: entry.provider, workspace: entry.workspace, listedAt: entry.listedAt, detail: String(entry.detail.prefix(2_000)), models: models)
    }

    public func encoded() -> Data? {
        var lists = self
        while true {
            guard let rows = try? JSONEncoder().encode(lists.entries), let array = try? JSONSerialization.jsonObject(with: rows),
                  let data = try? JSONSerialization.data(withJSONObject: ["version": 1, "lists": array], options: [.sortedKeys]) else { return nil }
            if data.count <= Self.maximumBytes || lists.entries.isEmpty { return data }
            lists.entries.removeLast()
        }
    }

    /// The list kept for this workspace, else the newest one this provider gave anywhere.
    public func list(provider: String, workspace: String?) -> Entry? {
        entries.first { $0.provider == provider && $0.workspace == workspace } ?? entries.first { $0.provider == provider }
    }

    /// Keeps `catalog` (a list the CLI just gave) as this provider and workspace's list, first as the newest read;
    /// true when the file should be written.
    @discardableResult
    public mutating func remember(_ catalog: ModelCatalog, provider: String, workspace: String?, at date: Date, writtenAt: Date?) -> Bool {
        guard catalog.isFresh, let entry = Self.validated(Entry(provider: provider, workspace: workspace, listedAt: Self.timestamp(date), detail: catalog.detail, models: catalog.models)) else { return false }
        let old = entries.firstIndex { $0.provider == provider && $0.workspace == workspace }
        let changed = old.map { entries[$0].models != entry.models || entries[$0].detail != entry.detail } ?? true
        if let old { entries.remove(at: old) }
        entries.insert(entry, at: 0)
        trim()
        return changed || writtenAt.map { date.timeIntervalSince($0) >= Self.rewriteAge } ?? true
    }

    /// At most `maximumEntries`: the oldest reads go.
    private mutating func trim() {
        if entries.count > Self.maximumEntries { entries.removeLast(entries.count - Self.maximumEntries) }
    }

    /// `entry`'s models as the list a read that failed for `reason` answers with.
    public static func kept(_ entry: Entry, reason: String) -> ModelCatalog {
        ModelCatalog(source: "cli", models: entry.models, detail: entry.detail, readFailure: reason, listedAt: entry.listedAt)
    }

    public static func timestamp(_ date: Date) -> String { ISO8601DateFormatter().string(from: date) }
    public static func date(_ text: String) -> Date? { ISO8601DateFormatter().date(from: text) }
}

/// The muted line under a model menu's refresh button, and the failure its help shows.
public struct ModelListNotice: Sendable, Equatable {
    public var line: String
    public var help: String
    public init(line: String, help: String) { self.line = line; self.help = help }
}

extension ModelCatalog {
    /// What the model menu says about this list: the built-in list after a read that failed, or the last list the
    /// CLI gave when a newer read failed. A list the CLI just gave, or one nothing has read yet, needs no line.
    public func notice(now: Date = Date()) -> ModelListNotice? {
        guard let failure = readFailure, !failure.isEmpty else { return nil }
        guard source == "cli" else { return ModelListNotice(line: L("composer.model.listFallback"), help: failure) }
        let age = listedAt.flatMap(KeptModelLists.date).map { ResumableSessions.relativeTime($0, now: now) } ?? "—"
        return ModelListNotice(line: L("composer.model.listKept", ["age": age]), help: failure)
    }
}
