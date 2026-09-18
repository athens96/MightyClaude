import Foundation

public struct CLIUpdateInstallation: Sendable, Equatable {
    public var provider: String
    public var version: String?
    /// native, brew-cask, brew-formula, npm, unknown, or missing.
    public var method: String
    public var executablePath: String?
    public var canUpdate: Bool
    public var detail: String

    public init(provider: String, version: String? = nil, method: String = "unknown", executablePath: String? = nil, canUpdate: Bool = false, detail: String) {
        self.provider = provider; self.version = version; self.method = method
        self.executablePath = executablePath; self.canUpdate = canUpdate; self.detail = detail
    }
}

public struct CLIUpdateResult: Sendable, Equatable {
    public var provider: String
    /// updated, current, skipped, failed, cancelled, or busy.
    public var status: String
    public var beforeVersion: String?
    public var afterVersion: String?
    public var method: String
    public var detail: String
    /// Bounded diagnostic output. Present only on an explicit details action.
    public var output: String

    public init(provider: String, status: String, beforeVersion: String? = nil, afterVersion: String? = nil, method: String = "unknown", detail: String, output: String = "") {
        self.provider = provider; self.status = status; self.beforeVersion = beforeVersion
        self.afterVersion = afterVersion; self.method = method; self.detail = detail; self.output = output
    }
}

private struct CLIUpdateConfiguration: Sendable {
    let environment: [String: String]
    let home: URL
    let metadataTimeout: TimeInterval
    let updateTimeout: TimeInterval
}

private struct CLIUpdateInvocation: Sendable {
    let executable: URL
    let arguments: [String]
    var environment: [String: String]
}

private struct CLIUpdatePlan: Sendable {
    let installation: CLIUpdateInstallation
    let invocation: CLIUpdateInvocation?
}

/// Updates only an already installed, recognized CLI through its own installer.
/// No shell command construction, sudo, fresh installations, or config writes.
public actor CLIUpdateService {
    private let configuration: CLIUpdateConfiguration
    private var active: (id: UUID, task: Task<CLIUpdateResult, Never>)?
    private var closing = false

    public init(environment: [String: String]? = nil) {
        configuration = CLIUpdateConfiguration(environment: environment ?? ProviderService.runtimeEnvironment(), home: FileManager.default.homeDirectoryForCurrentUser, metadataTimeout: 4, updateTimeout: 300)
    }

    // Filesystem fixtures use the real process implementation with private roots
    // and short deadlines. These overrides are not exposed to the app or wire.
    // The probe only has to beat a hung CLI. Two seconds was not enough for a
    // shell fixture when the whole suite spawns processes in parallel, and a
    // missed probe reads as "unknown" rather than as a timeout.
    init(environment: [String: String], homeDirectory: URL, metadataTimeout: TimeInterval = 15, updateTimeout: TimeInterval = 30) {
        configuration = CLIUpdateConfiguration(environment: environment, home: homeDirectory, metadataTimeout: metadataTimeout, updateTimeout: updateTimeout)
    }

    public func inspect(provider: String) async -> CLIUpdateInstallation {
        guard !closing else { return CLIUpdateInstallation(provider: provider, detail: "앱이 종료 중입니다.") }
        do { return try await Self.plan(provider: provider, configuration: configuration).installation }
        catch { return CLIUpdateInstallation(provider: provider, detail: Task.isCancelled ? "설치 확인을 취소했습니다." : "CLI 설치 정보를 확인하지 못했습니다.") }
    }

    public func update(provider: String) async -> CLIUpdateResult {
        guard !closing else { return CLIUpdateResult(provider: provider, status: "cancelled", detail: "앱이 종료 중입니다.") }
        guard active == nil else { return CLIUpdateResult(provider: provider, status: "busy", detail: "다른 CLI를 업데이트하고 있습니다.") }
        guard !Task.isCancelled else { return CLIUpdateResult(provider: provider, status: "cancelled", detail: "업데이트를 취소했습니다.") }
        let id = UUID(), configuration = configuration
        let task = Task { await Self.performUpdate(provider: provider, configuration: configuration) }
        active = (id, task)
        let result = await withTaskCancellationHandler(operation: { await task.value }, onCancel: { task.cancel() })
        if active?.id == id { active = nil }
        return result
    }

    public func cancel() async {
        guard let operation = active else { return }
        operation.task.cancel(); _ = await operation.task.value
        if active?.id == operation.id { active = nil }
    }

    public func shutdown() async { closing = true; await cancel() }

    private static func performUpdate(provider: String, configuration: CLIUpdateConfiguration) async -> CLIUpdateResult {
        var installation = CLIUpdateInstallation(provider: provider, detail: "")
        do {
            try Task.checkCancellation()
            let plan = try await plan(provider: provider, configuration: configuration)
            installation = plan.installation
            try Task.checkCancellation()
            guard let invocation = plan.invocation else {
                return CLIUpdateResult(provider: provider, status: "skipped", beforeVersion: installation.version, method: installation.method, detail: installation.detail)
            }
            let result = try await ProcessCapture.run(executable: invocation.executable, arguments: invocation.arguments,
                environment: invocation.environment, timeout: configuration.updateTimeout, maximumBytes: 1024 * 1024)
            try Task.checkCancellation()
            let output = boundedOutput(result)
            guard result.exitCode == 0 else {
                return CLIUpdateResult(provider: provider, status: "failed", beforeVersion: installation.version, method: installation.method,
                    detail: "업데이트 명령이 종료 코드 \(result.exitCode)로 실패했습니다. 설치 권한이나 네트워크 상태를 확인하세요.", output: output)
            }
            let after = try await installedCommand(provider: provider, configuration: configuration)
            try Task.checkCancellation()
            guard let version = after?.version else {
                return CLIUpdateResult(provider: provider, status: "failed", beforeVersion: installation.version, method: installation.method,
                    detail: "업데이트 명령은 끝났지만 CLI 버전을 다시 확인하지 못했습니다.", output: output)
            }
            let changed = installation.version != version
            return CLIUpdateResult(provider: provider, status: changed ? "updated" : "current", beforeVersion: installation.version, afterVersion: version,
                method: installation.method, detail: changed ? "CLI를 업데이트했습니다." : "업데이트 명령을 완료했습니다. 설치된 버전은 동일합니다.", output: output)
        } catch {
            let cancelled = error is CancellationError || Task.isCancelled
            return CLIUpdateResult(provider: provider, status: cancelled ? "cancelled" : "failed", beforeVersion: installation.version, method: installation.method,
                detail: cancelled ? "업데이트를 취소했습니다." : String(error.localizedDescription.prefix(600)))
        }
    }

    private static func plan(provider: String, configuration: CLIUpdateConfiguration) async throws -> CLIUpdatePlan {
        guard ProviderOptions.ids.contains(provider) else {
            return CLIUpdatePlan(installation: CLIUpdateInstallation(provider: provider, detail: "지원하지 않는 CLI입니다."), invocation: nil)
        }
        guard let command = try await installedCommand(provider: provider, configuration: configuration) else {
            let present = executableCandidates(provider, environment: configuration.environment).contains { FileManager.default.isExecutableFile(atPath: $0.path) }
            return CLIUpdatePlan(installation: CLIUpdateInstallation(provider: provider, method: present ? "unknown" : "missing",
                detail: present ? "CLI 버전을 확인하지 못해 업데이트하지 않았습니다." : "설치된 CLI가 없어 건너뜁니다. 새로 설치하지 않습니다."), invocation: nil)
        }
        let resolved = command.executable.resolvingSymlinksInPath().standardizedFileURL
        var installation = CLIUpdateInstallation(provider: provider, version: command.version, executablePath: command.executable.path,
            detail: "수동 설치 또는 확인할 수 없는 설치 방식입니다. 기존 설치 방법으로 직접 업데이트하세요.")
        var environment = configuration.environment
        if let brew = brewInstallation(resolved, provider: provider) {
            installation.method = brew.cask ? "brew-cask" : "brew-formula"
            guard FileManager.default.isExecutableFile(atPath: brew.executable.path) else {
                installation.detail = "Homebrew 설치이지만 해당 Homebrew 실행 파일을 찾지 못했습니다."
                return CLIUpdatePlan(installation: installation, invocation: nil)
            }
            // Refreshing package metadata is allowed; do not clean up or upgrade
            // unrelated installed dependents as a side effect of this request.
            environment["HOMEBREW_NO_INSTALL_CLEANUP"] = "1"
            environment["HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK"] = "1"
            environment["HOMEBREW_NO_ANALYTICS"] = "1"
            environment["HOMEBREW_NO_ASK"] = "1"
            environment["NONINTERACTIVE"] = "1"
            installation.canUpdate = true; installation.detail = "설치된 Homebrew의 해당 \(brew.cask ? "cask" : "formula")만 업데이트합니다."
            return CLIUpdatePlan(installation: installation, invocation: CLIUpdateInvocation(executable: brew.executable,
                arguments: ["upgrade", brew.cask ? "--cask" : "--formula", brew.package], environment: environment))
        }
        if provider == "claude" {
            var nativeRoots = [configuration.home.appendingPathComponent(".local/share/claude/versions")]
            if let dataHome = configuration.environment["XDG_DATA_HOME"], validAbsolutePath(dataHome) {
                nativeRoots.append(URL(fileURLWithPath: dataHome).appendingPathComponent("claude/versions"))
            }
            if nativeRoots.contains(where: { resolved.deletingLastPathComponent().path == $0.resolvingSymlinksInPath().standardizedFileURL.path }) {
                installation.method = "native"; installation.canUpdate = true
                installation.detail = "Claude Code의 기본 업데이트 명령을 사용합니다."
                return CLIUpdatePlan(installation: installation, invocation: CLIUpdateInvocation(executable: command.executable, arguments: ["update"], environment: environment))
            }
        }
        if let package = npmInstallation(resolved, provider: provider) {
            installation.method = "npm"
            guard package.version.range(of: "\\A[0-9]+\\.[0-9]+\\.[0-9]+(?:\\+[0-9A-Za-z.-]+)?\\z", options: .regularExpression) != nil else {
                installation.detail = "시험판 또는 확인할 수 없는 npm 채널은 자동 변경하지 않습니다. 기존 채널에서 직접 업데이트하세요."
                return CLIUpdatePlan(installation: installation, invocation: nil)
            }
            guard let npm = npmRuntime(prefix: package.prefix, environment: environment) else {
                installation.detail = "npm 설치는 확인했지만 해당 설치를 업데이트할 Node.js/npm을 찾지 못했습니다."
                return CLIUpdatePlan(installation: installation, invocation: nil)
            }
            // npm lifecycle scripts and the CLI's env-node launcher use the same
            // Node installation as the npm process, including version managers.
            environment["PATH"] = npm.node.deletingLastPathComponent().path + ":" + (environment["PATH"] ?? "")
            installation.canUpdate = true; installation.detail = "기존 npm 설치 위치에서 공식 패키지만 업데이트합니다."
            return CLIUpdatePlan(installation: installation, invocation: CLIUpdateInvocation(executable: npm.node,
                arguments: [npm.script.path, "install", "--global", "--prefix", package.prefix.path, package.name + "@latest", "--no-audit", "--no-fund"], environment: environment))
        }
        return CLIUpdatePlan(installation: installation, invocation: nil)
    }

    private static func installedCommand(provider: String, configuration: CLIUpdateConfiguration) async throws -> ProviderCommand? {
        guard ProviderOptions.ids.contains(provider) else { return nil }
        for path in executableCandidates(provider, environment: configuration.environment) where FileManager.default.isExecutableFile(atPath: path.path) {
            try Task.checkCancellation()
            do {
                let result = try await ProcessCapture.run(executable: path, arguments: ["--version"], environment: configuration.environment,
                    timeout: configuration.metadataTimeout, maximumBytes: 16_384)
                guard result.exitCode == 0 else { continue }
                let text = String(decoding: result.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                if !text.isEmpty { return ProviderCommand(provider: provider, executable: path, version: String(text.prefix(160))) }
            } catch { if error is CancellationError || Task.isCancelled { throw CancellationError() } }
        }
        return nil
    }

    private static func executableCandidates(_ name: String, environment: [String: String]) -> [URL] {
        var seen = Set<String>()
        return (environment["PATH"] ?? "").split(separator: ":").prefix(64).compactMap { entry in
            let path = String(entry)
            guard validAbsolutePath(path), seen.insert(path).inserted else { return nil }
            return URL(fileURLWithPath: path).appendingPathComponent(name)
        }
    }

    private static func validAbsolutePath(_ path: String) -> Bool { path.hasPrefix("/") && !path.contains("\0") }

    private static func brewInstallation(_ path: URL, provider: String) -> (executable: URL, package: String, cask: Bool)? {
        let package = ["claude": "claude-code", "codex": "codex", "gemini": "gemini-cli"][provider]!
        let parts = path.pathComponents
        guard let index = parts.firstIndex(where: { $0 == "Caskroom" || $0 == "Cellar" }), index > 1,
              parts.count > index + 3, parts[index + 1] == package else { return nil }
        let prefix = URL(fileURLWithPath: NSString.path(withComponents: Array(parts.prefix(index))))
        return (prefix.appendingPathComponent("bin/brew"), package, parts[index] == "Caskroom")
    }

    private static func npmInstallation(_ path: URL, provider: String) -> (prefix: URL, name: String, version: String)? {
        let name = ["claude": "@anthropic-ai/claude-code", "codex": "@openai/codex", "gemini": "@google/gemini-cli"][provider]!
        let components = path.pathComponents
        for index in components.indices where components[index] == "lib" && index + 3 < components.count && components[index + 1] == "node_modules" {
            let prefix = URL(fileURLWithPath: NSString.path(withComponents: Array(components.prefix(index))))
            let root = prefix.appendingPathComponent("lib/node_modules").appendingPathComponent(name)
            if officialPackage(root, name: name, binary: provider, executable: path) {
                return (prefix, name, packageMetadata(root)?["version"] as? String ?? "")
            }
        }
        return nil
    }

    private static func officialPackage(_ root: URL, name: String, binary: String, executable: URL) -> Bool {
        guard let json = packageMetadata(root), json["name"] as? String == name,
              let bin = (json["bin"] as? [String: String])?[binary] ?? json["bin"] as? String,
              !bin.hasPrefix("/"), !bin.contains("\0") else { return false }
        let target = root.appendingPathComponent(bin).resolvingSymlinksInPath().standardizedFileURL
        let packageRoot = root.resolvingSymlinksInPath().standardizedFileURL.path + "/"
        return target.path.hasPrefix(packageRoot) && target == executable
    }

    private static func packageMetadata(_ root: URL) -> [String: Any]? {
        let file = root.appendingPathComponent("package.json")
        guard let size = try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 262_144,
              let data = try? Data(contentsOf: file), data.count <= 262_144 else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func npmRuntime(prefix: URL, environment: [String: String]) -> (node: URL, script: URL)? {
        var candidates = [prefix.appendingPathComponent("bin/npm")]
        candidates += executableCandidates("npm", environment: environment)
        for launcher in candidates where FileManager.default.isExecutableFile(atPath: launcher.path) {
            let script = launcher.resolvingSymlinksInPath().standardizedFileURL
            var root = script.deletingLastPathComponent()
            for _ in 0..<7 {
                if officialPackage(root, name: "npm", binary: "npm", executable: script) {
                    var nodes = [launcher.deletingLastPathComponent().appendingPathComponent("node")]
                    nodes += executableCandidates("node", environment: environment)
                    if let node = nodes.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) { return (node, script) }
                }
                let parent = root.deletingLastPathComponent(); if parent == root { break }; root = parent
            }
        }
        return nil
    }

    private static func boundedOutput(_ result: ProcessResult) -> String {
        let text = String(decoding: result.stdout, as: UTF8.self) + (result.stderr.isEmpty ? "" : "\n" + String(decoding: result.stderr, as: UTF8.self))
        let safe = String(text.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) || $0 == "\n" || $0 == "\t" })
        return safe.count > 8192 ? String(safe.suffix(8192)) : safe
    }
}
