import Foundation
import Testing
@testable import MightyCore

/// A reply's shell blocks and their Run records (docs/agent-terminal-and-web.md,
/// "Running a shell block from a reply").
struct ShellBlockRunTests {
    // MARK: Which blocks

    @Test func shellLanguagesRunTheWholeBlockWithoutItsTrailingNewline() {
        for language in ["sh", "bash", "zsh", "shell", "BASH", "zsh title=x"] {
            #expect(ShellBlocks.command(language: language, code: "  ls -la\necho done\n\n") == "  ls -la\necho done")
        }
    }

    @Test func otherLanguagesAndPowerShellGetNoButtonOnMac() {
        for language in ["swift", "python", "json", "ps1", "powershell", "pwsh", "", nil] as [String?] {
            #expect(ShellBlocks.command(language: language, code: "ls") == nil)
        }
        #expect(ShellBlocks.command(language: "bash", code: "  \n\n") == nil)
    }

    @Test func consoleBlocksRunOnlyTheirPromptLines() {
        let code = "$ npm install\nadded 12 packages\n% git status\nOn branch main\n$not a prompt\n"
        #expect(ShellBlocks.command(language: "console", code: code) == "npm install\ngit status")
        #expect(ShellBlocks.command(language: "console", code: "PS> dir\n> echo hi") == nil)
    }

    @Test func consoleBlockWithoutCommandsGetsNoButton() {
        #expect(ShellBlocks.command(language: "console", code: "total 0\ndrwxr-xr-x  2 me  staff  64 Jan  1 .\n") == nil)
        #expect(ShellBlocks.command(language: "console", code: "$ \n") == nil)
    }

    @Test func keysAreTheEntryAndTheRunnableIndex() {
        #expect(ShellBlocks.key(entryId: "entry-1", index: 0) == "entry-1#0")
        #expect(ShellBlocks.key(entryId: "entry-1", index: 2) == "entry-1#2")
        #expect(ShellBlocks.key(entryId: "entry-1", index: 2) == ShellBlocks.key(entryId: "entry-1", index: 2))
    }

    @Test func aBlockStillStreamingHasNoClosingFence() {
        #expect(ShellBlocks.endsInOpenFence("Run:\n```bash\nnpm test"))
        #expect(!ShellBlocks.endsInOpenFence("Run:\n```bash\nnpm test\n```\nDone."))
        #expect(ShellBlocks.endsInOpenFence("```bash\nls\n```\nthen\n~~~sh\nmake"))
        // A shorter or different fence inside does not close it.
        #expect(ShellBlocks.endsInOpenFence("````md\n```bash\nls\n```\n"))
        #expect(!ShellBlocks.endsInOpenFence("````md\n```bash\nls\n```\n````"))
        // Fences in list items and quotes count too; inline code does not.
        #expect(!ShellBlocks.endsInOpenFence("- step\n  ```sh\n  ls\n  ```"))
        #expect(!ShellBlocks.endsInOpenFence("Use ``` `code` ``` inline"))
        #expect(!ShellBlocks.endsInOpenFence("No code here."))
    }

    // MARK: Records

    @Test func aRunEndsCompletedFailedOrStopped() {
        var done = ShellBlockRun(key: "e#0", command: "true")
        #expect(done.state == .running && done.endedAt == nil)
        done.finish(exitCode: 0, signal: nil, stopped: false, at: "t")
        #expect(done.state == .completed && done.exitCode == 0 && done.endedAt == "t")

        var failed = ShellBlockRun(key: "e#0", command: "false")
        failed.finish(exitCode: 2, signal: nil, stopped: false)
        #expect(failed.state == .failed && failed.exitCode == 2)

        var killed = ShellBlockRun(key: "e#0", command: "sleep 9")
        killed.finish(exitCode: nil, signal: SIGTERM, stopped: false)
        #expect(killed.state == .failed && killed.exitCode == 128 + Int(SIGTERM))

        var stopped = ShellBlockRun(key: "e#0", command: "sleep 9")
        stopped.finish(exitCode: nil, signal: SIGINT, stopped: true)
        #expect(stopped.state == .stopped)

        // Only a running record ends.
        stopped.finish(exitCode: 0, signal: nil, stopped: false)
        #expect(stopped.state == .stopped)

        var unstarted = ShellBlockRun(key: "e#0", command: "nope")
        unstarted.finish(exitCode: nil, signal: nil, stopped: false)
        #expect(unstarted.state == .failed && unstarted.exitCode == nil)
    }

    @Test func outputKeepsItsLast16KB() {
        var run = ShellBlockRun(key: "e#0", command: "yes")
        run.append(String(repeating: "a", count: ShellBlockRun.maximumOutputBytes))
        run.append("tail")
        #expect(run.output.utf8.count == ShellBlockRun.maximumOutputBytes)
        #expect(run.output.hasSuffix("tail"))
        // A cut never splits a character.
        let korean = ShellBlockRun.tail(String(repeating: "가", count: 10), maximumBytes: 7)
        #expect(korean == "가가")
        #expect(ShellBlockRun.tail("short") == "short")
    }

    @Test func restoreTurnsARunningRecordInterrupted() {
        let workspace = Workspace(id: "w", name: "W", path: "/tmp")
        var session = RunSession(id: "s", workspaceId: "w", title: "Claude", logs: [LogEntry(id: "e", kind: "assistant", text: "```bash\nls\n```")])
        var done = ShellBlockRun(key: "e#0", command: "ls")
        done.finish(exitCode: 0, signal: nil, stopped: false)
        session.shellRuns = [done, ShellBlockRun(key: "e#1", command: "sleep 100")]
        let saved = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: false)
        #expect(saved.sessions.first?.shellRuns?.map(\.state) == [.completed, .running])
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: true)
        #expect(restored.sessions.first?.shellRuns?.map(\.state) == [.completed, .interrupted])
        #expect(restored.sessions.first?.shellRuns?.last?.endedAt != nil)
    }

    @Test func normalizedKeepsOneRunPerKeyWithinTheBudget() {
        var budget = 1_000_000
        var first = ShellBlockRun(key: "e#0", command: "old"); first.output = "x"
        let second = ShellBlockRun(key: "e#0", command: "new")
        var long = ShellBlockRun(key: "e#1", command: "cat"); long.output = String(repeating: "b", count: 40_000)
        let kept = ShellBlockRun.normalized([first, long, second, ShellBlockRun(key: "", command: "x")], restoring: false, budget: &budget)
        #expect(kept?.map(\.command) == ["cat", "new"])
        #expect(kept?.first?.output.utf8.count == ShellBlockRun.maximumOutputBytes)
        #expect(budget == 1_000_000 - ShellBlockRun.maximumOutputBytes - 3 - 3)
        var empty = 0
        #expect(ShellBlockRun.normalized([second], restoring: false, budget: &empty) == nil)
    }

    @Test func runsRoundTripAndOldSavesReadNil() throws {
        var session = RunSession(workspaceId: "w", title: "t")
        session.shellRuns = [ShellBlockRun(key: "e#0", command: "ls", startedAt: "2026-01-01T00:00:00Z")]
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(decoded.shellRuns == session.shellRuns)
        var object = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        object.removeValue(forKey: "shellRuns")
        let old = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(old.shellRuns == nil)
        // A record this build cannot read is dropped, not the pane.
        object["shellRuns"] = [["key": "e#0", "command": "ls", "state": "exploded", "output": "", "startedAt": "t"]]
        let damaged = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(damaged.shellRuns == [])
    }

    // MARK: Runner

    @Test func startReturnsAHandleTheRunnerFollows() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextOutput = "hello\r\n"
        pane.nextRunning = false
        let runner = AgentTerminalRunner(pane: pane, clock: FakeAgentTerminalClock())
        let handle = try await runner.start(command: "echo hello")
        let result = try #require(await runner.readLatestOutput(handle: handle))
        #expect(result.status == .done && result.exitCode == 0 && result.output == "hello\n")
        #expect(pane.launched == ["echo hello"])
    }
}
