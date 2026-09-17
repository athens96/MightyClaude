import Foundation
import Testing
@testable import MightyCore

struct ToolPermissionPresentationTests {
    @Test func bashRequestShowsDescriptionAndCommandInsteadOfJSON() {
        let json = #"{"command" : "timeout 300 .venv/bin/python -m pytest -q 2>&1 | tail -20", "description" : "Run the test suite"}"#
        let value = ToolPermissionPresentation.make(toolName: "Bash", inputJSON: json)
        #expect(value.title == "명령 실행")
        #expect(value.headline == "Run the test suite")
        #expect(value.fields.map(\.label) == ["명령"])
        #expect(value.fields[0].value == "timeout 300 .venv/bin/python -m pytest -q 2>&1 | tail -20")
        #expect(value.fields[0].code); #expect(value.primaryCode?.label == "명령")
        let background = ToolPermissionPresentation.make(toolName: "Bash", inputJSON: #"{"command":"sleep 9","run_in_background":true,"timeout":120000}"#)
        #expect(background.headline == nil)
        #expect(background.fields.map { $0.label + "=" + $0.value } == ["명령=sleep 9", "제한 시간(ms)=120000", "백그라운드 실행=예"])
    }

    @Test func editWriteSearchAndAgentRequestsUseNamedFields() {
        let edit = ToolPermissionPresentation.make(toolName: "Edit", inputJSON: #"{"file_path":"/w/a.swift","old_string":"let a = 1","new_string":"let a = 2","replace_all":false}"#)
        #expect(edit.title == "파일 수정")
        #expect(edit.fields.map(\.label) == ["파일", "바꿀 내용", "새 내용", "모두 바꾸기"])
        #expect(edit.fields[3].value == "아니요"); #expect(edit.fields[1].code)
        let write = ToolPermissionPresentation.make(toolName: "Write", inputJSON: ##"{"file_path":"/w/b.md","content":"# Title"}"##)
        #expect(write.title == "파일 쓰기"); #expect(write.fields.map(\.value) == ["/w/b.md", "# Title"])
        let grep = ToolPermissionPresentation.make(toolName: "Grep", inputJSON: #"{"pattern":"TODO","path":"src","glob":"*.ts","-n":true}"#)
        #expect(grep.title == "내용 검색")
        #expect(grep.fields.map(\.label) == ["패턴", "경로", "파일 필터", "-n"])
        let agent = ToolPermissionPresentation.make(toolName: "Agent", inputJSON: #"{"description":"Explore repo","prompt":"Find callers","subagent_type":"Explore"}"#)
        #expect(agent.title == "하위 에이전트 실행"); #expect(agent.headline == "Explore repo")
        #expect(agent.fields.map(\.label) == ["에이전트 종류", "지시"])
    }

    @Test func mcpUnknownAndMalformedInputsStayReadableAndBounded() {
        let mcp = ToolPermissionPresentation.make(toolName: "mcp__mcp-gcoo__execute_sql", inputJSON: #"{"sql":"select 1","params":{"limit":5}}"#)
        #expect(mcp.title == "MCP 도구 · mcp-gcoo")
        #expect(mcp.fields.map(\.label) == ["params", "sql"])
        #expect(mcp.fields[0].value.contains("\"limit\" : 5"))
        let unknown = ToolPermissionPresentation.make(toolName: "Custom", inputJSON: "not json")
        #expect(unknown.title == "도구 실행"); #expect(unknown.headline == nil); #expect(unknown.fields.isEmpty)
        let long = ToolPermissionPresentation.make(toolName: "Write", inputJSON: "{\"file_path\":\"/w/c.txt\",\"content\":\"" + String(repeating: "x", count: 10_000) + "\"}")
        #expect(long.fields[1].value.hasSuffix("…"))
        #expect(long.fields[1].value.utf8.count <= ToolPermissionPresentation.maximumFieldBytes + 4)
        let many = "{" + (0..<20).map { "\"k\($0)\":\"v\"" }.joined(separator: ",") + "}"
        #expect(ToolPermissionPresentation.make(toolName: "Custom", inputJSON: many).fields.count == ToolPermissionPresentation.maximumFields)
    }
}
