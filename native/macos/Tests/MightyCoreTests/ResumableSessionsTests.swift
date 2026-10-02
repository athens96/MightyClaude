import Darwin
import Foundation
import Testing
@testable import MightyCore

/// Earlier Claude and Codex sessions of a workspace folder that a new pane can continue.
struct ResumableSessionsTests {
    // MARK: Fixtures

    private struct Fixture {
        let home: URL
        /// The workspace as the app knows it: a link to `real`.
        let workspace: String
        let real: String
        var projects: URL { home.appendingPathComponent(".claude/projects", isDirectory: true) }
        var codexSessions: URL { home.appendingPathComponent(".codex/sessions", isDirectory: true) }
    }

    private static let now = Date(timeIntervalSince1970: 1_790_000_000)

    private func fixture() throws -> Fixture {
        let root = URL(fileURLWithPath: realpath(FileManager.default.temporaryDirectory.path, nil).map { defer { free($0) }; return String(cString: $0) }!)
            .appendingPathComponent("mc-resume-\(UUID().uuidString)", isDirectory: true)
        let home = root.appendingPathComponent("home", isDirectory: true)
        let real = root.appendingPathComponent("work/real project", isDirectory: true)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: real, withIntermediateDirectories: true)
        let link = root.appendingPathComponent("work/linked", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: real)
        return Fixture(home: home, workspace: link.path, real: real.path)
    }
    private func line(_ object: [String: Any]) -> String {
        String(decoding: try! JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
    }
    private func id(_ n: Int) -> String { String(format: "%08x-0000-4000-8000-%012x", n, n) }
    private func thread(_ n: Int) -> String { String(format: "01a0%04x-0000-7000-8000-%012x", n, n) }

    private func claudeLines(cwd: String, prompt: String, model: String = "claude-sonnet-4-5", turns: Int = 1) -> [String] {
        var lines = [
            line(["type": "queue-operation", "operation": "enqueue", "timestamp": "2026-09-17T00:36:49.224Z", "content": prompt]),
            line(["type": "attachment", "cwd": cwd, "isSidechain": false, "attachment": ["type": "hook_success"]]),
        ]
        for turn in 0..<turns {
            lines += [
                line(["type": "user", "cwd": cwd, "isSidechain": false, "uuid": "u\(turn)", "message": ["role": "user", "content": turn == 0 ? prompt : "다음 \(turn)"]]),
                line(["type": "assistant", "cwd": cwd, "isSidechain": false, "message": ["model": model, "role": "assistant", "content": [["type": "tool_use", "id": "t\(turn)", "name": "Bash", "input": ["command": "ls"]]]]]),
                line(["type": "user", "cwd": cwd, "toolUseResult": ["stdout": "ok"], "message": ["role": "user", "content": [["type": "tool_result", "tool_use_id": "t\(turn)", "content": "ok"]]]]),
                line(["type": "assistant", "cwd": cwd, "isSidechain": false, "message": ["model": model, "role": "assistant", "content": [["type": "text", "text": "답"]]]]),
            ]
        }
        return lines
    }
    @discardableResult
    private func writeClaude(_ f: Fixture, folderPath: String, id: String, lines: [String], modified: Date, trailing: String = "\n") throws -> URL {
        let folder = f.projects.appendingPathComponent(SessionHistory.claudeProjectFolder(folderPath), isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(id + ".jsonl")
        try (lines.joined(separator: "\n") + trailing).write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        return url
    }

    private func codexLines(cwd: String, thread: String, prompt: String, model: String = "gpt-5.5", source: Any = "exec", turns: Int = 1) -> [String] {
        var lines = [line(["timestamp": "2026-09-30T07:55:38.307Z", "ordinal": 0, "type": "session_meta",
                           "payload": ["id": thread, "cwd": cwd, "source": source, "base_instructions": String(repeating: "x", count: 40_000)]])]
        for turn in 0..<turns {
            lines += [
                line(["type": "response_item", "payload": ["type": "message", "role": "user", "content": [["type": "input_text", "text": "<environment_context>injected</environment_context>"]]]]),
                line(["type": "event_msg", "payload": ["type": "task_started", "turn_id": "turn-\(turn)"]]),
                line(["type": "turn_context", "payload": ["turn_id": "turn-\(turn)", "cwd": cwd, "model": model]]),
                line(["type": "event_msg", "payload": ["type": "item_completed", "thread_id": thread,
                                                       "item": ["type": "UserMessage", "id": "m\(turn)", "content": [["type": "text", "text": turn == 0 ? prompt : "후속 \(turn)"]]]]]),
                line(["type": "event_msg", "payload": ["type": "task_complete"]]),
            ]
        }
        return lines
    }
    @discardableResult
    private func writeCodex(_ f: Fixture, day: String = "2026/09/30", thread: String, lines: [String], modified: Date) throws -> URL {
        let folder = f.codexSessions.appendingPathComponent(day, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("rollout-2026-09-30T16-55-38-\(thread).jsonl")
        try (lines.joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        return url
    }
    private func query(_ f: Fixture, excluding: Set<String> = [], environment: [String: String] = [:], candidates: Int = 600, sessions: Int = 200,
                       known: Set<String> = [], all: Bool = false) -> ResumableSessionQuery {
        var environment = environment
        if environment["CODEX_HOME"] == nil { environment["CODEX_HOME"] = f.home.appendingPathComponent(".codex").path }
        return ResumableSessionQuery(workspacePath: f.workspace, environment: environment, home: f.home, excluding: excluding,
                                     known: known, includeAutomated: all, now: Self.now, maximumCandidates: candidates, maximumSessions: sessions)
    }
    private func ago(_ minutes: Double) -> Date { Self.now.addingTimeInterval(-minutes * 60) }

    // MARK: Claude

    @Test func claudeSessionsComeFromTheWorkspaceFolderAndItsResolvedPath() throws {
        let f = try fixture()
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "링크 경로에서 시작"), modified: ago(10))
        try writeClaude(f, folderPath: f.real, id: id(2), lines: claudeLines(cwd: f.real, prompt: "실제 경로에서 시작", turns: 3), modified: ago(5))
        try writeClaude(f, folderPath: "/somewhere/else", id: id(3), lines: claudeLines(cwd: "/somewhere/else", prompt: "다른 폴더"), modified: ago(1))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.sessionID) == [id(2), id(1)])
        #expect(items.map(\.title) == ["실제 경로에서 시작", "링크 경로에서 시작"])
        #expect(items[0].requests == 3)
        #expect(items[0].model == "claude-sonnet-4-5")
        #expect(items.allSatisfy { $0.provider == "claude" })
    }

    @Test func claudeConfigFolderFollowsTheEnvironment() throws {
        let f = try fixture()
        let config = f.home.appendingPathComponent("custom-claude", isDirectory: true)
        let folder = config.appendingPathComponent("projects/" + SessionHistory.claudeProjectFolder(f.workspace), isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try claudeLines(cwd: f.workspace, prompt: "설정 폴더").joined(separator: "\n").appending("\n")
            .write(to: folder.appendingPathComponent(id(7) + ".jsonl"), atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: ago(3)], ofItemAtPath: folder.appendingPathComponent(id(7) + ".jsonl").path)
        let items = ResumableSessions.list(query(f, environment: ["CLAUDE_CONFIG_DIR": config.path]))
        #expect(items.map(\.sessionID) == [id(7)])
    }

    @Test func claudeLeavesOutSubagentsOtherNamesOtherFoldersAndEmptyRecords() throws {
        let f = try fixture()
        // Sub-agent records live in a folder named after the session.
        let parent = try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "부모"), modified: ago(9))
        let sub = parent.deletingPathExtension().appendingPathComponent("subagents", isDirectory: true)
        try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        try claudeLines(cwd: f.workspace, prompt: "하위").joined(separator: "\n").write(to: sub.appendingPathComponent(id(9) + ".jsonl"), atomically: true, encoding: .utf8)
        try writeClaude(f, folderPath: f.workspace, id: "agent-a959d195842a46e11", lines: claudeLines(cwd: f.workspace, prompt: "옛 하위"), modified: ago(8))
        // "/x/real-project" and "/x/real project" escape to the same folder name.
        try writeClaude(f, folderPath: f.workspace, id: id(2), lines: claudeLines(cwd: f.workspace + "-twin", prompt: "같은 이름의 다른 폴더"), modified: ago(7))
        let sidechain = [line(["type": "user", "cwd": f.workspace, "isSidechain": true, "message": ["role": "user", "content": "하위 요청"]])]
        try writeClaude(f, folderPath: f.workspace, id: id(3), lines: sidechain, modified: ago(6))
        let noPrompt = [line(["type": "attachment", "cwd": f.workspace, "attachment": ["type": "hook_success"]]),
                        line(["type": "user", "cwd": f.workspace, "isMeta": true, "message": ["role": "user", "content": "<local-command-caveat>"]])]
        try writeClaude(f, folderPath: f.workspace, id: id(4), lines: noPrompt, modified: ago(5))
        try writeClaude(f, folderPath: f.workspace, id: id(5), lines: [], modified: ago(4), trailing: "")
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.sessionID) == [id(1)])
    }

    @Test func titleIsTheFirstRequestTheUserTyped() throws {
        let f = try fixture()
        let cwd = f.workspace
        let lines = [
            line(["type": "attachment", "cwd": cwd, "attachment": ["type": "hook_success"]]),
            line(["type": "user", "cwd": cwd, "isMeta": true, "message": ["role": "user", "content": "Caveat: local commands"]]),
            line(["type": "user", "cwd": cwd, "origin": ["kind": "task-notification"], "message": ["role": "user", "content": "<task-notification>"]]),
            line(["type": "user", "cwd": cwd, "isCompactSummary": true, "message": ["role": "user", "content": "This session is being continued"]]),
            line(["type": "user", "cwd": cwd, "toolUseResult": ["stdout": ""], "message": ["role": "user", "content": [["type": "tool_result", "tool_use_id": "x", "content": "ok"]]]]),
            line(["type": "user", "cwd": cwd, "message": ["role": "user", "content": "[Request interrupted by user]"]]),
            line(["type": "user", "cwd": cwd, "message": ["role": "user", "content": [["type": "text", "text": "  첫 줄\n\n  둘째   줄  "]]]]),
            line(["type": "user", "cwd": cwd, "message": ["role": "user", "content": "나중 요청"]]),
        ]
        try writeClaude(f, folderPath: cwd, id: id(1), lines: lines, modified: ago(2))
        let slash = [line(["type": "user", "cwd": cwd, "message": ["role": "user", "content": "<command-name>/review</command-name>\n<command-args>main</command-args>"]])]
        try writeClaude(f, folderPath: cwd, id: id(2), lines: slash, modified: ago(1))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.title) == ["/review main", "첫 줄 둘째 줄"])
        #expect(items[1].requests == 2)
        #expect(ResumableSessions.oneLine(String(repeating: "가", count: 250)).count == 201)
        let pasted = ResumableSessions.oneLine(String(repeating: "word\n", count: 50_000))
        #expect(pasted.count == 201 && pasted.hasSuffix("…") && !pasted.contains("\n"))
        #expect(ResumableSessions.oneLine("짧은 요청") == "짧은 요청")
    }

    // MARK: Codex

    @Test func codexRolloutsAreFilteredByTheirRecordedFolder() throws {
        let f = try fixture()
        try writeCodex(f, thread: thread(1), lines: codexLines(cwd: f.real, thread: thread(1), prompt: "코덱스 요청", model: "gpt-5.5-codex", turns: 2), modified: ago(3))
        try writeCodex(f, day: "2026/08/15", thread: thread(2), lines: codexLines(cwd: f.workspace + "/", thread: thread(2), prompt: "오래 만든 세션"), modified: ago(1))
        try writeCodex(f, thread: thread(3), lines: codexLines(cwd: "/other/project", thread: thread(3), prompt: "다른 프로젝트"), modified: ago(2))
        try writeCodex(f, thread: thread(4), lines: codexLines(cwd: f.workspace, thread: thread(4), prompt: "하위 스레드",
                                                               source: ["subagent": ["thread_spawn": ["parent_thread_id": thread(1)]]]), modified: ago(2))
        try writeCodex(f, thread: thread(5), lines: codexLines(cwd: f.workspace, thread: thread(5), prompt: "가디언", source: ["subagent": ["other": "guardian"]]), modified: ago(2))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.sessionID) == [thread(2), thread(1)])
        #expect(items.map(\.title) == ["오래 만든 세션", "코덱스 요청"])
        #expect(items[1].requests == 2)
        #expect(items[1].model == "gpt-5.5-codex")
        #expect(items.allSatisfy { $0.provider == "codex" })
    }

    @Test func codexOlderUserMessageEventStillTitlesTheRow() throws {
        let f = try fixture()
        let lines = [line(["type": "session_meta", "payload": ["id": thread(1), "cwd": f.workspace, "source": "cli"]]),
                     line(["type": "event_msg", "payload": ["type": "user_message", "message": " 예전 형식 \n"]])]
        try writeCodex(f, thread: thread(1), lines: lines, modified: ago(1))
        #expect(ResumableSessions.list(query(f)).map(\.title) == ["예전 형식"])
    }

    // MARK: Exclusion, order and caps

    @Test func sessionsOpenPanesUseAreLeftOut() throws {
        let f = try fixture()
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "a"), modified: ago(3))
        try writeClaude(f, folderPath: f.workspace, id: id(2), lines: claudeLines(cwd: f.workspace, prompt: "b"), modified: ago(2))
        try writeCodex(f, thread: thread(1), lines: codexLines(cwd: f.workspace, thread: thread(1), prompt: "c"), modified: ago(1))
        var claude = RunSession(workspaceId: "other-workspace", title: "", kind: "claude", provider: "claude")
        claude.resumeId = id(2).uppercased()
        var codex = RunSession(workspaceId: "w", title: "", kind: "claude", provider: "codex")
        codex.resumeId = thread(1)
        var shell = RunSession(workspaceId: "w", title: "", kind: "shell")
        shell.resumeId = id(1)
        let used = ResumableSessions.inUse([claude, codex, shell, RunSession(workspaceId: "w", title: "")])
        #expect(used == [id(2), thread(1)])
        #expect(ResumableSessions.list(query(f, excluding: used)).map(\.sessionID) == [id(1)])
    }

    @Test func listIsNewestFirstAcrossProviders() throws {
        let f = try fixture()
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "c1"), modified: ago(30))
        try writeCodex(f, thread: thread(1), lines: codexLines(cwd: f.workspace, thread: thread(1), prompt: "x1"), modified: ago(20))
        try writeClaude(f, folderPath: f.real, id: id(2), lines: claudeLines(cwd: f.real, prompt: "c2"), modified: ago(10))
        try writeCodex(f, thread: thread(2), lines: codexLines(cwd: f.workspace, thread: thread(2), prompt: "x2"), modified: ago(40))
        #expect(ResumableSessions.list(query(f)).map(\.title) == ["c2", "x1", "c1", "x2"])
    }

    @Test func oneAgentsListingReadsOnlyThatAgentAndTheProbeStopsAtOne() throws {
        let f = try fixture()
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "c1"), modified: ago(30))
        try writeClaude(f, folderPath: f.workspace, id: id(2), lines: claudeLines(cwd: f.workspace, prompt: "c2"), modified: ago(10))
        try writeCodex(f, thread: thread(1), lines: codexLines(cwd: f.workspace, thread: thread(1), prompt: "x1"), modified: ago(20))
        try writeCodex(f, thread: thread(2), lines: codexLines(cwd: f.workspace, thread: thread(2), prompt: "User: 중첩"), modified: ago(5))
        #expect(ResumableSessions.listing(query(f), provider: "claude").items.map(\.title) == ["c2", "c1"])
        let codex = ResumableSessions.listing(query(f), provider: "codex")
        #expect(codex.items.map(\.title) == ["x1"] && codex.hidden == 1)
        #expect(ResumableSessions.listing(query(f), provider: "gemini") == ResumableSessionListing())
        #expect(ResumableSessions.listing(query(f), provider: "claude").items.first?.requests == 1)
        #expect(ResumableSessions.listing(query(f), provider: "claude").items.first?.model == "claude-sonnet-4-5")
        // The "창 추가" look-up: one session, automated runs hidden even when the picker shows all,
        // and only the head read: no request count, no model from the end.
        let probe = ResumableSessions.listing(AddAgentPane.probe(query(f, all: true)), provider: "claude")
        #expect(probe.items.map(\.title) == ["c2"])
        #expect(probe.items.first?.requests == nil && probe.items.first?.model == nil)
        #expect(ResumableSessions.listing(AddAgentPane.probe(query(f, all: true)), provider: "codex").items.map(\.title) == ["x1"])
        // A Codex folder holding only an automated run and a session a pane uses has nothing to ask about.
        let used = ResumableSessions.listing(AddAgentPane.probe(query(f, excluding: [thread(1)])), provider: "codex")
        #expect(used.items.isEmpty)
        #expect(AddAgentPane.step(provider: "codex", sessions: used.items, inUse: [thread(1)]) == .startNew)
    }

    @Test func aCancelledScanStopsBeforeReadingARecord() async throws {
        let f = try fixture()
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "c1"), modified: ago(3))
        try writeCodex(f, thread: thread(1), lines: codexLines(cwd: f.workspace, thread: thread(1), prompt: "x1"), modified: ago(2))
        let scan = query(f)
        #expect(ResumableSessions.listing(scan).items.count == 2)
        let counts = await Task.detached { () -> [Int] in
            withUnsafeCurrentTask { $0?.cancel() }
            return [ResumableSessions.listing(scan, provider: "claude").items.count, ResumableSessions.listing(scan, provider: "codex").items.count]
        }.value
        #expect(counts == [0, 0])
    }

    @Test func ageCandidateAndSessionLimitsBoundTheScan() throws {
        let f = try fixture()
        for n in 1...5 {
            try writeClaude(f, folderPath: f.workspace, id: id(n), lines: claudeLines(cwd: f.workspace, prompt: "c\(n)"), modified: ago(Double(n)))
        }
        try writeClaude(f, folderPath: f.workspace, id: id(9), lines: claudeLines(cwd: f.workspace, prompt: "old"), modified: Self.now.addingTimeInterval(-61 * 86_400))
        #expect(ResumableSessions.list(query(f)).map(\.title) == ["c1", "c2", "c3", "c4", "c5"])
        #expect(ResumableSessions.list(query(f, sessions: 2)).map(\.title) == ["c1", "c2"])
        // Only records read for a row use a candidate slot: excluded ones do not.
        #expect(ResumableSessions.list(query(f, excluding: [id(1)], candidates: 3)).map(\.title) == ["c2", "c3", "c4"])
    }

    // MARK: Damaged and large records

    @Test func partialUnreadableAndLinkedRecordsAreHandled() throws {
        let f = try fixture()
        // A line still being written: listed, but its requests are not counted.
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claudeLines(cwd: f.workspace, prompt: "쓰는 중"), modified: ago(1),
                        trailing: "\n{\"type\":\"user\",\"message\":{\"content\":\"미완")
        let locked = try writeClaude(f, folderPath: f.workspace, id: id(2), lines: claudeLines(cwd: f.workspace, prompt: "잠김"), modified: ago(2))
        chmod(locked.path, 0)
        defer { chmod(locked.path, 0o644) }
        let target = try writeClaude(f, folderPath: "/elsewhere", id: id(3), lines: claudeLines(cwd: f.workspace, prompt: "링크"), modified: ago(3))
        let folder = f.projects.appendingPathComponent(SessionHistory.claudeProjectFolder(f.workspace), isDirectory: true)
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent(id(4) + ".jsonl"), withDestinationURL: target)
        try writeClaude(f, folderPath: f.workspace, id: id(5), lines: ["not json", "{"], modified: ago(4))
        try writeCodex(f, thread: thread(1), lines: ["{\"type\":\"session_meta\"", "garbage"], modified: ago(1))
        try writeCodex(f, thread: thread(2), lines: [line(["type": "event_msg", "payload": ["type": "task_started"]])], modified: ago(1))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.sessionID) == [id(1)])
        #expect(items.first?.title == "쓰는 중")
        #expect(items.first?.requests == nil)
    }

    @Test func largeRecordsReadOnlyTheirHeadAndTail() throws {
        let f = try fixture()
        let filler = (0..<1_200).map { line(["type": "progress", "cwd": f.workspace, "data": String(repeating: "z", count: 1_000) + "\($0)"]) }
        let claude = claudeLines(cwd: f.workspace, prompt: "큰 기록", model: "claude-opus-4-1") + filler
            + [line(["type": "assistant", "cwd": f.workspace, "message": ["model": "claude-opus-4-6", "role": "assistant", "content": []]])]
        try writeClaude(f, folderPath: f.workspace, id: id(1), lines: claude, modified: ago(1))
        let codex = codexLines(cwd: f.workspace, thread: thread(1), prompt: "큰 롤아웃", model: "gpt-5.4") + filler
            + [line(["type": "turn_context", "payload": ["model": "gpt-5.5"]])]
        try writeCodex(f, thread: thread(1), lines: codex, modified: ago(2))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.title) == ["큰 기록", "큰 롤아웃"])
        #expect(items.map(\.requests) == [nil, nil])
        #expect(items.map(\.model) == ["claude-opus-4-6", "gpt-5.5"])
    }

    // MARK: Search, time and the new pane

    @Test func searchMatchesEveryWordOfTheTitle() {
        let url = URL(fileURLWithPath: "/x")
        let items = [ResumableSession(provider: "claude", sessionID: "a", title: "Fix the Login bug", modified: Self.now, requests: nil, model: nil, url: url),
                     ResumableSession(provider: "codex", sessionID: "b", title: "로그인 화면 다듬기", modified: Self.now, requests: nil, model: nil, url: url),
                     ResumableSession(provider: "codex", sessionID: "c", title: nil, modified: Self.now, requests: nil, model: nil, url: url)]
        #expect(ResumableSessions.filter(items, query: "  ").map(\.sessionID) == ["a", "b", "c"])
        #expect(ResumableSessions.filter(items, query: "login FIX").map(\.sessionID) == ["a"])
        #expect(ResumableSessions.filter(items, query: "로그인").map(\.sessionID) == ["b"])
        #expect(ResumableSessions.filter(items, query: "login 화면").isEmpty)
    }

    @Test func relativeTimeReadsInMinutesHoursAndDays() {
        LocaleOverride.$language.withValue(.ko) {
            #expect(ResumableSessions.relativeTime(Self.now.addingTimeInterval(-20), now: Self.now) == "방금")
            #expect(ResumableSessions.relativeTime(ago(5), now: Self.now) == "5분 전")
            #expect(ResumableSessions.relativeTime(ago(185), now: Self.now) == "3시간 전")
            #expect(ResumableSessions.relativeTime(ago(60 * 24 * 12 + 5), now: Self.now) == "12일 전")
        }
    }

    @Test func applyMakesTheNewPaneContinueTheSession() {
        let item = ResumableSession(provider: "codex", sessionID: thread(1), title: "This is a really long request that definitely exceeds forty characters",
                                    modified: Self.now, requests: 3, model: "gpt-5.5", url: URL(fileURLWithPath: "/x"))
        var session = RunSession(workspaceId: "w", title: "Codex", kind: "claude", provider: "codex")
        session.titleMode = nil
        ResumableSessions.apply(item, to: &session)
        #expect(session.provider == "codex")
        #expect(session.resumeId == thread(1))
        #expect(session.title == "This is a really long request that defin…")
        #expect(session.titleMode == "auto")
        #expect(session.titleHelp == "This is a really long request that defin…")

        var untitled = RunSession(workspaceId: "w", title: "Claude", kind: "claude", provider: "claude")
        ResumableSessions.apply(ResumableSession(provider: "claude", sessionID: id(1), title: nil, modified: Self.now, requests: nil, model: nil,
                                                 url: URL(fileURLWithPath: "/x")), to: &untitled)
        #expect(untitled.title == "Claude")
        #expect(untitled.resumeId == id(1))
    }

    @Test func aResumedPaneKeepsItsTitleUntilItsOwnFirstRequest() {
        let workspace = Workspace(id: "ws1", name: "Test", path: "/tmp")
        var resumed = RunSession(workspaceId: "ws1", title: "earlier session title", kind: "claude", provider: "claude")
        resumed.resumeId = id(1); resumed.titleMode = "auto"
        var asked = resumed
        asked.id = "asked"; asked.logs = [LogEntry(kind: "user", text: "a new request")]
        let result = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [resumed, asked]), restoring: true)
        #expect(result.sessions.map(\.title) == ["earlier session title", "a new request"])
    }

    // MARK: Review fixes

    @Test func interactiveClaudeTitlesComeFromWhatTheUserTyped() throws {
        let f = try fixture()
        let cwd = f.workspace
        let human: [String: Any] = ["origin": ["kind": "human"], "promptSource": "typed", "entrypoint": "cli"]
        func user(_ content: String, _ extra: [String: Any] = [:]) -> String {
            var object: [String: Any] = ["type": "user", "cwd": cwd, "entrypoint": "cli", "message": ["role": "user", "content": content]]
            object.merge(extra) { _, new in new }
            return line(object)
        }
        // A typed prompt after a local command's output.
        let typed = [user("<local-command-stdout>Login successful</local-command-stdout>"),
                     user("<task-notification>x</task-notification>", ["origin": ["kind": "task-notification"]]),
                     user("터미널에서 친 요청", human),
                     user("두 번째", ["origin": ["kind": "human"], "promptSource": "queued"])]
        try writeClaude(f, folderPath: cwd, id: id(1), lines: typed, modified: ago(3))
        // The user's own slash command opens the record; its output is not a title.
        let slash = [user("<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args></command-args>"),
                     user("<local-command-stdout>Set model to opus</local-command-stdout>"),
                     user("이어서 한 요청", human)]
        try writeClaude(f, folderPath: cwd, id: id(2), lines: slash, modified: ago(2))
        // Only injected lines: nothing to continue.
        try writeClaude(f, folderPath: cwd, id: id(3), lines: [user("<local-command-stdout>x</local-command-stdout>"),
                                                               user("peer", ["origin": ["kind": "peer"]])], modified: ago(1))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.title) == ["/model", "터미널에서 친 요청"])
        #expect(items.map(\.requests) == [2, 2])
        #expect(items.allSatisfy { !($0.title ?? "").hasPrefix("<") })
    }

    @Test func automatedRunsAreHiddenBeforeTheCapsUnlessShownOrKnown() throws {
        let f = try fixture()
        let cwd = f.workspace
        // Twenty nested `claude --print` records, newer than the real ones.
        for n in 1...20 {
            let prompt = n % 2 == 0 ? "User: 단계 \(n)\n\nAssistant: …" : "Assistant: 이전 답\n\nUser: 다음"
            try writeClaude(f, folderPath: cwd, id: id(n), lines: claudeLines(cwd: cwd, prompt: prompt), modified: ago(Double(n)))
        }
        try writeClaude(f, folderPath: cwd, id: id(30), lines: claudeLines(cwd: cwd, prompt: "진짜 요청 하나"), modified: ago(30))
        try writeClaude(f, folderPath: cwd, id: id(31), lines: claudeLines(cwd: cwd, prompt: "진짜 요청 둘"), modified: ago(31))
        try writeCodex(f, thread: thread(1), lines: codexLines(cwd: cwd, thread: thread(1), prompt: "User: 중첩 코덱스"), modified: ago(5))
        let hidden = ResumableSessions.listing(query(f, candidates: 2))
        #expect(hidden.items.map(\.title) == ["진짜 요청 하나", "진짜 요청 둘"])
        #expect(hidden.hidden == 21)
        // "모든 세션 보기" lists them, marked.
        let all = ResumableSessions.listing(query(f, all: true))
        #expect(all.items.count == 23 && all.hidden == 0)
        #expect(all.items.filter(\.automated).count == 21)
        // A session the app itself started or resumed is never hidden.
        let known = ResumableSessions.listing(query(f, known: [id(2).uppercased()]))
        #expect(known.items.map(\.sessionID) == [id(2), id(30), id(31)])
        #expect(known.items.first?.automated == false)
        #expect(ResumableSessions.automatedPrompt("User: x") && ResumableSessions.automatedPrompt("Assistant: y"))
        #expect(!ResumableSessions.automatedPrompt("User story를 정리해줘") && !ResumableSessions.automatedPrompt(nil))
    }

    @Test func codexTitlesFollowTheHistoryPromptRuleAcrossVersions() throws {
        let f = try fixture()
        let cwd = f.workspace
        func meta(_ thread: String, _ version: String) -> String {
            line(["type": "session_meta", "payload": ["id": thread, "cwd": cwd, "source": "exec", "originator": "codex_exec", "cli_version": version]])
        }
        func response(_ text: String) -> String {
            line(["type": "response_item", "payload": ["type": "message", "role": "user", "content": [["type": "input_text", "text": text]]]])
        }
        let started = line(["type": "event_msg", "payload": ["type": "task_started", "turn_id": "t1"]])
        let context = response("<environment_context>x</environment_context>")
        // 0.147: response items, then the user_message event.
        try writeCodex(f, thread: thread(1), lines: [meta(thread(1), "0.147.0"), started, context, response("응답 147"),
                                                     line(["type": "event_msg", "payload": ["type": "user_message", "message": "이벤트 147"]])], modified: ago(1))
        // 0.153: the UserMessage item.
        try writeCodex(f, thread: thread(2), lines: [meta(thread(2), "0.153.0"), started, context, response("응답 153"),
                                                     line(["type": "event_msg", "payload": ["type": "item_completed", "item": ["type": "UserMessage", "id": "m", "content": [["type": "text", "text": "아이템 153"]]]]])],
                       modified: ago(2))
        // 0.159: AGENTS.md instructions first; only the model-facing message.
        try writeCodex(f, thread: thread(3), lines: [meta(thread(3), "0.159.0"), started, response("# AGENTS.md instructions for /w"), response("응답 159")], modified: ago(3))
        // A first turn with injected context only: the second turn titles it.
        try writeCodex(f, thread: thread(4), lines: [meta(thread(4), "0.153.0"), started, context,
                                                     line(["type": "event_msg", "payload": ["type": "task_started", "turn_id": "t2"]]), response("둘째 턴 요청")], modified: ago(4))
        let items = ResumableSessions.list(query(f))
        #expect(items.map(\.title) == ["이벤트 147", "아이템 153", "응답 159", "둘째 턴 요청"])
    }

    @Test func aRecordWrittenMomentsAgoMayBeRunningElsewhere() {
        let item = ResumableSession(provider: "claude", sessionID: "a", title: nil, modified: ago(1), requests: nil, model: nil, url: URL(fileURLWithPath: "/x"))
        #expect(ResumableSessions.mayBeRunning(item, now: Self.now))
        var older = item; older.modified = ago(3)
        #expect(!ResumableSessions.mayBeRunning(older, now: Self.now))
    }

    @Test func knownSessionIdsArePersistedSmall() throws {
        let f = try fixture()
        let url = f.home.appendingPathComponent("data/known-sessions.json")
        #expect(KnownSessionIDs.load(url).isEmpty)
        let first = try #require(KnownSessionIDs.adding(id(1), to: []))
        #expect(KnownSessionIDs.adding(id(1).uppercased(), to: first) == nil)
        #expect(KnownSessionIDs.adding("../bad", to: first) == nil)
        try KnownSessionIDs.save(first, to: url)
        #expect(KnownSessionIDs.load(url) == [id(1)])
        let full = (0..<KnownSessionIDs.maximum).map { "s\($0)" }
        let next = try #require(KnownSessionIDs.adding("newest", to: full))
        #expect(next.count == KnownSessionIDs.maximum && next.first == "s1" && next.last == "newest")
    }
}
