import Foundation
import Testing
@testable import MightyCore

/// The source preview's 변경사항 (`FileDiff.swift`, `FileChanges.swift`):
/// native/contracts/fixtures/file-diff.json is the diff Windows
/// (`FileChangesVerification`) is held to as well; the rest mirror the Windows
/// capture, store, git base and phone route checks.
struct FileChangesTests {
    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/file-diff.json")
    }()

    private func temporary() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-changes-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func generated(_ spec: [String: Any], _ side: String) -> String {
        let count = spec[side] as? Int ?? 0, pattern = spec["line"] as? String ?? ""
        return (0..<count).map { pattern.replacingOccurrences(of: "{n}", with: String($0 + 1)) + "\n" }.joined()
    }

    @Test func theSharedCasesHold() throws {
        let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: Self.fixture)) as? [String: Any])
        #expect(root["context"] as? Int == FileDiff.context)
        #expect(root["maximumSideBytes"] as? Int == FileDiff.maximumSideBytes)
        #expect(root["maximumSideLines"] as? Int == FileDiff.maximumSideLines)
        #expect(root["maximumOutputLines"] as? Int == FileDiff.maximumOutputLines)
        let cases = try #require(root["cases"] as? [[String: Any]])
        #expect(cases.count >= 15)
        for item in cases {
            let name = item["name"] as? String ?? "?"
            let spec = item["generate"] as? [String: Any]
            let old = spec.map { generated($0, "old") } ?? (item["old"] as? String ?? "")
            let new = spec.map { generated($0, "new") } ?? (item["new"] as? String ?? "")
            let result = FileDiff.compute(old: old, new: new)
            guard let expect = item["expect"] as? [String: Any] else { #expect(result == nil, "\(name): no diff"); continue }
            let diff = try #require(result, "\(name): a diff")
            #expect(diff.added == expect["added"] as? Int && diff.removed == expect["removed"] as? Int, "\(name): +\(diff.added) -\(diff.removed)")
            #expect(diff.truncated == expect["truncated"] as? Bool && diff.unchanged == expect["unchanged"] as? Bool, "\(name)")
            if let hunks = expect["hunks"] {
                let wanted = try JSONDecoder().decode([FileDiffHunk].self, from: JSONSerialization.data(withJSONObject: hunks))
                #expect(diff.hunks == wanted, "\(name): \(diff.hunks)")
            } else {
                #expect(diff.hunks.count == expect["hunkCount"] as? Int, "\(name)")
                #expect(diff.hunks.reduce(0) { $0 + $1.lines.count } == expect["lineCount"] as? Int, "\(name)")
                let first = try #require(expect["firstHunk"] as? [String: Int]), hunk = try #require(diff.hunks.first)
                #expect([hunk.oldStart, hunk.oldLines, hunk.newStart, hunk.newLines] == [first["oldStart"], first["oldLines"], first["newStart"], first["newLines"]].map { $0 ?? -1 }, "\(name)")
                let last = try #require(diff.hunks.last?.lines.last), lastWanted = try #require(expect["lastLine"] as? [String: String])
                #expect(last.op == lastWanted["op"] && last.text == lastWanted["text"], "\(name)")
            }
        }
        // Beyond the fixture: the caps, and an edit script too long for Myers' trace.
        #expect(FileDiff.compute(old: String(repeating: "a", count: FileDiff.maximumSideBytes + 1), new: "") == nil)
        #expect(FileDiff.compute(old: String(repeating: "x\n", count: FileDiff.maximumSideLines + 1), new: "") == nil)
        let many = try #require(FileDiff.compute(old: (0..<3000).map { "a\($0)\n" }.joined(), new: (0..<3000).map { "b\($0)\n" }.joined()))
        #expect(many.removed == 3000 && many.added == 3000 && many.truncated && many.hunks.reduce(0) { $0 + $1.lines.count } == FileDiff.maximumOutputLines)
        #expect(many.hunks[0].lines.prefix(3000).allSatisfy { $0.op == "-" }, "past the edit distance the middle reads as removed then added")
        let cut = FileDiff.cut(try #require(FileDiff.compute(old: "a\nb\nc\n", new: "a\nB\nc\n")).hunks, lines: 2)
        #expect(cut.count == 1 && cut[0].oldStart == 1 && cut[0].oldLines == 2 && cut[0].newStart == 1 && cut[0].newLines == 1 && cut[0].lines.count == 2)
    }

    private func line(_ value: [String: Any]) throws -> Data {
        var data = try JSONSerialization.data(withJSONObject: value); data.append(10); return data
    }
    private func assistant(_ id: String, _ tool: String, _ path: String) throws -> Data {
        try line(["type": "assistant", "message": ["id": "m-" + id, "content": [["type": "tool_use", "id": id, "name": tool, "input": ["file_path": path]]]]])
    }
    private func result(_ id: String, _ toolUseResult: Any, error: Bool = false, key: String = "tool_use_result", parent: String? = nil) throws -> Data {
        var value: [String: Any] = ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": id, "is_error": error, "content": "ok"]]], key: toolUseResult]
        if let parent { value["parent_tool_use_id"] = parent }
        return try line(value)
    }

    @Test func theParserKeepsEachFilesFirstOriginalOfTheRun() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let root = directory.appendingPathComponent("workspace", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        func touch(_ name: String, _ text: String = "x\n") throws -> String { let url = root.appendingPathComponent(name); try Data(text.utf8).write(to: url); return url.path }
        let file = try touch("a.txt", "v3\n")
        let store = FileOriginalStore(directory: directory.appendingPathComponent("originals", isDirectory: true))
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        parser.fileOriginal = { path, original in _ = store.capture(sessionId: "pane", runId: "run-1", workspaceRoot: root, filePath: path, original: original) }
        parser.push(try assistant("t1", "Edit", file))
        parser.push(try result("t1", ["filePath": file, "originalFile": "v1\n", "structuredPatch": []]))
        parser.push(try assistant("t2", "Edit", file))
        parser.push(try result("t2", ["filePath": file, "originalFile": "v2\n"]))
        #expect(store.find(sessionId: "pane", runId: "run-1", relativePath: "a.txt") == FileOriginal(runId: "run-1", path: "a.txt", text: "v1\n", created: false), "the first original of the run is kept")
        let b = try touch("b.txt"), c = try touch("c.txt"), d = try touch("d.txt", "new\n")
        parser.push(try assistant("t3", "Read", b))
        parser.push(try result("t3", ["filePath": b, "originalFile": "read\n"]))
        parser.push(try assistant("t4", "Edit", c))
        parser.push(try result("t4", ["filePath": c, "originalFile": "failed\n"], error: true))
        parser.push(try assistant("t5", "MultiEdit", d))
        parser.push(try result("t5", ["filePath": d, "originalFile": "old\n"], key: "toolUseResult", parent: "agent-1"))
        #expect(store.find(sessionId: "pane", runId: "run-1", relativePath: "b.txt") == nil, "a Read's result is no original")
        #expect(store.find(sessionId: "pane", runId: "run-1", relativePath: "c.txt") == nil, "a failed edit is no original")
        #expect(store.find(sessionId: "pane", runId: "run-1", relativePath: "d.txt")?.text == "old\n", "a sub-agent's MultiEdit counts, toolUseResult as transcripts write it")
        let created = try touch("new.txt", "one\ntwo\n")
        parser.push(try assistant("t6", "Write", created))
        parser.push(try result("t6", ["type": "create", "filePath": created, "content": "one\ntwo\n", "originalFile": NSNull()]))
        #expect(store.find(sessionId: "pane", runId: "run-1", relativePath: "new.txt") == FileOriginal(runId: "run-1", path: "new.txt", text: "", created: true))
        let made = try #require(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "new.txt", sessionId: "pane"))
        #expect(made.base == "run" && made.runId == "run-1" && made.created == true && made.added == 2 && made.removed == 0)
        let changed = try #require(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "a.txt", sessionId: "pane", runId: "run-1"))
        #expect(changed.base == "run" && changed.hunks[0].lines.map { $0.op + $0.text } == ["-v1", "+v3"])
        let wire = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(changed)) as? [String: Any])
        #expect(wire["base"] as? String == "run" && wire["runId"] as? String == "run-1" && wire["headSha"] == nil)
    }

    @Test func theStoreKeepsItsCapsAndSurvivesARestart() throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let root = directory.appendingPathComponent("workspace", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        func touch(_ name: String) -> String { let url = root.appendingPathComponent(name); try? Data("now\n".utf8).write(to: url); return url.path }
        let originals = directory.appendingPathComponent("originals", isDirectory: true)
        let store = FileOriginalStore(directory: originals)
        #expect(!store.capture(sessionId: "pane", runId: "run-1", workspaceRoot: root, filePath: touch("big.txt"), original: String(repeating: "x", count: FileOriginalStore.maximumOriginalBytes + 1)))
        #expect(!store.capture(sessionId: "pane", runId: "run-1", workspaceRoot: root, filePath: touch("big.txt"), original: "small\n"), "a path refused once in a run stays refused")
        for n in 0..<FileOriginalStore.maximumFilesPerRun {
            #expect(store.capture(sessionId: "pane", runId: "run-2", workspaceRoot: root, filePath: touch("f\(n).txt"), original: "o\n"))
        }
        #expect(!store.capture(sessionId: "pane", runId: "run-2", workspaceRoot: root, filePath: touch("f64.txt"), original: "o\n"), "the 65th file of a run is refused")
        let megabyte = String(repeating: "m", count: FileOriginalStore.maximumOriginalBytes)
        for n in 0..<16 { #expect(store.capture(sessionId: "pane", runId: "run-3", workspaceRoot: root, filePath: touch("m\(n).txt"), original: megabyte)) }
        #expect(!store.capture(sessionId: "pane", runId: "run-3", workspaceRoot: root, filePath: touch("m16.txt"), original: "o\n"), "a run past 16 MiB keeps nothing more")
        #expect(store.capture(sessionId: "other", runId: "run-1", workspaceRoot: root, filePath: touch("a.txt"), original: "o\n"))
        #expect(store.capture(sessionId: "pane", runId: "run-4", workspaceRoot: root, filePath: touch("a.txt"), original: "first\n"))
        #expect(store.capture(sessionId: "pane", runId: "run-5", workspaceRoot: root, filePath: touch("a.txt"), original: "second\n"))
        let reopened = FileOriginalStore(directory: originals)
        #expect(reopened.find(sessionId: "pane", runId: nil, relativePath: "a.txt")?.runId == "run-5", "the newest run answers after a restart")
        #expect(reopened.find(sessionId: "pane", runId: "run-4", relativePath: "a.txt")?.text == "first\n")
        #expect(reopened.find(sessionId: "pane", runId: "run-9", relativePath: "a.txt") == nil && reopened.find(sessionId: "nobody", runId: nil, relativePath: "a.txt") == nil)
        reopened.retain(sessionId: "pane", keep: ["run-4"])
        #expect(reopened.runs(sessionId: "pane") == ["run-4"], "runs the graph let go are dropped")
        reopened.forget(sessionId: "other")
        #expect(reopened.find(sessionId: "other", runId: nil, relativePath: "a.txt") == nil, "a closed pane's originals go")
        reopened.capture(sessionId: "gone", runId: "run-1", workspaceRoot: root, filePath: touch("a.txt"), original: "o\n")
        reopened.prune(sessions: ["pane": ["run-4", "run-7"]])
        #expect(reopened.find(sessionId: "gone", runId: nil, relativePath: "a.txt") == nil && reopened.find(sessionId: "pane", runId: "run-4", relativePath: "a.txt") != nil)
        let outside = directory.appendingPathComponent("outside.txt"); try Data("x".utf8).write(to: outside)
        #expect(!reopened.capture(sessionId: "pane", runId: "run-6", workspaceRoot: root, filePath: outside.path, original: "x"))
        #expect(!reopened.capture(sessionId: "pane", runId: "run-6", workspaceRoot: root, filePath: "../outside.txt", original: "x"))
        #expect(!reopened.capture(sessionId: "pane", runId: "run-6", workspaceRoot: root, filePath: root.appendingPathComponent("missing.txt").path, original: "x"))
        #expect(!reopened.capture(sessionId: "../pane", runId: "run-6", workspaceRoot: root, filePath: touch("a.txt"), original: "x"))
    }

    private func git(_ folder: URL, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"] + arguments
        process.currentDirectoryURL = folder
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw MightyError("git \(arguments.joined(separator: " ")) failed") }
    }

    private func insideRepository(_ folder: URL) -> Bool {
        let process = Process(), pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = ["rev-parse", "--is-inside-work-tree"]
        process.currentDirectoryURL = folder
        process.standardOutput = pipe; process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return false }
        process.waitUntilExit()
        return process.terminationStatus == 0 && String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) == "true"
    }

    @Test func withoutARunOriginalGitsHeadIsTheBase() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let repository = directory.appendingPathComponent("repo", isDirectory: true), root = repository.appendingPathComponent("app", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try git(repository, ["init", "-q"])
        try Data("one\ntwo\nthree\n".utf8).write(to: root.appendingPathComponent("tracked.txt"))
        try Data([1, 0, 2]).write(to: root.appendingPathComponent("binary.dat"))
        try git(repository, ["add", "."]); try git(repository, ["commit", "-q", "-m", "fixture"])
        try Data("one\r\nTWO\r\nthree\r\n".utf8).write(to: root.appendingPathComponent("tracked.txt"))
        try Data("a\nb\n".utf8).write(to: root.appendingPathComponent("untracked.txt"))
        try Data([1, 0, 3]).write(to: root.appendingPathComponent("binary.dat"))
        let store = FileOriginalStore(directory: directory.appendingPathComponent("originals", isDirectory: true))
        let head = try #require(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "tracked.txt", sessionId: "pane"))
        #expect(head.base == "head" && head.created == nil && head.headSha?.count == 7 && head.hunks[0].lines.map { $0.op + $0.text } == [" one", "-two", "+TWO", " three"])
        let untracked = try #require(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "untracked.txt"))
        #expect(untracked.base == "head" && untracked.created == true && untracked.added == 2)
        #expect(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "binary.dat") == nil)
        #expect(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "../outside.txt") == nil)
        store.capture(sessionId: "pane", runId: "run-1", workspaceRoot: root, filePath: root.appendingPathComponent("tracked.txt").path, original: "one\nthree\n")
        #expect(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "tracked.txt", sessionId: "pane")?.base == "run")
        #expect(await FileChanges.read(store: store, workspaceRoot: root, relativePath: "tracked.txt", sessionId: "pane", runId: "run-2")?.base == "head")
        let unborn = directory.appendingPathComponent("unborn", isDirectory: true)
        try FileManager.default.createDirectory(at: unborn, withIntermediateDirectories: true); try git(unborn, ["init", "-q"])
        try Data("x\n".utf8).write(to: unborn.appendingPathComponent("a.txt"))
        let fresh = try #require(await FileChanges.read(store: store, workspaceRoot: unborn, relativePath: "a.txt"))
        #expect(fresh.base == "head" && fresh.headSha == nil && fresh.created == true && fresh.added == 1)
        let plain = directory.appendingPathComponent("plain", isDirectory: true)
        try FileManager.default.createDirectory(at: plain, withIntermediateDirectories: true)
        try Data("x\n".utf8).write(to: plain.appendingPathComponent("a.txt"))
        // The temporary folder is in no repository on a usual machine; one under a checkout cannot show this.
        if !insideRepository(plain) { #expect(await FileChanges.read(store: store, workspaceRoot: plain, relativePath: "a.txt") == nil) }
    }

    @Test func thePhoneAsksForChangesOnlyWithTheCapabilitysArguments() async throws {
        let directory = try temporary(); defer { try? FileManager.default.removeItem(at: directory) }
        let root = directory.appendingPathComponent("workspace", isDirectory: true), data = directory.appendingPathComponent("data", isDirectory: true)
        for url in [root, data] { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true) }
        try Data("one\nTWO\n".utf8).write(to: root.appendingPathComponent("a.txt"))
        let store = FileOriginalStore(directory: directory.appendingPathComponent("originals", isDirectory: true))
        store.capture(sessionId: "pane", runId: "run-1", workspaceRoot: root, filePath: root.appendingPathComponent("a.txt").path, original: "one\ntwo\n")
        let host = ChangesHost(root: root, store: store)
        let service = MobileRemoteService(dataDirectory: data, hostName: "Changes Mac", watchesNetwork: false)
        await service.attach(host)
        func get(_ path: String) async -> (status: Int, body: [String: Any]) {
            let reply = await service.route(method: "GET", path: path, body: nil, deviceId: "cGhvbmUtb25lLTAwMDAwMDA")
            return (reply.status, (try? JSONSerialization.jsonObject(with: reply.body) as? [String: Any]) ?? [:])
        }
        #expect((await get("/m1/info").body["capabilities"] as? [String])?.contains("fileChanges") == true)
        let plain = await get("/m1/workspaces/workspace-1/file?path=a.txt")
        #expect(plain.status == 200 && plain.body["changes"] == nil)
        let run = await get("/m1/workspaces/workspace-1/file?path=a.txt&changes=1&session=pane&run=run-1")
        let changes = try #require(run.body["changes"] as? [String: Any])
        #expect(run.status == 200 && changes["base"] as? String == "run" && changes["added"] as? Int == 1 && run.body["text"] as? String == "one\nTWO\n")
        let foreign = await get("/m1/workspaces/workspace-1/file?path=a.txt&changes=1&session=elsewhere")
        #expect(foreign.status == 200 && foreign.body["changes"] == nil, "a pane of another workspace lends no original")
        #expect(await get("/m1/workspaces/workspace-1/file?path=a.txt&session=pane").status == 400)
        #expect(await get("/m1/workspaces/workspace-1/file?path=a.txt&changes=yes").status == 400)
        #expect(await get("/m1/workspaces/workspace-1/file?path=a.txt&changes=1&run=..%2Fx").status == 400)
        #expect(await get("/m1/workspaces/workspace-1/files?changes=1").status == 400)
        #expect(await get("/m1/workspaces/workspace-1/file?path=..%2Fa.txt&changes=1&session=pane").status == 403)
        // A reply over the frame cuts the changes with the text.
        var huge = MobileFilePreview(workspaceId: "w", path: "a", name: "a", size: 1, modified: nil, type: "source")
        huge.text = String(repeating: "x", count: 900_000)
        let diff = try #require(FileDiff.compute(old: "", new: (0..<4000).map { String(repeating: "y", count: 200) + "\($0)\n" }.joined()))
        huge.changes = FileChangeSet(base: "head", diff: diff, created: false)
        let encoded = MobileWorkspaceFiles.encoded(huge)
        let decoded = try JSONDecoder().decode(MobileFilePreview.self, from: encoded)
        #expect(encoded.count <= MobileWorkspaceFiles.maximumReplyBytes && decoded.changes?.truncated == true && decoded.truncated == true)
    }
}

/// A Mac with one workspace at `root` and the pane "pane" in it; its file
/// changes come from `store` the way the app's bridge reads them.
private final class ChangesHost: MobileHostDelegate, @unchecked Sendable {
    let root: URL
    let store: FileOriginalStore
    init(root: URL, store: FileOriginalStore) { self.root = root; self.store = store }
    func mobileState() async -> MobileState {
        MobileState(revision: 1, hostName: "Changes Mac", workspaces: [MobileWorkspace(id: "workspace-1", name: "Repo", path: root.path)], sessions: [])
    }
    func mobileFileChanges(workspaceId: String, workspaceRoot: URL, path: String, sessionId: String?, runId: String?) async -> FileChangeSet? {
        let pane = sessionId == "pane" && workspaceId == "workspace-1" ? sessionId : nil
        return await FileChanges.read(store: store, workspaceRoot: workspaceRoot, relativePath: path, sessionId: pane, runId: pane == nil ? nil : runId)
    }
    func mobileSession(id: String) async -> MobileSessionDetail? { nil }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String { "started" }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String { "started" }
    func mobileStop(sessionId: String) async throws -> Bool { false }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {}
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {}
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { "new" }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {}
    func mobileRunNextQueued(sessionId: String) async throws {}
    func mobileRename(sessionId: String, title: String, titleMode: String?) async throws {}
    func mobileClose(sessionId: String) async throws {}
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage { MobileEntriesPage(entries: [], hasMore: false) }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {}
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] { [] }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? { nil }
}
