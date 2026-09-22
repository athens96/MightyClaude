import Foundation
import Testing
@testable import MightyCore

struct BedrockAuthDiagnosticsTests {
    private let key = "AWS_BEARER_TOKEN_BEDROCK"
    private let denied = "User: arn:aws:sts::123456789012:assumed-role/private-role/private-session is not authorized to perform: bedrock:InvokeModel on resource: arn:aws:bedrock:ap-northeast-2::foundation-model/private-model with an explicit deny in a service control policy"
    private func settings(_ value: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["env": [key: value]])
    }
    private func apiError(_ message: String, status: Int = 403, code: String? = nil) throws -> String {
        var body = ["Message": message]
        if let code { body["__type"] = code }
        let json = String(decoding: try JSONSerialization.data(withJSONObject: body), as: UTF8.self)
        return "AWS authentication failed · refresh your AWS credentials and retry · API Error: \(status) \(json)"
    }
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        parser.push(try JSONSerialization.data(withJSONObject: value)); parser.push("\n")
    }

    @Test func scpDenialUsesUnderlyingJSONInsteadOfCredentialWrapper() throws {
        for action in ["InvokeModel", "InvokeModelWithResponseStream"] {
            for suffix in ["", ": arn:aws:organizations::123456789012:policy/o-0123456789/service_control_policy/p-01234567"] {
                for connector in ["with", "because of"] {
                    for code in [nil, "AccessDeniedException", "private.namespace#AccessDeniedException"] as [String?] {
                        let message = denied.replacingOccurrences(of: "InvokeModel", with: action).replacingOccurrences(of: "with an explicit", with: connector + " an explicit") + suffix
                        let guidance = BedrockAuthDiagnostics.runtimeFailureGuidance(try apiError(message, code: code))
                        #expect(guidance == BedrockAuthDiagnostics.scpInvokeModelDeniedMessage)
                        #expect(guidance?.contains("private-") == false)
                        #expect(guidance?.contains("123456789012") == false)
                        #expect(guidance?.contains("refresh your AWS") == false)
                        #expect(guidance?.contains("arn:") == false)
                    }
                }
            }
        }
    }

    @Test func policyDocumentationAndOtherFailuresKeepTheirOriginalMeaning() throws {
        let cases = [
            try apiError(denied, status: 400),
            try apiError(denied, code: "ExpiredTokenException"),
            try apiError("Authentication failed: Please make sure your API Key is valid. " + denied),
            try apiError("The security token included in the request is expired. See service control policy documentation."),
            try apiError("Not authorized; see service control policy documentation for explicit deny examples."),
            try apiError("For example: " + denied),
            try apiError(denied.replacingOccurrences(of: "service control policy", with: "identity-based policy") + ". See service control policy documentation."),
            try apiError(denied.replacingOccurrences(of: "InvokeModel", with: "ListFoundationModels")),
            try apiError(denied.replacingOccurrences(of: "InvokeModel", with: "InvokeModelWithResponseStreamExtra")),
            try apiError(denied + ": arn:aws:iam::123456789012:policy/private"),
            try apiError(denied + ". See service control policy documentation."),
            try apiError(denied + ": https://private.invalid/policy"),
            try apiError(denied.replacingOccurrences(of: "with an explicit deny in", with: "because no permission is granted by")),
            "API Error: 403 invalid-json " + denied,
            denied,
        ]
        for raw in cases { #expect(BedrockAuthDiagnostics.runtimeFailureGuidance(raw) == nil) }
    }

    @Test func confirmedClaudeFailuresUseTheSameGuidanceInTranscriptAndGraph() throws {
        let raw = try apiError(denied)
        var logs: [(String, String)] = []
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append(($0, $1)) }, resume: { _ in },
                                     activityNamespace: "scp-fixture", graph: { nodes.append($0) }, graphInput: "Fixture")
        try send(["type": "assistant", "error": "authentication_failed", "message": ["id": "aws-error", "content": [["type": "text", "text": raw]]]], to: parser)
        try send(["type": "result", "subtype": "error_during_execution", "is_error": true, "errors": [raw], "result": raw], to: parser)
        parser.flush()
        #expect(parser.failed)
        #expect(logs.map(\.0) == ["assistant", "error"])
        #expect(logs.allSatisfy { $0.1 == BedrockAuthDiagnostics.scpInvokeModelDeniedMessage })
        #expect(nodes.last(where: { $0.kind == "main" })?.output == BedrockAuthDiagnostics.scpInvokeModelDeniedMessage)
        #expect(!String(decoding: try JSONEncoder().encode(nodes), as: UTF8.self).contains("private-"))
    }

    @Test func normalAssistantTextToolOutputAndOtherProvidersAreNotRewritten() throws {
        let raw = try apiError(denied)
        var logs: [String] = []
        var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, activity: { activities.append($0) })
        try send(["type": "assistant", "message": ["id": "quoted-example", "content": [["type": "text", "text": raw]]]], to: parser)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "tool-fixture", "is_error": true, "content": raw]]]], to: parser)
        try send(["type": "result", "subtype": "success", "is_error": false, "result": raw], to: parser)
        parser.flush()
        #expect(logs == [raw])
        #expect(!parser.failed)
        #expect(activities.contains { $0.output == raw })
        let codex = CLIStreamParser(provider: "codex", log: { logs.append($1) }, resume: { _ in })
        try send(["type": "turn.failed", "error": ["message": raw]], to: codex)
        codex.flush()
        #expect(logs.last == raw)
    }

    @Test func differentCredentialsProduceOnlyFixedGuidance() throws {
        let environment = [key: "fake-shell-credential-never-render"]
        let data = try settings("fake-settings-credential-never-render")
        for _ in 0..<10 {
            let detail = BedrockAuthDiagnostics.conflictDetail(environment: environment, userSettings: data)
            #expect(detail == BedrockAuthDiagnostics.conflictMessage)
            #expect(detail?.contains("fake-") == false)
            #expect(detail?.contains("invalid") == false)
        }
    }

    @Test func matchingMissingAndMalformedSourcesDoNotClaimConflict() throws {
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: "same-fake"], userSettings: try settings("same-fake")) == nil)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: "  same-fake\n"], userSettings: try settings("same-fake\t")) == nil)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [:], userSettings: try settings("fake")) == nil)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: ""], userSettings: try settings("fake")) == nil)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: "fake"], userSettings: try settings("  ")) == nil)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: "fake"], userSettings: try settings(42)) == nil)
        for data in [Data("broken".utf8), Data("{}".utf8), Data("[]".utf8), Data(repeating: 32, count: BedrockAuthDiagnostics.maximumSettingsBytes + 1)] {
            #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: "fake"], userSettings: data) == nil)
        }
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: [key: "fake"], userSettings: nil) == nil)
    }

    @Test func settingsReadsAreBoundedAndRespectCLIConfigDirectory() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("bedrock-diagnostic-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let standard = home.appendingPathComponent(".claude", isDirectory: true)
        let custom = home.appendingPathComponent("custom", isDirectory: true)
        try FileManager.default.createDirectory(at: standard, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: custom, withIntermediateDirectories: true)
        let file = standard.appendingPathComponent("settings.json")
        let original = try settings("fake-settings")
        try original.write(to: file)
        let env = [key: "fake-shell"]
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: env, home: home) == BedrockAuthDiagnostics.conflictMessage)
        #expect(try Data(contentsOf: file) == original)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: env.merging(["CLAUDE_CONFIG_DIR": custom.path], uniquingKeysWith: { _, new in new }), home: home) == nil)
        try original.write(to: custom.appendingPathComponent("settings.json"))
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: env.merging(["CLAUDE_CONFIG_DIR": "custom"], uniquingKeysWith: { _, new in new }), home: home) == BedrockAuthDiagnostics.conflictMessage)
        #expect(BedrockAuthDiagnostics.boundedSettingsData(standard) == nil)
        try Data(repeating: 32, count: BedrockAuthDiagnostics.maximumSettingsBytes + 1).write(to: file)
        #expect(BedrockAuthDiagnostics.conflictDetail(environment: env, home: home) == nil)
    }

    @Test func failedShellCaptureDoesNotClaimToKnowTheTerminalCredential() async throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("bedrock-fallback-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: home) }
        let config = home.appendingPathComponent(".claude")
        try FileManager.default.createDirectory(at: config, withIntermediateDirectories: true)
        try settings("fake-settings").write(to: config.appendingPathComponent("settings.json"))
        let shell = home.appendingPathComponent("failed-shell")
        let claude = home.appendingPathComponent("claude")
        try Data("#!/bin/sh\nexit 1\n".utf8).write(to: shell)
        try Data("#!/bin/sh\nprintf '%s\\n' '{\"loggedIn\":true,\"authMethod\":\"third_party\",\"apiProvider\":\"bedrock\"}'\n".utf8).write(to: claude)
        for file in [shell, claude] { try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path) }
        let resolver = CLIEnvironmentResolver(baseEnvironment: ["PATH": home.path, key: "fake-parent"], shell: shell, home: home)
        let service = CLIAccountService(home: home, environmentResolver: resolver)
        let status = await service.status(provider: "claude")
        #expect(status.method == "AWS Bedrock")
        #expect(!status.detail.contains(BedrockAuthDiagnostics.conflictMessage))
        #expect(status.detail.contains("앱 시작 환경"))
        #expect(!status.detail.contains("fake-"))
    }
}
