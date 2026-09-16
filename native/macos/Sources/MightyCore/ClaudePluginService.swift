import Foundation

private struct ClaudePluginConfiguration: Sendable {
    let environment: [String: String]
    let executable: URL?
    let readTimeout: TimeInterval
    let operationTimeout: TimeInterval
    let maximumBytes: Int
}

private struct ClaudePluginFailure: Error, Sendable {
    let status: String
    let detail: String
    var output: String = ""
}

private enum ClaudePluginTaskResult: Sendable {
    case snapshot(ClaudePluginSnapshot)
    case operation(ClaudePluginOperationResult)
}

/// Uses only installed Claude Code's plugin subcommands. Catalog reads never
/// add/update a marketplace; mutations require an explicit method invocation.
/// Official commands: https://code.claude.com/docs/en/plugins-reference
public actor ClaudePluginService {
    private let configuration: ClaudePluginConfiguration
    private var active: (id: UUID, task: Task<ClaudePluginTaskResult, Never>)?
    private var closing = false

    public init(environment: [String: String]? = nil) {
        configuration = Self.configuration(environment ?? ProviderService.runtimeEnvironment(), executable: nil,
                                           readTimeout: 20, operationTimeout: 180, maximumBytes: 8 * 1024 * 1024)
    }

    // Fake executables and deadlines are test-only; never accepted from the UI,
    // a remote peer, a catalog entry, or a plugin's manifest.
    init(environment: [String: String], executable: URL?, readTimeout: TimeInterval = 2,
         operationTimeout: TimeInterval = 5, maximumBytes: Int = 8 * 1024 * 1024) {
        configuration = Self.configuration(environment, executable: executable, readTimeout: readTimeout,
                                           operationTimeout: operationTimeout, maximumBytes: maximumBytes)
    }

    public func snapshot(workspace: Workspace) async -> ClaudePluginSnapshot {
        if workspace.remote != nil { return ClaudePluginSnapshot(status: "remote", detail: Self.remoteDetail) }
        guard !closing, !Task.isCancelled else { return ClaudePluginSnapshot(status: "cancelled", detail: "플러그인 조회를 취소했습니다.") }
        guard active == nil else { return ClaudePluginSnapshot(status: "busy", detail: "다른 플러그인 작업이 진행 중입니다.") }
        let configuration = configuration
        let task = Task<ClaudePluginTaskResult, Never> {
            do {
                let cwd = try Self.localDirectory(workspace)
                let command = try await Self.command(configuration, cwd: cwd)
                let value = try await Self.readSnapshot(configuration, command: command, cwd: cwd)
                return .snapshot(value)
            } catch {
                let failure = Self.failure(error)
                return .snapshot(ClaudePluginSnapshot(status: failure.status, detail: failure.detail, diagnosticOutput: failure.output))
            }
        }
        if case .snapshot(let value) = await finish(task) { return value }
        return ClaudePluginSnapshot(status: "failed", detail: "플러그인 목록을 읽지 못했습니다.")
    }

    public func install(pluginID: String, scope: String = "local", workspace: Workspace) async -> ClaudePluginOperationResult {
        await operation(workspace: workspace, pluginID: pluginID, scope: scope, marketplace: nil)
    }

    public func refreshMarketplace(name: String, workspace: Workspace) async -> ClaudePluginOperationResult {
        await operation(workspace: workspace, pluginID: nil, scope: nil, marketplace: name)
    }

    public func cancel() async {
        guard let operation = active else { return }
        operation.task.cancel(); _ = await operation.task.value
        if active?.id == operation.id { active = nil }
    }
    public func shutdown() async { closing = true; await cancel() }

    private func finish(_ task: Task<ClaudePluginTaskResult, Never>) async -> ClaudePluginTaskResult {
        let id = UUID(); active = (id, task)
        let result = await withTaskCancellationHandler(operation: { await task.value }, onCancel: { task.cancel() })
        if active?.id == id { active = nil }
        return result
    }

    private func operation(workspace: Workspace, pluginID: String?, scope: String?, marketplace: String?) async -> ClaudePluginOperationResult {
        if workspace.remote != nil { return ClaudePluginOperationResult(status: "remote", detail: Self.remoteDetail) }
        guard !closing, !Task.isCancelled else { return ClaudePluginOperationResult(status: "cancelled", detail: "플러그인 작업을 취소했습니다.") }
        guard active == nil else { return ClaudePluginOperationResult(status: "busy", detail: "다른 플러그인 작업이 진행 중입니다.") }
        if let pluginID {
            guard Self.pluginParts(pluginID) != nil, let scope, ["local", "project", "user"].contains(scope) else {
                return ClaudePluginOperationResult(status: "failed", detail: "플러그인 이름 또는 설치 범위가 올바르지 않습니다.")
            }
        } else if let marketplace {
            guard Self.identifier(marketplace) else { return ClaudePluginOperationResult(status: "failed", detail: "마켓플레이스 이름이 올바르지 않습니다.") }
        } else { return ClaudePluginOperationResult(status: "failed", detail: "플러그인 작업이 올바르지 않습니다.") }
        let configuration = configuration
        let task = Task<ClaudePluginTaskResult, Never> {
            do {
                let cwd = try Self.localDirectory(workspace)
                let command = try await Self.command(configuration, cwd: cwd)
                // Re-read the installed registry and cached catalog immediately
                // before a mutation. A removed/renamed source must not become an
                // implicit marketplace installation or an arbitrary CLI argument.
                let snapshot = try await Self.readSnapshot(configuration, command: command, cwd: cwd)
                try Task.checkCancellation()
                let arguments: [String]
                if let pluginID, let scope {
                    guard snapshot.available.contains(where: { $0.id == pluginID }),
                          let parts = Self.pluginParts(pluginID), snapshot.marketplaces.contains(where: { $0.name == parts.marketplace }) else {
                        throw ClaudePluginFailure(status: "failed", detail: "현재 등록된 마켓플레이스 목록에서 이 플러그인을 찾지 못했습니다. 목록을 다시 확인하세요.")
                    }
                    if snapshot.installed.contains(where: {
                        $0.pluginID == pluginID && $0.scope == scope &&
                        (scope == "user" || $0.projectPath.map { URL(fileURLWithPath: $0).resolvingSymlinksInPath().standardizedFileURL.path == cwd.path } == true)
                    }) {
                        return .operation(ClaudePluginOperationResult(status: "skipped", detail: "선택한 범위에 이미 설치되어 있습니다. 비활성 상태라면 Claude CLI에서 활성화하세요."))
                    }
                    // Do not pass --yes or --accept-command. Command sources and
                    // archive headersHelpers retain the CLI's explicit consent.
                    arguments = ["plugin", "install", pluginID, "--scope", scope, "--json"]
                } else if let marketplace {
                    guard snapshot.marketplaces.contains(where: { $0.name == marketplace }) else {
                        throw ClaudePluginFailure(status: "failed", detail: "등록되지 않은 마켓플레이스입니다. 기존 등록 목록에서 선택하세요.")
                    }
                    arguments = ["plugin", "marketplace", "update", marketplace]
                } else { throw ClaudePluginFailure(status: "failed", detail: "플러그인 작업이 올바르지 않습니다.") }
                let result = try await ProcessCapture.run(executable: command.executable, arguments: arguments,
                    environment: configuration.environment, cwd: cwd, timeout: configuration.operationTimeout, maximumBytes: 1024 * 1024)
                try Task.checkCancellation()
                let output = Self.output(result)
                if let pluginID, let scope {
                    // The CLI may print a marketplace-declared command before
                    // its result. Only the last nonempty stdout line is JSON.
                    guard let line = String(data: result.stdout, encoding: .utf8)?.split(whereSeparator: \.isNewline).last,
                          let json = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                          json["command"] as? String == "install", let outcome = json["outcome"] as? String,
                          ["ok", "failed"].contains(outcome) else {
                        throw ClaudePluginFailure(status: "failed", detail: "설치 결과를 확인하지 못했습니다. 목록을 다시 읽어 설치 상태를 확인하세요.", output: output)
                    }
                    if json["shownCommand"] != nil {
                        throw ClaudePluginFailure(status: "failed", detail: "이 플러그인은 추가 명령 실행 동의가 필요합니다. Claude CLI에서 표시된 명령을 확인한 뒤 설치하세요. 앱은 자동 승인하지 않습니다.", output: output)
                    }
                    guard result.exitCode == 0, outcome == "ok",
                          json["pluginId"] == nil || json["pluginId"] as? String == pluginID,
                          json["scope"] == nil || json["scope"] as? String == scope else {
                        throw ClaudePluginFailure(status: "failed", detail: "플러그인 설치에 실패했습니다. 네트워크·권한·조직 정책을 확인하세요.", output: output)
                    }
                    return .operation(ClaudePluginOperationResult(status: "succeeded", detail: "플러그인을 설치했습니다. 다음 Claude 실행부터 적용됩니다.", output: output))
                }
                guard result.exitCode == 0 else { throw ClaudePluginFailure(status: "failed", detail: "마켓플레이스 새로고침에 실패했습니다. 네트워크 상태와 접근 권한을 확인하세요.", output: output) }
                return .operation(ClaudePluginOperationResult(status: "succeeded", detail: "선택한 마켓플레이스 목록을 새로고침했습니다.", output: output))
            } catch {
                let failure = Self.failure(error)
                return .operation(ClaudePluginOperationResult(status: failure.status == "cancelled" ? "cancelled" : "failed", detail: failure.detail, output: failure.output))
            }
        }
        if case .operation(let value) = await finish(task) { return value }
        return ClaudePluginOperationResult(status: "failed", detail: "플러그인 작업을 완료하지 못했습니다.")
    }

    private static let remoteDetail = "원격 워크스페이스의 플러그인은 해당 호스트에서 관리하세요. 이 Mac의 설치는 변경하지 않습니다."

    private static func configuration(_ supplied: [String: String], executable: URL?, readTimeout: TimeInterval,
                                      operationTimeout: TimeInterval, maximumBytes: Int) -> ClaudePluginConfiguration {
        var environment = supplied
        environment.merge(["DISABLE_AUTOUPDATER": "1", "DISABLE_TELEMETRY": "1", "DISABLE_ERROR_REPORTING": "1",
            "CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL": "1", "CLAUDE_CODE_DISABLE_BACKGROUND_TASKS": "1",
            "CLAUDE_CODE_SKIP_PROMPT_HISTORY": "1", "GIT_TERMINAL_PROMPT": "0"]) { _, value in value }
        environment.removeValue(forKey: "FORCE_AUTOUPDATE_PLUGINS")
        return ClaudePluginConfiguration(environment: environment, executable: executable, readTimeout: readTimeout,
                                         operationTimeout: operationTimeout, maximumBytes: maximumBytes)
    }

    private static func localDirectory(_ workspace: Workspace) throws -> URL {
        guard workspace.remote == nil, workspace.path.hasPrefix("/"), !workspace.path.contains("\0"), workspace.path.utf8.count <= 16_384 else {
            throw ClaudePluginFailure(status: "failed", detail: "로컬 작업 폴더가 올바르지 않습니다.")
        }
        let url = URL(fileURLWithPath: workspace.path).resolvingSymlinksInPath().standardizedFileURL
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw ClaudePluginFailure(status: "failed", detail: "작업 폴더를 찾지 못했습니다.")
        }
        return url
    }

    private static func command(_ configuration: ClaudePluginConfiguration, cwd: URL) async throws -> ProviderCommand {
        var seen = Set<String>()
        let candidates = configuration.executable.map { [$0] } ?? (configuration.environment["PATH"] ?? "").split(separator: ":").prefix(64).compactMap { entry -> URL? in
            let path = String(entry)
            guard path.hasPrefix("/"), !path.contains("\0"), seen.insert(path).inserted else { return nil }
            return URL(fileURLWithPath: path).appendingPathComponent("claude")
        }
        var present = false
        for executable in candidates where FileManager.default.isExecutableFile(atPath: executable.path) {
            present = true; try Task.checkCancellation()
            let result = try await ProcessCapture.run(executable: executable, arguments: ["--version"], environment: configuration.environment,
                cwd: cwd, timeout: min(4, configuration.readTimeout), maximumBytes: 16_384)
            guard result.exitCode == 0 else { continue }
            let version = display(String(decoding: result.stdout, as: UTF8.self), limit: 160).trimmingCharacters(in: .whitespacesAndNewlines)
            guard let range = version.range(of: "\\A[0-9]+\\.[0-9]+\\.[0-9]+(?=\\s|$)", options: .regularExpression),
                  !version[range].split(separator: ".").compactMap({ Int($0) }).lexicographicallyPrecedes([2, 1, 268]) else {
                throw ClaudePluginFailure(status: "unsupported", detail: "이 플러그인 관리 화면은 JSON 설치 결과를 지원하는 Claude Code 2.1.268 이상이 필요합니다.")
            }
            return ProviderCommand(provider: "claude", executable: executable, version: version)
        }
        throw ClaudePluginFailure(status: present ? "failed" : "missing", detail: present ? "설치된 Claude CLI 버전을 확인하지 못했습니다." : "Claude CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요.")
    }

    private static func readSnapshot(_ configuration: ClaudePluginConfiguration, command: ProviderCommand, cwd: URL) async throws -> ClaudePluginSnapshot {
        let listing = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "list", "--json", "--available"],
            environment: configuration.environment, cwd: cwd, timeout: configuration.readTimeout, maximumBytes: configuration.maximumBytes)
        try Task.checkCancellation()
        guard listing.exitCode == 0 else { throw ClaudePluginFailure(status: "failed", detail: "Claude CLI에서 플러그인 목록을 읽지 못했습니다.", output: output(listing)) }
        let markets = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "marketplace", "list", "--json"],
            environment: configuration.environment, cwd: cwd, timeout: configuration.readTimeout, maximumBytes: 512 * 1024)
        try Task.checkCancellation()
        guard markets.exitCode == 0 else { throw ClaudePluginFailure(status: "failed", detail: "등록된 마켓플레이스 목록을 읽지 못했습니다.", output: output(markets)) }
        return try parseSnapshot(listing.stdout, marketplaces: markets.stdout, cwd: cwd, version: command.version)
    }

    static func parseSnapshot(_ data: Data, marketplaces marketplaceData: Data, cwd: URL, version: String) throws -> ClaudePluginSnapshot {
        guard data.count <= 8 * 1024 * 1024, marketplaceData.count <= 512 * 1024,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let installedRows = json["installed"] as? [[String: Any]], let availableRows = json["available"] as? [[String: Any]],
              let marketRows = try? JSONSerialization.jsonObject(with: marketplaceData) as? [[String: Any]],
              installedRows.count <= 10_000, availableRows.count <= 10_000, marketRows.count <= 256 else {
            throw ClaudePluginFailure(status: "failed", detail: "플러그인 목록 형식 또는 크기가 올바르지 않습니다. 빈 목록으로 처리하지 않았습니다.")
        }
        var marketNames = Set<String>()
        let marketplaces = marketRows.compactMap { row -> ClaudePluginMarketplace? in
            guard let name = row["name"] as? String, identifier(name), marketNames.insert(name).inserted else { return nil }
            return ClaudePluginMarketplace(name: name, sourceKind: sourceKind(row["source"]))
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        var catalogIDs = Set<String>()
        let available = availableRows.compactMap { row -> ClaudeCatalogPlugin? in
            guard let id = row["pluginId"] as? String, let parts = pluginParts(id), row["name"] as? String == parts.name,
                  row["marketplaceName"] as? String == parts.marketplace, marketNames.contains(parts.marketplace), catalogIDs.insert(id).inserted else { return nil }
            return ClaudeCatalogPlugin(id: id, name: parts.name, description: display(row["description"] as? String ?? "", limit: 4096),
                marketplace: parts.marketplace, version: (row["version"] as? String).map { display($0, limit: 160) }, sourceKind: sourceKind(row["source"]))
        }.sorted { $0.id.localizedStandardCompare($1.id) == .orderedAscending }
        let catalog = Dictionary(uniqueKeysWithValues: available.map { ($0.id, $0) })
        var installedIDs = Set<String>()
        let installed = installedRows.compactMap { row -> ClaudeInstalledPlugin? in
            guard let id = row["id"] as? String, let parts = pluginParts(id), let scope = row["scope"] as? String,
                  ["user", "project", "local", "managed", "session"].contains(scope) else { return nil }
            let projectPath = row["projectPath"] as? String
            if ["project", "local"].contains(scope) {
                // CLI lists installation records from other projects as well.
                // Their enabled flag can still be true in this cwd. Never call
                // those records effective merely because the name is enabled.
                guard let projectPath, applies(projectPath, to: cwd) else { return nil }
            }
            let value = ClaudeInstalledPlugin(pluginID: id, name: parts.name, marketplace: parts.marketplace,
                version: (row["version"] as? String).map { display($0, limit: 160) }, scope: scope,
                enabled: row["enabled"] as? Bool, projectPath: projectPath.map { String($0.prefix(16_384)) },
                description: display(row["description"] as? String ?? catalog[id]?.description ?? "", limit: 4096),
                errors: messages(row["errors"]), notes: messages(row["notes"]))
            return installedIDs.insert(value.id).inserted ? value : nil
        }.sorted { $0.id.localizedStandardCompare($1.id) == .orderedAscending }
        let detail = marketplaces.isEmpty ? "등록된 마켓플레이스가 없습니다. Claude CLI에서 marketplace add로 등록한 뒤 목록을 다시 읽으세요." : "현재 작업 폴더의 CLI 설정과 등록된 마켓플레이스의 캐시 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다."
        return ClaudePluginSnapshot(status: "ready", detail: detail, cliVersion: version, installed: installed, available: available,
                                    marketplaces: marketplaces, updatedAt: mightyTimestamp())
    }

    private static func applies(_ projectPath: String, to cwd: URL) -> Bool {
        guard projectPath.hasPrefix("/"), !projectPath.contains("\0"), projectPath.utf8.count <= 16_384 else { return false }
        let path = URL(fileURLWithPath: projectPath).resolvingSymlinksInPath().standardizedFileURL.path
        return cwd.path == path || cwd.path.hasPrefix(path == "/" ? "/" : path + "/")
    }
    private static func identifier(_ value: String) -> Bool {
        value.utf8.count <= 128 && value.range(of: "\\A[A-Za-z0-9][A-Za-z0-9._-]*\\z", options: .regularExpression) != nil
    }
    private static func pluginParts(_ value: String) -> (name: String, marketplace: String)? {
        let parts = value.split(separator: "@", omittingEmptySubsequences: false)
        guard parts.count == 2, identifier(String(parts[0])), identifier(String(parts[1])) else { return nil }
        return (String(parts[0]), String(parts[1]))
    }
    private static func sourceKind(_ value: Any?) -> String {
        let kind = (value as? [String: Any])?["source"] as? String ?? value as? String ?? "unknown"
        if ["github", "git", "git-subdir", "npm", "url", "directory", "file", "command", "zip"].contains(kind) { return kind }
        return kind.hasPrefix("./") || kind.hasPrefix("/") ? "directory" : "unknown"
    }
    private static func messages(_ value: Any?) -> [String] { ((value as? [String]) ?? []).prefix(16).map { display($0, limit: 1024) } }
    private static func display(_ value: String, limit: Int) -> String {
        String(value.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) || $0 == "\n" || $0 == "\t" }.prefix(limit))
    }
    private static func output(_ result: ProcessResult) -> String {
        display(String(decoding: (result.stdout + Data([10]) + result.stderr).prefix(16_384), as: UTF8.self), limit: 16_384)
    }
    private static func failure(_ error: Error) -> ClaudePluginFailure {
        if error is CancellationError || Task.isCancelled { return ClaudePluginFailure(status: "cancelled", detail: "플러그인 작업을 취소했습니다. 이미 저장된 CLI 변경은 자동으로 되돌리지 않습니다.") }
        if let failure = error as? ClaudePluginFailure { return failure }
        return ClaudePluginFailure(status: "failed", detail: "플러그인 작업을 완료하지 못했습니다. 실행 시간·출력 한도 또는 CLI 접근 상태를 확인하세요.", output: display(error.localizedDescription, limit: 1024))
    }
}
