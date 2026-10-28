import Foundation
import Testing
@testable import MightyCore

/// The composer "send to" rules the Windows Core and the phone read from the same file:
/// the target list, which targets a pane offers, which one a send uses, and the text it sends.
struct AskTargetsTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/ask-targets.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    static func cases(_ key: String) throws -> [[String: Any]] {
        let cases = try #require(try load()[key] as? [[String: Any]], "\(key)")
        #expect(!cases.isEmpty, "\(key) has no cases")
        return cases
    }

    static func optional<T>(_ value: Any?, as type: T.Type) -> T? { value is NSNull ? nil : value as? T }

    static func target(_ id: String?) throws -> AskTarget? {
        guard let id else { return nil }
        return try #require(AskTargets.all.first { $0.id == id }, "\(id)")
    }

    @Test func targetsAndSkillMatchTheFixture() throws {
        let fixture = try Self.load()
        #expect(fixture["skill"] as? String == AskTargets.skill)
        let targets = try #require(fixture["targets"] as? [[String: String]])
        #expect(targets.map { $0["id"] } == AskTargets.all.map(\.id))
        #expect(targets.map { $0["label"] } == AskTargets.all.map(\.label))
        #expect(targets.map { $0["executable"] } == AskTargets.all.map(\.executable))
    }

    @Test func everyAvailableCase() throws {
        for entry in try Self.cases("available") {
            let name = entry["name"] as? String ?? "unnamed"
            let slash = Set(try #require(entry["slash"] as? [String], "\(name)"))
            let installed = Set(try #require(entry["installed"] as? [String], "\(name)"))
            let result = AskTargets.available(isClaudeAgent: entry["isClaudeAgent"] as? Bool ?? false, slashInvocations: slash, installed: installed)
            #expect(result.map(\.id) == (entry["expect"] as? [String]), "\(name)")
        }
    }

    @Test func everyEffectiveCase() throws {
        for entry in try Self.cases("effective") {
            let name = entry["name"] as? String ?? "unnamed"
            let ids = try #require(entry["available"] as? [String], "\(name)")
            let available = AskTargets.all.filter { ids.contains($0.id) }
            let result = AskTargets.effective(saved: Self.optional(entry["saved"], as: String.self), available: available)
            #expect(result?.id == Self.optional(entry["expect"], as: String.self), "\(name)")
        }
    }

    @Test func everyWrapCase() throws {
        for entry in try Self.cases("wrap") {
            let name = entry["name"] as? String ?? "unnamed"
            let target = try Self.target(Self.optional(entry["target"], as: String.self))
            let text = try #require(entry["text"] as? String, "\(name)")
            #expect(AskTargets.wrap(text, target: target) == Self.optional(entry["expect"], as: String.self), "\(name)")
        }
    }

    @Test func askTargetRoundTripsAndOldSavesReadNil() throws {
        let session = RunSession(workspaceId: "w", title: "t", askTarget: "codex")
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(decoded.askTarget == "codex")
        var object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        object.removeValue(forKey: "askTarget")
        let old = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(old.askTarget == nil)
    }

    /// A saved target falls back to Claude only once both the PATH read and the
    /// pane's catalogue are in, and only when the target is not offered.
    @Test func aChoiceFallsBackOnlyWhenKnownToBeGone() {
        let codex = AskTargets.all.filter { $0.id == "codex" }
        #expect(AskTargets.fellBack(saved: "gemini", available: codex, probed: true, scanned: true))
        #expect(AskTargets.fellBack(saved: "codex", available: [], probed: true, scanned: true))
        #expect(!AskTargets.fellBack(saved: "codex", available: codex, probed: true, scanned: true))
        #expect(!AskTargets.fellBack(saved: nil, available: [], probed: true, scanned: true))
        #expect(!AskTargets.fellBack(saved: "gemini", available: codex, probed: false, scanned: true))
        #expect(!AskTargets.fellBack(saved: "gemini", available: codex, probed: true, scanned: false))
    }

    /// A PATH that is not the login shell's, or a read finding nothing after one that
    /// found some, never takes a CLI away and never judges a target gone.
    @Test func anUnreliableReadKeepsWhatWasFound() {
        let all = AskTargets.probed(previous: ["codex"], found: ["codex", "agy"], loginShell: true)
        #expect(all.installed == ["codex", "agy"] && all.conclusive)
        let fewer = AskTargets.probed(previous: ["codex", "agy"], found: ["agy"], loginShell: true)
        #expect(fewer.installed == ["agy"] && fewer.conclusive)
        let first = AskTargets.probed(previous: [], found: [], loginShell: true)
        #expect(first.installed.isEmpty && first.conclusive)
        let hiccup = AskTargets.probed(previous: ["codex"], found: [], loginShell: true)
        #expect(hiccup.installed == ["codex"] && !hiccup.conclusive)
        let fallback = AskTargets.probed(previous: ["codex"], found: ["agy"], loginShell: false)
        #expect(fallback.installed == ["codex", "agy"] && !fallback.conclusive)
    }

    /// A wrapped send titles its pane by the question; anything else as it is.
    @Test func theQuestionInsideAWrappedSend() {
        let codex = AskTargets.all.first { $0.id == "codex" }
        let wrapped = AskTargets.wrap("  왜 느린지 봐줘 ", target: codex) ?? ""
        #expect(AskTargets.question(wrapped) == "왜 느린지 봐줘")
        #expect(AskTargets.question("/oh-my-claudecode:ask muse hi") == "/oh-my-claudecode:ask muse hi")
        #expect(AskTargets.question("그냥 질문") == "그냥 질문")
        #expect(PaneTitle.autoTitle(for: RunSession(workspaceId: "w", title: "t", logs: [LogEntry(kind: "user", text: wrapped)]), defaultTitle: "Claude") == "왜 느린지 봐줘")
    }

    /// Restoring keeps a Claude agent pane's choice, even one no longer offered (it reads
    /// as Claude when sent), and drops it from every other pane, a remote workspace's included.
    @Test func restoringKeepsTheChoiceOnlyOnAClaudeAgentPane() {
        let workspace = Workspace(id: "w", name: "W", path: "/tmp")
        let claude = RunSession(id: "claude", workspaceId: "w", title: "c", askTarget: "codex")
        let stale = RunSession(id: "stale", workspaceId: "w", title: "s", askTarget: "no-such-target")
        let codex = RunSession(id: "codex", workspaceId: "w", title: "x", provider: "codex", askTarget: "codex")
        let shell = RunSession(id: "shell", workspaceId: "w", title: "t", kind: "shell", askTarget: "codex")
        let remoteWorkspace = Workspace(id: "r", name: "R", path: "/Users/someone/project", remote: RemoteWorkspaceReference(connectionId: "conn", workspaceId: "host-ws", hostName: "Host"))
        let remote = RunSession(id: "remote", workspaceId: "r", title: "r", askTarget: "codex")
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace, remoteWorkspace], sessions: [claude, stale, codex, shell, remote]), restoring: true)
        let saved = Dictionary(uniqueKeysWithValues: restored.sessions.map { ($0.id, $0.askTarget) })
        #expect(saved["claude"] == .some("codex"))
        #expect(saved["stale"] == .some("no-such-target"))
        #expect(saved["codex"] == .some(nil))
        #expect(saved["shell"] == .some(nil))
        // A remote workspace's Claude pane drops it too: its CLIs are on another computer.
        #expect(saved["remote"] == .some(nil))
    }
}
