import Foundation
import Testing
@testable import MightyCore

/// The Mighty graph of an ACP agent's run (opencode, Hermes): every shared recording replayed through the ACP channel, the stream parser and
/// its graph tracker. native/contracts/fixtures/acp-graphs.json holds what the Mac emits for each; Windows Core
/// (native/windows/MightyCode.Core.Tests/AcpGraphVerification.cs) replays the same recordings through its own channel, parser and tracker and
/// expects the same blocks.
struct AcpGraphTests {
    private static let url = GraphAgentIdentityTests.root.appendingPathComponent("native/contracts/fixtures/acp-graphs.json")

    private static func cases() throws -> [[String: Any]] {
        let document = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        return try #require(document["cases"] as? [[String: Any]])
    }

    private static func recording(_ provider: String, _ name: String) throws -> [Data] {
        let data = try Data(contentsOf: GraphAgentIdentityTests.root.appendingPathComponent("native/contracts/fixtures/\(provider)-acp-\(name).jsonl"))
        return data.split(separator: 10).map { Data($0) }
    }

    /// The case's request: its input, and the resume and permission mode the recording was made with.
    private static func request(_ provider: String, _ fields: [String: Any]) -> StartRunRequest {
        var request = StartRunRequest(sessionId: "pane", workspaceId: "workspace", input: fields["input"] as? String ?? "hi there", provider: provider,
                                      settings: RunSettings(permissionMode: fields["permissionMode"] as? String ?? "manual"))
        request.resumeId = fields["resumeId"] as? String
        return request
    }

    /// What the tracker emitted last for each block, in the order the blocks first came, as the fixture lists it.
    static func blocks(_ item: [String: Any]) throws -> [String: Any] {
        let provider = try #require(item["provider"] as? String)
        let request = Self.request(provider, item["request"] as? [String: Any] ?? [:])
        var emitted: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: provider, log: { _, _ in }, resume: { _ in }, activityNamespace: "run-" + provider,
                                     graph: { emitted.append($0) }, graphInput: request.input)
        let channel = ACPChannel(runId: "run-" + provider, request: request, workspacePath: "/tmp/project", attachments: try AttachmentPreparation([]),
                                 configValues: provider == "hermes" ? HermesAgent.configValues(request) : OpencodeAgent.configValues(request), freeChoices: provider == "hermes",
                                 write: { _ in }, event: { parser.receive(object: $0) }, emit: { _ in }, activity: { parser.permissionActivity($0, state: $1) },
                                 warning: { _ in }, fail: { _ in }, completed: {})
        channel.start()
        for line in try recording(provider, try #require(item["recording"] as? String)) { channel.receive(line + Data([10])) }
        parser.flush(); parser.finishActivities(stopped: false)
        // As the run ends (ProcessRunner): completed when the turn completed and nothing failed.
        let state = channel.turnCompleted && !channel.failed && !parser.failed ? "completed" : "error"
        parser.finishGraph(state: state)
        var order: [String] = []
        var last: [String: ExecutionGraphNode] = [:]
        for node in emitted {
            if last[node.id] == nil { order.append(node.id) }
            last[node.id] = node
        }
        let blocks = order.compactMap { last[$0] }.map { node -> [String: Any] in
            var block: [String: Any] = ["kind": node.kind, "state": node.state, "title": node.title]
            if let input = node.input { block["input"] = input }
            if let output = node.output { block["output"] = output }
            if let usage = node.usage {
                block["usage"] = ["inputTokens": usage.inputTokens, "outputTokens": usage.outputTokens, "cacheReadTokens": usage.cacheReadTokens, "cacheCreationTokens": usage.cacheCreationTokens]
            }
            return block
        }
        return ["state": state, "blocks": blocks]
    }

    private static func pretty(_ value: Any) -> String {
        (try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys])).flatMap { String(data: $0, encoding: .utf8) } ?? "?"
    }

    @Test func everyRecordingIsOneBlockWithTheAnswerAndOpencodesTokens() throws {
        try LocaleOverride.$language.withValue(.ko) {
            let cases = try Self.cases()
            #expect(cases.count == 16)
            for item in cases {
                let name = "\(item["provider"] ?? "?")-acp-\(item["recording"] ?? "?")"
                let made = try Self.blocks(item)
                guard let expect = item["expect"] as? [String: Any] else {
                    Issue.record("\(name) has no expectation; the Mac makes:\n\(Self.pretty(made))")
                    continue
                }
                #expect(NSDictionary(dictionary: made).isEqual(to: expect), "\(name) differs; the Mac makes:\n\(Self.pretty(made))")
            }
        }
    }

    /// The rules the fixture shows, said once: one block per request, opencode's tokens and Hermes's none, a sub-agent's text kept out.
    @Test func theRulesTheRecordingsShow() throws {
        try LocaleOverride.$language.withValue(.ko) {
            let cases = try Self.cases()
            for item in cases {
                let made = try Self.blocks(item)
                let blocks = try #require(made["blocks"] as? [[String: Any]])
                #expect(blocks.count == 1 && blocks[0]["kind"] as? String == "main")
                #expect(blocks[0]["title"] as? String == ProviderOptions.label(item["provider"] as? String ?? ""))
                if item["provider"] as? String == "hermes" { #expect(blocks[0]["usage"] == nil) }
            }
            let tools = try #require(cases.first { $0["provider"] as? String == "opencode" && $0["recording"] as? String == "tools" })
            let main = try #require((try Self.blocks(tools)["blocks"] as? [[String: Any]])?.first)
            // The child session's own reply (`_meta["opencode/child-session"]`) is not the answer.
            #expect(main["output"] as? String == "One test fails because main returns 1.")
        }
    }
}
