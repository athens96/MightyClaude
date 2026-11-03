import CryptoKit
import Foundation

/// A saved summary of one version: the topics, the end of the range they were made for (a different one now
/// means newer commits, `topicsStale`) and when.
public struct WorkMapSummaryEntry: Codable, Equatable, Sendable {
    public var rangeEnd: String
    public var topics: [WorkMapTopic]
    public var created: String

    public init(rangeEnd: String, topics: [WorkMapTopic], created: String = mightyTimestamp()) {
        self.rangeEnd = rangeEnd; self.topics = topics; self.created = created
    }
}

/// `<data>/work-map/<sha256(workspace real path)>.json`: `{ "versions": { "<key>": entry } }`, written whole to a
/// temporary name and moved over the old file.
public struct WorkMapSummaryStore: Sendable {
    public static let folderName = "work-map"
    private struct File: Codable { var versions: [String: WorkMapSummaryEntry] }

    public let directory: URL

    public init(directory: URL) { self.directory = directory }

    public func file(root: URL) -> URL {
        let digest = SHA256.hash(data: Data(WorkspaceFiles.realRoot(root).path.utf8)).map { String(format: "%02x", $0) }.joined()
        return directory.appendingPathComponent(digest + ".json")
    }

    /// Every saved summary of the workspace by version key; none when the file is missing or unreadable.
    public func load(root: URL) -> [String: WorkMapSummaryEntry] {
        guard let data = try? Data(contentsOf: file(root: root)), let file = try? JSONDecoder().decode(File.self, from: data) else { return [:] }
        return file.versions
    }

    public func save(_ entry: WorkMapSummaryEntry, key: String, root: URL) throws {
        var versions = load(root: root)
        versions[key] = entry
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try JSONEncoder().encode(File(versions: versions)).write(to: file(root: root), options: .atomic)
    }
}

/// Why a summary was not made; `message` is the localized card text.
public enum WorkMapSummaryError: Error, Equatable, Sendable {
    case cli, timeout, output

    public var message: String {
        switch self {
        case .cli: L("workMap.error.cli")
        case .timeout: L("workMap.error.timeout")
        case .output: L("workMap.error.output")
        }
    }
}

/// The topic summary of one version (docs/work-map.md, "주제 요약"): a prompt made from the version's commits,
/// requests and release notes, one headless Claude CLI run, and the answer read into topics.
public enum WorkMapSummary {
    public static let maximumTopics = 8
    public static let titleLength = 40
    public static let summaryLength = 160
    public static let notesLength = 8000
    public static let timeout: TimeInterval = 120
    public static let maximumOutputBytes = 4 * 1_048_576

    /// The fixture's `prompt`, word for word.
    public static let promptTemplate = "You group one software version's commits into topics for a project overview.\nAnswer in {language}. Reply with JSON only, no prose: {\"topics\":[{\"title\":\"…\",\"summary\":\"…\",\"commits\":[\"sha8\"],\"requests\":[\"id\"]}]}\nRules: 3 to 6 topics; a title of at most 6 words names a user-visible feature or fix; a summary of one sentence says what changed for the user; every commit sha8 belongs to exactly one topic; put a request id under the topic it asked for.\nVersion: {version}\nCommits (sha8 [lanes] scope: subject):\n{commits}\nRequests the user sent (id: text):\n{requests}\nRelease notes:\n{notes}"

    /// The template with each `{name}` filled in one pass, so text that holds a `{name}` is never filled again.
    public static func prompt(language: String, version: String, commits: String, requests: String, notes: String) -> String {
        let values = ["language": language, "version": version, "commits": commits, "requests": requests, "notes": notes]
        var output = ""
        var rest = Substring(promptTemplate)
        while let open = rest.firstIndex(of: "{") {
            output += rest[..<open]
            let tail = rest[open...]
            if let name = values.keys.first(where: { tail.hasPrefix("{" + $0 + "}") }) {
                output += values[name] ?? ""
                rest = tail.dropFirst(name.count + 2)
            } else {
                output += "{"
                rest = tail.dropFirst()
            }
        }
        return output + rest
    }

    /// The English name of the app's language, for the prompt's "Answer in …".
    public static func languageName(_ language: AppLanguage = .current) -> String {
        switch language {
        case .ko: "Korean"
        case .zh: "Chinese"
        case .ja: "Japanese"
        case .en, .system: "English"
        }
    }

    /// `sha8 [lanes] scope: subject`, a line per commit.
    public static func commitLine(_ commit: WorkMapCommit) -> String {
        let lanes = commit.lanes.joined(separator: ",")
        let scope = commit.scope ?? ""
        return scope.isEmpty ? "\(commit.sha) [\(lanes)] \(commit.subject)" : "\(commit.sha) [\(lanes)] \(scope): \(commit.subject)"
    }

    /// The prompt for `version`; `notes` is the release notes file's text, if there is one.
    public static func prompt(for version: WorkMapVersion, notes: String?, language: String = languageName()) -> String {
        let requests = version.requests.map { $0.id + ": " + $0.text.replacingOccurrences(of: "\n", with: " ") }
        let text = String((notes ?? "").prefix(notesLength))
        return prompt(language: language, version: version.version ?? version.key,
                      commits: version.commits.map(commitLine).joined(separator: "\n"),
                      requests: requests.isEmpty ? "(none)" : requests.joined(separator: "\n"),
                      notes: text.isEmpty ? "(none)" : text)
    }

    /// `release/<version>-notes.md`, the first `notesLength` characters; nil when there is none.
    public static func notes(root: URL, version: String?) -> String? {
        guard let version, WorkMap.versionName(file: version, newestTag: nil) != nil,
              let text = try? String(contentsOf: root.appendingPathComponent("release/\(version)-notes.md"), encoding: .utf8) else { return nil }
        return String(text.prefix(notesLength))
    }

    // MARK: The answer

    /// From the first `{` to its matching `}`, braces inside strings not counted; nil when it never closes.
    static func firstObject(in text: String) -> String? {
        let characters = Array(text)
        guard let start = characters.firstIndex(of: "{") else { return nil }
        var depth = 0, inString = false, escaped = false
        for index in start..<characters.count {
            let character = characters[index]
            if inString {
                if escaped { escaped = false } else if character == "\\" { escaped = true } else if character == "\"" { inString = false }
                continue
            }
            if character == "\"" { inString = true }
            else if character == "{" { depth += 1 }
            else if character == "}" {
                depth -= 1
                if depth == 0 { return String(characters[start...index]) }
            }
        }
        return nil
    }

    /// The topics an answer names. Only this version's commits and request ids stay, a commit named twice stays with
    /// the first topic, a topic left with no commit goes, and commits no topic took are gathered under `otherTitle`.
    /// nil when the answer cannot be read or holds no topic.
    public static func topics(answer: String, commits: [(sha: String, lanes: [String])], requests: [String], otherTitle: String) -> [WorkMapTopic]? {
        guard let json = firstObject(in: answer), let object = (try? JSONSerialization.jsonObject(with: Data(json.utf8))) as? [String: Any],
              let rows = object["topics"] as? [[String: Any]] else { return nil }
        let knownRequests = Set(requests)
        var lanesOf: [String: [String]] = [:]
        for commit in commits { lanesOf[commit.sha] = commit.lanes }
        var used = Set<String>()
        var topics: [WorkMapTopic] = []
        let titled = rows.compactMap { row -> (title: String, row: [String: Any])? in
            guard let title = (row["title"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty else { return nil }
            return (title, row)
        }
        for item in titled.prefix(maximumTopics) {
            let shas = ((item.row["commits"] as? [String]) ?? []).filter { lanesOf[$0] != nil && used.insert($0).inserted }
            guard !shas.isEmpty else { continue }
            var seen = Set<String>()
            let ids = ((item.row["requests"] as? [String]) ?? []).filter { knownRequests.contains($0) && seen.insert($0).inserted }
            let summary = ((item.row["summary"] as? String) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            topics.append(WorkMapTopic(title: String(item.title.prefix(titleLength)), summary: String(summary.prefix(summaryLength)),
                                       lanes: union(shas.flatMap { lanesOf[$0] ?? [] }), commits: shas, requests: ids))
        }
        guard !topics.isEmpty else { return nil }
        let rest = commits.map(\.sha).filter { !used.contains($0) }
        if !rest.isEmpty {
            topics.append(WorkMapTopic(title: otherTitle, summary: "", lanes: union(rest.flatMap { lanesOf[$0] ?? [] }), commits: rest, requests: []))
        }
        return topics
    }

    private static func union(_ lanes: [String]) -> [String] {
        let present = Set(lanes)
        return WorkMap.lanes.filter { present.contains($0) }
    }

    /// The text of the CLI's `result` event in its stream-json output; nil without one or when it reports an error.
    static func resultText(_ output: Data) -> String? {
        var text: String?
        for line in String(decoding: output, as: UTF8.self).split(separator: "\n") {
            guard let event = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? [String: Any], event["type"] as? String == "result" else { continue }
            text = (event["is_error"] as? Bool) == true ? nil : event["result"] as? String
        }
        return text
    }

    // MARK: The run

    /// One headless Claude CLI run (the model list probe's quiet setup, a temporary working folder, `timeout`
    /// seconds) that groups `version`'s commits into topics.
    public static func summarize(version: WorkMapVersion, root: URL, providers: ProviderService) async throws -> [WorkMapTopic] {
        guard let command = await providers.command(provider: "claude", workspacePath: nil) else { throw WorkMapSummaryError.cli }
        var environment = await providers.executionEnvironment().values
        environment.merge(ProviderService.quietClaudeEnvironment) { _, new in new }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-work-map-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let message = try ProviderInput.claudeUserMessage(prompt(for: version, notes: notes(root: root, version: version.version)))
        let arguments = ["--print", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose", "--no-session-persistence",
                         "--permission-mode", "dontAsk", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--tools", ""]
        let result: ProcessResult
        do {
            result = try await ProcessCapture.run(executable: command.executable, arguments: arguments, environment: environment, cwd: folder, input: message, timeout: timeout, maximumBytes: maximumOutputBytes)
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as MightyError where error == MightyError(L("process.error.timeout")) {
            throw WorkMapSummaryError.timeout
        } catch {
            throw WorkMapSummaryError.output
        }
        guard let answer = resultText(result.stdout),
              let topics = topics(answer: answer, commits: version.commits.map { ($0.sha, $0.lanes) }, requests: version.requests.map(\.id), otherTitle: L("workMap.topic.other")) else {
            throw WorkMapSummaryError.output
        }
        return topics
    }
}
