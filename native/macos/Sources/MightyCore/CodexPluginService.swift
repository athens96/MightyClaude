import Foundation

private struct CodexPluginConfiguration: Sendable {
    let environment: [String: String]
    let executable: URL?
    let readTimeout: TimeInterval
    let operationTimeout: TimeInterval
    let maximumBytes: Int
}

private struct CodexPluginFailure: Error, Sendable {
    let status: String
    let detail: String
    var output: String = ""
}

private enum CodexPluginTaskResult: Sendable {
    case snapshot(ClaudePluginSnapshot)
    case operation(ClaudePluginOperationResult)
}

/// Uses the installed Codex CLI plugin commands and its user-level registry.
/// Catalog reads never add or upgrade a marketplace. Mutations are explicit.
public actor CodexPluginService {
    private let configuration: CodexPluginConfiguration
    private var active: (id: UUID, task: Task<CodexPluginTaskResult, Never>)?
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
        let task = Task<CodexPluginTaskResult, Never> {
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

    public func install(pluginID: String, scope: String = "user", workspace: Workspace) async -> ClaudePluginOperationResult {
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

    private func finish(_ task: Task<CodexPluginTaskResult, Never>) async -> CodexPluginTaskResult {
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
            guard Self.pluginParts(pluginID) != nil, let scope, scope == "user" else {
                return ClaudePluginOperationResult(status: "failed", detail: "플러그인 이름 또는 설치 범위가 올바르지 않습니다.")
            }
        } else if let marketplace {
            guard Self.identifier(marketplace) else { return ClaudePluginOperationResult(status: "failed", detail: "마켓플레이스 이름이 올바르지 않습니다.") }
        } else { return ClaudePluginOperationResult(status: "failed", detail: "플러그인 작업이 올바르지 않습니다.") }
        let configuration = configuration
        let task = Task<CodexPluginTaskResult, Never> {
            do {
                let cwd = try Self.localDirectory(workspace)
                let command = try await Self.command(configuration, cwd: cwd)
                // Re-read the installed registry and policy-filtered catalog
                // immediately before a mutation. Only exact catalog IDs may be
                // installed; marketplace refreshes require registered Git sources.
                let snapshot = try await Self.readSnapshot(configuration, command: command, cwd: cwd)
                try Task.checkCancellation()
                let arguments: [String]
                if let pluginID {
                    if snapshot.installed.contains(where: { $0.pluginID == pluginID }) {
                        return .operation(ClaudePluginOperationResult(status: "skipped", detail: "이미 사용자 범위에 설치되어 있습니다. 비활성 상태라면 Codex에서 활성화하세요."))
                    }
                    guard snapshot.available.contains(where: { $0.id == pluginID }) else {
                        throw CodexPluginFailure(status: "failed", detail: "현재 설치 가능한 마켓플레이스 목록에서 이 플러그인을 찾지 못했습니다. 목록과 설치 정책을 다시 확인하세요.")
                    }
                    arguments = ["plugin", "add", pluginID, "--json"]
                } else if let marketplace {
                    guard let selected = snapshot.marketplaces.first(where: { $0.name == marketplace }) else {
                        throw CodexPluginFailure(status: "failed", detail: "등록되지 않은 마켓플레이스입니다. 기존 등록 목록에서 선택하세요.")
                    }
                    guard selected.sourceKind == "git" else {
                        return .operation(ClaudePluginOperationResult(status: "skipped", detail: "이 마켓플레이스는 Git 소스가 아니어서 Codex CLI로 갱신할 수 없습니다. 목록을 다시 읽으면 현재 상태를 확인할 수 있습니다."))
                    }
                    arguments = ["plugin", "marketplace", "upgrade", marketplace, "--json"]
                } else { throw CodexPluginFailure(status: "failed", detail: "플러그인 작업이 올바르지 않습니다.") }
                let result = try await ProcessCapture.run(executable: command.executable, arguments: arguments,
                    environment: configuration.environment, cwd: cwd, timeout: configuration.operationTimeout, maximumBytes: 1024 * 1024)
                try Task.checkCancellation()
                let output = Self.output(result)
                guard result.exitCode == 0,
                      let json = try? JSONSerialization.jsonObject(with: result.stdout) as? [String: Any] else {
                    throw CodexPluginFailure(status: "failed", detail: "플러그인 작업에 실패했거나 결과 형식이 올바르지 않습니다. 네트워크·권한·조직 정책을 확인하세요.", output: output)
                }
                if let pluginID, let parts = Self.pluginParts(pluginID) {
                    guard json["pluginId"] as? String == pluginID,
                          json["name"] as? String == parts.name,
                          json["marketplaceName"] as? String == parts.marketplace,
                          let installedPath = json["installedPath"] as? String, installedPath.hasPrefix("/"),
                          !installedPath.contains("\0") else {
                        throw CodexPluginFailure(status: "failed", detail: "설치 결과를 확인하지 못했습니다. 목록을 다시 읽어 설치 상태를 확인하세요.", output: output)
                    }
                    let verified = try await Self.readSnapshot(configuration, command: command, cwd: cwd)
                    guard verified.installed.contains(where: { $0.pluginID == pluginID }) else {
                        throw CodexPluginFailure(status: "failed", detail: "CLI가 설치 결과를 반환했지만 설치 목록에서 확인하지 못했습니다. 목록을 다시 읽으세요.", output: output)
                    }
                    return .operation(ClaudePluginOperationResult(status: "succeeded", detail: "플러그인을 사용자 범위에 설치했습니다. 새 Codex 세션부터 적용됩니다. 연결이 필요한 앱은 Codex에서 인증하세요.", output: output))
                }
                guard let marketplace,
                      let selected = json["selectedMarketplaces"] as? [String], selected == [marketplace],
                      json["upgradedRoots"] is [String], let errors = json["errors"] as? [Any], errors.isEmpty else {
                    throw CodexPluginFailure(status: "failed", detail: "마켓플레이스 새로고침을 확인하지 못했습니다. CLI 진단 출력을 확인하세요.", output: output)
                }
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
                                      operationTimeout: TimeInterval, maximumBytes: Int) -> CodexPluginConfiguration {
        var environment = supplied
        environment["GIT_TERMINAL_PROMPT"] = "0"
        return CodexPluginConfiguration(environment: environment, executable: executable, readTimeout: readTimeout,
                                         operationTimeout: operationTimeout, maximumBytes: maximumBytes)
    }

    private static func localDirectory(_ workspace: Workspace) throws -> URL {
        guard workspace.remote == nil, workspace.path.hasPrefix("/"), !workspace.path.contains("\0"), workspace.path.utf8.count <= 16_384 else {
            throw CodexPluginFailure(status: "failed", detail: "로컬 작업 폴더가 올바르지 않습니다.")
        }
        let url = URL(fileURLWithPath: workspace.path).resolvingSymlinksInPath().standardizedFileURL
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw CodexPluginFailure(status: "failed", detail: "작업 폴더를 찾지 못했습니다.")
        }
        return url
    }

    private static func command(_ configuration: CodexPluginConfiguration, cwd: URL) async throws -> ProviderCommand {
        var seen = Set<String>()
        let candidates = configuration.executable.map { [$0] } ?? (configuration.environment["PATH"] ?? "").split(separator: ":").prefix(64).compactMap { entry -> URL? in
            let path = String(entry)
            guard path.hasPrefix("/"), !path.contains("\0"), seen.insert(path).inserted else { return nil }
            return URL(fileURLWithPath: path).appendingPathComponent("codex")
        }
        var present = false
        for executable in candidates where FileManager.default.isExecutableFile(atPath: executable.path) {
            present = true; try Task.checkCancellation()
            let result = try await ProcessCapture.run(executable: executable, arguments: ["--version"], environment: configuration.environment,
                cwd: cwd, timeout: min(4, configuration.readTimeout), maximumBytes: 16_384)
            guard result.exitCode == 0 else { continue }
            let version = display(String(decoding: result.stdout, as: UTF8.self), limit: 160).trimmingCharacters(in: .whitespacesAndNewlines)
            // Probe the exact read/mutation interfaces, rather than guessing
            // which historic release first shipped this evolving CLI feature.
            for (arguments, flags) in [
                (["plugin", "list", "--help"], ["--json", "--available"]),
                (["plugin", "add", "--help"], ["--json"]),
                (["plugin", "marketplace", "list", "--help"], ["--json"]),
                (["plugin", "marketplace", "upgrade", "--help"], ["--json"])
            ] {
                let help = try await ProcessCapture.run(executable: executable, arguments: arguments, environment: configuration.environment,
                    cwd: cwd, timeout: min(4, configuration.readTimeout), maximumBytes: 65_536)
                let helpText = String(decoding: help.stdout, as: UTF8.self)
                guard help.exitCode == 0, flags.allSatisfy({ helpText.contains($0) }) else {
                    throw CodexPluginFailure(status: "unsupported", detail: "설치된 Codex CLI가 필요한 JSON 플러그인 명령을 지원하지 않습니다. 최신 Codex CLI로 업데이트하세요.")
                }
            }
            return ProviderCommand(provider: "codex", executable: executable, version: version)
        }
        throw CodexPluginFailure(status: present ? "failed" : "missing", detail: present ? "설치된 Codex CLI 버전을 확인하지 못했습니다." : "Codex CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요.")
    }

    private static func readSnapshot(_ configuration: CodexPluginConfiguration, command: ProviderCommand, cwd: URL) async throws -> ClaudePluginSnapshot {
        let listing = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "list", "--json", "--available"],
            environment: configuration.environment, cwd: cwd, timeout: configuration.readTimeout, maximumBytes: configuration.maximumBytes)
        try Task.checkCancellation()
        guard listing.exitCode == 0 else { throw CodexPluginFailure(status: "failed", detail: "Codex CLI에서 플러그인 목록을 읽지 못했습니다.", output: output(listing)) }
        let markets = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "marketplace", "list", "--json"],
            environment: configuration.environment, cwd: cwd, timeout: configuration.readTimeout, maximumBytes: 512 * 1024)
        try Task.checkCancellation()
        guard markets.exitCode == 0 else { throw CodexPluginFailure(status: "failed", detail: "등록된 마켓플레이스 목록을 읽지 못했습니다.", output: output(markets)) }
        var snapshot = try parseSnapshot(listing.stdout, marketplaces: markets.stdout, cwd: cwd, version: command.version)
        snapshot.diagnosticOutput = display(String(decoding: (listing.stderr + Data([10]) + markets.stderr).prefix(16_384), as: UTF8.self), limit: 16_384).trimmingCharacters(in: .whitespacesAndNewlines)
        if !snapshot.diagnosticOutput.isEmpty {
            snapshot.detail += " CLI 경고가 있습니다. 일부 목록이 최신 상태가 아닐 수 있으니 진단 출력을 확인하세요."
        }
        return snapshot
    }

    static func parseSnapshot(_ data: Data, marketplaces marketplaceData: Data, cwd: URL, version: String) throws -> ClaudePluginSnapshot {
        guard data.count <= 8 * 1024 * 1024, marketplaceData.count <= 512 * 1024,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let installedRows = json["installed"] as? [[String: Any]], let availableRows = json["available"] as? [[String: Any]],
              let marketJSON = try? JSONSerialization.jsonObject(with: marketplaceData) as? [String: Any],
              let marketRows = marketJSON["marketplaces"] as? [[String: Any]],
              installedRows.count <= 10_000, availableRows.count <= 10_000, marketRows.count <= 256 else {
            throw CodexPluginFailure(status: "failed", detail: "플러그인 목록 형식 또는 크기가 올바르지 않습니다. 빈 목록으로 처리하지 않았습니다.")
        }
        var marketNames = Set<String>()
        let marketplaces = try marketRows.map { row -> ClaudePluginMarketplace in
            guard let name = row["name"] as? String, identifier(name), marketNames.insert(name).inserted else {
                throw CodexPluginFailure(status: "failed", detail: "마켓플레이스 목록에 올바르지 않거나 중복된 이름이 있습니다.")
            }
            return ClaudePluginMarketplace(name: name, sourceKind: sourceKind(row["marketplaceSource"]))
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        func rowIdentity(_ row: [String: Any]) throws -> (id: String, name: String, marketplace: String) {
            guard let id = row["pluginId"] as? String, let parts = pluginParts(id), row["name"] as? String == parts.name,
                  row["marketplaceName"] as? String == parts.marketplace,
                  row["installed"] is Bool, row["enabled"] is Bool else {
                throw CodexPluginFailure(status: "failed", detail: "플러그인 목록에 올바르지 않은 항목이 있습니다.")
            }
            return (id, parts.name, parts.marketplace)
        }
        var catalogIDs = Set<String>()
        var restricted = 0
        let available = try availableRows.compactMap { row -> ClaudeCatalogPlugin? in
            let parts = try rowIdentity(row)
            guard catalogIDs.insert(parts.id).inserted else {
                throw CodexPluginFailure(status: "failed", detail: "플러그인 목록에 중복 항목이 있습니다.")
            }
            guard let policy = row["installPolicy"] as? String,
                  ["AVAILABLE", "INSTALLED_BY_DEFAULT"].contains(policy) else {
                restricted += 1; return nil
            }
            return ClaudeCatalogPlugin(id: parts.id, name: parts.name, description: display(row["description"] as? String ?? "", limit: 4096),
                marketplace: parts.marketplace, version: (row["version"] as? String).map { display($0, limit: 160) }, sourceKind: sourceKind(row["source"]))
        }.sorted { $0.id.localizedStandardCompare($1.id) == .orderedAscending }
        let catalog = Dictionary(uniqueKeysWithValues: available.map { ($0.id, $0) })
        var installedIDs = Set<String>()
        let installed = try installedRows.map { row -> ClaudeInstalledPlugin in
            let parts = try rowIdentity(row)
            guard row["installed"] as? Bool == true, installedIDs.insert(parts.id).inserted else {
                throw CodexPluginFailure(status: "failed", detail: "설치 목록에 올바르지 않거나 중복된 항목이 있습니다.")
            }
            return ClaudeInstalledPlugin(pluginID: parts.id, name: parts.name, marketplace: parts.marketplace,
                version: (row["version"] as? String).map { display($0, limit: 160) }, scope: "user",
                enabled: row["enabled"] as? Bool,
                description: display(row["description"] as? String ?? catalog[parts.id]?.description ?? "", limit: 4096),
                errors: messages(row["errors"]), notes: messages(row["notes"]))
        }.sorted { $0.id.localizedStandardCompare($1.id) == .orderedAscending }
        var detail = marketplaces.isEmpty ? "등록된 마켓플레이스가 없습니다. Codex CLI에서 plugin marketplace add로 등록한 뒤 목록을 다시 읽으세요." : "Codex 사용자 설치 목록과 현재 작업 폴더의 설정을 반영한 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다."
        if restricted > 0 { detail += " 설치 정책으로 설치할 수 없는 항목 \(restricted)개는 제외했습니다." }
        return ClaudePluginSnapshot(status: "ready", detail: detail, cliVersion: version, installed: installed, available: available,
                                    marketplaces: marketplaces, updatedAt: mightyTimestamp())
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
        let kind = (value as? [String: Any])?["sourceType"] as? String ?? (value as? [String: Any])?["source"] as? String ?? "unknown"
        if ["local", "remote", "github", "git", "directory"].contains(kind) { return kind }
        return kind.hasPrefix("./") || kind.hasPrefix("/") ? "directory" : "unknown"
    }
    private static func messages(_ value: Any?) -> [String] { ((value as? [String]) ?? []).prefix(16).map { display($0, limit: 1024) } }
    private static func display(_ value: String, limit: Int) -> String {
        String(value.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) || $0 == "\n" || $0 == "\t" }.prefix(limit))
    }
    private static func output(_ result: ProcessResult) -> String {
        display(String(decoding: (result.stdout + Data([10]) + result.stderr).prefix(16_384), as: UTF8.self), limit: 16_384)
    }
    private static func failure(_ error: Error) -> CodexPluginFailure {
        if error is CancellationError || Task.isCancelled { return CodexPluginFailure(status: "cancelled", detail: "플러그인 작업을 취소했습니다. 이미 저장된 CLI 변경은 자동으로 되돌리지 않습니다.") }
        if let failure = error as? CodexPluginFailure { return failure }
        return CodexPluginFailure(status: "failed", detail: "플러그인 작업을 완료하지 못했습니다. 실행 시간·출력 한도 또는 CLI 접근 상태를 확인하세요.", output: display(error.localizedDescription, limit: 1024))
    }
}
