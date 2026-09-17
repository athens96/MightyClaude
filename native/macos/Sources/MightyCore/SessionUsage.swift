import Foundation
import CoreFoundation

/// Display names for rate-limit windows. Polled quota rows use "session" /
/// "weekly"; the Mods reading keeps the CLI's own kinds ("five_hour",
/// "seven_day", "seven_day_opus", "spend_limit"). Both must read the same.
public enum RateLimitWindowLabel {
    public static func label(_ kind: String) -> String {
        switch kind.lowercased() {
        case "session", "five_hour", "5h", "primary": return "세션"
        case "weekly", "seven_day", "7d", "secondary": return "주간"
        case "daily": return "일간"
        case "monthly": return "월간"
        case "spend_limit": return "지출 한도"
        default: break
        }
        let lowered = kind.lowercased()
        if lowered.hasPrefix("seven_day_") { return "주간 " + model(String(kind.dropFirst("seven_day_".count))) }
        if lowered.hasPrefix("five_hour_") { return "세션 " + model(String(kind.dropFirst("five_hour_".count))) }
        if let range = lowered.range(of: #"^(\d+)m$"#, options: .regularExpression) { return lowered[range].dropLast() + "분" }
        if let range = lowered.range(of: #"^(\d+)h$"#, options: .regularExpression) { return lowered[range].dropLast() + "시간" }
        return kind.replacingOccurrences(of: "_", with: " ")
    }
    private static func model(_ value: String) -> String {
        guard let first = value.first else { return value }
        return first.uppercased() + value.dropFirst()
    }
}

public struct SessionRateLimit: Codable, Sendable, Equatable {
    public var kind: String
    public var percentUsed: Double?
    public var resetsAt: String?
    public init(kind: String, percentUsed: Double?, resetsAt: String? = nil) { self.kind = kind; self.percentUsed = percentUsed; self.resetsAt = resetsAt }
}

/// Direct CLI measurements, never account quota or an estimate from log text.
/// Input includes cached input; cache/reasoning fields are subsets, not extras.
public struct SessionUsage: Codable, Sendable, Equatable {
    public var provider: String
    public var source: String
    public var tokenScope: String
    public var model: String?
    public var providerSessionId: String?
    public var inputTokens: Int?
    public var outputTokens: Int?
    public var cacheReadTokens: Int?
    public var cacheWriteTokens: Int?
    public var reasoningTokens: Int?
    public var totalTokens: Int?
    public var contextUsedTokens: Int?
    public var contextWindowTokens: Int?
    public var costUSD: Double?
    public var costScope: String?
    public var rateLimits: [SessionRateLimit]?
    public var rateLimitsUpdatedAt: String?
    public var updatedAt: String

    public var contextPercent: Double? {
        guard let used = contextUsedTokens, let window = contextWindowTokens, used >= 0, window > 0 else { return nil }
        return min(100, max(0, Double(used) / Double(window) * 100))
    }
    public init(provider: String, source: String, tokenScope: String = "run", model: String? = nil, providerSessionId: String? = nil,
                inputTokens: Int? = nil, outputTokens: Int? = nil, cacheReadTokens: Int? = nil, cacheWriteTokens: Int? = nil,
                reasoningTokens: Int? = nil, totalTokens: Int? = nil, contextUsedTokens: Int? = nil, contextWindowTokens: Int? = nil,
                costUSD: Double? = nil, costScope: String? = nil, rateLimits: [SessionRateLimit]? = nil, rateLimitsUpdatedAt: String? = nil, updatedAt: String = mightyTimestamp()) {
        self.provider = provider; self.source = source; self.tokenScope = tokenScope; self.model = model; self.providerSessionId = providerSessionId
        self.inputTokens = inputTokens; self.outputTokens = outputTokens; self.cacheReadTokens = cacheReadTokens; self.cacheWriteTokens = cacheWriteTokens
        self.reasoningTokens = reasoningTokens; self.totalTokens = totalTokens; self.contextUsedTokens = contextUsedTokens; self.contextWindowTokens = contextWindowTokens
        self.costUSD = costUSD; self.costScope = costScope; self.rateLimits = rateLimits; self.rateLimitsUpdatedAt = rateLimitsUpdatedAt; self.updatedAt = updatedAt
    }
}

public enum SessionUsageSupport {
    public static let maximumTokens = 9_000_000_000_000
    public static let scopes = ["response", "run", "session"]

    public static func normalized(_ value: SessionUsage) -> SessionUsage? {
        guard ProviderOptions.ids.contains(value.provider), scopes.contains(value.tokenScope),
              !value.source.isEmpty, value.source.utf8.count <= 80,
              value.source == ActivitySupport.clean(value.source, maximumBytes: 80, singleLine: true),
              value.updatedAt.utf8.count <= 80, validDate(value.updatedAt) else { return nil }
        var clean = value
        clean.model = value.model.flatMap { CoreValidation.model($0) ? $0 : nil }
        clean.providerSessionId = value.providerSessionId.flatMap { CoreValidation.identifier($0) ? $0 : nil }
        clean.inputTokens = valid(value.inputTokens); clean.outputTokens = valid(value.outputTokens)
        clean.cacheReadTokens = valid(value.cacheReadTokens); clean.cacheWriteTokens = valid(value.cacheWriteTokens)
        clean.reasoningTokens = valid(value.reasoningTokens); clean.totalTokens = valid(value.totalTokens)
        clean.contextUsedTokens = valid(value.contextUsedTokens)
        clean.contextWindowTokens = value.contextWindowTokens.flatMap { (1...1_000_000_000).contains($0) ? $0 : nil }
        if let input = clean.inputTokens {
            if let cached = clean.cacheReadTokens, cached > input { clean.cacheReadTokens = nil }
            if let written = clean.cacheWriteTokens, written > input { clean.cacheWriteTokens = nil }
        }
        if let output = clean.outputTokens, let reasoning = clean.reasoningTokens, reasoning > output { clean.reasoningTokens = nil }
        clean.costUSD = value.costUSD.flatMap { $0.isFinite && $0 >= 0 && $0 <= 1_000_000_000 ? $0 : nil }
        clean.costScope = clean.costUSD == nil ? nil : value.costScope.flatMap { scopes.contains($0) ? $0 : nil }
        clean.rateLimitsUpdatedAt = value.rateLimitsUpdatedAt.flatMap { $0.utf8.count <= 80 && validDate($0) ? $0 : nil }
        if let windows = value.rateLimits {
            var seen = Set<String>()
            clean.rateLimits = windows.prefix(16).compactMap { window in
                guard !window.kind.isEmpty, window.kind.utf8.count <= 80, window.kind == ActivitySupport.clean(window.kind, maximumBytes: 80, singleLine: true), seen.insert(window.kind).inserted else { return nil }
                var normalized = window
                normalized.percentUsed = window.percentUsed.flatMap { $0.isFinite && $0 >= 0 && $0 <= (window.kind == "spend_limit" ? 1_000_000 : 100) ? $0 : nil }
                normalized.resetsAt = window.resetsAt.flatMap { $0.utf8.count <= 80 && validDate($0) ? $0 : nil }
                return normalized.percentUsed == nil && normalized.resetsAt == nil ? nil : normalized
            }
        }
        return clean
    }

    /// `rate_limit_info` of a Claude stream event: `unifiedWindows` keyed by
    /// window kind with utilization 0...1 and an epoch-second reset time.
    static func rateLimitWindows(_ info: [String: Any]) -> [SessionRateLimit] {
        func window(_ kind: String, _ row: [String: Any]) -> SessionRateLimit? {
            let percent = (row["utilization"] as? NSNumber).flatMap { number -> Double? in
                let value = number.doubleValue
                return CFGetTypeID(number) != CFBooleanGetTypeID() && value.isFinite && value >= 0 && value <= 1 ? value * 100 : nil
            }
            let reset = (row["resetsAt"] as? NSNumber).flatMap { number -> String? in
                let seconds = number.doubleValue
                guard seconds.isFinite, seconds > 0, seconds < 4_102_444_800 else { return nil }
                return ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: seconds))
            }
            guard percent != nil || reset != nil, !kind.isEmpty, kind.utf8.count <= 80 else { return nil }
            return SessionRateLimit(kind: kind, percentUsed: percent, resetsAt: reset)
        }
        if let unified = info["unifiedWindows"] as? [String: Any] {
            let windows = unified.keys.sorted().prefix(16).compactMap { key in (unified[key] as? [String: Any]).flatMap { window(key, $0) } }
            if !windows.isEmpty { return windows }
        }
        if let kind = info["rateLimitType"] as? String, let row = window(kind, info) { return [row] }
        return []
    }

    static func valid(_ value: Int?) -> Int? { value.flatMap { (0...maximumTokens).contains($0) ? $0 : nil } }
    static func count(_ raw: Any?) -> Int? {
        guard let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite,
              number.doubleValue.rounded() == number.doubleValue, number.doubleValue >= 0, number.doubleValue <= Double(maximumTokens) else { return nil }
        return number.intValue
    }
    static func cost(_ raw: Any?) -> Double? {
        guard let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite,
              number.doubleValue >= 0, number.doubleValue <= 1_000_000_000 else { return nil }
        return number.doubleValue
    }
    static func sum(_ values: [Int?], requireAll: Bool = true) -> Int? {
        if requireAll && values.contains(where: { $0 == nil }) { return nil }
        let known = values.compactMap { $0 }; guard !known.isEmpty else { return nil }
        let result = known.reduce(0, +); return valid(result)
    }
    private static func validDate(_ text: String) -> Bool {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if formatter.date(from: text) != nil { return true }
        formatter.formatOptions = [.withInternetDateTime]; return formatter.date(from: text) != nil
    }
}

public extension RunSession {
    mutating func recordSessionUsage(_ event: RunEvent) {
        guard event.sessionId == id, event.type == "usage", kind == "claude", let usage = event.usage,
              usage.provider == provider, let clean = SessionUsageSupport.normalized(usage) else { return }
        sessionUsage = clean
    }
}

/// One parser owns one process. Totals are replacement snapshots: resumed
/// Codex/Gemini totals and repeated result messages are never added together.
final class SessionUsageTracker {
    private var value: SessionUsage
    private var emitted: SessionUsage?
    private let callback: ((SessionUsage) -> Void)?
    private var lastModSequence: Int = -1
    private var authoritativeModContext = false
    private var claudeResultSeen = false

    init(provider: String, callback: ((SessionUsage) -> Void)?) {
        value = SessionUsage(provider: provider, source: provider + ".stream-json", tokenScope: provider == "claude" ? "response" : "session")
        self.callback = callback
    }
    func consume(_ event: [String: Any]) {
        guard callback != nil, let type = event["type"] as? String else { return }
        switch value.provider {
        case "claude": consumeClaude(event, type: type)
        case "codex":
            if type == "thread.started" { setIdentity(event["thread_id"]); publish() }
            if type == "turn.completed", let usage = event["usage"] as? [String: Any] {
                guard let input = SessionUsageSupport.count(usage["input_tokens"]), let output = SessionUsageSupport.count(usage["output_tokens"]) else { return }
                value.source = "codex.exec"; value.tokenScope = "session"
                value.inputTokens = input; value.outputTokens = output
                value.cacheReadTokens = SessionUsageSupport.count(usage["cached_input_tokens"])
                value.cacheWriteTokens = SessionUsageSupport.count(usage["cache_write_input_tokens"])
                value.reasoningTokens = SessionUsageSupport.count(usage["reasoning_output_tokens"])
                value.totalTokens = SessionUsageSupport.sum([value.inputTokens, value.outputTokens])
                // exec omits ThreadTokenUsage.last/modelContextWindow. Session
                // totals cannot be used as the current context's numerator.
                publish()
            }
        case "gemini":
            if type == "init" { setIdentity(event["session_id"]); setModel(event["model"]); publish() }
            if type == "result", let stats = event["stats"] as? [String: Any] {
                guard let input = SessionUsageSupport.count(stats["input_tokens"]), let output = SessionUsageSupport.count(stats["output_tokens"]), let total = SessionUsageSupport.count(stats["total_tokens"]) else { return }
                value.source = "gemini.stream-json"; value.tokenScope = "session"
                value.inputTokens = input; value.outputTokens = output
                value.cacheReadTokens = SessionUsageSupport.count(stats["cached"])
                value.totalTokens = total
                // Gemini's total may include internal categories that stdout
                // doesn't expose separately. Preserve its declared total.
                if let models = stats["models"] as? [String: Any], models.count == 1 { setModel(models.keys.first) }
                publish()
            }
        default: break
        }
    }

    func consumeMod(_ metadata: ModMetadata) {
        guard value.provider == "claude", metadata.event == "session.usage", metadata.agentId == nil,
              let incoming = metadata.usage, let clean = SessionUsageSupport.normalized(incoming), clean.provider == "claude" else { return }
        if let sequence = metadata.sequence {
            guard sequence > lastModSequence else { return }; lastModSequence = sequence
        }
        setIdentity(metadata.claudeSessionId)
        authoritativeModContext = true
        value.contextUsedTokens = clean.contextUsedTokens; value.contextWindowTokens = clean.contextWindowTokens
        if let model = clean.model { value.model = model }
        if let cost = clean.costUSD { value.costUSD = cost; value.costScope = "session" }
        if let limits = clean.rateLimits { value.rateLimits = limits; value.rateLimitsUpdatedAt = clean.rateLimitsUpdatedAt ?? clean.updatedAt }
        value.source = "claude.mods+stream-json"
        publish()
    }

    private func consumeClaude(_ event: [String: Any], type: String) {
        guard event["parent_tool_use_id"] == nil || event["parent_tool_use_id"] is NSNull else { return }
        setIdentity(event["session_id"])
        if type == "system", event["subtype"] as? String == "init" { setModel(event["model"]); publish() }
        if type == "system", event["subtype"] as? String == "compact_boundary" {
            value.contextUsedTokens = nil; authoritativeModContext = false; publish()
        }
        // The CLI reports the account's rate-limit windows with every turn, so
        // quota display never needs the login Keychain.
        if type == "rate_limit_event", let info = event["rate_limit_info"] as? [String: Any] {
            let windows = SessionUsageSupport.rateLimitWindows(info)
            if !windows.isEmpty { value.rateLimits = windows; value.rateLimitsUpdatedAt = mightyTimestamp(); publish() }
        }
        if type == "assistant", let message = event["message"] as? [String: Any] {
            let oldModel = value.model; setModel(message["model"])
            if oldModel != value.model { value.contextWindowTokens = nil; authoritativeModContext = false }
            if let usage = message["usage"] as? [String: Any] {
                guard let input = claudeInput(usage), SessionUsageSupport.count(usage["output_tokens"]) != nil else { return }
                // Repeated blocks for one API response carry the same usage.
                // Keep the latest response, never sum content-block snapshots.
                if !claudeResultSeen { setClaudeCounts(usage); value.tokenScope = "response" }
                // A newer API response supersedes the previous context reading,
                // including a Mods observation made before the first response.
                value.contextUsedTokens = input
            }
            publish()
        }
        if type == "result" {
            claudeResultSeen = true; value.tokenScope = "run"
            if let models = event["modelUsage"] as? [String: Any], !models.isEmpty, models.count <= 128 {
                let rows = models.values.compactMap { $0 as? [String: Any] }
                if rows.count == models.count {
                    value.inputTokens = SessionUsageSupport.sum(rows.map { row in
                        SessionUsageSupport.sum([SessionUsageSupport.count(row["inputTokens"]), SessionUsageSupport.count(row["cacheReadInputTokens"]), SessionUsageSupport.count(row["cacheCreationInputTokens"])])
                    })
                    value.outputTokens = SessionUsageSupport.sum(rows.map { SessionUsageSupport.count($0["outputTokens"]) })
                    value.cacheReadTokens = SessionUsageSupport.sum(rows.map { SessionUsageSupport.count($0["cacheReadInputTokens"]) })
                    value.cacheWriteTokens = SessionUsageSupport.sum(rows.map { SessionUsageSupport.count($0["cacheCreationInputTokens"]) })
                    value.reasoningTokens = SessionUsageSupport.sum(rows.map { SessionUsageSupport.count($0["thinkingTokens"]) })
                    value.totalTokens = SessionUsageSupport.sum([value.inputTokens, value.outputTokens])
                    if value.model == nil, models.count == 1 { setModel(models.keys.first) }
                    if !authoritativeModContext, let model = value.model, let row = models[model] as? [String: Any] {
                        value.contextWindowTokens = SessionUsageSupport.count(row["contextWindow"])
                    }
                }
            } else if let usage = event["usage"] as? [String: Any] { setClaudeCounts(usage) }
            if value.costScope != "session", let cost = SessionUsageSupport.cost(event["total_cost_usd"]) { value.costUSD = cost; value.costScope = "run" }
            publish()
        }
    }

    private func claudeInput(_ usage: [String: Any]) -> Int? {
        // Missing cache counters in older Anthropic responses mean zero; an
        // explicitly malformed counter must instead leave the measurement unknown.
        SessionUsageSupport.sum([SessionUsageSupport.count(usage["input_tokens"]),
            usage["cache_read_input_tokens"] == nil ? 0 : SessionUsageSupport.count(usage["cache_read_input_tokens"]),
            usage["cache_creation_input_tokens"] == nil ? 0 : SessionUsageSupport.count(usage["cache_creation_input_tokens"])])
    }
    private func setClaudeCounts(_ usage: [String: Any]) {
        value.inputTokens = claudeInput(usage); value.outputTokens = SessionUsageSupport.count(usage["output_tokens"])
        value.cacheReadTokens = SessionUsageSupport.count(usage["cache_read_input_tokens"])
        value.cacheWriteTokens = SessionUsageSupport.count(usage["cache_creation_input_tokens"])
        value.reasoningTokens = nil; value.totalTokens = SessionUsageSupport.sum([value.inputTokens, value.outputTokens])
    }
    private func setModel(_ raw: Any?) { if let model = raw as? String, CoreValidation.model(model) { value.model = model } }
    private func setIdentity(_ raw: Any?) {
        guard let id = raw as? String, CoreValidation.identifier(id) else { return }
        if let previous = value.providerSessionId, previous != id {
            value = SessionUsage(provider: value.provider, source: value.source, tokenScope: value.provider == "claude" ? "response" : "session")
            lastModSequence = -1; authoritativeModContext = false; claudeResultSeen = false
        }
        value.providerSessionId = id
    }
    private func publish() {
        guard callback != nil else { return }
        value.updatedAt = emitted?.updatedAt ?? value.updatedAt
        guard var clean = SessionUsageSupport.normalized(value), clean != emitted else { return }
        clean.updatedAt = mightyTimestamp(); value = clean; emitted = clean; callback?(clean)
    }
}
