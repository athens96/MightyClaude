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
    case update(PluginAutoUpdateResult)
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
    // a catalog entry, or a plugin's manifest.
    init(environment: [String: String], executable: URL?, readTimeout: TimeInterval = 2,
         operationTimeout: TimeInterval = 5, maximumBytes: Int = 8 * 1024 * 1024) {
        configuration = Self.configuration(environment, executable: executable, readTimeout: readTimeout,
                                           operationTimeout: operationTimeout, maximumBytes: maximumBytes)
    }

    public func snapshot(workspace: Workspace) async -> ClaudePluginSnapshot {
        guard !closing, !Task.isCancelled else { return ClaudePluginSnapshot(status: "cancelled", detail: L("plugins.detail.cancelled")) }
        guard active == nil else { return ClaudePluginSnapshot(status: "busy", detail: L("plugins.operation.busy")) }
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
        return ClaudePluginSnapshot(status: "failed", detail: L("plugins.detail.failed"))
    }

    public func install(pluginID: String, scope: String = "user", workspace: Workspace) async -> ClaudePluginOperationResult {
        await operation(workspace: workspace, pluginID: pluginID, scope: scope, marketplace: nil)
    }

    public func refreshMarketplace(name: String, workspace: Workspace) async -> ClaudePluginOperationResult {
        await operation(workspace: workspace, pluginID: nil, scope: nil, marketplace: name)
    }

    /// Upgrades every registered Git marketplace (`codex plugin marketplace
    /// upgrade`), which is how Codex brings installed plugins up to date.
    public func upgradeMarketplaces(workspace: Workspace) async -> PluginAutoUpdateResult {
        guard !closing, !Task.isCancelled else { return PluginAutoUpdateResult(status: "cancelled", detail: L("pluginAutoUpdate.cancelled")) }
        guard active == nil else { return PluginAutoUpdateResult(status: "busy", detail: L("pluginAutoUpdate.busy")) }
        let configuration = configuration
        let task = Task<CodexPluginTaskResult, Never> {
            do {
                let cwd = try Self.localDirectory(workspace)
                let command = try await Self.command(configuration, cwd: cwd)
                let snapshot = try await Self.readSnapshot(configuration, command: command, cwd: cwd)
                try Task.checkCancellation()
                guard snapshot.marketplaces.contains(where: { $0.sourceKind == "git" }) else {
                    return .update(PluginAutoUpdateResult(status: "skipped", detail: L("pluginAutoUpdate.noGitMarketplaces")))
                }
                let result = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "marketplace", "upgrade", "--json"],
                    environment: configuration.environment, cwd: cwd, timeout: configuration.operationTimeout, maximumBytes: 1024 * 1024)
                try Task.checkCancellation()
                guard result.exitCode == 0 else { return .update(PluginAutoUpdateResult(status: "failed", detail: L("pluginAutoUpdate.failed"))) }
                // The exit code decides; the JSON only adds detail. Unknown or
                // missing keys from another CLI version are not a failure.
                let json = try? JSONSerialization.jsonObject(with: result.stdout) as? [String: Any]
                let selected = (json?["selectedMarketplaces"] as? [Any])?.compactMap { $0 as? String } ?? []
                if let errors = json?["errors"] as? [Any], !errors.isEmpty {
                    return .update(PluginAutoUpdateResult(status: "failed", detail: L("pluginAutoUpdate.marketplaceErrorsTemplate", ["count": String(errors.count)]), failed: selected))
                }
                let detail = selected.isEmpty ? L("pluginAutoUpdate.marketplacesUpgraded") : L("pluginAutoUpdate.marketplacesTemplate", ["count": String(selected.count)])
                return .update(PluginAutoUpdateResult(status: "succeeded", detail: detail, updated: selected))
            } catch {
                let failure = Self.failure(error)
                return .update(PluginAutoUpdateResult(status: failure.status == "cancelled" ? "cancelled" : ["missing", "unsupported"].contains(failure.status) ? "skipped" : "failed", detail: failure.detail))
            }
        }
        if case .update(let value) = await finish(task) { return value }
        return PluginAutoUpdateResult(status: "failed", detail: L("pluginAutoUpdate.failed"))
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
        guard !closing, !Task.isCancelled else { return ClaudePluginOperationResult(status: "cancelled", detail: L("plugins.operation.cancelled")) }
        guard active == nil else { return ClaudePluginOperationResult(status: "busy", detail: L("plugins.operation.busy")) }
        if let pluginID {
            guard Self.pluginParts(pluginID) != nil, let scope, scope == "user" else {
                return ClaudePluginOperationResult(status: "failed", detail: L("plugins.install.badIdOrScope"))
            }
        } else if let marketplace {
            guard Self.identifier(marketplace) else { return ClaudePluginOperationResult(status: "failed", detail: L("plugins.marketplace.badName")) }
        } else { return ClaudePluginOperationResult(status: "failed", detail: L("plugins.operation.invalid")) }
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
                        return .operation(ClaudePluginOperationResult(status: "skipped", detail: L("plugins.codex.installSkipped")))
                    }
                    guard snapshot.available.contains(where: { $0.id == pluginID }) else {
                        throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.installNotFound"))
                    }
                    arguments = ["plugin", "add", pluginID, "--json"]
                } else if let marketplace {
                    guard let selected = snapshot.marketplaces.first(where: { $0.name == marketplace }) else {
                        throw CodexPluginFailure(status: "failed", detail: L("plugins.marketplace.notRegistered"))
                    }
                    guard selected.sourceKind == "git" else {
                        return .operation(ClaudePluginOperationResult(status: "skipped", detail: L("plugins.codex.marketplaceNotGit")))
                    }
                    arguments = ["plugin", "marketplace", "upgrade", marketplace, "--json"]
                } else { throw CodexPluginFailure(status: "failed", detail: L("plugins.operation.invalid")) }
                let result = try await ProcessCapture.run(executable: command.executable, arguments: arguments,
                    environment: configuration.environment, cwd: cwd, timeout: configuration.operationTimeout, maximumBytes: 1024 * 1024)
                try Task.checkCancellation()
                let output = Self.output(result)
                guard result.exitCode == 0,
                      let json = try? JSONSerialization.jsonObject(with: result.stdout) as? [String: Any] else {
                    throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.operationFailed"), output: output)
                }
                if let pluginID, let parts = Self.pluginParts(pluginID) {
                    guard json["pluginId"] as? String == pluginID,
                          json["name"] as? String == parts.name,
                          json["marketplaceName"] as? String == parts.marketplace,
                          let installedPath = json["installedPath"] as? String, installedPath.hasPrefix("/"),
                          !installedPath.contains("\0") else {
                        throw CodexPluginFailure(status: "failed", detail: L("plugins.install.unconfirmed"), output: output)
                    }
                    let verified = try await Self.readSnapshot(configuration, command: command, cwd: cwd)
                    guard verified.installed.contains(where: { $0.pluginID == pluginID }) else {
                        throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.installVerifyFailed"), output: output)
                    }
                    return .operation(ClaudePluginOperationResult(status: "succeeded", detail: L("plugins.codex.installSucceeded"), output: output))
                }
                guard let marketplace,
                      let selected = json["selectedMarketplaces"] as? [String], selected == [marketplace],
                      json["upgradedRoots"] is [String], let errors = json["errors"] as? [Any], errors.isEmpty else {
                    throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.marketplaceRefreshUnconfirmed"), output: output)
                }
                return .operation(ClaudePluginOperationResult(status: "succeeded", detail: L("plugins.marketplace.refreshSucceeded"), output: output))
            } catch {
                let failure = Self.failure(error)
                return .operation(ClaudePluginOperationResult(status: failure.status == "cancelled" ? "cancelled" : "failed", detail: failure.detail, output: failure.output))
            }
        }
        if case .operation(let value) = await finish(task) { return value }
        return ClaudePluginOperationResult(status: "failed", detail: L("plugins.operation.notCompleted"))
    }

    private static func configuration(_ supplied: [String: String], executable: URL?, readTimeout: TimeInterval,
                                      operationTimeout: TimeInterval, maximumBytes: Int) -> CodexPluginConfiguration {
        var environment = supplied
        environment["GIT_TERMINAL_PROMPT"] = "0"
        return CodexPluginConfiguration(environment: environment, executable: executable, readTimeout: readTimeout,
                                         operationTimeout: operationTimeout, maximumBytes: maximumBytes)
    }

    private static func localDirectory(_ workspace: Workspace) throws -> URL {
        guard workspace.path.hasPrefix("/"), !workspace.path.contains("\0"), workspace.path.utf8.count <= 16_384 else {
            throw CodexPluginFailure(status: "failed", detail: L("plugins.detail.invalidWorkspace"))
        }
        let url = URL(fileURLWithPath: workspace.path).resolvingSymlinksInPath().standardizedFileURL
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw CodexPluginFailure(status: "failed", detail: L("plugins.detail.missingWorkspace"))
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
                    throw CodexPluginFailure(status: "unsupported", detail: L("plugins.codex.detailUnsupported"))
                }
            }
            return ProviderCommand(provider: "codex", executable: executable, version: version)
        }
        throw CodexPluginFailure(status: present ? "failed" : "missing", detail: present ? L("plugins.codex.detailUnknownVersion") : L("plugins.codex.detailMissingCli"))
    }

    private static func readSnapshot(_ configuration: CodexPluginConfiguration, command: ProviderCommand, cwd: URL) async throws -> ClaudePluginSnapshot {
        let listing = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "list", "--json", "--available"],
            environment: configuration.environment, cwd: cwd, timeout: configuration.readTimeout, maximumBytes: configuration.maximumBytes)
        try Task.checkCancellation()
        guard listing.exitCode == 0 else { throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.detailListingFailed"), output: output(listing)) }
        let markets = try await ProcessCapture.run(executable: command.executable, arguments: ["plugin", "marketplace", "list", "--json"],
            environment: configuration.environment, cwd: cwd, timeout: configuration.readTimeout, maximumBytes: 512 * 1024)
        try Task.checkCancellation()
        guard markets.exitCode == 0 else { throw CodexPluginFailure(status: "failed", detail: L("plugins.detail.marketplacesFailed"), output: output(markets)) }
        var snapshot = try parseSnapshot(listing.stdout, marketplaces: markets.stdout, cwd: cwd, version: command.version)
        snapshot.diagnosticOutput = display(String(decoding: (listing.stderr + Data([10]) + markets.stderr).prefix(16_384), as: UTF8.self), limit: 16_384).trimmingCharacters(in: .whitespacesAndNewlines)
        if !snapshot.diagnosticOutput.isEmpty {
            snapshot.detail += L("plugins.codex.detailWarningSuffix")
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
            throw CodexPluginFailure(status: "failed", detail: L("plugins.detail.malformed"))
        }
        var marketNames = Set<String>()
        let marketplaces = try marketRows.map { row -> ClaudePluginMarketplace in
            guard let name = row["name"] as? String, identifier(name), marketNames.insert(name).inserted else {
                throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.marketplaceNamesInvalid"))
            }
            return ClaudePluginMarketplace(name: name, sourceKind: sourceKind(row["marketplaceSource"]))
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        func rowIdentity(_ row: [String: Any]) throws -> (id: String, name: String, marketplace: String) {
            guard let id = row["pluginId"] as? String, let parts = pluginParts(id), row["name"] as? String == parts.name,
                  row["marketplaceName"] as? String == parts.marketplace,
                  row["installed"] is Bool, row["enabled"] is Bool else {
                throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.listEntryInvalid"))
            }
            return (id, parts.name, parts.marketplace)
        }
        var catalogIDs = Set<String>()
        var restricted = 0
        let available = try availableRows.compactMap { row -> ClaudeCatalogPlugin? in
            let parts = try rowIdentity(row)
            guard catalogIDs.insert(parts.id).inserted else {
                throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.listEntryDuplicate"))
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
                throw CodexPluginFailure(status: "failed", detail: L("plugins.codex.installedInvalid"))
            }
            return ClaudeInstalledPlugin(pluginID: parts.id, name: parts.name, marketplace: parts.marketplace,
                version: (row["version"] as? String).map { display($0, limit: 160) }, scope: "user",
                enabled: row["enabled"] as? Bool,
                description: display(row["description"] as? String ?? catalog[parts.id]?.description ?? "", limit: 4096),
                errors: messages(row["errors"]), notes: messages(row["notes"]))
        }.sorted { $0.id.localizedStandardCompare($1.id) == .orderedAscending }
        var detail = marketplaces.isEmpty ? L("plugins.codex.detailNoMarketplaces") : L("plugins.codex.detailReady")
        if restricted > 0 { detail += L("plugins.codex.detailRestrictedSuffix", ["count": String(restricted)]) }
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
        if error is CancellationError || Task.isCancelled { return CodexPluginFailure(status: "cancelled", detail: L("plugins.operation.cancelledKeepsChanges")) }
        if let failure = error as? CodexPluginFailure { return failure }
        return CodexPluginFailure(status: "failed", detail: L("plugins.detail.incomplete"), output: display(error.localizedDescription, limit: 1024))
    }
}
