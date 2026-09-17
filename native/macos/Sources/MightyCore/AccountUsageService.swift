import Foundation
import Security
import CryptoKit
import CoreFoundation

struct AccountUsageHTTPResponse: Sendable {
    var status: Int
    var data: Data
    var retryAfter: String?
}
struct ClaudeQuotaCredential: Sendable {
    var token: String
    var plan: String?
}
enum AccountUsageFailure: Error {
    case unavailable(String), invalidResponse, authentication, rateLimited(TimeInterval), network
    /// macOS refused the login keychain item without a user prompt. Only an
    /// interactive read (a click) may show the Keychain dialog.
    case keychainPermission
}

/// Local account reads only. The caller selects providers with local open sessions.
/// Codex owns its authentication; Claude credentials are read without rotation or writes.
public actor AccountUsageService {
    private let probe: @Sendable (String, Bool) async throws -> AccountUsageSnapshot
    private let now: @Sendable () -> Date
    private var tasks: [String: Task<AccountUsageSnapshot, Never>] = [:]
    private var cache: [String: AccountUsageSnapshot] = [:]
    private var nextRead: [String: Date] = [:]
    private var closing = false

    public init() {
        let environment = ProviderService.runtimeEnvironment()
        probe = { provider, interactive in
            if provider == "claude" { return try await Self.claude(environment: environment, interactive: interactive) }
            if provider == "codex" {
                let providers = ProviderService(environment: environment)
                let command = await providers.command(provider: provider)
                await providers.shutdown()
                guard let command else { throw AccountUsageFailure.unavailable("Codex CLI를 설치하고 로그인하세요.") }
                return try await CodexAccountProbe.read(command: command, environment: environment)
            }
            throw AccountUsageFailure.unavailable(provider == "gemini"
                ? "Gemini CLI는 이 연결 방식에서 계정 한도를 제공하지 않습니다. CLI의 /stats에서 확인하세요."
                : "지원하지 않는 계정입니다.")
        }
        now = { Date() }
    }

    init(now: @escaping @Sendable () -> Date = { Date() }, probe: @escaping @Sendable (String) async throws -> AccountUsageSnapshot) {
        self.now = now; self.probe = { provider, _ in try await probe(provider) }
    }
    init(now: @escaping @Sendable () -> Date = { Date() }, interactiveProbe: @escaping @Sendable (String, Bool) async throws -> AccountUsageSnapshot) {
        self.now = now; self.probe = interactiveProbe
    }

    /// `interactive` marks a read the user asked for; only then may the login
    /// keychain show its access dialog, and a pending permission state retries.
    public func read(provider: String, force: Bool = false, interactive: Bool = false) async -> AccountUsageSnapshot {
        guard !closing, !Task.isCancelled else { return AccountUsageSnapshot(provider: provider, status: "cancelled", detail: "계정 조회를 종료했습니다.") }
        if let task = tasks[provider] { return await task.value }
        if interactive, cache[provider]?.status == "permission" { nextRead.removeValue(forKey: provider) }
        let instant = now()
        // Force refresh still respects server cooldown and the minimum request interval.
        if let deadline = nextRead[provider], deadline > instant, let saved = cache[provider] { return saved }
        if !force, let saved = cache[provider], let stamp = saved.fetchedAt.flatMap(Self.date), instant.timeIntervalSince(stamp) < 60 { return saved }
        let operation = probe
        let task = Task { [weak self] in
            let result: AccountUsageSnapshot
            do {
                var snapshot = try await operation(provider, interactive)
                try Task.checkCancellation()
                snapshot.fetchedAt = Self.timestamp(instant)
                result = snapshot
            } catch {
                guard let self else { return AccountUsageSnapshot(provider: provider, status: "cancelled") }
                return await self.failed(provider: provider, error: error, at: instant)
            }
            guard let self else { return result }
            await self.store(result, at: instant)
            return result
        }
        tasks[provider] = task
        let result = await withTaskCancellationHandler(operation: { await task.value }, onCancel: { task.cancel() })
        tasks.removeValue(forKey: provider)
        return result
    }

    public func shutdown() async {
        closing = true
        let active = Array(tasks.values); active.forEach { $0.cancel() }
        for task in active { _ = await task.value }
        tasks.removeAll(); cache.removeAll(); nextRead.removeAll()
    }

    private func store(_ value: AccountUsageSnapshot, at date: Date) {
        guard !closing else { return }
        cache[value.provider] = value
        let delay = value.retryAfterSeconds.map { $0.isFinite ? min(86400, max(60, $0)) : 300 } ?? 5
        nextRead[value.provider] = now().addingTimeInterval(delay)
    }
    private func failed(provider: String, error: Error, at date: Date) -> AccountUsageSnapshot {
        if Task.isCancelled || error is CancellationError || closing { return AccountUsageSnapshot(provider: provider, status: "cancelled", detail: "계정 조회를 취소했습니다.") }
        var delay: TimeInterval = 60
        var detail = "계정 사용량을 갱신하지 못했습니다. 잠시 후 다시 확인하세요."
        var preserve = true
        var status: String?
        if let failure = error as? AccountUsageFailure {
            switch failure {
            case .unavailable(let reason): detail = reason; preserve = false
            case .authentication: detail = "CLI 로그인을 다시 확인하세요. 계정 한도 조회 권한이 없거나 로그인이 만료되었습니다."; preserve = false
            case .rateLimited(let seconds): delay = min(86400, max(60, seconds)); detail = "조회가 제한되었습니다. 잠시 후 자동으로 다시 확인합니다."
            case .keychainPermission:
                // Automatic polling must stay silent; the user grants access by refreshing.
                delay = 3600; preserve = false; status = "permission"
                detail = "macOS Keychain의 Claude Code 로그인 정보에 접근해야 계정 한도를 읽을 수 있습니다. 새로고침을 누르면 접근 허용 창이 열립니다. \"항상 허용\"을 선택하면 이 빌드에서는 다시 묻지 않습니다."
            case .invalidResponse, .network: break
            }
        }
        var value = preserve ? cache[provider] ?? AccountUsageSnapshot(provider: provider) : AccountUsageSnapshot(provider: provider)
        value.status = status ?? (value.windows.isEmpty ? (preserve ? "error" : "unavailable") : "stale")
        value.detail = value.windows.isEmpty ? detail : detail + " 마지막으로 확인한 값입니다."
        cache[provider] = value; nextRead[provider] = now().addingTimeInterval(delay)
        return value
    }

    static func number(_ value: Any?) -> Double? {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { return nil }
        return n.doubleValue
    }
    static func text(_ value: Any?) -> String? {
        guard let value = value as? String else { return nil }
        let clean = String(value.unicodeScalars.filter { $0.properties.generalCategory != .control }.prefix(160)).trimmingCharacters(in: .whitespacesAndNewlines)
        return clean.isEmpty ? nil : clean
    }
    static func timestamp(_ value: Date) -> String { ISO8601DateFormatter().string(from: value) }
    static func date(_ value: String) -> Date? {
        let format = ISO8601DateFormatter()
        if let date = format.date(from: value) { return date }
        format.formatOptions.insert(.withFractionalSeconds); return format.date(from: value)
    }
    static func reset(_ value: Any?) -> String? {
        if let raw = value as? String, let parsed = date(raw) { return timestamp(parsed) }
        if let number = number(value), number > 0, number < 32_503_680_000 { return timestamp(Date(timeIntervalSince1970: number)) }
        return nil
    }

    static func mapCodex(account: [String: Any], limits: [String: Any]) throws -> AccountUsageSnapshot {
        guard let account = account["account"] as? [String: Any], account["type"] as? String == "chatgpt" else { throw AccountUsageFailure.unavailable("ChatGPT로 Codex CLI에 로그인하면 계정 한도를 확인할 수 있습니다.") }
        let buckets = limits["rateLimitsByLimitId"] as? [String: Any]
        guard let primary = buckets?["codex"] as? [String: Any] ?? limits["rateLimits"] as? [String: Any] else { throw AccountUsageFailure.invalidResponse }
        var windows: [AccountUsageWindow] = []
        for (key, kind) in [("primary", "session"), ("secondary", "weekly")] {
            guard let row = primary[key] as? [String: Any], let used = number(row["usedPercent"]), (0...100).contains(used) else { continue }
            let mins = number(row["windowDurationMins"]).flatMap { $0 > 0 && $0 <= 525_600 && $0.rounded() == $0 ? Int($0) : nil }
            // Respect a reported nonstandard period rather than relabel it as a week.
            let windowKind = mins.map { $0 == 10080 ? "weekly" : $0 == 300 ? "session" : "\($0)m" } ?? kind
            windows.append(AccountUsageWindow(kind: windowKind, usedPercent: used, resetsAt: reset(row["resetsAt"]), windowMinutes: mins))
        }
        return AccountUsageSnapshot(provider: "codex", accountLabel: text(account["email"]), plan: text(primary["planType"]) ?? text(account["planType"]), windows: windows, status: windows.isEmpty ? "unavailable" : "available", detail: windows.isEmpty ? "이 계정에서 사용량 한도 창을 제공하지 않습니다." : "Codex 계정 한도")
    }

    static func mapClaude(_ body: [String: Any], profile: [String: Any] = [:], plan: String? = nil) -> AccountUsageSnapshot {
        var windows: [AccountUsageWindow] = []
        for (key, kind, minutes) in [("five_hour", "session", 300), ("seven_day", "weekly", 10080), ("seven_day_sonnet", "Sonnet", 10080)] {
            guard let row = body[key] as? [String: Any], let used = number(row["utilization"]), (0...100).contains(used) else { continue }
            windows.append(AccountUsageWindow(kind: kind, usedPercent: used, resetsAt: reset(row["resets_at"]), windowMinutes: minutes))
        }
        let account = profile["account"] as? [String: Any]
        let organization = profile["organization"] as? [String: Any]
        return AccountUsageSnapshot(provider: "claude", accountLabel: text(account?["email"]), plan: text(organization?["rate_limit_tier"]) ?? text(plan), windows: windows, status: windows.isEmpty ? "unavailable" : "available", detail: windows.isEmpty ? "이 Claude 계정에서 구독 한도를 제공하지 않습니다." : "Claude 계정 한도")
    }

    static func claude(environment: [String: String], load: (@Sendable () throws -> ClaudeQuotaCredential?)? = nil,
                       http: @escaping @Sendable (URLRequest) async throws -> AccountUsageHTTPResponse = accountUsageHTTP,
                       interactive: Bool = false) async throws -> AccountUsageSnapshot {
        // Never forward a custom provider's credentials to the production endpoint.
        for key in ["CLAUDE_CODE_CUSTOM_OAUTH_URL", "CLAUDE_LOCAL_OAUTH_API_BASE", "USE_LOCAL_OAUTH", "USE_STAGING_OAUTH", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"] {
            if let value = environment[key], !value.isEmpty, !["0", "false"].contains(value.lowercased()) { throw AccountUsageFailure.unavailable("사용자 지정 인증의 계정 한도는 CLI에서 확인하세요.") }
        }
        guard let credentials = try (load ?? { try ClaudeQuotaCredentials.read(environment: environment, interactive: interactive) })() else { throw AccountUsageFailure.authentication }
        func request(_ path: String) -> URLRequest {
            var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/" + path)!, timeoutInterval: 10)
            request.httpMethod = "GET"
            request.setValue("Bearer " + credentials.token, forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
            return request
        }
        let response = try await http(request("usage"))
        try checkHTTP(response)
        guard let body = try JSONSerialization.jsonObject(with: response.data) as? [String: Any] else { throw AccountUsageFailure.invalidResponse }
        var profile: [String: Any] = [:]
        var retryAfter: TimeInterval?
        if let reply = try? await http(request("profile")) {
            if reply.status == 429 { retryAfter = retryInterval(reply.retryAfter) }
            else if (200..<300).contains(reply.status), let value = try? JSONSerialization.jsonObject(with: reply.data) as? [String: Any] { profile = value }
        }
        try Task.checkCancellation()
        var snapshot = mapClaude(body, profile: profile, plan: credentials.plan)
        snapshot.retryAfterSeconds = retryAfter
        return snapshot
    }
    static func checkHTTP(_ response: AccountUsageHTTPResponse) throws {
        if response.status == 401 || response.status == 403 { throw AccountUsageFailure.authentication }
        if response.status == 429 {
            throw AccountUsageFailure.rateLimited(retryInterval(response.retryAfter))
        }
        guard (200..<300).contains(response.status), response.data.count <= 1024 * 1024 else { throw AccountUsageFailure.network }
    }
    private static func retryInterval(_ value: String?) -> TimeInterval {
        var seconds = value.flatMap(Double.init) ?? 300
        if let raw = value, Double(raw) == nil {
            let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(secondsFromGMT: 0); formatter.dateFormat = "EEE',' dd MMM yyyy HH':'mm':'ss z"
            if let date = formatter.date(from: raw) { seconds = date.timeIntervalSinceNow }
        }
        return seconds.isFinite ? min(86400, max(60, seconds)) : 300
    }
}

private enum ClaudeQuotaCredentials {
    /// The login keychain guards Claude Code's item with an application ACL,
    /// and `kSecUseAuthenticationUIFail` does not cover that dialog. Disable
    /// keychain UI for the duration of an automatic read so the app never asks
    /// at launch; a denied item becomes `keychainPermission` for the caller.
    static func read(environment: [String: String], interactive: Bool = false) throws -> ClaudeQuotaCredential? {
        var previousInteraction: DarwinBoolean = true
        if !interactive {
            SecKeychainGetUserInteractionAllowed(&previousInteraction)
            SecKeychainSetUserInteractionAllowed(false)
        }
        defer { if !interactive { SecKeychainSetUserInteractionAllowed(previousInteraction.boolValue) } }
        var permissionDenied = false
        let home = FileManager.default.homeDirectoryForCurrentUser
        let config = environment["CLAUDE_CONFIG_DIR"]
        let directory = config.map { URL(fileURLWithPath: $0) } ?? home.appendingPathComponent(".claude")
        var services = ["Claude Code-credentials"]
        if let config {
            guard config.hasPrefix("/"), !config.contains("\0") else { return nil }
            let hash = SHA256.hash(data: Data(config.precomposedStringWithCanonicalMapping.utf8)).map { String(format: "%02x", $0) }.joined()
            // A custom profile must never borrow the default account's credentials.
            services = ["Claude Code-credentials-" + hash.prefix(8)]
        }
        for service in services {
            var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                      kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne,
                                      kSecUseAuthenticationUI as String: kSecUseAuthenticationUIFail]
            query[kSecAttrAccount as String] = NSUserName()
            var result: CFTypeRef?
            var status = SecItemCopyMatching(query as CFDictionary, &result)
            if status == errSecItemNotFound { query.removeValue(forKey: kSecAttrAccount as String); status = SecItemCopyMatching(query as CFDictionary, &result) }
            if status == errSecSuccess, let data = result as? Data, let value = parse(data) { return value }
            if status == errSecInteractionNotAllowed || status == errSecAuthFailed || status == errSecUserCanceled { permissionDenied = true }
        }
        let path = directory.appendingPathComponent(".credentials.json")
        if let size = try? path.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 262144,
           let data = try? Data(contentsOf: path), let value = parse(data) { return value }
        if permissionDenied { throw AccountUsageFailure.keychainPermission }
        return nil
    }
    static func parse(_ data: Data) -> ClaudeQuotaCredential? {
        guard data.count <= 262144, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let oauth = json["claudeAiOauth"] as? [String: Any], let token = oauth["accessToken"] as? String,
              !token.isEmpty, token.utf8.count <= 16384, !token.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) else { return nil }
        if let expires = AccountUsageService.number(oauth["expiresAt"]), expires <= Date().timeIntervalSince1970 * 1000 { return nil }
        if let scopes = oauth["scopes"] as? [String], !scopes.isEmpty, !scopes.contains("user:profile") { return nil }
        return ClaudeQuotaCredential(token: token, plan: AccountUsageService.text(oauth["subscriptionType"]))
    }
}

private final class AccountNoRedirect: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
private func accountUsageHTTP(_ request: URLRequest) async throws -> AccountUsageHTTPResponse {
    let config = URLSessionConfiguration.ephemeral
    config.timeoutIntervalForRequest = 10; config.timeoutIntervalForResource = 12
    config.httpShouldSetCookies = false; config.urlCache = nil; config.urlCredentialStorage = nil
    let session = URLSession(configuration: config, delegate: AccountNoRedirect(), delegateQueue: nil)
    defer { session.invalidateAndCancel() }
    let (stream, response) = try await session.bytes(for: request)
    guard let response = response as? HTTPURLResponse, response.expectedContentLength <= 1024 * 1024 else { throw AccountUsageFailure.invalidResponse }
    var body = Data()
    for try await byte in stream {
        guard body.count < 1024 * 1024 else { throw AccountUsageFailure.invalidResponse }
        body.append(byte)
    }
    return AccountUsageHTTPResponse(status: response.statusCode, data: body, retryAfter: response.value(forHTTPHeaderField: "Retry-After"))
}

/// Uses the installed CLI's own auth, and never starts/resumes a thread or calls a tool.
private final class CodexAccountProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var child: NativeChildProcess?
    private var buffer = Data()
    private var bytes = 0
    private var expected = 1
    private var account: [String: Any] = [:]
    private var result: Result<AccountUsageSnapshot, AccountUsageFailure>?
    private var continuation: CheckedContinuation<Result<AccountUsageSnapshot, AccountUsageFailure>, Never>?

    private func attach(_ child: NativeChildProcess) { lock.lock(); self.child = child; let done = result != nil; lock.unlock(); if done { child.stop() } }
    private func send(_ value: [String: Any]) { guard let data = try? JSONSerialization.data(withJSONObject: value) else { return }; child?.write(data + Data([10])) }
    private func complete(_ result: Result<AccountUsageSnapshot, AccountUsageFailure>) { lock.lock(); finish(result); lock.unlock() }
    private func finish(_ value: Result<AccountUsageSnapshot, AccountUsageFailure>) {
        guard result == nil else { return }
        result = value; let pending = continuation; continuation = nil; pending?.resume(returning: value)
    }
    private func consume(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        guard result == nil else { return }
        bytes += data.count
        guard bytes <= 1024 * 1024 else { finish(.failure(.invalidResponse)); return }
        buffer.append(data)
        while let index = buffer.firstIndex(of: 10) {
            let line = Data(buffer[..<index]); buffer.removeSubrange(...index)
            guard let message = try? JSONSerialization.jsonObject(with: line) as? [String: Any], message["id"] as? Int == expected else { continue }
            if let failure = message["error"] as? [String: Any] {
                let code = AccountUsageService.number(failure["code"])
                let raw = failure["message"] as? String ?? ""
                finish(.failure(code == 429 || raw.contains("429") ? .rateLimited(300) : .network)); return
            }
            guard let payload = message["result"] as? [String: Any] else { finish(.failure(.invalidResponse)); return }
            if expected == 1 {
                send(["method": "initialized"]); expected = 2
                send(["id": 2, "method": "account/read", "params": ["refreshToken": false]])
            } else if expected == 2 {
                account = payload
                guard let value = payload["account"] as? [String: Any], value["type"] as? String == "chatgpt" else {
                    finish(.failure(.unavailable("ChatGPT로 Codex CLI에 로그인하면 계정 한도를 확인할 수 있습니다."))); return
                }
                expected = 3; send(["id": 3, "method": "account/rateLimits/read"])
            } else {
                do { finish(.success(try AccountUsageService.mapCodex(account: account, limits: payload))) }
                catch { finish(.failure(.invalidResponse)) }
                return
            }
        }
    }
    private func wait() async -> Result<AccountUsageSnapshot, AccountUsageFailure> {
        await withCheckedContinuation { continuation in
            lock.lock()
            if let result { lock.unlock(); continuation.resume(returning: result) }
            else { self.continuation = continuation; lock.unlock() }
        }
    }
    static func read(command: ProviderCommand, environment: [String: String], timeoutSeconds: Double = 15) async throws -> AccountUsageSnapshot {
        let state = CodexAccountProbe()
        return try await withTaskCancellationHandler(operation: {
            try Task.checkCancellation()
            let child = try NativeChildProcess(executable: command.executable,
                arguments: ["app-server", "--listen", "stdio://"], environment: environment,
                cwd: FileManager.default.temporaryDirectory, stdout: { state.consume($0) }, stderr: { _ in }, exited: { _ in state.complete(.failure(.network)) })
            state.attach(child)
            state.send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "mightyclaude_account_usage", "version": "0.1.0"]]])
            let deadline = Task { do { try await Task.sleep(for: .seconds(timeoutSeconds)); state.complete(.failure(.network)) } catch { } }
            let result = await state.wait()
            deadline.cancel(); child.stop(); _ = await child.wait(timeout: 2)
            try Task.checkCancellation()
            return try result.get()
        }, onCancel: { state.complete(.failure(.network)) })
    }
}

// Internal seams keep tests entirely inside fake processes and HTTP transports.
extension AccountUsageService {
    static func probeCodex(command: ProviderCommand, environment: [String: String], timeout: Double = 15) async throws -> AccountUsageSnapshot {
        try await CodexAccountProbe.read(command: command, environment: environment, timeoutSeconds: timeout)
    }
    static func parseClaudeCredential(_ data: Data) -> ClaudeQuotaCredential? { ClaudeQuotaCredentials.parse(data) }
}
