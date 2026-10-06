import Foundation

/// Who each CLI is signed in as, and how to change it. The three CLIs keep
/// their own credentials; the app only asks them (or reads their account
/// files) for account labels and never exposes or persists credentials.
public struct CLIAccountStatus: Sendable, Equatable {
    public var provider: String
    public var installed: Bool
    /// nil when the CLI could not be asked.
    public var loggedIn: Bool?
    /// How the CLI is signed in, as shown to the user (follows the app language).
    public var method: String?
    /// The same method as a language-free id (`CLIAccountMethod`), for decisions.
    public var methodId: String?
    public var account: String?
    public var plan: String?
    public var detail: String
    /// False when the sign-in is not something the app can undo (an API key
    /// in the environment, Vertex AI credentials).
    public var canSignOut: Bool
    /// False when status only detects configuration, without checking remote
    /// access. nil preserves the CLI's ordinary account-status semantics.
    public var accessVerified: Bool?
    public init(provider: String, installed: Bool = true, loggedIn: Bool? = nil, method: String? = nil, methodId: String? = nil, account: String? = nil, plan: String? = nil, detail: String = "", canSignOut: Bool = true, accessVerified: Bool? = nil) {
        self.provider = provider; self.installed = installed; self.loggedIn = loggedIn; self.method = method; self.methodId = methodId; self.account = account; self.plan = plan; self.detail = detail; self.canSignOut = canSignOut; self.accessVerified = accessVerified
    }
    /// "user@example.com · Max · claude.ai"
    public var summary: String {
        guard loggedIn == true else { return loggedIn == false ? L("settings.cliAccounts.summarySignedOut") : (detail.isEmpty ? L("settings.cliAccounts.summaryUnknown") : detail) }
        if accessVerified == false {
            return [method, L("cliAccounts.summary.configured"), L("cliAccounts.summary.accessUnverified")].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        }
        let parts = [account, plan, method].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? L("settings.cliAccounts.summarySignedIn") : parts.joined(separator: " · ")
    }
}

/// Language-free ids of the sign-in methods `CLIAccountStatus.methodId` carries.
/// Decisions compare these, never the displayed `method`.
public enum CLIAccountMethod {
    public static let claudeSubscription = "claude-subscription"
    public static let claudeConsole = "claude-console"
    public static let claudeAPIKey = "claude-api-key"
    public static let claudeBedrock = "claude-bedrock"
    public static let claudeVertex = "claude-vertex"
    public static let claudeFoundry = "claude-foundry"
    public static let claudeExternal = "claude-external"
    public static let claudeOther = "claude-other"
    public static let codexChatGPT = "codex-chatgpt"
    public static let codexAPIKey = "codex-api-key"
    public static let geminiGoogle = "gemini-google"
    public static let geminiAPIKey = "gemini-api-key"
    public static let geminiVertex = "gemini-vertex"
    public static let geminiOther = "gemini-other"
}

public enum CLILoginOption: String, Sendable, CaseIterable {
    case account      // the provider's normal sign-in (browser)
    case console      // Claude: Anthropic Console billing instead of the subscription
    case bedrock      // Claude: the CLI's interactive AWS configuration wizard
}

public enum CLIAccountSupport {
    /// `claude auth status --json`
    public static func parseClaudeStatus(_ data: Data) -> CLIAccountStatus {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let loggedIn = object["loggedIn"] as? Bool else {
            return CLIAccountStatus(provider: "claude", detail: L("settings.cliAccounts.detailClaudeParseError"))
        }
        let authMethod = object["authMethod"] as? String
        let apiProvider = object["apiProvider"] as? String
        let externalMethods = ["bedrock": "AWS Bedrock", "vertex": "Google Vertex AI", "foundry": "Microsoft Foundry"]
        let externalIds = ["bedrock": CLIAccountMethod.claudeBedrock, "vertex": CLIAccountMethod.claudeVertex, "foundry": CLIAccountMethod.claudeFoundry]
        if authMethod == "third_party" || apiProvider.flatMap({ externalMethods[$0] }) != nil || authMethod == "api_key" {
            let method = apiProvider.flatMap { externalMethods[$0] } ?? (authMethod == "api_key" ? L("cliAccounts.method.anthropicApiKey") : L("cliAccounts.method.external"))
            let methodId = apiProvider.flatMap { externalIds[$0] } ?? (authMethod == "api_key" ? CLIAccountMethod.claudeAPIKey : CLIAccountMethod.claudeExternal)
            let detail = apiProvider == "bedrock"
                ? L("cliAccounts.detail.bedrockDetected")
                : L("cliAccounts.detail.externalDetected")
            // OAuth account labels may still be present from a previous login;
            // they do not identify the active external-provider credentials.
            return CLIAccountStatus(provider: "claude", loggedIn: loggedIn, method: method, methodId: methodId, detail: detail,
                                    canSignOut: false, accessVerified: false)
        }
        let method: String?, methodId: String?
        switch authMethod {
        case "claude.ai": method = L("windows.cli.method.claudeSubscription"); methodId = CLIAccountMethod.claudeSubscription
        case "console": method = "Anthropic Console"; methodId = CLIAccountMethod.claudeConsole
        case "none", nil: method = nil; methodId = nil
        default: method = L("cliAccounts.method.other"); methodId = CLIAccountMethod.claudeOther
        }
        let plan = (object["subscriptionType"] as? String).map { clean($0).capitalized }
        let organisation = (object["orgName"] as? String).map(clean)
        let email = (object["email"] as? String).map(clean)
        return CLIAccountStatus(provider: "claude", loggedIn: loggedIn, method: method, methodId: methodId, account: email ?? organisation, plan: plan)
    }

    /// The method label of a Codex API-key login, which a browser sign-in cannot renew.
    public static var codexAPIKeyMethod: String { L("windows.cli.method.apiKey") }
    /// `codex login status` text plus the account claims inside `auth.json`'s id token.
    public static func parseCodexStatus(text: String, authJSON: Data?) -> CLIAccountStatus {
        let lowered = text.lowercased()
        guard lowered.contains("logged in") || lowered.contains("not logged in") else { return CLIAccountStatus(provider: "codex", detail: L("settings.cliAccounts.detailCodexParseError")) }
        guard !lowered.contains("not logged in") else { return CLIAccountStatus(provider: "codex", loggedIn: false) }
        let method = lowered.contains("chatgpt") ? "ChatGPT" : lowered.contains("api key") ? codexAPIKeyMethod : nil
        let methodId = lowered.contains("chatgpt") ? CLIAccountMethod.codexChatGPT : lowered.contains("api key") ? CLIAccountMethod.codexAPIKey : nil
        var account: String?, plan: String?
        if let authJSON, let object = try? JSONSerialization.jsonObject(with: authJSON) as? [String: Any],
           let tokens = object["tokens"] as? [String: Any], let idToken = tokens["id_token"] as? String, let claims = jwtClaims(idToken) {
            account = (claims["email"] as? String).map(clean)
            plan = ((claims["https://api.openai.com/auth"] as? [String: Any])?["chatgpt_plan_type"] as? String).map { clean($0).capitalized }
        }
        return CLIAccountStatus(provider: "codex", loggedIn: true, method: method, methodId: methodId, account: account, plan: plan)
    }

    /// The method label of Gemini's Google sign-in, the only one a sign-in renews.
    public static var geminiGoogleMethod: String { L("windows.cli.method.googleAccount") }
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
            return CLIAccountStatus(provider: "gemini", loggedIn: true, method: geminiGoogleMethod, methodId: CLIAccountMethod.geminiGoogle, account: active)
        case "gemini-api-key":
            let present = environment["GEMINI_API_KEY"]?.isEmpty == false
            return CLIAccountStatus(provider: "gemini", loggedIn: present ? true : nil, method: L("windows.cli.method.geminiApiKey"), methodId: CLIAccountMethod.geminiAPIKey,
                                    detail: present ? L("settings.cliAccounts.detailGeminiApiKeyPresent") : L("settings.cliAccounts.detailGeminiApiKeyAbsent"), canSignOut: false)
        case "vertex-ai":
            let configured = environment["GOOGLE_APPLICATION_CREDENTIALS"]?.isEmpty == false || environment["GOOGLE_CLOUD_PROJECT"]?.isEmpty == false
                || FileManager.default.fileExists(atPath: home.appendingPathComponent(".config/gcloud/application_default_credentials.json").path)
            return CLIAccountStatus(provider: "gemini", loggedIn: configured ? true : nil, method: "Vertex AI", methodId: CLIAccountMethod.geminiVertex,
                                    detail: configured ? L("settings.cliAccounts.detailVertexPresent") : L("settings.cliAccounts.detailVertexAbsent"), canSignOut: false)
        default: return CLIAccountStatus(provider: "gemini", loggedIn: hasOAuth ? true : nil, method: selected.map(clean), methodId: selected == nil ? nil : CLIAccountMethod.geminiOther, account: active, canSignOut: hasOAuth)
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

    /// The command typed into the in-app terminal. Bedrock uses the CLI's
    /// interactive wizard; its provider flag applies only to that process.
    public static func loginCommand(provider: String, option: CLILoginOption = .account) -> String? {
        switch provider {
        case "claude":
            switch option {
            case .account: return "claude auth login"
            case .console: return "claude auth login --console"
            case .bedrock: return "CLAUDE_CODE_USE_BEDROCK=1 claude /setup-bedrock"
            }
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
    private let environmentResolver: CLIEnvironmentResolver
    public init(environment: [String: String]? = nil, home: URL = FileManager.default.homeDirectoryForCurrentUser, environmentResolver: CLIEnvironmentResolver = .shared) { fixedEnvironment = environment; self.home = home; self.environmentResolver = environmentResolver }

    private func environmentSnapshot() async -> CLIEnvironmentSnapshot {
        if let fixedEnvironment { return CLIEnvironmentSnapshot(values: fixedEnvironment, source: .provided) }
        return await environmentResolver.resolve(workspacePath: home.path, forceRefresh: true)
    }

    /// Call once after an authentication change, before refreshing accounts.
    public func invalidateEnvironment() async {
        if fixedEnvironment == nil { await environmentResolver.invalidate(workspacePath: home.path) }
    }

    /// When Gemini's credentials file was last written (`CLIGeminiLogin`).
    public nonisolated func geminiCredentialsStamp() -> Date? { CLIGeminiLogin.credentialsStamp(home: home) }

    /// The environment the status and logout commands run with, for a sign-in
    /// the app runs in the background itself.
    public func commandEnvironment() async -> [String: String] { await environmentSnapshot().values }

    public func status(provider: String) async -> CLIAccountStatus {
        let snapshot = await environmentSnapshot()
        var result = await status(provider: provider, snapshot: snapshot)
        if let detail = snapshot.fallbackDetail { result.detail += (result.detail.isEmpty ? "" : " ") + detail }
        return result
    }

    private func status(provider: String, snapshot: CLIEnvironmentSnapshot) async -> CLIAccountStatus {
        let environment = snapshot.values
        if provider == "gemini" {
            guard installed("gemini", environment: environment) else { return CLIAccountStatus(provider: provider, installed: false, detail: L("settings.cliAccounts.detailGeminiNotInstalled")) }
            return CLIAccountSupport.geminiStatus(home: home, environment: environment)
        }
        guard let arguments = CLIAccountSupport.statusArguments(provider: provider) else { return CLIAccountStatus(provider: provider, installed: false, detail: L("settings.cliAccounts.detailUnsupportedProvider")) }
        guard installed(arguments[0], environment: environment) else { return CLIAccountStatus(provider: provider, installed: false, detail: L("settings.cliAccounts.detailNotInstalledTemplate", ["provider": ProviderOptions.label(provider)])) }
        guard let result = await run(arguments, environment: environment, timeout: 20) else { return CLIAccountStatus(provider: provider, detail: L("settings.cliAccounts.detailRunFailed")) }
        if provider == "claude" {
            var status = CLIAccountSupport.parseClaudeStatus(result.stdout)
            // Only the CLI's own JSON says "signed out"; a timeout or an old
            // CLI without `auth status` stays unknown.
            if status.loggedIn == nil { status.detail = result.exitCode == -1 ? L("settings.cliAccounts.detailClaudeTimeout") : L("settings.cliAccounts.detailClaudeUnknown") }
            if snapshot.source != .processFallback, status.methodId == CLIAccountMethod.claudeBedrock, let conflict = BedrockAuthDiagnostics.conflictDetail(environment: environment, home: home) {
                status.detail += (status.detail.isEmpty ? "" : " ") + conflict
            }
            return status
        }
        let text = String(decoding: result.stdout + result.stderr, as: UTF8.self)
        let auth = CLIAccountSupport.boundedData(CLIAccountSupport.codexHome(home: home, environment: environment).appendingPathComponent("auth.json"))
        return CLIAccountSupport.parseCodexStatus(text: text, authJSON: auth)
    }

    /// Runs the CLI's logout (or Gemini's file-level sign-out) and reports the new status.
    public func logout(provider: String) async -> CLIAccountStatus {
        let snapshot = await environmentSnapshot()
        if provider == "gemini" {
            do { try CLIAccountSupport.geminiLogout(home: home) } catch { return CLIAccountStatus(provider: provider, detail: L("settings.cliAccounts.detailGeminiLogoutFailedTemplate", ["reason": error.localizedDescription])) }
        } else if let arguments = CLIAccountSupport.logoutArguments(provider: provider) {
            _ = await run(arguments, environment: snapshot.values, timeout: 30)
        }
        var result = await status(provider: provider, snapshot: snapshot)
        if let detail = snapshot.fallbackDetail { result.detail += (result.detail.isEmpty ? "" : " ") + detail }
        return result
    }

    private func installed(_ command: String, environment: [String: String]) -> Bool {
        (environment["PATH"] ?? "").split(separator: ":").contains { FileManager.default.isExecutableFile(atPath: String($0) + "/" + command) }
    }
    private func run(_ arguments: [String], environment: [String: String], timeout: TimeInterval) async -> ProcessResult? {
        let stdout = AppUpdateService.OutputSink(), stderr = AppUpdateService.OutputSink()
        var env = environment; env["NO_COLOR"] = "1"; env["CI"] = "1"
        guard let child = try? NativeChildProcess(executable: URL(fileURLWithPath: "/usr/bin/env"), arguments: arguments, environment: env, cwd: home,
                                                  stdout: { stdout.append($0) }, stderr: { stderr.append($0) }, exited: { _ in }) else { return nil }
        child.closeInput()
        let code = await child.wait(timeout: timeout)
        return ProcessResult(exitCode: code, stdout: stdout.data, stderr: stderr.data)
    }
}
