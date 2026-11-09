import Foundation
import Testing
@testable import MightyCore

/// 신뢰와 진단 (`TrustTools.swift`): secrets before a commit, a request's audit, redaction.
struct TrustToolsTests {
    @Test func secretsAreFoundInAddedLinesOnly() {
        let diff = """
        diff --git a/config.js b/config.js
        --- a/config.js
        +++ b/config.js
        @@ -1,2 +10,3 @@
         const a = 1
        -const old = "AKIAABCDEFGHIJKLMNOP"
        +const key = "AKIAABCDEFGHIJKLMNOP"
        +const ok = "nothing here"
        +const gh = "ghp_\(String(repeating: "a", count: 36))"
        """
        let findings = SecretScan.scanDiff(diff)
        #expect(findings.map { "\($0.path):\($0.line):\($0.kind)" } == ["config.js:11:AWS access key", "config.js:13:GitHub token"])
        #expect(SecretScan.scan("-----BEGIN OPENSSH PRIVATE KEY-----", path: "k").first?.kind == "Private key")
        #expect(SecretScan.scan("sk-ant-api03-\(String(repeating: "x", count: 30))", path: "f").map(\.kind).contains("Anthropic key"))
        #expect(SecretScan.scan("let skip = \"sk-short\"", path: "f").isEmpty)
        #expect(SecretScan.scanNames([".env.local", "src/app.swift", "deploy/key.pem"]).map(\.path) == [".env.local", "deploy/key.pem"])
    }

    @Test func aRunsToolsBecomeOneRowEach() {
        func entry(_ id: String, _ tool: String, _ summary: String, _ state: String) -> LogEntry {
            LogEntry(kind: "output", text: "", activity: AgentActivity(id: id, provider: "claude", kind: "tool", state: state, toolName: tool, summary: summary))
        }
        let agent = MightyGraphAgent(id: "a1", title: "explorer", input: "", status: "completed", entries: [entry("t3", "WebFetch", "https://x", "completed")])
        let run = MightyGraphRun(id: "r", input: "do it", status: "completed",
                                 rootEntries: [entry("t1", "Bash", "swift test", "running"), entry("t2", "Edit", "a.swift", "completed"), entry("t1", "Bash", "swift test", "error"), LogEntry(kind: "assistant", text: "hi")],
                                 agents: [agent], resultEntries: [])
        let rows = RunAudit.rows(run)
        #expect(rows.map(\.id) == ["t1", "t2", "t3"])
        #expect(rows.map(\.kind) == [.command, .write, .web] && rows[0].state == "error" && rows[2].agent == "explorer")
        let text = RunAudit.text(rows, title: "# T", labels: [.command: "Commands"])
        #expect(text.contains("## Commands (1)\n- [Bash] swift test ✗") && text.contains("- [WebFetch] https://x · explorer"))
    }

    @Test func theHomeFolderIsRedacted() {
        #expect(DiagnosticsSummary.redact("/Users/me/work and /Users/me", home: "/Users/me") == "~/work and ~")
        #expect(DiagnosticsSummary.redact("/x", home: "/") == "/x")
    }
}
