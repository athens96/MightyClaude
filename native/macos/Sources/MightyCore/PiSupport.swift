import Darwin
import Foundation

/// Where pi (`@mariozechner/pi-coding-agent`) keeps its files, and what the app reads of
/// them: whether any sign-in exists (`~/.pi/agent/auth.json`, or an API key in the
/// environment) and the session records for "resume an earlier session". The app never
/// writes any of it and never shows a credential.
public enum PiAgent {
    /// The API key variables pi reads (`pi --help`, docs/providers.md). Any one non-empty
    /// counts as a sign-in; the Cloudflare and Azure companions alone do not.
    public static let apiKeyVariables = [
        "ANTHROPIC_API_KEY", "ANTHROPIC_OAUTH_TOKEN", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY", "DEEPSEEK_API_KEY", "GEMINI_API_KEY",
        "GROQ_API_KEY", "CEREBRAS_API_KEY", "XAI_API_KEY", "FIREWORKS_API_KEY", "OPENROUTER_API_KEY", "AI_GATEWAY_API_KEY", "ZAI_API_KEY",
        "MISTRAL_API_KEY", "MINIMAX_API_KEY", "MINIMAX_CN_API_KEY", "MOONSHOT_API_KEY", "OPENCODE_API_KEY", "KIMI_API_KEY", "CLOUDFLARE_API_KEY",
        "XIAOMI_API_KEY", "XIAOMI_TOKEN_PLAN_CN_API_KEY", "XIAOMI_TOKEN_PLAN_AMS_API_KEY", "XIAOMI_TOKEN_PLAN_SGP_API_KEY", "HF_TOKEN",
        "AWS_BEARER_TOKEN_BEDROCK",
    ]

    /// The tool the app's extension gives pi so it can ask the user (`ask_user`: a question, its
    /// options, `multiple`). pi has no question tool of its own.
    public static let questionTool = "ask_user"
    static let questionExtensionName = "pi-ask-user.js"
    /// The extension in the app's resource bundle (Agents/), loaded per run with `pi -e`; the
    /// user's `~/.pi` is not touched. Nil when the bundle lacks it: pi then runs without the tool.
    public static func questionExtension() -> URL? { bundledAgentFile(questionExtensionName) }
    /// A file of the resource bundle's Agents/ folder, searched like the other bundled resources.
    static func bundledAgentFile(_ name: String) -> URL? {
        var candidates: [URL] = []
        for root in BundledStyleSource.searchRoots() {
            let bundle = root.appendingPathComponent(BundledStyleSource.bundleName, isDirectory: true)
            candidates.append(bundle.appendingPathComponent("Contents/Resources/Agents/" + name))
            candidates.append(bundle.appendingPathComponent("Agents/" + name))
        }
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent("Agents/" + name)) }
        return candidates.first { FileManager.default.isReadableFile(atPath: $0.path) }
    }

    /// `$PI_CODING_AGENT_DIR`, else `~/.pi/agent`.
    public static func directory(home: URL, environment: [String: String]) -> URL {
        environment["PI_CODING_AGENT_DIR"].flatMap { $0.hasPrefix("/") ? URL(fileURLWithPath: $0, isDirectory: true) : nil }
            ?? home.appendingPathComponent(".pi/agent", isDirectory: true)
    }
    public static func authFile(home: URL, environment: [String: String]) -> URL {
        directory(home: home, environment: environment).appendingPathComponent("auth.json")
    }

    /// What `auth.json` holds: the providers with a credential, and whether one of them is a
    /// subscription (OAuth) sign-in rather than an API key. An empty or unreadable file holds none.
    public static func credentials(authJSON: Data?) -> (providers: [String], subscription: Bool) {
        guard let authJSON, let object = try? JSONSerialization.jsonObject(with: authJSON) as? [String: Any] else { return ([], false) }
        let entries = object.filter { $0.value is [String: Any] }
        let providers = entries.keys.sorted().prefix(16).map { CLIAccountSupport.clean($0) }
        let subscription = entries.values.contains { ($0 as? [String: Any])?["type"] as? String == "oauth" }
        return (Array(providers), subscription)
    }
    /// The API key variables set to something in `environment`.
    public static func environmentKeys(_ environment: [String: String]) -> [String] {
        apiKeyVariables.filter { !(environment[$0] ?? "").isEmpty }
    }

    /// The folder of `sessions/` a working directory's sessions are kept in:
    /// `--<path with / \ : turned into ->--`.
    public static func sessionFolder(_ path: String) -> String {
        "--" + String(path.drop(while: { $0 == "/" || $0 == "\\" }).map { "/\\:".contains($0) ? "-" : $0 }) + "--"
    }
    /// Where pi keeps the sessions of `workspacePaths`: `$PI_CODING_AGENT_SESSION_DIR` as it
    /// is, else one folder per path under `<agent dir>/sessions`.
    public static func sessionFolders(workspacePaths: Set<String>, home: URL, environment: [String: String]) -> [URL] {
        if let override = environment["PI_CODING_AGENT_SESSION_DIR"], override.hasPrefix("/") { return [URL(fileURLWithPath: override, isDirectory: true)] }
        let sessions = directory(home: home, environment: environment).appendingPathComponent("sessions", isDirectory: true)
        var seen = Set<String>()
        return workspacePaths.sorted().compactMap { path in
            let name = sessionFolder(path)
            return seen.insert(name).inserted ? sessions.appendingPathComponent(name, isDirectory: true) : nil
        }
    }
    /// The record of session `id`: `<timestamp>_<id>.jsonl` in one of the workspace's folders.
    public static func sessionFile(id: String, workspacePath: String, home: URL, environment: [String: String]) -> URL? {
        guard CoreValidation.identifier(id) else { return nil }
        for folder in sessionFolders(workspacePaths: ResumableSessions.workspacePaths(workspacePath), home: home, environment: environment) {
            for (url, _) in ResumableSessions.files(in: folder, extension: "jsonl") where url.lastPathComponent.hasSuffix("_" + id + ".jsonl") { return url }
        }
        return nil
    }
    /// A session's id from its file name (`2024-12-03T14-00-00-000Z_<uuid>.jsonl`).
    static func sessionID(fileName: String) -> String? {
        guard fileName.hasSuffix(".jsonl"), let underscore = fileName.firstIndex(of: "_") else { return nil }
        let id = String(fileName[fileName.index(after: underscore)...].dropLast(".jsonl".count))
        return CoreValidation.identifier(id) ? id : nil
    }
}

extension CLIAccountSupport {
    /// The `pi --list-models` text of a pi with no model to offer.
    static func piHasNoModels(_ output: String) -> Bool { output.lowercased().contains("no models available") }

    /// pi has no status command. It is signed in when it can list a model; what it is signed in
    /// with comes from `auth.json` (a subscription or API keys) or from the environment's API
    /// keys, and a list from `models.json` alone (a local server) still counts. "No models
    /// available" reads signed out; a failed or timed-out call stays unknown.
    public static func piStatus(exitCode: Int32, output: String, authJSON: Data?, environment: [String: String]) -> CLIAccountStatus {
        if piHasNoModels(output) { return CLIAccountStatus(provider: "pi", loggedIn: false, canSignOut: false) }
        guard exitCode == 0 else {
            return CLIAccountStatus(provider: "pi", detail: exitCode == -1 ? L("settings.cliAccounts.detailPiTimeout") : L("settings.cliAccounts.detailPiUnknown"), canSignOut: false)
        }
        let stored = PiAgent.credentials(authJSON: authJSON)
        if !stored.providers.isEmpty {
            return CLIAccountStatus(provider: "pi", loggedIn: true, method: stored.subscription ? L("cliAccounts.method.piSubscription") : L("windows.cli.method.apiKey"),
                                    methodId: stored.subscription ? CLIAccountMethod.piSubscription : CLIAccountMethod.piAPIKey,
                                    account: stored.providers.joined(separator: ", "), detail: L("settings.cliAccounts.detailPiSignOut"), canSignOut: false)
        }
        if !PiAgent.environmentKeys(environment).isEmpty {
            return CLIAccountStatus(provider: "pi", loggedIn: true, method: L("cliAccounts.method.piEnvironmentKey"), methodId: CLIAccountMethod.piAPIKey, canSignOut: false)
        }
        return CLIAccountStatus(provider: "pi", loggedIn: true, method: L("cliAccounts.method.piModelsFile"), methodId: CLIAccountMethod.piModels, canSignOut: false)
    }
}

// MARK: Resumable sessions

extension ResumableSessions {
    /// `<agent dir>/sessions/--<path>--/<timestamp>_<id>.jsonl` of the workspace folder.
    /// The first line is the header (`type: session`, `id`, `cwd`); a request is a `message`
    /// entry whose message has the `user` role. Sessions branched in place stay one record.
    static func pi(_ query: ResumableSessionQuery, budget: inout Int) -> ResumableSessionListing {
        let paths = workspacePaths(query.workspacePath)
        var candidates: [(url: URL, id: String, modified: Date)] = []
        var seen = Set<String>()
        for folder in PiAgent.sessionFolders(workspacePaths: paths, home: query.home, environment: query.environment) {
            for (url, modified) in files(in: folder, extension: "jsonl") {
                guard let id = PiAgent.sessionID(fileName: url.lastPathComponent), seen.insert(id.lowercased()).inserted else { continue }
                candidates.append((url, id, modified))
            }
        }
        var listing = ResumableSessionListing(), read = 0
        for candidate in recent(candidates, query: query) {
            guard listing.items.count < query.maximumSessions, read < query.maximumCandidates, !Task.isCancelled else { break }
            guard !query.excluding.contains(candidate.id.lowercased()) else { continue }
            switch piSession(candidate.url, id: candidate.id, modified: candidate.modified, paths: paths, known: query.known.contains(candidate.id.lowercased()),
                             includeAutomated: query.includeAutomated, headOnly: query.headOnly, budget: &budget) {
            case .listed(let item): read += 1; listing.items.append(item)
            case .hidden: listing.hidden += 1
            case .skipped: read += 1
            }
        }
        return listing
    }

    static func piSession(_ url: URL, id: String, modified: Date, paths: Set<String>, known: Bool = false,
                          includeAutomated: Bool = true, headOnly: Bool = false, budget: inout Int) -> Outcome {
        guard let fd = CodexSessionFiles.open(url) else { return .skipped }
        defer { Darwin.close(fd) }
        guard let size = CodexSessionFiles.size(fd), size > 0 else { return .skipped }
        var title: String?, headerChecked = false, foreign = false
        let headComplete = forEachLine(fd, size: size, limit: headBytes) { line in
            if !headerChecked {
                headerChecked = true
                guard let header = json(line), header["type"] as? String == "session", let cwd = header["cwd"] as? String, matches(cwd, paths) else { foreign = true; return false }
                return true
            }
            title = piPrompt(line)
            return title == nil
        }
        // A session without a single request has nothing to continue.
        if foreign || (title == nil && headComplete) { return .skipped }
        let automated = !known && automatedPrompt(title)
        if automated && !includeAutomated { return .hidden }
        var requests: Int?
        if !headOnly && size <= countBytes && budget >= size {
            budget -= size
            var count = 0
            let complete = forEachLine(fd, size: size, limit: size) { line in
                if piPrompt(line) != nil { count += 1 }
                return true
            }
            if complete { requests = count }
        }
        let model = headOnly ? nil : tailModel(fd, size: size, piModel)
        return .listed(ResumableSession(provider: "pi", sessionID: id, title: title.map(oneLine), modified: modified, requests: requests, model: model, url: url, automated: automated))
    }

    /// The text of a user request line; nil for any other line.
    static func piPrompt(_ line: Data) -> String? {
        guard HistoryScan.contains(line, #""role":"user""#), let object = json(line), object["type"] as? String == "message",
              let message = object["message"] as? [String: Any], message["role"] as? String == "user" else { return nil }
        let text: String
        if let value = message["content"] as? String { text = value }
        else if let blocks = message["content"] as? [[String: Any]] { text = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined(separator: "\n") }
        else { return nil }
        let prompt = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return prompt.isEmpty ? nil : prompt
    }
    static func piModel(_ line: Data) -> String? {
        guard HistoryScan.contains(line, #""role":"assistant""#), let object = json(line), object["type"] as? String == "message",
              let model = (object["message"] as? [String: Any])?["model"] as? String, CoreValidation.model(model) else { return nil }
        return model
    }
}
