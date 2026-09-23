import Foundation

public actor StateRepository {
    public static let maximumStateBytes = 8 * 1024 * 1024
    private let directory: URL
    private let legacyStateURL: URL?
    private var loaded = false
    private var snapshot = AppSnapshot()
    private var approved: [String: Workspace] = [:]

    public nonisolated static func defaultLegacyStateURL() -> URL? {
        guard let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else { return nil }
        return ["MightyClaude", "mighty-claude"].map { support.appendingPathComponent($0).appendingPathComponent("workspace-state.json") }.first { FileManager.default.fileExists(atPath: $0.path) }
    }

    public init(directory: URL, legacyStateURL: URL? = StateRepository.defaultLegacyStateURL()) {
        self.directory = directory; self.legacyStateURL = legacyStateURL
    }
    private var stateURL: URL { directory.appendingPathComponent("workspace-state.json") }

    private func ensureLoaded() throws {
        guard !loaded else { return }
        let exists = FileManager.default.fileExists(atPath: stateURL.path)
        let source = exists ? stateURL : legacyStateURL.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
        var restored = AppSnapshot()
        if let source {
            do {
                let values = try source.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
                guard values.isRegularFile == true, let size = values.fileSize, size <= Self.maximumStateBytes else { throw MightyError("저장된 상태 파일의 크기나 형식이 올바르지 않습니다.") }
                let data = try Data(contentsOf: source)
                guard data.count <= Self.maximumStateBytes, let object = try JSONSerialization.jsonObject(with: data) as? [String: Any], object["version"] as? Int == 1, object["workspaces"] is [Any], object["sessions"] is [Any] else { throw MightyError("저장된 상태 파일을 읽지 못했습니다. 원본을 보존했습니다.") }
                restored = Self.decodeSnapshot(data, restoring: true)
            } catch { throw MightyError("저장된 상태를 불러오지 못했습니다. 원본 파일은 변경하지 않았습니다. \(error.localizedDescription)") }
        }
        // Copy into the native profile only. The Electron profile remains intact.
        if !exists, source != nil { try persist(restored) }
        snapshot = restored
        for workspace in snapshot.workspaces { approved[workspace.id] = workspace }
        loaded = true
    }

    public func load() throws -> AppSnapshot { try ensureLoaded(); return snapshot }

    public func approveWorkspace(_ workspace: Workspace) throws -> Workspace {
        try ensureLoaded()
        guard workspace.remote == nil, CoreValidation.identifier(workspace.id), workspace.path.hasPrefix("/"), !workspace.path.contains("\0"), workspace.path.count <= 4096 else { throw MightyError("로컬 폴더 정보가 올바르지 않습니다.") }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: workspace.path, isDirectory: &isDirectory), isDirectory.boolValue else { throw MightyError("워크스페이스 폴더를 찾을 수 없습니다.") }
        var resolved = workspace
        resolved.path = URL(fileURLWithPath: workspace.path).resolvingSymlinksInPath().path
        if let existing = approved.values.first(where: { $0.remote == nil && $0.path == resolved.path }) { return existing }
        approved[resolved.id] = resolved
        return resolved
    }

    public func approveRemoteWorkspace(connectionId: String, workspace: Workspace, hostName: String) throws -> Workspace {
        try ensureLoaded()
        guard CoreValidation.identifier(connectionId), CoreValidation.identifier(workspace.id), workspace.remote == nil, Self.absolutePath(workspace.path, remote: true) else { throw MightyError("원격 워크스페이스 정보가 올바르지 않습니다.") }
        let existing = approved.values.first { $0.remote?.connectionId == connectionId && $0.remote?.workspaceId == workspace.id }
        let item = Workspace(id: existing?.id ?? UUID().uuidString, name: String(workspace.name.prefix(120)), path: workspace.path, createdAt: existing?.createdAt ?? mightyTimestamp(), remote: RemoteWorkspaceReference(connectionId: connectionId, workspaceId: workspace.id, hostName: String(hostName.prefix(120))))
        approved[item.id] = item
        return item
    }

    public func workspace(id: String) throws -> Workspace {
        try ensureLoaded()
        guard let workspace = approved[id] else { throw MightyError("등록된 워크스페이스를 찾을 수 없습니다.") }
        return workspace
    }
    public func resolveLocalWorkspace(id: String) throws -> Workspace {
        let item = try workspace(id: id)
        guard item.remote == nil else { throw MightyError("원격 폴더는 연결된 호스트에서 실행해야 합니다.") }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: item.path, isDirectory: &isDirectory), isDirectory.boolValue else { throw MightyError("워크스페이스 폴더를 찾을 수 없습니다.") }
        return item
    }

    public func save(_ value: AppSnapshot) throws {
        try ensureLoaded()
        guard value.version == 1, value.workspaces.count <= 64, value.sessions.count <= 128 else { throw MightyError("저장할 상태가 올바르지 않습니다.") }
        for workspace in value.workspaces {
            guard let known = approved[workspace.id], workspace.path == known.path, workspace.remote == known.remote else { throw MightyError("폴더 선택 또는 원격 가져오기로 승인한 워크스페이스만 저장할 수 있습니다.") }
        }
        let result = Self.normalize(value, restoring: false)
        try persist(result)
        snapshot = result
    }
    public func flush() throws { try ensureLoaded() }

    private func persist(_ value: AppSnapshot) throws {
        let data = try JSONEncoder().encode(value)
        guard data.count <= Self.maximumStateBytes else { throw MightyError("저장할 실행 기록이 너무 큽니다.") }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try data.write(to: stateURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: stateURL.path)
    }

    public nonisolated static func absolutePath(_ path: String, remote: Bool) -> Bool {
        guard path.count <= 4096, !path.contains("\0") else { return false }
        return path.hasPrefix("/") || (remote && (path.hasPrefix("\\\\") || path.range(of: "^[A-Za-z]:[\\\\/]", options: .regularExpression) != nil))
    }

    public nonisolated static func decodeSnapshot(_ data: Data, restoring: Bool = true) -> AppSnapshot {
        guard data.count <= maximumStateBytes, let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any], object["version"] as? Int == 1 else { return AppSnapshot() }
        let decoder = JSONDecoder()
        func decode<T: Decodable>(_ value: Any, _ type: T.Type) -> T? { guard JSONSerialization.isValidJSONObject(value), let data = try? JSONSerialization.data(withJSONObject: value) else { return nil }; return try? decoder.decode(type, from: data) }
        let workspaces = ((object["workspaces"] as? [Any]) ?? []).prefix(64).compactMap { decode($0, Workspace.self) }
        let sessions = ((object["sessions"] as? [Any]) ?? []).prefix(128).compactMap { decode($0, RunSession.self) }
        var paneLayouts: [String: PaneLayoutNode]?
        if let layouts = object["paneLayouts"] as? [String: Any] {
            paneLayouts = [:]
            for workspace in workspaces {
                if let value = layouts[workspace.id], let root = decode(value, PaneLayoutNode.self) { paneLayouts?[workspace.id] = root }
            }
        }
        func workspaceStrings(_ name: String) -> [String: String]? {
            guard let values = object[name] as? [String: Any] else { return nil }
            var result: [String: String] = [:]
            for workspace in workspaces { if let value = values[workspace.id] as? String { result[workspace.id] = value } }
            return result
        }
        // JSONSerialization bridges 0/1 through NSNumber; `as? Bool` would
        // silently enable this opt-in for a numeric value. Decode a JSON Bool.
        let autoUpdateCLIs: Bool? = object["autoUpdateCLIs"].flatMap { value in
            guard let bytes = try? JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed]) else { return nil }
            return try? decoder.decode(Bool.self, from: bytes)
        }
        let mobileRemote: MobileRemoteSettings? = (object["mobileRemote"] as? [String: Any]).flatMap { value in
            guard let bytes = try? JSONSerialization.data(withJSONObject: value) else { return nil }
            return try? decoder.decode(MobileRemoteSettings.self, from: bytes)
        }
        return normalize(AppSnapshot(workspaces: workspaces, sessions: sessions, activeWorkspaceId: object["activeWorkspaceId"] as? String, activeSessionId: object["activeSessionId"] as? String, layout: object["layout"] as? String ?? "grid", theme: object["theme"] as? String ?? "dark", sidebarWidth: object["sidebarWidth"] as? Double ?? 252, paneLayouts: paneLayouts, paneLayoutModes: workspaceStrings("paneLayoutModes"), paneLayoutActiveSessionIds: workspaceStrings("paneLayoutActiveSessionIds"), autoUpdateCLIs: autoUpdateCLIs, expandedWorkspaceIds: object["expandedWorkspaceIds"] as? [String], mobileRemote: mobileRemote), restoring: restoring)
    }

    public static let totalLogBudget = 4 * 1024 * 1024
    public static let totalGraphBudget = 2 * 1024 * 1024

    /// Splits `total` across sessions so that small histories keep everything
    /// and only the largest ones are trimmed: each session in ascending order
    /// of demand takes what it needs up to an equal share of what is left.
    /// Without this, one big session drained the whole budget and every
    /// session saved after it lost its history.
    public nonisolated static func fairShares(demands: [Int], total: Int) -> [Int] {
        var shares = Array(repeating: 0, count: demands.count)
        var remaining = max(0, total)
        let order = demands.indices.sorted { demands[$0] < demands[$1] }
        for (position, index) in order.enumerated() {
            let left = order.count - position
            let grant = min(max(0, demands[index]), remaining / left)
            shares[index] = grant; remaining -= grant
        }
        return shares
    }

    /// Demand estimates deliberately exceed what normalization charges: a
    /// session whose estimate fell short would be trimmed with budget to spare.
    static func approximateLogBytes(_ logs: [LogEntry]) -> Int {
        guard !logs.isEmpty else { return 0 }
        let bytes = logs.reduce(0) { $0 + $1.text.utf8.count + ($1.activity.map { $0.summary.utf8.count + ($0.toolName?.utf8.count ?? 0) + ($0.output?.utf8.count ?? 0) } ?? 0) + 256 }
        return bytes + bytes / 4 + 4096
    }
    static func approximateGraphBytes(_ runs: [MightyGraphRun]) -> Int {
        guard !runs.isEmpty else { return 0 }
        func entryBytes(_ values: [LogEntry]) -> Int { values.reduce(0) { $0 + 1024 + $1.text.utf8.count + ($1.activity?.output?.utf8.count ?? 0) + ($1.activity?.summary.utf8.count ?? 0) } }
        let bytes = runs.reduce(0) { total, run in
            total + 2048 + run.id.utf8.count * 2 + (run.sourceRunID?.utf8.count ?? 0) + run.input.utf8.count + (run.finalOutput?.utf8.count ?? 0) * 3 + entryBytes(run.rootEntries)
                + run.agents.reduce(0) { $0 + 1536 + $1.id.utf8.count * 2 + ($1.parentID?.utf8.count ?? 0) + $1.title.utf8.count + $1.input.utf8.count + entryBytes($1.entries) }
        }
        return bytes + bytes / 4 + 4096
    }

    /// `knownStyleIds` is the caller's own list; nil keeps the shape check
    /// only. The app never passes it — a workspace manifest is scanned after
    /// its panes are restored, so filtering by id would wipe them every launch
    /// (docs/mighty-styles.md §3.4).
    private nonisolated static func normalizedStyle(_ session: RunSession, knownStyleIds: Set<String>?) -> String? {
        guard session.kind == "claude", session.provider == "claude", let style = session.mightyStyle else { return nil }
        guard MightyStyleIDs.isValidShape(style) else { return nil }
        guard let knownStyleIds else { return style }
        return knownStyleIds.contains(style) ? style : nil
    }

    public nonisolated static func normalize(_ value: AppSnapshot, restoring: Bool, at date: Date = Date(),
                                             knownStyleIds: Set<String>? = nil) -> AppSnapshot {
        var output = AppSnapshot(); var workspaceIds = Set<String>(); var sessionIds = Set<String>()
        for var workspace in value.workspaces.prefix(64) {
            guard CoreValidation.identifier(workspace.id), !workspaceIds.contains(workspace.id), absolutePath(workspace.path, remote: workspace.remote != nil) else { continue }
            if let remote = workspace.remote, !CoreValidation.identifier(remote.connectionId) || !CoreValidation.identifier(remote.workspaceId) || remote.hostName.isEmpty { continue }
            workspace.name = String(workspace.name.prefix(120)); workspaceIds.insert(workspace.id); output.workspaces.append(workspace)
        }
        // Shares are computed over the sessions that will survive, so a
        // dropped or duplicate session cannot take budget from a real one.
        let candidates = value.sessions.prefix(128).filter { session in
            CoreValidation.identifier(session.id) && sessionIds.insert(session.id).inserted && workspaceIds.contains(session.workspaceId) && ["claude", "shell"].contains(session.kind)
        }
        let logShares = fairShares(demands: candidates.map { approximateLogBytes(TranscriptRetention.trimmed($0.logs)) }, total: totalLogBudget)
        let graphShares = fairShares(demands: candidates.map { approximateGraphBytes($0.graphRuns ?? []) }, total: totalGraphBudget)
        for (position, original) in candidates.enumerated() {
            var session = original
            var logBudget = logShares[position], graphBudget = graphShares[position]
            session.title = legacyNumberedTitle(String(session.title.prefix(120)))
            session.provider = ProviderOptions.normalizeProvider(session.provider); session.model = CoreValidation.model(session.model) ? session.model : "default"
            session.settings = ProviderOptions.normalizedSettings(provider: session.provider, settings: session.settings)
            if !["idle", "running", "completed", "error", "stopped"].contains(session.status) { session.status = "idle" }
            if restoring && session.status == "running" { session.status = "stopped" }
            if let id = session.resumeId, !CoreValidation.identifier(id) { session.resumeId = nil }
            session.agentViewMode = ["default", "mighty"].contains(session.agentViewMode ?? "") ? session.agentViewMode : nil
            session.mightyStyle = normalizedStyle(session, knownStyleIds: knownStyleIds)
            if session.mightyStyle == nil { session.mightyStyleHash = nil }
            session.graphRuns = session.graphRuns.map { MightyGraphSupport.normalized($0, restoring: restoring, budget: &graphBudget, provider: session.provider) }
            // A history the budget emptied is not "no history": drop the empty
            // array so the graph is rebuilt from the logs, as for old sessions.
            if let runs = session.graphRuns, runs.isEmpty, !session.logs.isEmpty { session.graphRuns = nil }
            session.logs = TranscriptRetention.trimmed(session.logs).compactMap { entry in
                guard CoreValidation.identifier(entry.id), ["user", "assistant", "system", "output", "error"].contains(entry.kind), logBudget > 0 else { return nil }
                var log = entry; log.text = ActivitySupport.prefixUTF8(log.text, maximumBytes: min(log.kind == "assistant" ? 131_072 : 32_768, logBudget)); logBudget -= log.text.utf8.count
                if let provider = log.provider, !ProviderOptions.ids.contains(provider) { log.provider = nil }
                if let activity = log.activity {
                    log.activity = ActivitySupport.normalized(activity, restoring: restoring)
                    if var clean = log.activity {
                        clean.output = clean.output.map { ActivitySupport.clean($0, maximumBytes: max(0, min(ActivitySupport.maximumOutputBytes, logBudget))) }
                        if clean.output?.isEmpty == true { clean.output = nil }
                        logBudget -= clean.summary.utf8.count + (clean.toolName?.utf8.count ?? 0) + (clean.output?.utf8.count ?? 0)
                        log.activity = clean
                    }
                }
                return log
            }
            session.graphBlockSizes = session.kind == "shell" ? nil : MightyGraphBlockSize.normalized(session.graphBlockSizes, runs: session.mightyGraphRuns)
            session.graphResultSize = session.kind == "shell" ? nil : session.graphResultSize?.normalized
            if session.kind == "shell" { session.runTiming = nil; session.sessionUsage = nil }
            else {
                session.sessionUsage = session.sessionUsage.flatMap { $0.provider == session.provider ? SessionUsageSupport.normalized($0) : nil }
                if session.runTiming?.isValid == false { session.runTiming = nil }
                if session.runTiming == nil { session.runTiming = AgentRunTiming.inferred(from: session.logs, running: !restoring && session.status == "running") }
                if restoring || session.status != "running" { session.runTiming?.interrupt() }
                else { session.runTiming?.observe(at: date) }
            }
            output.sessions.append(session)
        }
        output.activeWorkspaceId = value.activeWorkspaceId.flatMap { workspaceIds.contains($0) ? $0 : nil } ?? output.workspaces.first?.id
        output.activeSessionId = output.sessions.first { $0.id == value.activeSessionId && $0.workspaceId == output.activeWorkspaceId }?.id ?? output.sessions.first { $0.workspaceId == output.activeWorkspaceId }?.id
        output.layout = PaneLayouts.viewModes.contains(value.layout) ? value.layout : "grid"; output.theme = value.theme == "light" ? "light" : "dark"
        if let layouts = value.paneLayouts {
            output.paneLayouts = [:]
            for workspace in output.workspaces {
                guard let root = layouts[workspace.id] else { continue }
                let ids = output.sessions.filter { $0.workspaceId == workspace.id }.map(\.id)
                let active = workspace.id == output.activeWorkspaceId ? output.activeSessionId : value.paneLayoutActiveSessionIds?[workspace.id]
                if let normalized = PaneLayouts.normalized(root: root, sessionIds: ids, activeId: active) { output.paneLayouts?[workspace.id] = normalized }
            }
        }
        output.paneLayoutModes = PaneLayouts.workspaceModes(workspaceIds: output.workspaces.map(\.id), activeWorkspaceId: output.activeWorkspaceId, legacyMode: output.layout, layouts: output.paneLayouts, savedModes: value.paneLayoutModes)
        output.paneLayoutActiveSessionIds = [:]
        for workspace in output.workspaces {
            let ids = output.sessions.filter { $0.workspaceId == workspace.id }.map(\.id)
            let preferred = workspace.id == output.activeWorkspaceId ? output.activeSessionId : value.paneLayoutActiveSessionIds?[workspace.id]
            if let selected = preferred.flatMap({ ids.contains($0) ? $0 : nil }) ?? PaneLayouts.firstSelectedSession(in: output.paneLayouts?[workspace.id]) ?? ids.first {
                output.paneLayoutActiveSessionIds?[workspace.id] = selected
            }
        }
        output.sidebarWidth = value.sidebarWidth.isFinite ? min(400, max(200, value.sidebarWidth)) : 252
        output.autoUpdateCLIs = value.autoUpdateCLIs
        output.expandedWorkspaceIds = value.expandedWorkspaceIds.map { Array(Set($0).intersection(workspaceIds)).sorted() }
        output.mobileRemote = value.mobileRemote?.normalized
        return output
    }

    /// Panes used to be named "Claude 1", "터미널 2". New panes carry the bare
    /// name, and saved auto-generated names are folded the same way. A title the
    /// user typed is left alone unless it exactly matches that generated form.
    public nonisolated static func legacyNumberedTitle(_ title: String) -> String {
        let bases = ["Claude", "Codex", "Gemini", "터미널", "원격 명령"]
        for base in bases where title.hasPrefix(base + " ") {
            let suffix = title.dropFirst(base.count + 1)
            if !suffix.isEmpty, suffix.allSatisfy({ $0.isASCII && $0.isNumber }) { return base }
        }
        return title
    }
}
