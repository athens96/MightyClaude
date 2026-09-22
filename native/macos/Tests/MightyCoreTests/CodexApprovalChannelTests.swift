import Testing
import Foundation
@testable import MightyCore

@Suite struct CodexApprovalChannelTests {
    private final class Harness {
        var writes: [[String: Any]] = []
        var events: [[String: Any]] = []
        var cards: [ToolPermissionRequest] = []
        var errors: [String] = []
        var warnings: [String] = []
        var completions = 0
        var channel: CodexApprovalChannel!
        init(resume: String? = nil, network: Bool = false) throws {
            let request = StartRunRequest(sessionId: "session", workspaceId: "workspace", input: "private prompt", provider: "codex", settings: .init(permissionMode: "onRequest", networkAccess: network), resumeId: resume)
            channel = CodexApprovalChannel(runId: "run", request: request, workspacePath: "/work", attachments: try AttachmentPreparation([]),
                write: { [weak self] in self?.writes.append(Self.object($0)) }, event: { [weak self] in self?.events.append(Self.object($0)) },
                emit: { [weak self] in self?.cards.append($0) }, activity: { _, _ in }, warning: { [weak self] in self?.warnings.append($0) },
                fail: { [weak self] in self?.errors.append($0) }, completed: { [weak self] in self?.completions += 1 })
        }
        static func object(_ data: Data) -> [String: Any] { (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:] }
        func frame(_ obj: [String: Any]) { channel.receive(try! JSONSerialization.data(withJSONObject: obj) + Data([10])) }
        func response(_ obj: [String: Any]) { frame(["id": writes.last!["id"]!, "result": obj]) }
        func ready() {
            channel.start(); response([:]); response(["thread": ["id": "thread"]]); response(["turn": ["id": "turn"]])
        }
        func ask(id: Any = 7, params extra: [String: Any] = [:], method: String = "item/commandExecution/requestApproval") {
            var params: [String: Any] = ["threadId": "thread", "turnId": "turn", "itemId": "item", "command": "glab auth status", "cwd": "/work", "startedAtMs": 1]
            params.merge(extra) { _, rhs in rhs }; frame(["id": id, "method": method, "params": params])
        }
        func notify(_ method: String, _ extra: [String: Any]) {
            var params: [String: Any] = ["threadId": "thread", "turnId": "turn"]
            params.merge(extra) { _, rhs in rhs }; frame(["method": method, "params": params])
        }
    }
    @Test func testHandshakeAndPromptOnlySentAfterThreadWithExplicitPolicy() throws {
        let h = try Harness(network: true); h.channel.start()
        #expect(!(String(describing: h.writes).contains("private prompt")))
        h.response([:]); #expect(h.channel.initialized)
        #expect((h.writes[1]["method"] as? String) == ("initialized"))
        let thread = h.writes.last!["params"] as! [String: Any]
        #expect((thread["approvalPolicy"] as? String) == ("on-request"))
        #expect((thread["approvalsReviewer"] as? String) == ("user"))
        #expect((thread["sandbox"] as? String) == ("workspace-write"))
        h.response(["thread": ["id": "thread"]])
        let turn = h.writes.last!["params"] as! [String: Any]
        let sandbox = turn["sandboxPolicy"] as! [String: Any]
        #expect((sandbox["writableRoots"] as? [String]) == (["/work"]))
        #expect((sandbox["networkAccess"] as? Bool) == (true))
        #expect(((turn["input"] as? [[String: Any]])?.first?["text"] as? String) == ("private prompt"))
        #expect((turn["approvalsReviewer"] as? String) == ("user"))
    }
    @Test func testResumeOverridesOldPolicyAndRejectsWrongThread() throws {
        let h = try Harness(resume: "thread"); h.channel.start(); h.response([:])
        #expect((h.writes.last?["method"] as? String) == ("thread/resume"))
        #expect(((h.writes.last?["params"] as? [String: Any])?["approvalPolicy"] as? String) == ("on-request"))
        h.response(["thread": ["id": "other"]]); #expect(h.channel.failed)
        #expect(!(h.writes.contains { $0["method"] as? String == "turn/start" }))
    }
    @Test func testOneShotApprovalAndDuplicateClick() throws {
        let h = try Harness(); h.ready(); h.ask()
        let card = try #require(h.cards.last)
        #expect((card.runId) == ("run")); #expect(card.canAllow)
        #expect(card.inputJSON.contains("glab auth status")); #expect(card.inputJSON.contains("/work"))
        try h.channel.respond(requestId: card.id, allow: true)
        #expect(((h.writes.last?["result"] as? [String: Any])?["decision"] as? String) == ("accept"))
        let count = h.writes.count
        #expect(throws: (any Error).self) { try h.channel.respond(requestId: card.id, allow: true) }; #expect((count) == (h.writes.count))
        #expect(!(String(describing: h.writes).contains("acceptForSession")))
    }
    @Test func testDuplicateServerIDFailsClosedAndCancelsCard() throws {
        let h = try Harness(); h.ready(); h.ask(); h.ask()
        #expect(h.channel.failed); #expect((h.cards.last?.state) == ("cancelled"))
        #expect(!(h.writes.contains { ($0["result"] as? [String: Any])?["decision"] as? String == "accept" }))
    }
    @Test func testStaleScopeCannotCreateApproval() throws {
        let h = try Harness(); h.ready(); h.ask(params: ["turnId": "old"])
        #expect(h.cards.isEmpty); #expect((h.writes.last?["error"]) != nil)
    }
    @Test func testMissingCommandAndSessionOnlyDecisionCannotAllow() throws {
        let h = try Harness(); h.ready(); h.ask(params: ["command": NSNull()])
        let first = try #require(h.cards.last); #expect(!(first.canAllow))
        #expect(throws: (any Error).self) { try h.channel.respond(requestId: first.id, allow: true) }
        try h.channel.respond(requestId: first.id, allow: false)
        h.ask(id: 8, params: ["availableDecisions": ["acceptForSession", "decline"]])
        #expect(!(try #require(h.cards.last).canAllow))
    }
    @Test func testFileApprovalRequiresExactCachedDiffAndNoGrantRoot() throws {
        let h = try Harness(); h.ready()
        h.ask(method: "item/fileChange/requestApproval"); #expect(!(try #require(h.cards.last).canAllow))
        h.notify("item/started", ["item": ["type": "fileChange", "id": "item", "changes": [["path": "/work/a", "kind": ["type": "update"], "diff": "+new"]]]])
        h.ask(id: 8, method: "item/fileChange/requestApproval")
        let card = try #require(h.cards.last); #expect(card.canAllow); #expect(card.inputJSON.contains("+new"))
        try h.channel.respond(requestId: card.id, allow: true)
        h.ask(id: 9, params: ["grantRoot": "/"], method: "item/fileChange/requestApproval")
        #expect(!(try #require(h.cards.last).canAllow))
    }
    @Test func testUnsupportedPermissionAndInputRequestsSettleWithoutGrant() throws {
        let h = try Harness(); h.ready()
        h.ask(method: "item/permissions/requestApproval")
        let result = try #require(h.writes.last?["result"] as? [String: Any])
        #expect((result["scope"] as? String) == ("turn")); #expect(((result["permissions"] as? [String: Any])?.count) == (0))
        h.ask(id: 8, method: "item/tool/requestUserInput"); #expect((h.writes.last?["error"]) != nil)
        #expect(h.cards.isEmpty)
    }
    @Test func testPayloadAndPendingLimitsNeverApprove() throws {
        let h = try Harness(); h.ready()
        h.ask(params: ["command": String(repeating: "x", count: CodexApprovalChannel.maximumInputBytes + 1)])
        #expect(h.cards.isEmpty)
        #expect(((h.writes.last?["result"] as? [String: Any])?["decision"] as? String) == ("decline"))
        for id in 10..<27 { h.ask(id: id) }
        #expect((h.cards.count) == (16))
        #expect(((h.writes.last?["result"] as? [String: Any])?["decision"] as? String) == ("decline"))
    }
    @Test func testStreamChunksAndMalformedEOF() throws {
        let h = try Harness(); h.channel.start()
        let data = try JSONSerialization.data(withJSONObject: ["id": h.writes.last!["id"]!, "result": [:]]) + Data([10])
        for byte in data { h.channel.receive(Data([byte])) }
        #expect(h.channel.initialized)
        h.channel.flush(); #expect(h.channel.failed)
        let broken = try Harness(); broken.channel.receive(Data("not-json\n".utf8)); #expect(broken.channel.failed)
        let oversized = try Harness(); oversized.channel.receive(Data(repeating: 65, count: CodexApprovalChannel.maximumFrameBytes + 1)); #expect(oversized.channel.failed)
    }
    @Test func testCompletedTurnMapsOutputAndUsageAndNeverLeaksReasoning() throws {
        let h = try Harness(); h.ready()
        h.notify("item/completed", ["item": ["id": "thought", "type": "reasoning", "content": ["private reasoning"]]])
        h.notify("item/completed", ["item": ["id": "message", "type": "agentMessage", "text": "Done"]])
        h.notify("thread/tokenUsage/updated", ["tokenUsage": ["last": ["inputTokens": 10, "cachedInputTokens": 3, "outputTokens": 2]]])
        h.notify("turn/completed", ["turn": ["id": "turn", "status": "completed", "items": []]])
        #expect(h.channel.turnCompleted); #expect(!(h.channel.failed)); #expect((h.completions) == (1))
        #expect(((h.events.last?["usage"] as? [String: Any])?["input_tokens"] as? Int) == (10))
        #expect(!(String(describing: h.events).contains("private reasoning")))
        #expect(h.events.contains { ($0["item"] as? [String: Any])?["type"] as? String == "agent_message" })
        h.channel.flush(); #expect(h.errors.isEmpty)
    }
    @Test func testFailedTurnAndInitializationTimeout() throws {
        let h = try Harness(); h.ready(); h.notify("turn/completed", ["turn": ["id": "turn", "status": "failed", "error": ["message": "denied"]]])
        #expect(h.channel.failed); #expect(!(h.channel.turnCompleted))
        let timeout = try Harness(); timeout.channel.start(); timeout.channel.initializationTimedOut(); #expect(timeout.channel.failed)
    }
    @Test func testTurnStartedNotificationBeforeResponseScopesApproval() throws {
        let h = try Harness(); h.channel.start(); h.response([:]); h.response(["thread": ["id": "thread"]])
        h.notify("turn/started", ["turn": ["id": "turn"]]); h.ask(); #expect((h.cards.count) == (1))
    }
    @Test func testResolvedOrCompletedItemCannotReceiveLateApproval() throws {
        let h = try Harness(); h.ready(); h.ask()
        let id = try #require(h.cards.last?.id)
        h.notify("serverRequest/resolved", ["requestId": 7])
        #expect((h.cards.last?.state) == ("cancelled"))
        #expect(throws: (any Error).self) { try h.channel.respond(requestId: id, allow: true) }
        h.ask(id: 8)
        let next = try #require(h.cards.last?.id)
        h.notify("item/completed", ["item": ["id": "item", "type": "commandExecution", "command": "glab auth status", "status": "completed"]])
        #expect(throws: (any Error).self) { try h.channel.respond(requestId: next, allow: true) }
    }

    @Test func testHandshakeTimeoutCoversThreadAndTurnButNotRunningTurn() throws {
        let thread = try Harness(); thread.channel.start(); thread.response([:])
        #expect(thread.channel.initialized)
        thread.channel.initializationTimedOut(); #expect(thread.channel.failed)
        let turn = try Harness(); turn.channel.start(); turn.response([:]); turn.response(["thread": ["id": "thread"]])
        turn.channel.initializationTimedOut(); #expect(turn.channel.failed)
        let running = try Harness(); running.ready(); running.channel.initializationTimedOut()
        #expect(!(running.channel.failed))
    }

    @Test func testMixedAvailableDecisionsAcceptsOnlyExplicitOneShotAndNullUsesDefault() throws {
        let h = try Harness(); h.ready()
        h.ask(params: ["availableDecisions": ["accept", ["acceptWithExecpolicyAmendment": ["execpolicy_amendment": ["git"]]], "decline"] as [Any]])
        #expect(try #require(h.cards.last).canAllow)
        h.ask(id: 8, params: ["availableDecisions": NSNull()])
        #expect(try #require(h.cards.last).canAllow)
        h.ask(id: 9, params: ["availableDecisions": [["acceptWithExecpolicyAmendment": ["execpolicy_amendment": ["git"]]], "decline"] as [Any]])
        #expect(!(try #require(h.cards.last).canAllow))
    }

}
