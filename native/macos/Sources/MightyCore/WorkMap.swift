import Foundation

/// One commit of a version (docs/work-map.md). `sha` is the first 8 characters, `date` the
/// author date (`%aI`), `files` the first `WorkMap.maximumFilesPerCommit` paths it touched.
public struct WorkMapCommit: Codable, Equatable, Sendable {
    public var sha: String
    public var date: String
    public var lanes: [String]
    public var scope: String?
    public var subject: String
    public var files: [String]
    public var filesCount: Int

    public init(sha: String, date: String, lanes: [String], scope: String?, subject: String, files: [String], filesCount: Int) {
        self.sha = sha; self.date = date; self.lanes = lanes; self.scope = scope; self.subject = subject; self.files = files; self.filesCount = filesCount
    }

    private enum CodingKeys: String, CodingKey { case sha, date, lanes, scope, subject, files, filesCount }
    /// `scope` is written as null when there is none, as the contract shows.
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(sha, forKey: .sha); try container.encode(date, forKey: .date); try container.encode(lanes, forKey: .lanes)
        try container.encode(scope, forKey: .scope); try container.encode(subject, forKey: .subject)
        try container.encode(files, forKey: .files); try container.encode(filesCount, forKey: .filesCount)
    }
}

/// One topic of a summarized version.
public struct WorkMapTopic: Codable, Equatable, Sendable {
    public var title: String
    public var summary: String
    public var lanes: [String]
    public var commits: [String]
    public var requests: [String]

    public init(title: String, summary: String, lanes: [String], commits: [String], requests: [String]) {
        self.title = title; self.summary = summary; self.lanes = lanes; self.commits = commits; self.requests = requests
    }
}

/// A request the user sent from one of the workspace's open panes, placed on the version it led to.
public struct WorkMapRequest: Codable, Equatable, Sendable {
    public var id: String
    public var paneId: String
    public var text: String
    public var date: String?
    public var files: [String]
    public var commits: [String]

    public init(id: String, paneId: String, text: String, date: String?, files: [String], commits: [String]) {
        self.id = id; self.paneId = paneId; self.text = text; self.date = date; self.files = files; self.commits = commits
    }

    private enum CodingKeys: String, CodingKey { case id, paneId, text, date, files, commits }
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id); try container.encode(paneId, forKey: .paneId); try container.encode(text, forKey: .text)
        try container.encode(date, forKey: .date); try container.encode(files, forKey: .files); try container.encode(commits, forKey: .commits)
    }
}

public struct WorkMapVersion: Codable, Equatable, Sendable {
    public var key: String
    public var version: String?
    public var tag: String?
    public var date: String
    public var status: String
    public var releaseStep: String?
    public var rangeEnd: String
    public var total: Int
    public var truncated: Bool
    public var counts: [String: Int]
    public var commits: [WorkMapCommit]
    public var topics: [WorkMapTopic]?
    public var topicsStale: Bool
    public var summaryError: String?
    public var requests: [WorkMapRequest]

    public init(key: String, version: String?, tag: String?, date: String, status: String, releaseStep: String?, rangeEnd: String, total: Int, truncated: Bool,
                counts: [String: Int], commits: [WorkMapCommit], topics: [WorkMapTopic]? = nil, topicsStale: Bool = false, summaryError: String? = nil, requests: [WorkMapRequest] = []) {
        self.key = key; self.version = version; self.tag = tag; self.date = date; self.status = status; self.releaseStep = releaseStep; self.rangeEnd = rangeEnd
        self.total = total; self.truncated = truncated; self.counts = counts; self.commits = commits; self.topics = topics
        self.topicsStale = topicsStale; self.summaryError = summaryError; self.requests = requests
    }

    private enum CodingKeys: String, CodingKey {
        case key, version, tag, date, status, releaseStep, rangeEnd, total, truncated, counts, commits, topics, topicsStale, summaryError, requests
    }
    /// The nullable fields are written as null, as the contract shows.
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(key, forKey: .key); try container.encode(version, forKey: .version); try container.encode(tag, forKey: .tag)
        try container.encode(date, forKey: .date); try container.encode(status, forKey: .status); try container.encode(releaseStep, forKey: .releaseStep)
        try container.encode(rangeEnd, forKey: .rangeEnd); try container.encode(total, forKey: .total); try container.encode(truncated, forKey: .truncated)
        try container.encode(counts, forKey: .counts); try container.encode(commits, forKey: .commits); try container.encode(topics, forKey: .topics)
        try container.encode(topicsStale, forKey: .topicsStale); try container.encode(summaryError, forKey: .summaryError); try container.encode(requests, forKey: .requests)
    }
}

/// What the work map shows for one workspace (`workMapState`, `GET /m1/workspaces/{id}/workmap`).
public struct WorkMapModel: Codable, Equatable, Sendable {
    public var available: Bool
    public var reason: String?
    public var head: String?
    public var lanes: [String]
    public var summarizing: String?
    public var versions: [WorkMapVersion]

    public init(available: Bool, reason: String? = nil, head: String? = nil, lanes: [String] = WorkMap.lanes, summarizing: String? = nil, versions: [WorkMapVersion] = []) {
        self.available = available; self.reason = reason; self.head = head; self.lanes = lanes; self.summarizing = summarizing; self.versions = versions
    }

    public static func unavailable(_ reason: String) -> WorkMapModel { WorkMapModel(available: false, reason: reason) }

    private enum CodingKeys: String, CodingKey { case available, reason, head, lanes, summarizing, versions }
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(available, forKey: .available); try container.encode(reason, forKey: .reason); try container.encode(head, forKey: .head)
        try container.encode(lanes, forKey: .lanes); try container.encode(summarizing, forKey: .summarizing); try container.encode(versions, forKey: .versions)
    }

    /// The phone's body: the model without the commits' `files` (it has `filesCount`), with the protocol number.
    public func phoneBody() -> Data {
        guard let data = try? JSONEncoder().encode(self), var object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return Data("{}".utf8) }
        object["protocol"] = 1
        let versions = (object["versions"] as? [[String: Any]] ?? []).map { version -> [String: Any] in
            var version = version
            version["commits"] = (version["commits"] as? [[String: Any]] ?? []).map { commit -> [String: Any] in
                var commit = commit; commit.removeValue(forKey: "files"); return commit
            }
            return version
        }
        object["versions"] = versions
        return (try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes])) ?? Data("{}".utf8)
    }
}

/// A request the user sent, before it is placed: the text, when it started and the files its run changed.
public struct WorkMapRequestInput: Equatable, Sendable {
    public var id: String
    public var paneId: String
    public var text: String
    public var date: String?
    public var files: [String]

    public init(id: String, paneId: String, text: String, date: String?, files: [String]) {
        self.id = id; self.paneId = paneId; self.text = text; self.date = date; self.files = files
    }
}

/// The work map's rules (docs/work-map.md; Windows `MightyClaude.Core/WorkMap.cs`), held by
/// `native/contracts/fixtures/work-map.json`. Read only: git is asked, nothing is written.
public enum WorkMap {
    public static let maximumVersions = 12
    public static let maximumCommitsPerVersion = 400
    public static let maximumFilesPerCommit = 50
    public static let maximumRequests = 200
    public static let maximumCommitsPerRequest = 20
    public static let requestTextLength = 200
    public static let gitTimeout: TimeInterval = 10
    public static let lanes = ["mac", "windows", "phone", "ci", "etc"]
    public static let releaseSteps = ["check", "version", "push", "ci", "download", "manifest", "page", "upload", "github", "verify"]
    public static let nextKey = "next"
    public static let dotMinimum = 6.0
    public static let dotRange = 14.0

    // MARK: Lanes

    /// A subject split into its scope, the rest and the lanes the scope names.
    public struct ParsedSubject: Equatable, Sendable {
        public var scope: String?
        public var text: String
        public var lanes: [String]
    }

    /// `(mac, windows) title` or the conventional `fix(windows): title`; anything else has no scope.
    public static func parseSubject(_ subject: String) -> ParsedSubject {
        guard let parts = split(subject) else { return ParsedSubject(scope: nil, text: subject, lanes: ["etc"]) }
        let (scope, text) = parts
        return ParsedSubject(scope: scope, text: text, lanes: lanes(forScope: scope))
    }

    private static func split(_ subject: String) -> (String, String)? {
        if subject.hasPrefix("("), let close = subject.firstIndex(of: ")") {
            let scope = String(subject[subject.index(after: subject.startIndex)..<close])
            return (scope, String(subject[subject.index(after: close)...]).trimmingCharacters(in: .whitespaces))
        }
        var index = subject.startIndex
        while index < subject.endIndex, subject[index].isASCII, subject[index].isLetter { index = subject.index(after: index) }
        guard index > subject.startIndex, index < subject.endIndex, subject[index] == "(", let close = subject[index...].firstIndex(of: ")") else { return nil }
        var colon = subject.index(after: close)
        if colon < subject.endIndex, subject[colon] == "!" { colon = subject.index(after: colon) }
        guard colon < subject.endIndex, subject[colon] == ":" else { return nil }
        let scope = String(subject[subject.index(after: index)..<close])
        return (scope, String(subject[subject.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
    }

    /// Each comma part's first word picks a lane; the lanes come in `lanes` order, once each; none found is `etc`.
    public static func lanes(forScope scope: String) -> [String] {
        var found = Set<String>()
        for part in scope.split(separator: ",") {
            guard let word = part.split(whereSeparator: { $0.isWhitespace }).first?.lowercased() else { continue }
            switch word {
            case "mac", "macos": found.insert("mac")
            case "windows", "web", "winui": found.insert("windows")
            case "phone", "mobile", "android": found.insert("phone")
            case "ci", "release": found.insert("ci")
            default: break
            }
        }
        let ordered = WorkMap.lanes.filter { found.contains($0) }
        return ordered.isEmpty ? ["etc"] : ordered
    }

    // MARK: Tags and versions

    /// The numbers of `vX.Y.Z`, nil for any other name.
    static func tagNumbers(_ name: String) -> [Int]? {
        guard name.hasPrefix("v") else { return nil }
        let parts = name.dropFirst().split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var numbers: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.allSatisfy({ ("0"..."9").contains($0) }), let number = Int(part) else { return nil }
            numbers.append(number)
        }
        return numbers
    }

    /// Every `vX.Y.Z` name once, newest first.
    static func sortedTags(_ names: [String]) -> [String] {
        var seen = Set<String>()
        let valid = names.compactMap { name -> (String, [Int])? in tagNumbers(name).map { (name, $0) } }.filter { seen.insert($0.0).inserted }
        return valid.sorted { $1.1.lexicographicallyPrecedes($0.1) }.map { $0.0 }
    }

    /// The tags the map shows: release tags only, newest first, at most `maximumVersions`.
    public static func keptTags(_ names: [String]) -> [String] { Array(sortedTags(names).prefix(maximumVersions)) }

    /// The VERSION file's number when it is `x.y.z` and above the newest tag.
    public static func versionName(file: String?, newestTag: String?) -> String? {
        guard let text = file?.trimmingCharacters(in: .whitespacesAndNewlines), let numbers = tagNumbers("v" + text) else { return nil }
        if let newestTag, let newest = tagNumbers(newestTag), !newest.lexicographicallyPrecedes(numbers) { return nil }
        return text
    }

    /// `release/<version>/state.json` read: "releasing" with the next undone step once the push is done and the
    /// verify is not; anything else, including a file that is not JSON, is "unreleased".
    public static func releaseStatus(state: Data?) -> (status: String, step: String?) {
        guard let state, let object = (try? JSONSerialization.jsonObject(with: state)) as? [String: Any], let done = object["done"] as? [String: Any],
              (done["push"] as? Bool) == true, (done["verify"] as? Bool) != true else { return ("unreleased", nil) }
        return ("releasing", releaseSteps.first { (done[$0] as? Bool) != true })
    }

    /// The overview dot's diameter: `6 + 14·√(n / largest)`, rounded; none for no commits.
    public static func dotSize(count: Int, largest: Int) -> Double {
        guard count > 0, largest > 0 else { return 0 }
        return (dotMinimum + dotRange * (Double(min(count, largest)) / Double(largest)).squareRoot()).rounded()
    }

    // MARK: Requests

    /// The workspace's requests: each open pane's graph runs (notice runs and empty inputs left out) with the
    /// files `store` kept for the run.
    public static func requestInputs(sessions: [RunSession], store: FileOriginalStore?) -> [WorkMapRequestInput] {
        var inputs: [WorkMapRequestInput] = []
        for session in sessions {
            for run in session.graphRuns ?? [] where run.noticeRun != true {
                let text = run.input.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { continue }
                let files = store?.files(sessionId: session.id, runId: FileOriginalStore.originalsRun(run.id, in: session)) ?? []
                inputs.append(WorkMapRequestInput(id: session.id + "/" + run.id, paneId: session.id, text: String(text.prefix(requestTextLength)), date: run.rootEntries.first?.timestamp, files: files))
            }
        }
        return inputs
    }

    /// Puts each request on its version (newest `maximumRequests` only) and links the commits it led to.
    static func place(_ inputs: [WorkMapRequestInput], in versions: inout [WorkMapVersion], newestTagDate: Date?) {
        struct Flat { let versionIndex: Int; let sha: String; let date: Date?; let files: Set<String>; let order: Int }
        let formatter = ISO8601DateFormatter()
        var flat: [Flat] = []
        for (index, version) in versions.enumerated() {
            for commit in version.commits { flat.append(Flat(versionIndex: index, sha: commit.sha, date: formatter.date(from: commit.date), files: Set(commit.files), order: flat.count)) }
        }
        let hasNext = versions.first?.key == nextKey
        let newestFirst = inputs.enumerated().sorted { a, b in
            let left = a.element.date.flatMap(formatter.date(from:)) ?? .distantPast, right = b.element.date.flatMap(formatter.date(from:)) ?? .distantPast
            return left != right ? left > right : a.offset > b.offset
        }.prefix(maximumRequests).map(\.element)
        for input in newestFirst {
            let started = input.date.flatMap(formatter.date(from:))
            let touched = Set(input.files)
            let linked = flat.filter { commit in
                guard !touched.isEmpty, !commit.files.isDisjoint(with: touched) else { return false }
                guard let started else { return true }
                return (commit.date ?? .distantPast) >= started
            }.sorted { a, b in
                let left = a.date ?? .distantPast, right = b.date ?? .distantPast
                return left != right ? left < right : a.order > b.order
            }.prefix(maximumCommitsPerRequest)
            var target: Int?
            if let first = linked.first { target = first.versionIndex }
            else if hasNext, let started, newestTagDate.map({ started > $0 }) ?? true { target = 0 }
            guard let target else { continue }
            versions[target].requests.append(WorkMapRequest(id: input.id, paneId: input.paneId, text: input.text, date: input.date, files: input.files, commits: linked.map(\.sha)))
        }
    }

    // MARK: Reading

    private struct Tag { let name: String; let date: String; let tip: String }

    /// The map of the repository at `root`. `cached` are the saved summaries by version key, `errors` the last failed
    /// summary's words by key.
    public static func read(root: URL, requests: [WorkMapRequestInput], cached: [String: WorkMapSummaryEntry] = [:], errors: [String: String] = [:], summarizing: String? = nil) async -> WorkMapModel {
        let path = root.path
        func git(_ arguments: [String]) async -> ProcessResult? { await DelegationGit.run(arguments, in: path, timeout: gitTimeout) }
        guard let inside = await git(["rev-parse", "--is-inside-work-tree"]) else { return .unavailable("gitFailed") }
        guard inside.exitCode == 0, DelegationGit.line(inside.stdout) == "true" else { return .unavailable("notGit") }
        guard let head = await git(["rev-parse", "HEAD"]) else { return .unavailable("gitFailed") }
        // A repository with no commit yet has nothing to map.
        guard head.exitCode == 0, DelegationGit.isObjectName(DelegationGit.line(head.stdout)) else { return WorkMapModel(available: true, summarizing: summarizing) }
        let headSha = DelegationGit.line(head.stdout)

        guard let tagList = await git(["for-each-ref", "refs/tags", "--format=%(refname:short)%1f%(creatordate:iso-strict)%1f%(*objectname)%1f%(objectname)"]), tagList.exitCode == 0 else { return .unavailable("gitFailed") }
        var tags: [String: Tag] = [:]
        for line in String(decoding: tagList.stdout, as: UTF8.self).split(separator: "\n") {
            let fields = line.split(separator: "\u{1F}", omittingEmptySubsequences: false).map(String.init)
            guard fields.count == 4, tagNumbers(fields[0]) != nil else { continue }
            tags[fields[0]] = Tag(name: fields[0], date: fields[1], tip: fields[2].isEmpty ? fields[3] : fields[2])
        }
        let sorted = sortedTags(Array(tags.keys))
        let visible = Array(sorted.prefix(maximumVersions))

        struct Window { let key: String; let tag: Tag?; let range: String; let end: String }
        var ranges: [Window] = []
        ranges.append(Window(key: nextKey, tag: nil, range: sorted.first.map { "refs/tags/\($0)..HEAD" } ?? "HEAD", end: headSha))
        for (index, name) in visible.enumerated() {
            guard let tag = tags[name] else { continue }
            let below = index + 1 < sorted.count ? sorted[index + 1] : nil
            ranges.append(Window(key: name, tag: tag, range: below.map { "refs/tags/\($0)..refs/tags/\(name)" } ?? "refs/tags/\(name)", end: tag.tip))
        }

        let versionFile = try? String(contentsOf: root.appendingPathComponent("VERSION"), encoding: .utf8)
        let nextName = versionName(file: versionFile, newestTag: sorted.first)

        var versions: [WorkMapVersion] = []
        for item in ranges {
            guard let counted = await git(["rev-list", "--no-merges", "--count", item.range, "--"]), counted.exitCode == 0,
                  let total = Int(DelegationGit.line(counted.stdout)) else { return .unavailable("gitFailed") }
            if item.tag == nil, total == 0 { continue }
            guard let log = await git(["-c", "core.quotepath=false", "log", "--no-merges", "--max-count=\(maximumCommitsPerVersion)", "--format=%x1e%H%x1f%aI%x1f%s", "--name-only", item.range, "--"]),
                  log.exitCode == 0 else { return .unavailable("gitFailed") }
            let commits = parseLog(String(decoding: log.stdout, as: UTF8.self))
            var counts: [String: Int] = [:]
            for commit in commits { for lane in commit.lanes { counts[lane, default: 0] += 1 } }
            var status = "released", step: String?, name: String?, date = ""
            if let tag = item.tag {
                name = String(tag.name.dropFirst()); date = String(tag.date.prefix(10))
            } else {
                name = nextName; date = String((commits.first?.date ?? "").prefix(10)); status = "unreleased"
                if let nextName, let state = try? Data(contentsOf: root.appendingPathComponent("release/\(nextName)/state.json")) {
                    (status, step) = releaseStatus(state: state)
                }
            }
            var version = WorkMapVersion(key: item.key, version: name, tag: item.tag?.name, date: date, status: status, releaseStep: step, rangeEnd: item.end,
                                         total: total, truncated: total > commits.count, counts: counts, commits: commits)
            if let entry = cached[item.key] { version.topics = entry.topics; version.topicsStale = entry.rangeEnd != item.end }
            version.summaryError = errors[item.key]
            versions.append(version)
        }
        let formatter = ISO8601DateFormatter()
        place(requests, in: &versions, newestTagDate: sorted.first.flatMap { tags[$0] }.flatMap { formatter.date(from: $0.date) })
        return WorkMapModel(available: true, head: String(headSha.prefix(8)), summarizing: summarizing, versions: versions)
    }

    /// `git log --format=%x1e%H%x1f%aI%x1f%s --name-only` read: one commit per record separator.
    static func parseLog(_ output: String) -> [WorkMapCommit] {
        output.split(separator: "\u{1E}").compactMap { record -> WorkMapCommit? in
            let lines = record.split(separator: "\n", omittingEmptySubsequences: false)
            guard let first = lines.first else { return nil }
            let fields = first.split(separator: "\u{1F}", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
            guard fields.count == 3, fields[0].count >= 8 else { return nil }
            let files = lines.dropFirst().map(String.init).filter { !$0.isEmpty }
            let parsed = parseSubject(fields[2])
            return WorkMapCommit(sha: String(fields[0].prefix(8)), date: fields[1], lanes: parsed.lanes, scope: parsed.scope, subject: parsed.text,
                                 files: Array(files.prefix(maximumFilesPerCommit)), filesCount: files.count)
        }
    }
}
