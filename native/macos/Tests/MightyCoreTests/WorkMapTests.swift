import Foundation
import Testing
@testable import MightyCore

/// The work map's rules (docs/work-map.md) that the Windows Core and the phone read from the same file
/// (`native/contracts/fixtures/work-map.json`), and a real repository read through git. Every wait here
/// is an awaited process or a file read: nothing blocks the cooperative pool.
struct WorkMapTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/work-map.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    static func cases(_ key: String) throws -> [[String: Any]] {
        let cases = try #require(try load()[key] as? [[String: Any]], "\(key)")
        #expect(!cases.isEmpty, "\(key) has no cases")
        return cases
    }

    @Test func limitsAndVocabulariesMatchTheFixture() throws {
        let fixture = try Self.load()
        #expect(fixture["maximumVersions"] as? Int == WorkMap.maximumVersions)
        #expect(fixture["maximumCommitsPerVersion"] as? Int == WorkMap.maximumCommitsPerVersion)
        #expect(fixture["maximumFilesPerCommit"] as? Int == WorkMap.maximumFilesPerCommit)
        #expect(fixture["maximumRequests"] as? Int == WorkMap.maximumRequests)
        #expect(fixture["maximumCommitsPerRequest"] as? Int == WorkMap.maximumCommitsPerRequest)
        #expect(fixture["requestTextLength"] as? Int == WorkMap.requestTextLength)
        #expect(fixture["maximumTopics"] as? Int == WorkMapSummary.maximumTopics)
        #expect(fixture["titleLength"] as? Int == WorkMapSummary.titleLength)
        #expect(fixture["summaryLength"] as? Int == WorkMapSummary.summaryLength)
        #expect(fixture["notesLength"] as? Int == WorkMapSummary.notesLength)
        #expect(fixture["summaryTimeoutSeconds"] as? Int == Int(WorkMapSummary.timeout))
        #expect(fixture["lanes"] as? [String] == WorkMap.lanes)
        #expect(fixture["releaseSteps"] as? [String] == WorkMap.releaseSteps)
        #expect(fixture["prompt"] as? String == WorkMapSummary.promptTemplate)
        let dot = try #require(fixture["dotSize"] as? [String: Any])
        #expect(dot["minimum"] as? Double == WorkMap.dotMinimum)
        #expect(dot["range"] as? Double == WorkMap.dotRange)
    }

    @Test func everyLaneCase() throws {
        for entry in try Self.cases("laneCases") {
            let subject = try #require(entry["subject"] as? String)
            let parsed = WorkMap.parseSubject(subject)
            #expect(parsed.scope == entry["scope"] as? String, "\(subject)")
            #expect(parsed.text == entry["text"] as? String, "\(subject)")
            #expect(parsed.lanes == entry["lanes"] as? [String], "\(subject)")
        }
    }

    @Test func everyTagCase() throws {
        for entry in try Self.cases("tagCases") {
            let tags = try #require(entry["tags"] as? [String])
            #expect(WorkMap.keptTags(tags) == entry["kept"] as? [String], "\(tags)")
        }
        let many = (1...20).map { "v0.1.\($0)" }
        #expect(WorkMap.keptTags(many).count == WorkMap.maximumVersions)
        #expect(WorkMap.keptTags(many).first == "v0.1.20")
    }

    @Test func everyVersionNameCase() throws {
        for entry in try Self.cases("versionNameCases") {
            let file = entry["file"] as? String, newest = entry["newestTag"] as? String
            #expect(WorkMap.versionName(file: file, newestTag: newest) == entry["version"] as? String, "\(file ?? "nil") after \(newest ?? "nil")")
        }
    }

    @Test func everyReleaseStatusCase() throws {
        for entry in try Self.cases("releaseStatusCases") {
            var data: Data?
            if let text = entry["state"] as? String { data = Data(text.utf8) }
            else if let object = entry["state"] as? [String: Any] { data = try JSONSerialization.data(withJSONObject: object) }
            let status = WorkMap.releaseStatus(state: data)
            #expect(status.status == entry["status"] as? String, "\(entry)")
            #expect(status.step == entry["step"] as? String, "\(entry)")
        }
    }

    @Test func everyDotSizeCase() throws {
        let dot = try #require(try Self.load()["dotSize"] as? [String: Any])
        for entry in try #require(dot["cases"] as? [[String: Any]]) {
            let count = try #require(entry["count"] as? Int), largest = try #require(entry["largest"] as? Int), size = try #require(entry["size"] as? Int)
            #expect(WorkMap.dotSize(count: count, largest: largest) == Double(size), "\(count) of \(largest)")
        }
    }

    @Test func promptFillsEachNameOnce() {
        let text = WorkMapSummary.prompt(language: "Korean", version: "0.4.49", commits: "abc12345 [mac] note {requests}", requests: "(none)", notes: "{version}")
        #expect(text.contains("Answer in Korean."))
        #expect(text.contains("Version: 0.4.49\n"))
        #expect(text.contains("abc12345 [mac] note {requests}\n"))
        #expect(text.hasSuffix("Release notes:\n{version}"))
        #expect(text.contains("{\"topics\":[{\"title\":\"…\""))
    }

    @Test func everySummaryCase() throws {
        for entry in try Self.cases("summaryCases") {
            let name = entry["name"] as? String ?? "unnamed"
            let commits = try #require(entry["commits"] as? [[String: Any]], "\(name)").map { (sha: $0["sha"] as? String ?? "", lanes: $0["lanes"] as? [String] ?? []) }
            let topics = WorkMapSummary.topics(answer: entry["answer"] as? String ?? "", commits: commits, requests: entry["requests"] as? [String] ?? [], otherTitle: "@other")
            if entry["error"] as? Bool == true { #expect(topics == nil, "\(name)"); continue }
            let expected = try #require(entry["topics"] as? [[String: Any]], "\(name)").map {
                WorkMapTopic(title: $0["title"] as? String ?? "", summary: $0["summary"] as? String ?? "", lanes: $0["lanes"] as? [String] ?? [],
                             commits: $0["commits"] as? [String] ?? [], requests: $0["requests"] as? [String] ?? [])
            }
            #expect(topics == expected, "\(name)")
        }
    }

    @Test func theResultEventIsTheAnswer() {
        let stream = "{\"type\":\"system\"}\n{\"type\":\"result\",\"is_error\":false,\"result\":\"{\\\"topics\\\":[]}\"}\n"
        #expect(WorkMapSummary.resultText(Data(stream.utf8)) == "{\"topics\":[]}")
        #expect(WorkMapSummary.resultText(Data("{\"type\":\"result\",\"is_error\":true,\"result\":\"x\"}\n".utf8)) == nil)
        #expect(WorkMapSummary.resultText(Data("not json".utf8)) == nil)
    }

    @Test func theSummaryIsKeptPerWorkspace() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("work-map-store-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = WorkMapSummaryStore(directory: directory)
        let root = directory.appendingPathComponent("workspace", isDirectory: true)
        #expect(store.load(root: root).isEmpty)
        let topic = WorkMapTopic(title: "A", summary: "a", lanes: ["mac"], commits: ["aaaaaaa1"], requests: [])
        try store.save(WorkMapSummaryEntry(rangeEnd: "end1", topics: [topic]), key: "v0.1.0", root: root)
        try store.save(WorkMapSummaryEntry(rangeEnd: "end2", topics: [topic]), key: "next", root: root)
        let loaded = store.load(root: root)
        #expect(Set(loaded.keys) == ["v0.1.0", "next"])
        #expect(loaded["next"]?.rangeEnd == "end2" && loaded["next"]?.topics == [topic])
        #expect(store.file(root: root).deletingPathExtension().lastPathComponent.count == 64)
        #expect(store.load(root: directory.appendingPathComponent("other")).isEmpty)
    }

    @Test func theModelWritesNullsAndThePhoneGetsNoCommitFiles() throws {
        let commit = WorkMapCommit(sha: "aaaaaaa1", date: "2026-11-03T01:14:00+09:00", lanes: ["mac"], scope: nil, subject: "s", files: ["a.txt"], filesCount: 1)
        let version = WorkMapVersion(key: "next", version: nil, tag: nil, date: "2026-11-03", status: "unreleased", releaseStep: nil, rangeEnd: "e", total: 1, truncated: false, counts: ["mac": 1], commits: [commit])
        let model = WorkMapModel(available: true, head: "aaaaaaa1", versions: [version])
        let object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(model)) as? [String: Any])
        #expect(object["reason"] is NSNull && object["summarizing"] is NSNull)
        let encoded = try #require((object["versions"] as? [[String: Any]])?.first)
        #expect(encoded["topics"] is NSNull && encoded["tag"] is NSNull && encoded["version"] is NSNull)
        let phone = try #require(JSONSerialization.jsonObject(with: model.phoneBody()) as? [String: Any])
        let phoneCommit = try #require(((phone["versions"] as? [[String: Any]])?.first?["commits"] as? [[String: Any]])?.first)
        #expect(phoneCommit["files"] == nil && phoneCommit["filesCount"] as? Int == 1 && phoneCommit["scope"] is NSNull)
        #expect(phone["protocol"] as? Int == 1)
    }
}

/// A real repository: tags, the unreleased range, VERSION, the release state and the requests placed on it.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"))
struct WorkMapRepositoryTests {
    private func git(_ arguments: [String], in folder: URL) async throws -> String {
        let settings = ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false"]
        let result = try #require(await DelegationGit.run(settings + arguments, in: folder.path, timeout: 30))
        #expect(result.exitCode == 0, "git \(arguments): \(String(decoding: result.stderr, as: UTF8.self))")
        return DelegationGit.line(result.stdout)
    }

    private func commit(_ message: String, file: String, text: String, in folder: URL) async throws {
        try Data(text.utf8).write(to: folder.appendingPathComponent(file))
        _ = try await git(["add", "-A"], in: folder)
        _ = try await git(["commit", "-m", message], in: folder)
    }

    private func repository() async throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("work-map-repo-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        _ = try await git(["init", "-b", "main"], in: folder)
        try await commit("(mac) first", file: "a.txt", text: "1", in: folder)
        _ = try await git(["tag", "v0.1.0"], in: folder)
        _ = try await git(["tag", "not-a-release-v9"], in: folder)
        try await commit("(windows) second", file: "b.txt", text: "1", in: folder)
        try await commit("fix(phone): third", file: "a.txt", text: "2", in: folder)
        _ = try await git(["tag", "v0.1.1"], in: folder)
        _ = try await git(["checkout", "-b", "side"], in: folder)
        try await commit("(ci) side work", file: "c.txt", text: "1", in: folder)
        _ = try await git(["checkout", "main"], in: folder)
        try await commit("(release) bump", file: "VERSION", text: "0.1.2\n", in: folder)
        _ = try await git(["merge", "--no-ff", "side", "-m", "Merge branch 'side'"], in: folder)
        // release/ is never committed: it only exists on the PC that releases.
        let state = folder.appendingPathComponent("release/0.1.2", isDirectory: true)
        try FileManager.default.createDirectory(at: state, withIntermediateDirectories: true)
        try Data("{\"done\":{\"check\":true,\"version\":true,\"push\":true}}".utf8).write(to: state.appendingPathComponent("state.json"))
        return folder
    }

    @Test func versionsRangesAndLanesComeFromGit() async throws {
        let folder = try await repository()
        defer { try? FileManager.default.removeItem(at: folder) }
        let model = await WorkMap.read(root: folder, requests: [])
        let headSha = try await git(["rev-parse", "HEAD"], in: folder)
        let secondTip = try await git(["rev-parse", "v0.1.1"], in: folder)
        #expect(model.available && model.reason == nil)
        #expect(model.head == String(headSha.prefix(8)))
        #expect(model.versions.map(\.key) == ["next", "v0.1.1", "v0.1.0"])

        let next = model.versions[0]
        #expect(next.version == "0.1.2" && next.tag == nil)
        #expect(next.status == "releasing" && next.releaseStep == "ci")
        #expect(next.total == 2 && !next.truncated, "the merge is left out")
        #expect(Set(next.commits.map(\.subject)) == ["side work", "bump"])
        #expect(next.rangeEnd == headSha)
        #expect(next.counts == ["ci": 2])

        let second = model.versions[1]
        #expect(second.version == "0.1.1" && second.tag == "v0.1.1" && second.status == "released" && second.releaseStep == nil)
        #expect(second.commits.map(\.subject) == ["third", "second"])
        #expect(second.commits.map(\.scope) == ["phone", "windows"])
        #expect(second.commits.map(\.lanes) == [["phone"], ["windows"]])
        #expect(second.counts == ["phone": 1, "windows": 1])
        #expect(second.commits[0].files == ["a.txt"] && second.commits[0].filesCount == 1)
        #expect(second.rangeEnd == secondTip)
        #expect(second.date.count == 10)

        let first = model.versions[2]
        #expect(first.commits.map(\.subject) == ["first"] && first.total == 1)
    }

    @Test func requestsAreLinkedToTheCommitsTheirFilesLedTo() async throws {
        let folder = try await repository()
        defer { try? FileManager.default.removeItem(at: folder) }
        let old = "2000-01-01T00:00:00Z", future = "2999-01-01T00:00:00Z"
        let inputs = [
            WorkMapRequestInput(id: "p/b", paneId: "p", text: "b", date: old, files: ["b.txt"]),
            WorkMapRequestInput(id: "p/a", paneId: "p", text: "a", date: old, files: ["a.txt"]),
            WorkMapRequestInput(id: "p/future", paneId: "p", text: "f", date: future, files: []),
            WorkMapRequestInput(id: "p/lost", paneId: "p", text: "l", date: old, files: []),
            WorkMapRequestInput(id: "p/later", paneId: "p", text: "x", date: future, files: ["a.txt"]),
        ]
        let model = await WorkMap.read(root: folder, requests: inputs)
        let byKey = Dictionary(uniqueKeysWithValues: model.versions.map { ($0.key, $0) })
        #expect(Set(byKey["next"]?.requests.map(\.id) ?? []) == ["p/future", "p/later"])
        #expect(byKey["v0.1.1"]?.requests.map(\.id) == ["p/b"])
        #expect(byKey["v0.1.1"]?.requests.first?.commits == byKey["v0.1.1"]?.commits.filter { $0.subject == "second" }.map(\.sha))
        #expect(byKey["v0.1.0"]?.requests.map(\.id) == ["p/a"], "the request lands on the oldest commit it led to")
        #expect(byKey["v0.1.0"]?.requests.first?.commits.count == 2)
        #expect(!model.versions.flatMap(\.requests).contains { $0.id == "p/lost" })
    }

    @Test func aSavedSummaryIsStaleOnceTheRangeMoves() async throws {
        let folder = try await repository()
        defer { try? FileManager.default.removeItem(at: folder) }
        let topic = WorkMapTopic(title: "A", summary: "a", lanes: ["mac"], commits: ["aaaaaaa1"], requests: [])
        let current = await WorkMap.read(root: folder, requests: [])
        let end = try #require(current.versions.first { $0.key == "v0.1.0" }?.rangeEnd)
        let model = await WorkMap.read(root: folder, requests: [], cached: ["v0.1.0": WorkMapSummaryEntry(rangeEnd: end, topics: [topic]), "v0.1.1": WorkMapSummaryEntry(rangeEnd: "old", topics: [topic])],
                                       errors: ["next": "failed"], summarizing: "next")
        let byKey = Dictionary(uniqueKeysWithValues: model.versions.map { ($0.key, $0) })
        #expect(byKey["v0.1.0"]?.topics == [topic] && byKey["v0.1.0"]?.topicsStale == false)
        #expect(byKey["v0.1.1"]?.topics == [topic] && byKey["v0.1.1"]?.topicsStale == true)
        #expect(byKey["next"]?.topics == nil && byKey["next"]?.summaryError == "failed")
        #expect(model.summarizing == "next")
    }

    @Test func aFolderThatIsNotARepositoryHasNoMap() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("work-map-plain-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let model = await WorkMap.read(root: folder, requests: [])
        #expect(!model.available && model.reason == "notGit" && model.versions.isEmpty)
    }
}
