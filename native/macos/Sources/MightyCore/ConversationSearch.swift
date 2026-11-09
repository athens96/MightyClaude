import Foundation

/// 대화 검색: finds the workspace's past sessions (`ResumableSessions`) whose
/// words hold every term of a query, in the CLIs' own session records. Only
/// what a person or the agent said counts — the request and the answers —
/// not tool calls or their output, so a hit is something to read again.
/// Read only; nothing is indexed or kept.
public enum ConversationSearch {
    public struct Hit: Sendable, Equatable, Identifiable {
        public var id: String { session.id }
        public let session: ResumableSession
        /// The words around the first match, on one line.
        public let snippet: String
        /// Messages that matched.
        public let matches: Int
    }

    /// A record larger than this is read from its end only (the newest part).
    public static let maximumBytes = 24 * 1_048_576
    public static let snippetRadius = 60

    /// Lower-cased terms of a query, split on white space; empty for a blank query.
    public static func terms(_ query: String) -> [String] {
        query.lowercased().split(whereSeparator: \.isWhitespace).map(String.init)
    }

    /// The message words of one JSON line of a session record, or nil when
    /// the line is no message (a tool call or result, metadata, bad JSON).
    /// Claude (`message.content`), Codex (`payload` of a `message` or a
    /// `user_message`/`agent_message` event) and other records with plain `text` fields.
    public static func messageText(_ line: Substring) -> String? {
        guard let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let message = object["message"] as? [String: Any], let role = message["role"] as? String, ["user", "assistant"].contains(role) {
            return texts(message["content"])
        }
        if let payload = object["payload"] as? [String: Any] {
            switch payload["type"] as? String {
            case "message": return ["user", "assistant"].contains(payload["role"] as? String ?? "") ? texts(payload["content"]) : nil
            case "user_message", "agent_message": return payload["message"] as? String
            default: return nil
            }
        }
        if let role = object["role"] as? String, ["user", "assistant", "model"].contains(role) {
            return texts(object["content"] ?? object["parts"] ?? object["text"])
        }
        return nil
    }

    /// Text of a message's content: a string, or the `text` of its text parts (tool parts left out).
    static func texts(_ content: Any?) -> String? {
        if let text = content as? String { return text }
        guard let parts = content as? [Any] else { return nil }
        let words = parts.compactMap { part -> String? in
            if let text = part as? String { return text }
            guard let part = part as? [String: Any] else { return nil }
            let type = part["type"] as? String ?? "text"
            guard ["text", "input_text", "output_text"].contains(type) else { return nil }
            return part["text"] as? String
        }
        return words.isEmpty ? nil : words.joined(separator: "\n")
    }

    /// The words around the first of `terms` in `text`, on one line.
    public static func snippet(_ text: String, terms: [String]) -> String {
        let flat = text.split(whereSeparator: \.isNewline).joined(separator: " ")
        let lower = flat.lowercased()
        guard let first = terms.compactMap({ lower.range(of: $0) }).min(by: { $0.lowerBound < $1.lowerBound }) else {
            return String(flat.prefix(snippetRadius * 2))
        }
        let offset = lower.distance(from: lower.startIndex, to: first.lowerBound)
        let start = flat.index(flat.startIndex, offsetBy: max(0, offset - snippetRadius))
        let end = flat.index(start, offsetBy: min(snippetRadius * 2 + terms[0].count, flat.distance(from: start, to: flat.endIndex)))
        return (start > flat.startIndex ? "…" : "") + flat[start..<end].trimmingCharacters(in: .whitespaces) + (end < flat.endIndex ? "…" : "")
    }

    /// Searches one record's lines: the snippet of its first matching message and how many matched.
    public static func search(lines: some Sequence<Substring>, terms: [String]) -> (snippet: String, matches: Int)? {
        guard !terms.isEmpty else { return nil }
        var first: String?, count = 0
        for line in lines {
            // Cheap test before the JSON parse: every term must be somewhere on the line.
            let lower = line.lowercased()
            guard terms.allSatisfy({ lower.contains($0) }), let text = messageText(line) else { continue }
            let words = text.lowercased()
            guard terms.allSatisfy({ words.contains($0) }) else { continue }
            count += 1
            if first == nil { first = snippet(text, terms: terms) }
        }
        return first.map { ($0, count) }
    }

    /// The sessions among `sessions` whose messages hold every term, newest first, at most `limit`.
    public static func search(_ sessions: [ResumableSession], query: String, limit: Int = 50) -> [Hit] {
        let terms = terms(query)
        guard !terms.isEmpty else { return [] }
        var hits: [Hit] = []
        for session in sessions.sorted(by: { $0.modified > $1.modified }) {
            if Task.isCancelled || hits.count >= limit { break }
            guard let text = read(session.url) else { continue }
            if let found = search(lines: text.split(separator: "\n"), terms: terms) {
                hits.append(Hit(session: session, snippet: found.snippet, matches: found.matches))
            }
        }
        return hits
    }

    /// Who said a message line: "user" or "assistant" (a model reply), nil for no message.
    public static func role(_ line: Substring) -> String? {
        guard messageText(line) != nil, let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let payload = object["payload"] as? [String: Any]
        let role = (object["message"] as? [String: Any])?["role"] as? String ?? payload?["role"] as? String
            ?? (payload?["type"] as? String == "user_message" ? "user" : payload?["type"] as? String == "agent_message" ? "assistant" : nil)
            ?? object["role"] as? String
        return role == "user" ? "user" : "assistant"
    }

    /// The session as Markdown: a title, then each message under its speaker's heading.
    /// Codex writes a message twice (its response item and its event); a message equal to the one before it is left out.
    public static func markdown(lines: some Sequence<Substring>, title: String, userLabel: String, agentLabel: String) -> String {
        var out = ["# " + title]
        var last: String?
        for line in lines {
            guard let text = messageText(line), let role = role(line) else { continue }
            let body = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !body.isEmpty, body != last else { continue }
            last = body
            out.append("## " + (role == "user" ? userLabel : agentLabel) + "\n\n" + body)
        }
        return out.joined(separator: "\n\n") + "\n"
    }

    /// The session record `url` as Markdown (`markdown(lines:…)`); nil when it cannot be read.
    public static func markdown(url: URL, title: String, userLabel: String, agentLabel: String) -> String? {
        read(url).map { markdown(lines: $0.split(separator: "\n"), title: title, userLabel: userLabel, agentLabel: agentLabel) }
    }

    /// The record's text, its last `maximumBytes` when larger.
    static func read(_ url: URL) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd() else { return nil }
        let start = size > UInt64(maximumBytes) ? size - UInt64(maximumBytes) : 0
        guard (try? handle.seek(toOffset: start)) != nil, let data = try? handle.readToEnd() else { return nil }
        return String(decoding: data, as: UTF8.self)
    }
}
