import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// `native/contracts/fixtures/push.json`, read by the Mac, Windows (`MobilePushVerification`) and the phone (`push-seal.test.ts`).
enum PushFixture {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/push.json")
    }()
    struct Seal: Decodable {
        let deviceToken, clientId, handle, tokenHash, keyB64, nonceB64, aad, plaintext, envelope, otherHandle: String
        let tampered: [String]
    }
    struct Expected: Decodable {
        let id: String
        let kind: MobilePushKind
        let sessionId, workspace, title, summary: String
        let permission: MobilePushPermission?
        /// The summary the pushed bytes carry when it differs (`""` once the message would pass 2048 bytes).
        let pushSummary: String?
    }
    struct Step: Decodable {
        let note: String
        let panes: [MobilePushPane]
        let expect: [Expected]
    }
    struct Planner: Decodable {
        let hostId, hostName, at: String
        let steps: [Step]
    }
    struct File: Decodable {
        let seal: Seal
        let planner: Planner
    }
    static func load() throws -> File { try JSONDecoder().decode(File.self, from: Data(contentsOf: url)) }
}

struct MobilePushSealTests {
    @Test func theKeyComesFromTheTokenHashAndTheClientId() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(key.withUnsafeBytes { Data($0) }.base64EncodedString() == seal.keyB64)
        #expect(MobileDeviceRegistry.hash(seal.deviceToken) == seal.tokenHash)
        #expect(MobilePushSeal.key(tokenHash: "not hex", clientId: seal.clientId) == nil)
        #expect(MobilePushSeal.key(tokenHash: "+f" + seal.tokenHash.dropFirst(2), clientId: seal.clientId) == nil)
    }

    @Test func sealingWithTheFixtureNonceGivesTheFixtureEnvelope() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        let nonce = try ChaChaPoly.Nonce(data: try #require(Data(base64Encoded: seal.nonceB64)))
        #expect(try MobilePushSeal.seal(Data(seal.plaintext.utf8), key: key, handle: seal.handle, nonce: nonce) == seal.envelope)
    }

    @Test func openingChecksTheTagTheVersionAndTheHandle() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(MobilePushSeal.open(seal.envelope, key: key, handle: seal.handle) == Data(seal.plaintext.utf8))
        for tampered in seal.tampered { #expect(MobilePushSeal.open(tampered, key: key, handle: seal.handle) == nil) }
        #expect(MobilePushSeal.open(seal.envelope, key: key, handle: seal.otherHandle) == nil)
        #expect(MobilePushSeal.open(String(repeating: "A", count: 3_073), key: key, handle: seal.handle) == nil)
    }

    @Test func aMessageOverTheLimitIsNotSealed() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(throws: MightyError.self) { try MobilePushSeal.seal(Data(count: 2_049), key: key, handle: seal.handle) }
        let longest = try MobilePushSeal.seal(Data(count: 2_048), key: key, handle: seal.handle)
        #expect(longest.count <= MobilePushSeal.maximumEnvelope)
    }
}

struct MobilePushPlannerTests {
    @Test func theSharedStepsSendWhatTheFixtureExpects() throws {
        let planner = try PushFixture.load().planner
        var subject = MobilePushPlanner()
        for step in planner.steps {
            let sent = subject.observe(step.panes, hostId: planner.hostId, hostName: planner.hostName, at: planner.at)
            #expect(sent.map(\.id) == step.expect.map(\.id), "\(step.note)")
            for (message, expected) in zip(sent, step.expect) {
                #expect(message.v == 1 && message.hostId == planner.hostId && message.hostName == planner.hostName && message.at == planner.at, "\(step.note)")
                #expect(message.kind == expected.kind && message.sessionId == expected.sessionId && message.workspace == expected.workspace, "\(step.note)")
                #expect(message.title == expected.title && message.summary == expected.summary && message.permission == expected.permission, "\(step.note)")
                let encoded = try #require(message.encodedForPush(), "\(step.note)")
                #expect(encoded.count <= MobilePushSeal.maximumPlaintext, "\(step.note)")
                let pushed = try JSONDecoder().decode(MobilePushMessage.self, from: encoded)
                #expect(pushed.summary == (expected.pushSummary ?? expected.summary) && pushed.id == expected.id && pushed.title == expected.title, "\(step.note)")
            }
        }
    }

    @Test func requestsTakeTheirKindAndButtonsFromTheCard() {
        func permission(_ toolName: String, canAllow: Bool = true, plan: String? = nil, antigravity: MobileAntigravityCard? = nil) -> MobilePermission {
            MobilePermission(id: "r", runId: "u", toolName: toolName, title: "t", summary: "s", canAllow: canAllow, plan: plan, antigravity: antigravity)
        }
        #expect(MobilePushRequest(permission("Bash")).buttons == .claude)
        #expect(MobilePushRequest(permission("Bash", canAllow: false)).buttons == MobilePushButtons.none)
        #expect(MobilePushRequest(permission("AskUserQuestion")).kind == .question)
        #expect(MobilePushRequest(permission(ClaudePlanMode.toolName, plan: "p")).kind == .plan)
        let once = MobileAntigravityCard(actions: [MobileAntigravityAction(kind: "command", action: "command", displayName: "RunCommand", target: "rm x", rule: "command(rm x)", onceOnly: true)])
        let always = MobileAntigravityCard(actions: [MobileAntigravityAction(kind: "command", action: "command", displayName: "RunCommand", target: "ls", rule: "command(ls)")])
        let ruleless = MobileAntigravityCard(actions: [MobileAntigravityAction(kind: "other", action: "mcp", displayName: "Mcp")])
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, antigravity: once)).buttons == .antigravityOnce)
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, antigravity: always)).buttons == .antigravity)
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, canAllow: false, antigravity: ruleless)).buttons == MobilePushButtons.none)
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, antigravity: once)).kind == .permission)
    }

    @Test func aWideMessageStaysWithinThePushLimit() throws {
        var subject = MobilePushPlanner()
        _ = subject.observe([], hostId: "h", hostName: "H", at: "t")
        let wide = String(repeating: "가", count: 80)
        let pane = MobilePushPane(sessionId: "p", workspace: wide, title: wide, status: "running", preview: nil,
                                  requests: [MobilePushRequest(id: "r", runId: "u", kind: .permission, summary: String(repeating: "😀", count: 300), buttons: .claude)], signInLost: false)
        let message = try #require(subject.observe([pane], hostId: "h", hostName: wide, at: "t").first)
        #expect(message.summary.unicodeScalars.count == 200)
        let encoded = try #require(message.encodedForPush())
        #expect(encoded.count <= MobilePushSeal.maximumPlaintext)
    }

    @Test func printingADeviceOrAMessageShowsNoHandleTokenHashOrContent() {
        let device = MobilePushDevice(deviceId: "a", handle: "handle-secret", tokenHash: String(repeating: "f", count: 64), kinds: [.permission])
        let message = MobilePushMessage(id: "request:u:r", kind: .permission, hostId: "h", hostName: "host-secret", workspace: "workspace-secret", title: "title-secret",
                                        summary: "summary-secret", sessionId: "pane-1", permission: nil, at: "t")
        for text in ["\(device)", String(reflecting: device), "\([device])"] {
            #expect(!text.contains("handle-secret") && !text.contains(device.tokenHash) && text.contains("a"))
        }
        for text in ["\(message)", String(reflecting: message), "\([message])"] {
            #expect(!["host-secret", "workspace-secret", "title-secret", "summary-secret"].contains { text.contains($0) } && text.contains("request:u:r"))
        }
    }

    @Test func aDeviceGetsOnlyTheKindsItTurnedOnAndNothingForTheSessionItIsViewing() {
        let message = MobilePushMessage(id: "x", kind: .completed, hostId: "h", hostName: "H", workspace: "w", title: "t", summary: "", sessionId: "pane-1", permission: nil, at: "t")
        let all = MobilePushDevice(deviceId: "a", handle: "ha", tokenHash: String(repeating: "0", count: 64), kinds: Set(MobilePushKind.allCases))
        let quiet = MobilePushDevice(deviceId: "b", handle: "hb", tokenHash: String(repeating: "0", count: 64), kinds: [.permission])
        let now = Date(timeIntervalSince1970: 1_000)
        #expect(MobilePushRecipients.targets(for: message, devices: [all, quiet], viewing: [:], now: now) == [all])
        let watching = ["a": MobilePushViewing(sessionId: "pane-1", until: now.addingTimeInterval(10))]
        #expect(MobilePushRecipients.targets(for: message, devices: [all], viewing: watching, now: now).isEmpty)
        let elsewhere = ["a": MobilePushViewing(sessionId: "pane-2", until: now.addingTimeInterval(10))]
        #expect(MobilePushRecipients.targets(for: message, devices: [all], viewing: elsewhere, now: now) == [all])
        let expired = ["a": MobilePushViewing(sessionId: "pane-1", until: now.addingTimeInterval(-1))]
        #expect(MobilePushRecipients.targets(for: message, devices: [all], viewing: expired, now: now) == [all])
    }
}

struct MobilePushRelayTests {
    @Test func theSendAddressFollowsTheRelaysScheme() {
        #expect(MobilePushRelay.sendURL("wss://relay.example.invalid")?.absoluteString == "https://relay.example.invalid/push/v1/send")
        #expect(MobilePushRelay.sendURL("ws://127.0.0.1:8787")?.absoluteString == "http://127.0.0.1:8787/push/v1/send")
        #expect(MobilePushRelay.sendURL("wss://relay.example.invalid/ws?x=1")?.absoluteString == "https://relay.example.invalid/push/v1/send")
        #expect(MobilePushRelay.sendURL("ftp://relay.example.invalid") == nil)
    }

    @Test func theRelaysAnswerSaysWhetherToRetryOrForget() {
        #expect(MobilePushSendOutcome(status: 202, retryAfter: nil) == .sent)
        for gone in [403, 404, 410] { #expect(MobilePushSendOutcome(status: gone, retryAfter: nil) == .gone) }
        #expect(MobilePushSendOutcome(status: 429, retryAfter: 5) == .retry(after: 5))
        #expect(MobilePushSendOutcome(status: 502, retryAfter: nil) == .retry(after: nil))
        #expect(MobilePushSendOutcome(status: 503, retryAfter: nil) == .failed(status: 503))
        #expect(MobilePushSendOutcome(status: 401, retryAfter: nil) == .failed(status: 401))
    }

    /// Only plain seconds count, and a relay cannot ask the Mac to wait past an hour.
    @Test func aRetryAfterIsSecondsWithinAnHour() {
        #expect(MobilePushHTTPTransport.retryAfter("5") == 5)
        #expect(MobilePushHTTPTransport.retryAfter(" 12 ") == 12)
        #expect(MobilePushHTTPTransport.retryAfter("999999") == 3600)
        for bad in [nil, "", "-1", "nan", "inf", "Wed, 21 Oct 2026 07:28:00 GMT"] { #expect(MobilePushHTTPTransport.retryAfter(bad) == nil) }
    }

    /// A relay that answers 3xx is refused, not followed: the bearer token and the envelope go nowhere else.
    @Test func aRedirectIsAFailureAndIsNeverFollowed() async throws {
        let hits = PushHits()
        let elsewhere = HTTPServer(address: "127.0.0.1", port: 0) { _ in hits.hit(); return HTTPResponse(status: 200, body: Data("{}".utf8)) }
        let elsewherePort = try await elsewhere.start()
        let redirecting = HTTPServer(address: "127.0.0.1", port: 0) { _ in
            HTTPResponse(status: 307, body: Data(), headers: ["Location": "http://127.0.0.1:\(elsewherePort)/push/v1/send"])
        }
        let port = try await redirecting.start()
        let url = try #require(URL(string: "http://127.0.0.1:\(port)/push/v1/send"))
        let outcome = await MobilePushHTTPTransport().send(MobilePushSendRequest(serverId: "s", handle: "h", payload: "p"), to: url, hostToken: "t")
        #expect(outcome == .failed(status: 307))
        #expect(hits.count == 0)
        await redirecting.stop(); await elsewhere.stop()
    }

    @Test func theRedirectPolicyAnswersNil() {
        let session = URLSession.shared, task = session.dataTask(with: URL(string: "https://relay.example.invalid/")!)
        let response = HTTPURLResponse(url: URL(string: "https://relay.example.invalid/")!, statusCode: 302, httpVersion: nil, headerFields: nil)!
        var decision: URLRequest?? = nil
        MobilePushNoRedirect.shared.urlSession(session, task: task, willPerformHTTPRedirection: response,
                                               newRequest: URLRequest(url: URL(string: "https://relay.example.invalid/next")!)) { decision = .some($0) }
        #expect(decision == .some(nil))
    }
}

private final class PushHits: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.lock(); defer { lock.unlock() }; return value }
    func hit() { lock.lock(); value += 1; lock.unlock() }
}

/// What the Mac hands the planner (`MobilePushPane.project`), read from what the app holds.
struct MobilePushProjectionTests {
    static let workspaces = [Workspace(id: "w1", name: "demo", path: "/tmp/demo")]

    static func project(_ sessions: [RunSession], permissions: [String: [ToolPermissionRequest]] = [:], autoAllowed: Set<String> = [],
                        antigravity: AntigravityPermissionBoard = AntigravityPermissionBoard(), login: [String: String] = [:]) -> [MobilePushPane] {
        MobilePushPane.project(sessions: sessions, workspaces: workspaces, permissions: permissions,
                               autoAllowed: { sessionId, request in autoAllowed.contains(sessionId + "|" + request.id) },
                               antigravity: antigravity, login: login)
    }

    @Test func aWaitingPaneIsStillRunningAndCarriesItsPendingRequests() {
        let pane = WorkDashboardTests.pane("p1", status: "running", logs: [
            LogEntry(kind: "assistant", text: "the last words"),
            LogEntry(kind: "assistant", text: "Bash", activity: AgentActivity(provider: "claude", kind: "tool", state: "running", summary: "Bash")),
            LogEntry(kind: "assistant", text: ""),
        ])
        let permissions = ["p1": [WorkDashboardTests.request("a"), WorkDashboardTests.request("q", question: true), WorkDashboardTests.request("done", state: "allowed")]]
        let panes = Self.project([pane], permissions: permissions)
        #expect(panes.count == 1)
        let first = panes.first
        #expect(first?.status == "running")
        #expect(first?.workspace == "demo" && first?.title == "pane p1")
        #expect(first?.preview == "the last words")
        #expect(first?.requests.map(\.id) == ["a", "q"])
        #expect(first?.requests.map(\.kind) == [.permission, .question])
        #expect(first?.signInLost == false)
    }

    @Test func aRequestAStyleIsAnsweringOnItsOwnIsLeftOut() {
        let permissions = ["p1": [WorkDashboardTests.request("auto"), WorkDashboardTests.request("ask")]]
        let panes = Self.project([WorkDashboardTests.pane("p1", status: "running")], permissions: permissions, autoAllowed: ["p1|auto"])
        #expect(panes.first?.requests.map(\.id) == ["ask"])
    }

    @Test func anAntigravityCardAndALostSignInAreIncluded() {
        var board = AntigravityPermissionBoard()
        let card = AntigravityPermissionRequest(id: "agy-1", runId: "r", actions: [AntigravityDeniedAction(kind: .command, action: "command", displayName: "RunCommand", target: "ls", rule: "command(ls)")])
        board.received(card, sessionId: "p1")
        let panes = Self.project([WorkDashboardTests.pane("p1", provider: "gemini", status: "completed"), WorkDashboardTests.pane("p2", status: "error")],
                                 antigravity: board, login: ["p2": "claude"])
        #expect(panes.map(\.sessionId) == ["p1", "p2"])
        #expect(panes[0].requests.map(\.id) == ["agy-1"])
        #expect(panes[0].requests.first?.kind == .permission)
        #expect(panes[0].signInLost == false && panes[1].signInLost == true)
    }

    @Test func filesPanesAndOtherComputersPanesAreLeftOut() {
        let remote = Workspace(id: "w2", name: "elsewhere", path: "/tmp/elsewhere", remote: RemoteWorkspaceReference(connectionId: "c", workspaceId: "rw", hostName: "h"))
        let sessions = [WorkDashboardTests.pane("p1"), WorkDashboardTests.pane(FilePaneKind.paneId(workspaceId: "w1"), kind: FilePaneKind.kind), WorkDashboardTests.pane("r1", workspace: "w2")]
        let panes = MobilePushPane.project(sessions: sessions, workspaces: Self.workspaces + [remote], permissions: [:], autoAllowed: { _, _ in false },
                                           antigravity: AntigravityPermissionBoard(), login: [:])
        #expect(panes.map(\.sessionId) == ["p1"])
    }

    @Test func thePreviewIsCut() {
        let pane = WorkDashboardTests.pane("p1", logs: [LogEntry(kind: "assistant", text: String(repeating: "가", count: 500))])
        #expect(Self.project([pane]).first?.preview?.count == MobilePushPane.previewLimit)
    }
}

struct MobilePushPlannerResetTests {
    /// Starting over (phone remote access switched back on) replays nothing that changed meanwhile,
    /// and a pane's run numbers keep counting so an id already sent is not reused.
    @Test func startingOverReplaysNothingAndKeepsCounting() {
        var planner = MobilePushPlanner()
        var pane = MobilePushPane(sessionId: "p", workspace: "w", title: "t", status: "running", preview: nil, requests: [], signInLost: false)
        _ = planner.observe([pane], hostId: "h", hostName: "n", at: "a")
        pane.status = "completed"
        #expect(planner.observe([pane], hostId: "h", hostName: "n", at: "a").map(\.id) == ["run:p:1:completed"])
        planner.reset()
        pane.status = "running"
        pane.requests = [MobilePushRequest(id: "x", runId: "r", kind: .permission, summary: "", buttons: .claude)]
        #expect(planner.observe([pane], hostId: "h", hostName: "n", at: "a").isEmpty)
        pane.requests = []
        pane.status = "completed"
        #expect(planner.observe([pane], hostId: "h", hostName: "n", at: "a").map(\.id) == ["run:p:2:completed"])
    }
}
