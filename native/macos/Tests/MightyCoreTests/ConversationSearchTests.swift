import Foundation
import Testing
@testable import MightyCore

/// 대화 검색 (`ConversationSearch.swift`): only messages count, every term must match, newest sessions first.
struct ConversationSearchTests {
    @Test func messagesOfEachRecordShapeAreReadAndToolTrafficIsNot() {
        #expect(ConversationSearch.messageText(#"{"type":"user","message":{"role":"user","content":"마이그레이션 해 줘"}}"#) == "마이그레이션 해 줘")
        #expect(ConversationSearch.messageText(#"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"},{"type":"tool_use","name":"Bash"}]}}"#) == "done")
        #expect(ConversationSearch.messageText(#"{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"secret output"}]}}"#) == nil)
        #expect(ConversationSearch.messageText(#"{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"fix ci"}]}}"#) == "fix ci")
        #expect(ConversationSearch.messageText(#"{"type":"response_item","payload":{"type":"function_call_output","output":"x"}}"#) == nil)
        #expect(ConversationSearch.messageText(#"{"type":"event_msg","payload":{"type":"agent_message","message":"ok"}}"#) == "ok")
        #expect(ConversationSearch.messageText(#"{"role":"model","parts":[{"text":"hello"}]}"#) == "hello")
        #expect(ConversationSearch.messageText("not json") == nil)
    }

    @Test func everyTermMustMatchOneMessage() throws {
        let lines = [
            #"{"type":"user","message":{"role":"user","content":"Add a database migration for users"}}"#,
            #"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"The migration is in db/migrate."}]}}"#,
            #"{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"migration users"}]}}"#,
        ].joined(separator: "\n")
        let found = try #require(ConversationSearch.search(lines: lines.split(separator: "\n"), terms: ConversationSearch.terms("Migration  USERS")))
        #expect(found.matches == 1)
        #expect(found.snippet == "Add a database migration for users")
        #expect(ConversationSearch.search(lines: lines.split(separator: "\n"), terms: ["migration", "nowhere"]) == nil)
        #expect(ConversationSearch.search(lines: lines.split(separator: "\n"), terms: []) == nil)
    }

    @Test func aSnippetCentresOnTheMatch() {
        let text = String(repeating: "a ", count: 100) + "needle\nhere " + String(repeating: "b ", count: 100)
        let snippet = ConversationSearch.snippet(text, terms: ["needle"])
        #expect(snippet.hasPrefix("…") && snippet.hasSuffix("…") && snippet.contains("needle here"))
    }

    @Test func sessionsAreSearchedNewestFirst() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-search-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        func session(_ id: String, _ text: String, age: TimeInterval) throws -> ResumableSession {
            let url = folder.appendingPathComponent(id + ".jsonl")
            try Data(#"{"type":"user","message":{"role":"user","content":"\#(text)"}}"#.utf8).write(to: url)
            return ResumableSession(provider: "claude", sessionID: id, title: text, modified: Date(timeIntervalSinceNow: -age), requests: 1, model: nil, url: url)
        }
        let sessions = [try session("old", "deploy the relay", age: 100), try session("new", "relay deploy fails", age: 1), try session("other", "unrelated", age: 0)]
        #expect(ConversationSearch.search(sessions, query: "relay deploy").map(\.session.sessionID) == ["new", "old"])
        #expect(ConversationSearch.search(sessions, query: "relay", limit: 1).map(\.session.sessionID) == ["new"])
        #expect(ConversationSearch.search(sessions, query: "   ").isEmpty)
    }

    @Test func aSessionBecomesMarkdownWithoutToolTrafficOrRepeats() {
        let lines = [
            #"{"type":"user","message":{"role":"user","content":"fix it"}}"#,
            #"{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"x"}]}}"#,
            #"{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"fixed"}]}}"#,
            #"{"type":"event_msg","payload":{"type":"agent_message","message":"fixed"}}"#,
        ].joined(separator: "\n")
        let markdown = ConversationSearch.markdown(lines: lines.split(separator: "\n"), title: "T", userLabel: "Me", agentLabel: "Agent")
        #expect(markdown == "# T\n\n## Me\n\nfix it\n\n## Agent\n\nfixed\n")
    }
}
