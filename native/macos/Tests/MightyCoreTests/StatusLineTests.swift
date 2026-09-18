import Foundation
import Testing
@testable import MightyCore

struct StatusLineTests {
    private func write(_ text: String, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
    }

    @Test func configFollowsClaudePrecedenceAndOnlyCommandEntries() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("statusline-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), workspace = root.appendingPathComponent("repo")
        try write(#"{"statusLine": {"type": "command", "command": "echo user", "padding": 2}}"#, to: home.appendingPathComponent(".claude/settings.json"))
        let user = try #require(StatusLineConfig.load(workspacePath: workspace.path, home: home, environment: [:]))
        #expect(user.command == "echo user" && user.padding == 2 && user.source == "사용자 설정")
        try write(#"{"statusLine": {"type": "command", "command": "echo project"}}"#, to: workspace.appendingPathComponent(".claude/settings.json"))
        #expect(StatusLineConfig.load(workspacePath: workspace.path, home: home, environment: [:])?.command == "echo project")
        try write(#"{"statusLine": {"type": "command", "command": "echo local"}}"#, to: workspace.appendingPathComponent(".claude/settings.local.json"))
        #expect(StatusLineConfig.load(workspacePath: workspace.path, home: home, environment: [:])?.command == "echo local")
        // A non-command entry at the winning level disables the line instead of falling through.
        try write(#"{"statusLine": {"type": "other"}}"#, to: workspace.appendingPathComponent(".claude/settings.local.json"))
        #expect(StatusLineConfig.load(workspacePath: workspace.path, home: home, environment: [:]) == nil)
        #expect(StatusLineConfig.load(workspacePath: nil, home: home, environment: [:])?.command == "echo user")
        let custom = root.appendingPathComponent("custom-config")
        try write(#"{"statusLine": {"type": "command", "command": "echo custom"}}"#, to: custom.appendingPathComponent("settings.json"))
        #expect(StatusLineConfig.load(workspacePath: nil, home: home, environment: ["CLAUDE_CONFIG_DIR": custom.path])?.command == "echo custom")
        #expect(StatusLineConfig.load(workspacePath: nil, home: root.appendingPathComponent("nowhere"), environment: [:]) == nil)
        // Discovery keeps both levels apart so the app can gate the repository's command.
        try write(#"{"statusLine": {"type": "command", "command": "echo local"}}"#, to: workspace.appendingPathComponent(".claude/settings.local.json"))
        try write(#"{"statusLine": {"type": "command", "command": "echo user2"}, "outputStyle": "Explanatory", "alwaysThinkingEnabled": true}"#, to: home.appendingPathComponent(".claude/settings.json"))
        let discovery = StatusLineConfig.discover(workspacePath: workspace.path, home: home, environment: [:])
        #expect(discovery.workspace?.command == "echo local" && discovery.workspace?.fromWorkspace == true && discovery.user?.command == "echo user2" && discovery.user?.fromWorkspace == false)
        #expect(discovery.user?.outputStyle == "Explanatory" && discovery.user?.thinkingEnabled == true && discovery.workspace?.thinkingEnabled == nil)
        #expect(discovery.preferred == discovery.workspace)
        #expect(discovery.workspace?.fingerprint != discovery.user?.fingerprint && discovery.workspace?.fingerprint.count == 64)
        try write(#"{"statusLine": {"type": "other"}}"#, to: workspace.appendingPathComponent(".claude/settings.local.json"))
        let disabled = StatusLineConfig.discover(workspacePath: workspace.path, home: home, environment: [:])
        #expect(disabled.workspaceDisabled && disabled.workspace == nil && disabled.user?.command == "echo user2" && disabled.preferred == nil)
    }

    @Test func payloadUsesTheCLIsFieldNamesAndTranscriptLayout() throws {
        let home = URL(fileURLWithPath: "/Users/me")
        #expect(StatusLineSupport.transcriptPath(cwd: "/Users/me/Work/My.App", sessionId: "abc", home: home, environment: [:]) == "/Users/me/.claude/projects/-Users-me-Work-My-App/abc.jsonl")
        #expect(StatusLineSupport.transcriptPath(cwd: "/x", sessionId: "s", home: home, environment: ["CLAUDE_CONFIG_DIR": "/cfg"]) == "/cfg/projects/-x/s.jsonl")
        let reset = ISO8601DateFormatter().string(from: Date().addingTimeInterval(3600))
        let context = StatusLineContext(sessionId: "sess", cwd: "/repo", projectDir: "/repo", modelId: "claude-fable-5-1", modelName: "Fable 5.1", version: "2.1.274",
                                        costUSD: 1.5, durationMs: 4000, inputTokens: 120_000, outputTokens: 3000, cacheReadTokens: 100_000, cacheWriteTokens: 5000,
                                        contextUsedTokens: 150_000, contextWindowTokens: 1_000_000, effort: "high", fastMode: true,
                                        rateLimits: [SessionRateLimit(kind: "five_hour", percentUsed: 42.5, resetsAt: reset), SessionRateLimit(kind: "seven_day", percentUsed: 12, resetsAt: "2000-01-01T00:00:00Z"), SessionRateLimit(kind: "other", percentUsed: 1)])
        let payload = StatusLineSupport.payload(context, home: home, environment: [:])
        #expect(payload["hook_event_name"] as? String == "Status" && payload["session_id"] as? String == "sess" && payload["version"] as? String == "2.1.274")
        #expect((payload["model"] as? [String: Any])?["display_name"] as? String == "Fable 5.1")
        #expect((payload["workspace"] as? [String: Any])?["project_dir"] as? String == "/repo")
        let window = try #require(payload["context_window"] as? [String: Any])
        #expect(window["context_window_size"] as? Int == 1_000_000 && window["used_percentage"] as? Double == 15 && window["remaining_percentage"] as? Double == 85)
        // current_usage describes the context itself, not the cumulative counters.
        let usage = try #require(window["current_usage"] as? [String: Any])
        #expect(usage["input_tokens"] as? Int == 150_000 && usage["cache_read_input_tokens"] as? Int == 0 && usage["output_tokens"] as? Int == 0)
        #expect(payload["output_style"] == nil && payload["thinking"] == nil)
        var styled = context; styled.outputStyle = "Explanatory"; styled.thinkingEnabled = false
        let styledPayload = StatusLineSupport.payload(styled, home: home, environment: [:])
        #expect((styledPayload["output_style"] as? [String: Any])?["name"] as? String == "Explanatory" && (styledPayload["thinking"] as? [String: Any])?["enabled"] as? Bool == false)
        #expect(payload["exceeds_200k_tokens"] as? Bool == false && payload["fast_mode"] as? Bool == true)
        #expect((payload["effort"] as? [String: Any])?["level"] as? String == "high")
        #expect((payload["cost"] as? [String: Any])?["total_cost_usd"] as? Double == 1.5)
        let limits = try #require(payload["rate_limits"] as? [String: Any])
        #expect((limits["five_hour"] as? [String: Any])?["used_percentage"] as? Double == 42.5)
        #expect((limits["five_hour"] as? [String: Any])?["resets_at"] is Int)
        #expect(limits["seven_day"] == nil) // already reset
        #expect(JSONSerialization.isValidJSONObject(payload))
        // Nothing measured yet: nulls, not guesses.
        let empty = StatusLineSupport.payload(StatusLineContext(sessionId: "s", cwd: "/r", projectDir: "/r", modelId: "default", modelName: "CLI 기본값", version: ""), home: home, environment: [:])
        let emptyWindow = try #require(empty["context_window"] as? [String: Any])
        #expect(emptyWindow["used_percentage"] is NSNull && emptyWindow["current_usage"] is NSNull && empty["rate_limits"] == nil && empty["effort"] == nil)
    }

    @Test func ansiParsingKeepsColoursWeightAndStripsOtherEscapes() {
        let line = "\u{1B}[2mrepo:\u{1B}[0m\u{1B}[36mMighty\u{1B}[0m \u{1B}[1;38;5;208mbold\u{1B}[22m \u{1B}[38;2;10;20;30mrgb\u{1B}[39m\u{1B}]8;;http://x\u{07}link\u{1B}]8;;\u{07} \u{1B}[2Kplain 🚀"
        let segments = ANSIText.parse(line)
        #expect(segments.map(\.text) == ["repo:", "Mighty", " ", "bold", " ", "rgb", "link plain 🚀"])
        #expect(segments[0].dim && segments[0].foreground == nil)
        #expect(segments[1].foreground == .standard(6) && !segments[1].dim)
        #expect(segments[3].bold && segments[3].foreground == .palette(208))
        #expect(!segments[5].bold && segments[5].foreground == .rgb(10, 20, 30))
        #expect(segments[6].foreground == nil)
        #expect(ANSIText.parse("").isEmpty)
        #expect(ANSIText.parse("\u{1B}[").isEmpty)
        #expect(ANSIText.parse("\u{1B}[91mbright\u{1B}[0m")[0].foreground == .standard(9))
        // Colon sub-parameters set the colour; unknown tokens never reset the style.
        let colon = ANSIText.parse("\u{1B}[1m\u{1B}[38:2::10:20:30mrgb\u{1B}[?25hstill bold")
        #expect(colon[0].foreground == .rgb(10, 20, 30) && colon[0].bold && colon.last?.bold == true)
        #expect(ANSIText.parse("\u{1B}[mreset").first?.bold == false)
        let lines = StatusLineSupport.lines(from: "one\r\ntwo\n\n\n")
        #expect(lines.count == 2 && lines[1][0].text == "two")
        #expect(StatusLineSupport.lines(from: (1...10).map(String.init).joined(separator: "\n")).count == StatusLineSupport.maximumLines)
    }

    @Test func runnerFeedsStdinCapturesColourAndEnforcesTimeout() async throws {
        let cwd = FileManager.default.temporaryDirectory.path
        let env = ["PATH": "/usr/bin:/bin"]
        let echo = StatusLineConfig(command: #"printf '\033[36m%s\033[0m %s' "$(cat | sed -n 's/.*"display_name":"\([^"]*\)".*/\1/p')" "$PWD""#, source: "t")
        let result = await StatusLineSupport.run(echo, payload: ["model": ["display_name": "Fable"]], cwd: cwd, environment: env)
        #expect(result.status == 0 && result.error == nil)
        #expect(result.lines.first?.first?.text == "Fable" && result.lines.first?.first?.foreground == .standard(6))
        #expect(result.plainText.hasSuffix(cwd) || result.plainText.contains(cwd))
        let slow = await StatusLineSupport.run(StatusLineConfig(command: "sleep 5; echo late", source: "t"), payload: [:], cwd: cwd, environment: env, timeout: 0.4)
        #expect(slow.timedOut && slow.lines.isEmpty && slow.error?.contains("제한 시간") == true)
        let failing = await StatusLineSupport.run(StatusLineConfig(command: "echo oops >&2; exit 3", source: "t"), payload: [:], cwd: cwd, environment: env)
        #expect(failing.status == 3 && failing.lines.isEmpty && failing.error == "oops")
        let partial = await StatusLineSupport.run(StatusLineConfig(command: "echo shown; exit 1", source: "t"), payload: [:], cwd: cwd, environment: env)
        #expect(partial.status == 1 && partial.plainText == "shown" && partial.error == nil)
        let flood = await StatusLineSupport.run(StatusLineConfig(command: "yes | head -c 200000", source: "t"), payload: [:], cwd: cwd, environment: env)
        #expect(flood.lines.count == StatusLineSupport.maximumLines)
        // A command that ignores stdin and exits at once must not hurt the host (SIGPIPE-safe write).
        let ignoring = await StatusLineSupport.run(StatusLineConfig(command: "exit 0", source: "t"), payload: ["big": String(repeating: "x", count: 60_000)], cwd: cwd, environment: env)
        #expect(ignoring.status == 0 && ignoring.lines.isEmpty && ignoring.error == nil)
        // A backgrounded grandchild holding stdout cannot hang the result past the drain.
        let started = Date()
        let background = await StatusLineSupport.run(StatusLineConfig(command: "sleep 20 & echo quick", source: "t"), payload: [:], cwd: cwd, environment: env, timeout: 5)
        #expect(background.plainText == "quick" && Date().timeIntervalSince(started) < 4)
    }
}
