import Foundation

/// One thing the app depends on and can check or install from Settings:
/// Tailscale for remote access, each agent CLI, and any plugin the app
/// requires for an installed agent (see `ComponentCatalog`).
public struct ComponentStatus: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var title: String
    /// installed · missing · attention · checking · unsupported
    public var state: String
    public var version: String?
    public var detail: String
    public var actions: [ComponentAction]
    public init(id: String, title: String, state: String, version: String? = nil, detail: String, actions: [ComponentAction] = []) {
        self.id = id; self.title = title; self.state = state; self.version = version; self.detail = detail; self.actions = actions
    }
}

public struct ComponentAction: Codable, Sendable, Equatable, Identifiable {
    /// install · open-store · launch · login · connect · update · install-plugin · copy-command
    public var id: String
    public var title: String
    public var primary: Bool
    public init(id: String, title: String, primary: Bool = true) { self.id = id; self.title = title; self.primary = primary }
}

/// A CLI marketplace plugin the app needs when that agent is installed.
/// Empty today: the Claude Mod the app relies on is bundled and loaded per
/// run, so nothing has to be installed into the agent. Adding an entry here
/// makes Settings check and install it through the existing plugin services.
public struct RequiredPlugin: Codable, Sendable, Equatable, Identifiable {
    public var provider: String
    public var pluginID: String
    public var title: String
    public var reason: String
    public var id: String { provider + ":" + pluginID }
    public init(provider: String, pluginID: String, title: String, reason: String) {
        self.provider = provider; self.pluginID = pluginID; self.title = title; self.reason = reason
    }
}

public enum ComponentCatalog {
    public static let requiredPlugins: [RequiredPlugin] = []
    public static let tailscaleAppStoreURL = "macappstore://apps.apple.com/app/id1475387142"
    public static let tailscaleDownloadURL = "https://tailscale.com/download/mac"
    /// Copyable install commands for agents that are not installed. The app
    /// never runs these itself: fresh CLI installs stay a deliberate user step.
    public static func installCommand(provider: String) -> String? {
        switch provider {
        case "claude": return "npm install -g @anthropic-ai/claude-code"
        case "codex": return "npm install -g @openai/codex"
        case "gemini": return "npm install -g @google/gemini-cli"
        default: return nil
        }
    }
}

public struct TailscaleInspection: Sendable, Equatable {
    /// missing · needs-launch · needs-login · needs-connect · connected
    public var phase: String
    public var appPath: String?
    public var cliPath: String?
    public var version: String?
    public var addresses: [String]
    public var brewAvailable: Bool
    /// Why Homebrew cannot be used right now (e.g. the Xcode license), or nil.
    public var brewIssue: String?
    public var detail: String
    public init(phase: String, appPath: String? = nil, cliPath: String? = nil, version: String? = nil, addresses: [String] = [], brewAvailable: Bool = false, brewIssue: String? = nil, detail: String) {
        self.phase = phase; self.appPath = appPath; self.cliPath = cliPath; self.version = version; self.addresses = addresses; self.brewAvailable = brewAvailable; self.brewIssue = brewIssue; self.detail = detail
    }
}

public enum TailscaleInstallOutcome: Sendable, Equatable {
    case installed(String)
    case openStore
    case failed(String)
}

/// Checks, installs (Homebrew cask) and logs in Tailscale without any shell
/// command construction: every step is a direct executable with fixed
/// arguments, and the store fallback is a URL the app opens.
public actor TailscaleInstaller {
    private let environment: [String: String]
    private let applicationPaths: [URL]
    private let cliCandidates: [URL]
    private let brewCandidates: [URL]
    /// Homebrew casks need git, and Apple's git shim refuses to run until the
    /// Xcode license is accepted. Probing it is instant and decisive.
    private let gitProbe: URL
    private let statusTimeout: TimeInterval
    private let installTimeout: TimeInterval
    private let loginTimeout: TimeInterval
    private var busy = false

    public init(environment: [String: String]? = nil) {
        let env = environment ?? ProviderService.runtimeEnvironment()
        let home = FileManager.default.homeDirectoryForCurrentUser
        self.init(environment: env,
                  applicationPaths: [URL(fileURLWithPath: "/Applications/Tailscale.app"), home.appendingPathComponent("Applications/Tailscale.app")],
                  cliCandidates: Self.defaultCLICandidates(environment: env),
                  brewCandidates: [URL(fileURLWithPath: "/opt/homebrew/bin/brew"), URL(fileURLWithPath: "/usr/local/bin/brew")],
                  gitProbe: URL(fileURLWithPath: "/usr/bin/git"), statusTimeout: 5, installTimeout: 600, loginTimeout: 8)
    }

    init(environment: [String: String], applicationPaths: [URL], cliCandidates: [URL], brewCandidates: [URL], gitProbe: URL, statusTimeout: TimeInterval, installTimeout: TimeInterval, loginTimeout: TimeInterval) {
        self.environment = environment; self.applicationPaths = applicationPaths; self.cliCandidates = cliCandidates; self.brewCandidates = brewCandidates; self.gitProbe = gitProbe
        self.statusTimeout = statusTimeout; self.installTimeout = installTimeout; self.loginTimeout = loginTimeout
    }

    static func defaultCLICandidates(environment: [String: String]) -> [URL] {
        let search = (environment["PATH"] ?? "").split(separator: ":").map(String.init).filter { $0.hasPrefix("/") }
        var seen = Set<String>(); var result: [URL] = []
        for directory in search + ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"] where seen.insert(directory).inserted {
            result.append(URL(fileURLWithPath: directory).appendingPathComponent("tailscale"))
        }
        result.append(URL(fileURLWithPath: "/Applications/Tailscale.app/Contents/MacOS/Tailscale"))
        result.append(URL(fileURLWithPath: "/Applications/Tailscale.app/Contents/MacOS/tailscale"))
        return result
    }

    private var installedApp: URL? { applicationPaths.first { FileManager.default.fileExists(atPath: $0.path) } }
    private var cli: URL? { cliCandidates.first { FileManager.default.isExecutableFile(atPath: $0.path) } }
    private var brew: URL? { brewCandidates.first { FileManager.default.isExecutableFile(atPath: $0.path) } }

    /// Homebrew refuses to run until Xcode's license is accepted, and that
    /// happens in seconds, so probe it before offering the button.
    private func brewHealth() async -> String? {
        guard let brew else { return "Homebrew가 설치되어 있지 않습니다." }
        var env = environment; env["NONINTERACTIVE"] = "1"; env["HOMEBREW_NO_AUTO_UPDATE"] = "1"; env["HOMEBREW_NO_ENV_HINTS"] = "1"
        guard let result = try? await ProcessCapture.run(executable: brew, arguments: ["--version"], environment: env, timeout: statusTimeout) else { return "Homebrew를 실행하지 못했습니다." }
        guard result.exitCode == 0 else { return Self.brewFailureHint(String(decoding: result.stdout + result.stderr, as: UTF8.self)) }
        if FileManager.default.isExecutableFile(atPath: gitProbe.path),
           let git = try? await ProcessCapture.run(executable: gitProbe, arguments: ["--version"], environment: env, timeout: statusTimeout), git.exitCode != 0 {
            let output = String(decoding: git.stdout + git.stderr, as: UTF8.self)
            if output.contains("Xcode license") { return Self.brewFailureHint(output) }
        }
        return nil
    }
    static func brewFailureHint(_ output: String) -> String {
        if output.contains("Xcode license") { return "Homebrew가 Xcode 라이선스 동의를 요구합니다. 터미널에서 `sudo xcodebuild -license accept`를 실행한 뒤 다시 시도하세요." }
        return "Homebrew를 사용할 수 없습니다: " + String(output.trimmingCharacters(in: .whitespacesAndNewlines).suffix(300))
    }

    public func inspect() async -> TailscaleInspection {
        let app = installedApp; let cli = cli
        let issue = await brewHealth()
        let brew = issue == nil
        guard app != nil || cli != nil else {
            let detail = brew ? "Homebrew로 Tailscale을 설치할 수 있습니다." : "Mac App Store에서 Tailscale을 설치하세요." + (issue.map { $0.hasPrefix("Homebrew가 설치") ? "" : " " + $0 } ?? "")
            return TailscaleInspection(phase: "missing", brewAvailable: brew, brewIssue: issue, detail: detail)
        }
        guard let cli else {
            return TailscaleInspection(phase: "needs-launch", appPath: app?.path, brewAvailable: brew, detail: "Tailscale 앱을 한 번 실행하면 명령줄 도구가 준비됩니다.")
        }
        var version: String?
        if let result = try? await ProcessCapture.run(executable: cli, arguments: ["version"], environment: environment, timeout: statusTimeout), result.exitCode == 0 {
            version = String(decoding: result.stdout, as: UTF8.self).split(separator: "\n").first.map { String($0).trimmingCharacters(in: .whitespaces) }
        }
        guard let result = try? await ProcessCapture.run(executable: cli, arguments: ["status", "--json"], environment: environment, timeout: statusTimeout),
              let json = try? JSONSerialization.jsonObject(with: result.stdout) as? [String: Any] else {
            return TailscaleInspection(phase: "needs-launch", appPath: app?.path, cliPath: cli.path, version: version, brewAvailable: brew, detail: "Tailscale이 실행 중이 아닙니다. 앱을 실행하세요.")
        }
        let own = json["Self"] as? [String: Any] ?? [:]
        let addresses = ((json["TailscaleIPs"] as? [String]) ?? (own["TailscaleIPs"] as? [String]) ?? []).map(RemoteIPPolicy.normalized).filter { RemoteIPPolicy.allowed($0) }
        let backend = json["BackendState"] as? String ?? ""
        switch backend {
        case "Running" where !addresses.isEmpty:
            let name = own["DNSName"] as? String
            return TailscaleInspection(phase: "connected", appPath: app?.path, cliPath: cli.path, version: version, addresses: addresses, brewAvailable: brew,
                                       detail: "연결됨 · " + (name.map { $0.hasSuffix(".") ? String($0.dropLast()) : $0 } ?? addresses[0]))
        case "NeedsLogin", "NeedsMachineAuth":
            return TailscaleInspection(phase: "needs-login", appPath: app?.path, cliPath: cli.path, version: version, brewAvailable: brew, detail: backend == "NeedsMachineAuth" ? "관리자가 이 기기를 승인해야 합니다." : "Tailscale 계정으로 로그인하세요.")
        case "Stopped":
            return TailscaleInspection(phase: "needs-connect", appPath: app?.path, cliPath: cli.path, version: version, brewAvailable: brew, detail: "로그인은 되어 있지만 연결이 꺼져 있습니다.")
        default:
            return TailscaleInspection(phase: "needs-launch", appPath: app?.path, cliPath: cli.path, version: version, brewAvailable: brew, detail: "Tailscale 상태: \(backend.isEmpty ? "알 수 없음" : backend). 앱을 실행하세요.")
        }
    }

    /// Homebrew cask when Homebrew is present; otherwise the caller opens the
    /// App Store page. Only one install or login runs at a time.
    public func install() async -> TailscaleInstallOutcome {
        guard !busy else { return .failed("다른 설치 작업이 진행 중입니다.") }
        guard let brew else { return .openStore }
        busy = true; defer { busy = false }
        var env = environment
        env["NONINTERACTIVE"] = "1"; env["HOMEBREW_NO_AUTO_UPDATE"] = "1"; env["HOMEBREW_NO_ENV_HINTS"] = "1"; env["HOMEBREW_NO_INSTALL_UPGRADE"] = "1"
        do {
            let result = try await ProcessCapture.run(executable: brew, arguments: ["install", "--cask", "tailscale"], environment: env, timeout: installTimeout, maximumBytes: 4 * 1024 * 1024)
            let output = String(decoding: result.stdout + result.stderr, as: UTF8.self)
            guard result.exitCode == 0 else { return .failed(output.contains("Xcode license") ? Self.brewFailureHint(output) : "Homebrew 설치가 실패했습니다 (종료 코드 \(result.exitCode)). " + String(output.suffix(600))) }
            return .installed(String(output.suffix(400)))
        } catch { return .failed("Homebrew를 실행하지 못했습니다: \(error.localizedDescription)") }
    }

    /// Starts the login flow and returns the browser URL the CLI prints. The
    /// CLI keeps waiting in the background; the daemon completes the login
    /// once the browser finishes, so the process is not kept alive here.
    public func loginURL() async -> URL? {
        guard !busy, let cli else { return nil }
        busy = true; defer { busy = false }
        let result = try? await ProcessCapture.run(executable: cli, arguments: ["login"], environment: environment, timeout: loginTimeout)
        let text = result.map { String(decoding: $0.stdout + $0.stderr, as: UTF8.self) } ?? ""
        guard let range = text.range(of: #"https://login\.tailscale\.com/\S+"#, options: .regularExpression) else { return nil }
        return URL(string: String(text[range]))
    }

    /// `tailscale up` for a logged-in but stopped node.
    public func connect() async -> String? {
        guard !busy, let cli else { return nil }
        busy = true; defer { busy = false }
        let result = try? await ProcessCapture.run(executable: cli, arguments: ["up"], environment: environment, timeout: 15)
        guard let result, result.exitCode == 0 else { return result.map { String(decoding: $0.stderr, as: UTF8.self).suffix(300) }.map(String.init) ?? "연결 명령을 실행하지 못했습니다." }
        return nil
    }
}
