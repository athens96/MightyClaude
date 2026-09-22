import Foundation
import Testing
@testable import MightyCore

@Suite struct CodexApprovalPresentationTests {
    private func presentation(_ name: String, _ input: [String: Any]) throws -> ToolPermissionPresentation {
        let data = try JSONSerialization.data(withJSONObject: input, options: [.sortedKeys])
        return ToolPermissionPresentation.make(toolName: name, inputJSON: String(decoding: data, as: UTF8.self))
    }

    @Test func commandAndNetworkScopeAreCompleteAndExplicit() throws {
        let command = "fixture " + String(repeating: "x", count: 9_000) + "; tail-must-be-visible"
        let value = try presentation("command_execution", ["command": command, "cwd": "/workspace", "reason": "키체인과 네트워크 접근", "networkApprovalContext": ["host": "gitlab.example.test", "protocol": "https", "port": 443], "availableDecisions": ["accept", "decline"]])
        #expect(value.title == "명령 실행")
        #expect(value.primaryCode?.value == command)
        #expect(value.fields.first { $0.label == "작업 폴더" }?.value == "/workspace")
        #expect(value.fields.first { $0.label == "네트워크 호스트" }?.value == "gitlab.example.test")
        #expect(value.fields.first { $0.label == "네트워크 프로토콜" }?.value == "https")
        #expect(value.fields.first { $0.label == "추가 네트워크 조건" }?.value.contains("443") == true)
        #expect(value.fields.first { $0.label == "승인 요청 이유" }?.value == "키체인과 네트워크 접근")
        #expect(value.fields.first { $0.label == "추가 요청 정보" }?.value.contains("availableDecisions") == true)
    }

    @Test func fileDiffAndGrantScopeAreNotSilentlyTruncated() throws {
        let diff = "@@ fixture @@\n" + String(repeating: "+line\n", count: 1_500) + "+end-of-diff"
        let changes: [[String: Any]] = [["path": "/workspace/file.swift", "kind": ["type": "update"], "diff": diff]]
        let value = try presentation("file_change", ["changes": changes, "grantRoot": "/outside/workspace", "reason": "fixture diff"])
        #expect(value.title == "파일 수정")
        let changesText = try #require(value.fields.first { $0.label == "파일 변경 전체" }?.value)
        let decoded = try #require(JSONSerialization.jsonObject(with: Data(changesText.utf8)) as? [[String: Any]])
        #expect(decoded.first?["diff"] as? String == diff)
        #expect(value.fields.first { $0.label == "추가 권한 경로" }?.value == "/outside/workspace")
    }

    @Test func controlAndInvisibleFormatCharactersAreRenderedLiterally() throws {
        let value = try presentation("command_execution", ["command": "one\u{001b}[2J\r\u{202e}tail\n\tlast", "cwd": "/work\u{200b}space", "reason": "why\u{0000}"])
        #expect(value.primaryCode?.value == "one\\u001b[2J\\u000d\\u202etail\n\tlast")
        #expect(value.fields.first { $0.label == "작업 폴더" }?.value == "/work\\u200bspace")
        #expect(value.fields.first { $0.label == "승인 요청 이유" }?.value == "why\\u0000")
    }
}
