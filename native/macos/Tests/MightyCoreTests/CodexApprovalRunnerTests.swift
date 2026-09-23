import Foundation
import Testing
@testable import MightyCore

private final class CodexRunnerEvents: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); stored.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return stored }
}

@Suite(.serialized) struct CodexApprovalRunnerTests {
    private struct Fixture {
        let root: URL
        let providers: ProviderService
        let runner: ProcessRunner
        let events: CodexRunnerEvents
        var workspace: Workspace { Workspace(id: "workspace", name: "Codex fixture", path: root.path) }
        func request(_ session: String = "pane") -> StartRunRequest {
            StartRunRequest(sessionId: session, workspaceId: "workspace", input: "fixture request, never sent to a model", provider: "codex", settings: RunSettings(permissionMode: "onRequest"))
        }
        func close() async {
            await runner.shutdown(); await providers.shutdown()
            try? FileManager.default.removeItem(at: root)
        }
    }

    private func fixture() throws -> Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-codex-runner-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let binary = root.appendingPathComponent("codex")
        // This executable is a local RPC fixture: it never invokes a shell command,
        // accesses credentials, connects to the network, or performs inference.
        let source = #"""
        #!/usr/bin/python3
        import json, os, pathlib, sys
        if "--version" in sys.argv:
            print("codex-cli 0.153.4"); sys.exit(0)
        root = pathlib.Path.cwd()
        thread = "fixture-thread"
        turn = "fixture-turn"
        def send(obj):
            print(json.dumps(obj), flush=True)
        def result(frame, obj):
            send({"id": frame["id"], "result": obj})
        def notify(method, params):
            send({"method": method, "params": dict({"threadId": thread, "turnId": turn}, **params)})
        for line in sys.stdin:
            frame = json.loads(line)
            method = frame.get("method")
            if method == "initialize":
                result(frame, {"userAgent": "fixture"})
            elif method == "initialized":
                pass
            elif method == "model/list":
                result(frame, {"data": [], "nextCursor": None})
            elif method in ("thread/start", "thread/resume"):
                (root / "run-started.json").write_text(json.dumps(frame))
                result(frame, {"thread": {"id": thread}})
                if (root / "eof-before-turn").exists():
                    sys.exit(0)
            elif method == "turn/start":
                (root / "turn-start.json").write_text(json.dumps(frame))
                result(frame, {"turn": {"id": turn, "status": "inProgress"}})
                if (root / "eof-before-result").exists():
                    sys.exit(0)
                notify("item/started", {"item": {"id": "command-1", "type": "commandExecution", "command": "fixture-read-only", "status": "inProgress"}})
                send({"id": 77, "method": "item/commandExecution/requestApproval", "params": {
                    "threadId": thread, "turnId": turn, "itemId": "command-1", "command": "fixture-read-only",
                    "cwd": str(root), "reason": "fixture permission only", "availableDecisions": ["accept", "decline"]}})
            elif frame.get("id") == 77 and "result" in frame:
                (root / "response.json").write_text(json.dumps(frame))
                decision = frame["result"]["decision"]
                notify("item/completed", {"item": {"id": "command-1", "type": "commandExecution", "command": "fixture-read-only", "status": "completed" if decision == "accept" else "declined", "aggregatedOutput": "fixture command output", "exitCode": 0}})
                notify("item/completed", {"item": {"id": "message-1", "type": "agentMessage", "text": "fixture answer " + decision}})
                notify("thread/tokenUsage/updated", {"tokenUsage": {"last": {"inputTokens": 5, "cachedInputTokens": 2, "outputTokens": 3}}})
                notify("turn/completed", {"turn": {"id": turn, "status": "completed"}})
                # The host must close stdin after completion. An early close would
                # prevent approval and an extra reply is recorded as a failure.
                for extra in sys.stdin:
                    with (root / "extra-responses.jsonl").open("a") as out:
                        out.write(extra)
                sys.exit(0)
            else:
                sys.exit(31)
        """#
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)
        let providers = ProviderService(binaryOverrides: ["codex": binary], environment: ["PATH": "/usr/bin:/bin"])
        let events = CodexRunnerEvents()
        let runner = ProcessRunner(providerService: providers, pluginDirectory: root) { events.append($0) }
        return Fixture(root: root, providers: providers, runner: runner, events: events)
    }

    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(30)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Codex runner fixture timed out") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
    private func object(_ url: URL) throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test func processApprovalRoundTripsKeepStaleRepliesOutAndDeliverLegacyOutput() async throws {
        let fixture = try fixture()
        do {
            var previous: ToolPermissionRequest?
            for (index, allow) in [true, false].enumerated() {
                let responseURL = fixture.root.appendingPathComponent("response.json")
                if FileManager.default.fileExists(atPath: responseURL.path) { try FileManager.default.removeItem(at: responseURL) }
                try await fixture.runner.start(request: fixture.request(), workspace: fixture.workspace, allowPermissionPrompts: true)
                try await wait { fixture.events.values().filter { $0.permission?.state == "pending" }.count == index + 1 }
                let ask = try #require(fixture.events.values().compactMap(\.permission).last { $0.state == "pending" })
                #expect(ask.canAllow)
                #expect(!FileManager.default.fileExists(atPath: responseURL.path))
                await #expect(throws: MightyError.self) { try await fixture.runner.respondToPermission(sessionId: "wrong-pane", runId: ask.runId, requestId: ask.id, allow: true) }
                await #expect(throws: MightyError.self) { try await fixture.runner.respondToPermission(sessionId: "pane", runId: "stale-run", requestId: ask.id, allow: true) }
                if let previous {
                    #expect(previous.runId != ask.runId)
                    await #expect(throws: MightyError.self) { try await fixture.runner.respondToPermission(sessionId: "pane", runId: previous.runId, requestId: previous.id, allow: true) }
                }
                #expect(!FileManager.default.fileExists(atPath: responseURL.path))
                try await fixture.runner.respondToPermission(sessionId: "pane", runId: ask.runId, requestId: ask.id, allow: allow)
                await #expect(throws: MightyError.self) { try await fixture.runner.respondToPermission(sessionId: "pane", runId: ask.runId, requestId: ask.id, allow: allow) }
                try await wait { fixture.events.values().filter { $0.status == "completed" }.count == index + 1 }
                let response = try object(responseURL)
                #expect(response["id"] as? Int == 77)
                #expect(response["result"] as? [String: String] == ["decision": allow ? "accept" : "decline"])
                #expect(fixture.events.values().contains { $0.entry?.kind == "assistant" && $0.entry?.text == "fixture answer " + (allow ? "accept" : "decline") })
                #expect(fixture.events.values().contains { $0.resumeId == "fixture-thread" })
                #expect(!fixture.events.values().contains { $0.status == "error" })
                #expect(!FileManager.default.fileExists(atPath: fixture.root.appendingPathComponent("extra-responses.jsonl").path))
                let params = try #require(try object(fixture.root.appendingPathComponent("turn-start.json"))["params"] as? [String: Any])
                #expect(params["approvalPolicy"] as? String == "on-request")
                #expect(params["approvalsReviewer"] as? String == "user")
                #expect((params["sandboxPolicy"] as? [String: Any])?["networkAccess"] as? Bool == false)
                previous = ask
            }
            try await fixture.runner.start(request: fixture.request(), workspace: fixture.workspace, allowPermissionPrompts: true)
            try await wait { fixture.events.values().filter { $0.permission?.state == "pending" }.count == 3 }
            let stopped = try #require(fixture.events.values().compactMap(\.permission).last { $0.state == "pending" })
            await fixture.runner.stop(id: "pane")
            #expect(fixture.events.values().contains { $0.permission?.id == stopped.id && $0.permission?.state == "cancelled" })
            await #expect(throws: MightyError.self) { try await fixture.runner.respondToPermission(sessionId: "pane", runId: stopped.runId, requestId: stopped.id, allow: true) }
        } catch { await fixture.close(); throw error }
        await fixture.close()
    }

    @Test func cleanEOFWithoutTurnCompletionIsAnError() async throws {
        let fixture = try fixture()
        do {
            for marker in ["eof-before-turn", "eof-before-result"] {
                let markerURL = fixture.root.appendingPathComponent(marker)
                try Data().write(to: markerURL)
                try await fixture.runner.start(request: fixture.request(marker), workspace: fixture.workspace, allowPermissionPrompts: true)
                try await wait { fixture.events.values().contains { $0.sessionId == marker && $0.status == "error" } }
                #expect(!fixture.events.values().contains { $0.sessionId == marker && $0.status == "completed" })
                try FileManager.default.removeItem(at: markerURL)
            }
        } catch { await fixture.close(); throw error }
        await fixture.close()
    }

    @Test func noninteractiveStartRejectsApprovalModeBeforeLaunchingATurn() async throws {
        let fixture = try fixture()
        do {
            await #expect(throws: MightyError.self) { try await fixture.runner.start(request: fixture.request(), workspace: fixture.workspace) }
            #expect(!FileManager.default.fileExists(atPath: fixture.root.appendingPathComponent("run-started.json").path))
            #expect(!fixture.events.values().contains { $0.status == "running" || $0.status == "completed" })
        }
        await fixture.close()
    }
}
