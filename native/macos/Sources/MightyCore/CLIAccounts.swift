import Foundation

/// Who each CLI is signed in as, and how to change it. The three CLIs keep
/// their own credentials; the app only asks them (or reads their account
/// files) for the account label, never for tokens.
public struct CLIAccountStatus: Sendable, Equatable {
    public var provider: String
    public var installed: Bool
    /// nil when the CLI could not be asked.
    public var loggedIn: Bool?
    public var method: String?
    public var account: String?
    public var plan: String?
    public var detail: String
    /// False when the sign-in is not something the app can undo (an API key
    /// in the environment, Vertex AI credentials).
    public var canSignOut: Bool
    public init(provider: String, installed: Bool = true, loggedIn: Bool? = nil, method: String? = nil, account: String? = nil, plan: String? = nil, detail: String = "", canSignOut: Bool = true) {
        self.provider = provider; self.installed = installed; self.loggedIn = loggedIn; self.method = method; self.account = account; self.plan = plan; self.detail = detail; self.canSignOut = canSignOut
    }
    /// "user@example.com · Max · claude.ai"
    public var summary: String {
        guard loggedIn == true else { return loggedIn == false ? "로그인되지 않음" : (detail.isEmpty ? "상태를 확인하지 못했습니다." : detail) }
        let parts = [account, plan, method].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? "로그인됨" : parts.joined(separator: " · ")
    }
}

public enum CLILoginOption: String, Sendable, CaseIterable {
    case account      // the provider's normal sign-in (browser)
    case console      // Claude: Anthropic Console billing instead of the subscription
}

public enum CLIAccountSupport {
    /// `claude auth status --json`
    public static func parseClaudeStatus(_ data: Data) -> CLIAccountStatus {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let loggedIn = object["loggedIn"] as? Bool else {
            return CLIAccountStatus(provider: "claude", detail: "Claude 로그인 상태를 읽지 못했습니다.")
        }
        let method = (object["authMethod"] as? String).map { $0 == "claude.ai" ? "Claude 구독" : $0 == "console" ? "Anthropic Console" : clean($0) }
        let plan = (object["subscriptionType"] as? String).map { clean($0).capitalized }
        let organisation = (object["orgName"] as? String).map(clean)
        let email = (object["email"] as? String).map(clean)
        return CLIAccountStatus(provider: "claude", loggedIn: loggedIn, method: method, account: email ?? organisation, plan: plan)
    }

    /// `codex login status` text plus the account claims inside `auth.json`'s id token.
    public static func parseCodexStatus(text: String, authJSON: Data?) -> CLIAccountStatus {
        let lowered = text.lowercased()
        guard lowered.contains("logged in") || lowered.contains("not logged in") else { return CLIAccountStatus(provider: "codex", detail: "Codex 로그인 상태를 읽지 못했습니다.") }
        guard !lowered.contains("not logged in") else { return CLIAccountStatus(provider: "codex", loggedIn: false) }
        let method = lowered.contains("chatgpt") ? "ChatGPT" : lowered.contains("api key") ? "API 키" : nil
        var account: String?, plan: String?
        if let authJSON, let object = try? JSONSerialization.jsonObject(with: authJSON) as? [String: Any],
           let tokens = object["tokens"] as? [String: Any], let idToken = tokens["id_token"] as? String, let claims = jwtClaims(idToken) {
            account = (claims["email"] as? String).map(clean)
            plan = ((claims["https://api.openai.com/auth"] as? [String: Any])?["chatgpt_plan_type"] as? String).map { clean($0).capitalized }
        }
        return CLIAccountStatus(provider: "codex", loggedIn: true, method: method, account: account, plan: plan)
    }

    /// Gemini has no status command: the selected auth type is in
    /// `settings.json`, the Google account in `google_accounts.json`, and the
    /// OAuth session exists while `oauth_creds.json` does.
    public static func geminiStatus(home: URL = FileManager.default.homeDirectoryForCurrentUser, environment: [String: String] = ProcessInfo.processInfo.environment) -> CLIAccountStatus {
        let directory = home.appendingPathComponent(".gemini", isDirectory: true)
        func object(_ name: String) -> [String: Any]? {
            guard let data = boundedData(directory.appendingPathComponent(name)) else { return nil }
            return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        }
        let settings = object("settings.json")
        let selected = ((settings?["security"] as? [String: Any])?["auth"] as? [String: Any])?["selectedType"] as? String ?? settings?["selectedAuthType"] as? String
        let hasOAuth = FileManager.default.fileExists(atPath: directory.appendingPathComponent("oauth_creds.json").path)
        let active = (object("google_accounts.json")?["active"] as? String).map(clean)
        switch selected {
        case "oauth-personal", nil:
            guard hasOAuth else { return CLIAccountStatus(provider: "gemini", loggedIn: false) }
            return CLIAccountStatus(provider: "gemini", loggedIn: true, method: "Google 계정", account: active)
        case "gemini-api-key":
            let present = environment["GEMINI_API_KEY"]?.isEmpty == false
            return CLIAccountStatus(provider: "gemini", loggedIn: present ? true : nil, method: "Gemini API 키",
                                    detail: present ? "GEMINI_API_KEY 환경 변수로 인증합니다. 바꾸려면 그 값을 바꾸거나 Gemini의 /auth에서 방식을 바꾸세요." : "GEMINI_API_KEY 환경 변수를 확인하세요.", canSignOut: false)
        case "vertex-ai":
            let configured = environment["GOOGLE_APPLICATION_CREDENTIALS"]?.isEmpty == false || environment["GOOGLE_CLOUD_PROJECT"]?.isEmpty == false
                || FileManager.default.fileExists(atPath: home.appendingPathComponent(".config/gcloud/application_default_credentials.json").path)
            return CLIAccountStatus(provider: "gemini", loggedIn: configured ? true : nil, method: "Vertex AI",
                                    detail: configured ? "Google Cloud 자격 증명으로 인증합니다. gcloud에서 계정을 바꾸세요." : "Vertex AI 자격 증명을 확인하지 못했습니다.", canSignOut: false)
        default: return CLIAccountStatus(provider: "gemini", loggedIn: hasOAuth ? true : nil, method: selected.map(clean), account: active, canSignOut: hasOAuth)
        }
    }

    /// Signs Gemini out the way its `/auth` screen does: drop the OAuth
    /// session and move the active Google account to the old list.
    public static func geminiLogout(home: URL = FileManager.default.homeDirectoryForCurrentUser) throws {
        let directory = home.appendingPathComponent(".gemini", isDirectory: true)
        let credentials = directory.appendingPathComponent("oauth_creds.json")
        if FileManager.default.fileExists(atPath: credentials.path) { try FileManager.default.removeItem(at: credentials) }
        let accounts = directory.appendingPathComponent("google_accounts.json")
        guard let data = try? Data(contentsOf: accounts), var object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let active = object["active"] as? String else { return }
        var old = object["old"] as? [String] ?? []
        if !old.contains(active) { old.append(active) }
        object["old"] = old; object["active"] = NSNull()
        // Another tool owns this file: keep its mode across the atomic rewrite.
        let mode = try? FileManager.default.attributesOfItem(atPath: accounts.path)[.posixPermissions]
        try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted]).write(to: accounts, options: .atomic)
        if let mode { try? FileManager.default.setAttributes([.posixPermissions: mode], ofItemAtPath: accounts.path) }
    }

    /// The command typed into the in-app terminal to sign in. Each opens the
    /// browser; Gemini asks for the auth method when it starts signed out.
    public static func loginCommand(provider: String, option: CLILoginOption = .account) -> String? {
        switch provider {
        case "claude": return option == .console ? "claude auth login --console" : "claude auth login"
        case "codex": return "codex login"
        case "gemini": return "gemini"
        default: return nil
        }
    }
    /// argv for the CLI's own status / logout command (Gemini has neither).
    public static func statusArguments(provider: String) -> [String]? {
        switch provider { case "claude": return ["claude", "auth", "status", "--json"]; case "codex": return ["codex", "login", "status"]; default: return nil }
    }
    public static func logoutArguments(provider: String) -> [String]? {
        switch provider { case "claude": return ["claude", "auth", "logout"]; case "codex": return ["codex", "logout"]; default: return nil }
    }

    static func jwtClaims(_ token: String) -> [String: Any]? {
        let parts = token.split(separator: ".")
        guard parts.count == 3 else { return nil }
        var payload = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while payload.count % 4 != 0 { payload.append("=") }
        guard let data = Data(base64Encoded: payload), data.count <= 65_536 else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }
    static func clean(_ value: String) -> String { ActivitySupport.clean(value, maximumBytes: 200, singleLine: true) }
    /// Reads a small regular file, checking its size before loading it.
    static func boundedData(_ url: URL, maximumBytes: Int = 1_048_576) -> Data? {
        guard let values = try? url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]), values.isRegularFile == true,
              let size = values.fileSize, size <= maximumBytes else { return nil }
        return try? Data(contentsOf: url)
    }
    /// Codex keeps its files under `$CODEX_HOME` when that is set.
    public static func codexHome(home: URL, environment: [String: String]) -> URL {
        environment["CODEX_HOME"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) } ?? home.appendingPathComponent(".codex", isDirectory: true)
    }
}

public actor CLIAccountService {
    private let fixedEnvironment: [String: String]?
    private let home: URL
    /// The PATH is recomputed per call so a CLI installed after launch is found.
    private var environment: [String: String] { fixedEnvironment ?? ProviderService.runtimeEnvironment() }
    public init(environment: [String: String]? = nil, home: URL = FileManager.default.homeDirectoryForCurrentUser) { fixedEnvironment = environment; self.home = home }

    public func status(provider: String) async -> CLIAccountStatus {
        if provider == "gemini" {
            guard await installed("gemini") else { return CLIAccountStatus(provider: provider, installed: false, detail: "Gemini CLI가 설치되어 있지 않습니다.") }
            return CLIAccountSupport.geminiStatus(home: home, environment: environment)
        }
        guard let arguments = CLIAccountSupport.statusArguments(provider: provider) else { return CLIAccountStatus(provider: provider, installed: false, detail: "지원하지 않는 실행기입니다.") }
        guard await installed(arguments[0]) else { return CLIAccountStatus(provider: provider, installed: false, detail: "\(ProviderOptions.label(provider)) CLI가 설치되어 있지 않습니다.") }
        guard let result = await run(arguments, timeout: 20) else { return CLIAccountStatus(provider: provider, detail: "상태 명령을 실행하지 못했습니다.") }
        if provider == "claude" {
            var status = CLIAccountSupport.parseClaudeStatus(result.stdout)
            // Only the CLI's own JSON says "signed out"; a timeout or an old
            // CLI without `auth status` stays unknown.
            if status.loggedIn == nil { status.detail = result.exitCode == -1 ? "Claude 상태 확인이 제한 시간 안에 끝나지 않았습니다." : "이 Claude CLI에서 로그인 상태를 읽지 못했습니다. CLI를 업데이트해 보세요." }
            return status
        }
        let text = String(decoding: result.stdout + result.stderr, as: UTF8.self)
        let auth = CLIAccountSupport.boundedData(CLIAccountSupport.codexHome(home: home, environment: environment).appendingPathComponent("auth.json"))
        return CLIAccountSupport.parseCodexStatus(text: text, authJSON: auth)
    }

    /// Runs the CLI's logout (or Gemini's file-level sign-out) and reports the new status.
    public func logout(provider: String) async -> CLIAccountStatus {
        if provider == "gemini" {
            do { try CLIAccountSupport.geminiLogout(home: home) } catch { return CLIAccountStatus(provider: provider, detail: "Gemini 로그아웃에 실패했습니다: \(error.localizedDescription)") }
        } else if let arguments = CLIAccountSupport.logoutArguments(provider: provider) {
            _ = await run(arguments, timeout: 30)
        }
        return await status(provider: provider)
    }

    private func installed(_ command: String) async -> Bool {
        (environment["PATH"] ?? "").split(separator: ":").contains { FileManager.default.isExecutableFile(atPath: String($0) + "/" + command) }
    }
    private func run(_ arguments: [String], timeout: TimeInterval) async -> ProcessResult? {
        let stdout = AppUpdateService.OutputSink(), stderr = AppUpdateService.OutputSink()
        var env = environment; env["NO_COLOR"] = "1"; env["CI"] = "1"
        guard let child = try? NativeChildProcess(executable: URL(fileURLWithPath: "/usr/bin/env"), arguments: arguments, environment: env, cwd: home,
                                                  stdout: { stdout.append($0) }, stderr: { stderr.append($0) }, exited: { _ in }) else { return nil }
        child.closeInput()
        let code = await child.wait(timeout: timeout)
        return ProcessResult(exitCode: code, stdout: stdout.data, stderr: stderr.data)
    }
}
