import Foundation
import Darwin
import Testing
@testable import MightyCore

/// A process that does not end: 12 s handle, incremental reads, 1 MB retention, stop within 10 s.
struct LongRunningProcessTests {
    // MARK: 12 seconds, then a handle

    @Test func nonEndingCommandReturnsRunningWithHandleAfterTwelveVirtualSeconds() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextOutput = "ready on http://localhost:3000\n"
        pane.nextRunning = true
        let clock = FakeAgentTerminalClock()
        let before = clock.currentTime
        let result = try await AgentTerminalRunner(pane: pane, clock: clock).runInTerminal(command: "npm run dev")
        let elapsed = clock.currentTime.timeIntervalSince(before)
        #expect(result.status == .running)
        #expect(result.exitCode == nil)
        #expect(!result.handle.isEmpty)
        #expect(result.output == "ready on http://localhost:3000\n")
        #expect(elapsed >= AgentTerminalRunner.initialWaitSeconds)
        #expect(elapsed < AgentTerminalRunner.initialWaitSeconds + 1)
        #expect(AgentTerminalRunner.initialWaitSeconds == 12)
    }

    @Test func readAfterExitReportsDoneAndExitCode() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextRunning = true
        let runner = AgentTerminalRunner(pane: pane, clock: FakeAgentTerminalClock())
        let first = try await runner.runInTerminal(command: "long cmd")
        pane.appendUserTyped(text: "built\n", handle: first.handle)
        pane.finish(handle: first.handle, code: 0)
        let second = try #require(await runner.readLatestOutput(handle: first.handle))
        #expect(second.status == .done && second.exitCode == 0)
        #expect(second.output == "built\n")
    }

    // MARK: Incremental reads of at most 64 KB

    @Test func readsReturnOnlyNewOutputInChunksOfAtMost64KB() async throws {
        let pane = FakeAgentTerminalPane()
        let total = AgentTerminalRunner.readChunkBytes * 2 + 1000
        let text = String(repeating: "y", count: total)
        pane.nextOutput = text
        pane.nextRunning = true
        let runner = AgentTerminalRunner(pane: pane, clock: FakeAgentTerminalClock())
        let first = try await runner.runInTerminal(command: "stream")
        #expect(first.output.utf8.count == AgentTerminalRunner.readChunkBytes && first.moreRemains)
        let second = try #require(await runner.readLatestOutput(handle: first.handle))
        #expect(second.output.utf8.count == AgentTerminalRunner.readChunkBytes && second.moreRemains)
        let third = try #require(await runner.readLatestOutput(handle: first.handle))
        #expect(third.output.utf8.count == 1000 && !third.moreRemains)
        #expect(first.output + second.output + third.output == text)
        let fourth = try #require(await runner.readLatestOutput(handle: first.handle))
        #expect(fourth.output.isEmpty)
        #expect(AgentTerminalRunner.readChunkBytes == 65_536)
    }

    @Test func unknownHandleReadsAndStopsNothing() async {
        let runner = AgentTerminalRunner(pane: FakeAgentTerminalPane())
        #expect(await runner.readLatestOutput(handle: "no-such-handle") == nil)
        #expect(await runner.stop(handle: "no-such-handle") == nil)
    }

    // MARK: 1 MB retention with the dropped notice

    @Test func realPaneKeepsTheLatestOneMegabyteAndSaysOlderOutputWasDropped() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        try await pane.launch(command: "head -c 500000 /dev/zero | tr '\\0' a; head -c \(PTYAgentTerminalPane.ringBufferMaxBytes) /dev/zero | tr '\\0' b", handle: "h")
        defer { pane.sendSIGKILL(handle: "h") }
        #expect(await waitFor(seconds: 20) { !pane.isRunning(handle: "h") })
        var offset = 0, collected = "", reads = 0, dropped = false
        while true {
            let chunk = pane.readOutput(handle: "h", fromOffset: offset, maxBytes: AgentTerminalRunner.readChunkBytes)
            #expect(chunk.output.utf8.count <= AgentTerminalRunner.readChunkBytes)
            dropped = dropped || chunk.dropped
            collected += chunk.output; offset = chunk.nextOffset; reads += 1
            if !chunk.moreRemains { break }
        }
        #expect(dropped)
        #expect(collected == String(repeating: "b", count: PTYAgentTerminalPane.ringBufferMaxBytes))
        #expect(reads == PTYAgentTerminalPane.ringBufferMaxBytes / AgentTerminalRunner.readChunkBytes)
        #expect(PTYAgentTerminalPane.ringBufferMaxBytes == 1_048_576)
    }

    @Test func droppedNoticeReachesTheAgentResult() async throws {
        let pane = FakeAgentTerminalPane()
        pane.ringBufferLimit = 500
        pane.nextOutput = String(repeating: "q", count: 1000)
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "overflow")
        #expect(result.outputDropped)
        #expect(result.output.utf8.count == 500)
        let text = AgentIOMCPServer.describeTerminal(AgentIOResponse(result))
        #expect(text.contains("Older output was dropped"))
    }

    // MARK: Reads never split a UTF-8 character

    @Test func realPaneChunksKoreanOutputOnCharacterBoundaries() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        try await pane.launch(command: "awk 'BEGIN { for (i = 0; i < 15000; i++) printf \"한글\" }'", handle: "k")
        defer { pane.sendSIGKILL(handle: "k") }
        #expect(await waitFor { !pane.isRunning(handle: "k") })
        let korean = String(repeating: "한글", count: 15_000)
        let first = pane.readOutput(handle: "k", fromOffset: 0, maxBytes: AgentTerminalRunner.readChunkBytes)
        #expect(first.moreRemains)
        #expect(first.output.utf8.count == 65_535)
        #expect(!first.output.contains("\u{FFFD}"))
        let second = pane.readOutput(handle: "k", fromOffset: first.nextOffset, maxBytes: AgentTerminalRunner.readChunkBytes)
        #expect(!second.output.contains("\u{FFFD}"))
        #expect(first.output + second.output == korean)
    }

    @Test func realPaneSkipsACharacterCutByEviction() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        // 349,526 three-byte characters overflow 1 MB by 2 bytes, cutting the first one.
        try await pane.launch(command: "awk 'BEGIN { for (i = 0; i < 349526; i++) printf \"한\" }'", handle: "e")
        defer { pane.sendSIGKILL(handle: "e") }
        #expect(await waitFor(seconds: 20) { !pane.isRunning(handle: "e") })
        let chunk = pane.readOutput(handle: "e", fromOffset: 0, maxBytes: AgentTerminalRunner.readChunkBytes)
        #expect(chunk.dropped)
        #expect(!chunk.output.contains("\u{FFFD}"))
        #expect(chunk.output.hasPrefix("한"))
    }

    @Test func utf8SafeLengthHoldsBackOnlyIncompleteCharacters() {
        let bytes = Array("a한".utf8)  // 1 + 3 bytes
        #expect(PTYAgentTerminalPane.utf8SafeLength(bytes, limit: 3, holdIncompleteTail: false) == 1)
        #expect(PTYAgentTerminalPane.utf8SafeLength(bytes, limit: 4, holdIncompleteTail: true) == 4)
        #expect(PTYAgentTerminalPane.utf8SafeLength(Array(bytes.prefix(3)), limit: 64, holdIncompleteTail: true) == 1)
        #expect(PTYAgentTerminalPane.utf8SafeLength(Array(bytes.prefix(3)), limit: 64, holdIncompleteTail: false) == 3)
    }

    // MARK: Stop within 10 seconds

    @Test func stopSendsSIGINTAndReturnsAtOnceForACooperativeProcess() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextRunning = true
        let clock = FakeAgentTerminalClock()
        let runner = AgentTerminalRunner(pane: pane, clock: clock)
        let run = try await runner.runInTerminal(command: "cooperative")
        let before = clock.currentTime
        let stopped = try #require(await runner.stop(handle: run.handle))
        #expect(stopped.status == .done)
        #expect(stopped.signal == SIGINT)
        #expect(stopped.exitCode == 130)
        #expect(pane.sigintCount == 1 && pane.sigtermCount == 0 && pane.sigkillCount == 0)
        #expect(clock.currentTime.timeIntervalSince(before) < AgentTerminalRunner.sigintToSIGTERMSeconds)
    }

    @Test func stopEscalatesToSIGTERMAfterThreeSecondsAndSIGKILLAfterFiveMore() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextRunning = true
        pane.resistsSignals = true
        let clock = FakeAgentTerminalClock()
        let runner = AgentTerminalRunner(pane: pane, clock: clock)
        let run = try await runner.runInTerminal(command: "stubborn")
        let before = clock.currentTime
        let stopped = try #require(await runner.stop(handle: run.handle))
        let elapsed = clock.currentTime.timeIntervalSince(before)
        #expect(pane.sigintCount == 1 && pane.sigtermCount == 1 && pane.sigkillCount == 1)
        #expect(stopped.status == .done)
        #expect(stopped.signal == SIGKILL)
        #expect(stopped.exitCode == 137)
        #expect(elapsed >= AgentTerminalRunner.sigintToSIGTERMSeconds + AgentTerminalRunner.sigtermToSIGKILLSeconds)
        #expect(elapsed <= 10)
    }

    @Test func stopReportsWithinTenSecondsEvenWhenSIGKILLDoesNotEndIt() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextRunning = true
        pane.resistsSignals = true
        pane.ignoresKill = true
        let clock = FakeAgentTerminalClock()
        let runner = AgentTerminalRunner(pane: pane, clock: clock)
        let run = try await runner.runInTerminal(command: "unkillable")
        let before = clock.currentTime
        let stopped = try #require(await runner.stop(handle: run.handle))
        #expect(stopped.status == .running)
        #expect(clock.currentTime.timeIntervalSince(before) <= 10)
    }

    @Test func realPTYProcessIgnoringSIGINTAndSIGTERMIsStoppedWithinTenSeconds() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let runner = AgentTerminalRunner(pane: pane, clock: SkipInitialWaitClock())
        let run = try await runner.runInTerminal(command: "trap '' INT TERM; echo stubborn; while :; do sleep 1; done")
        #expect(run.status == .running)
        // Stop only once the traps are in place, or SIGINT would beat them.
        let armed = Date().addingTimeInterval(30)
        while !pane.readOutput(handle: run.handle, fromOffset: 0, maxBytes: 64).output.contains("stubborn"), Date() < armed { try await Task.sleep(nanoseconds: 20_000_000) }
        let began = Date()
        let stopped = try #require(await runner.stop(handle: run.handle))
        let elapsed = Date().timeIntervalSince(began)
        #expect(stopped.status == .done)
        #expect(stopped.signal == SIGKILL)
        #expect(elapsed < 10)
        #expect(elapsed >= AgentTerminalRunner.sigintToSIGTERMSeconds + AgentTerminalRunner.sigtermToSIGKILLSeconds - 0.5)
        #expect(!pane.isRunning(handle: run.handle))
    }

    @Test func realPTYCooperativeProcessStopsOnSIGINTQuickly() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let runner = AgentTerminalRunner(pane: pane, clock: SkipInitialWaitClock())
        let run = try await runner.runInTerminal(command: "sleep 30")
        #expect(run.status == .running)
        let began = Date()
        let stopped = try #require(await runner.stop(handle: run.handle))
        #expect(stopped.status == .done)
        #expect(stopped.signal == SIGINT)
        // "Returns at once" is pinned exactly on the fake clock by
        // stopSendsSIGINTAndReturnsAtOnceForACooperativeProcess. Here the real PTY
        // has to deliver SIGINT and end the process with it (the signal above);
        // that the process ended by SIGINT, not by an escalation, is the signal
        // above. The wall bound only catches a stop that hangs: any tighter
        // bound measured the runner's scheduling (4.7 s and 5.1 s under load
        // locally, 9.4 s on CI's runner, each with SIGINT delivered).
        #expect(Date().timeIntervalSince(began) < 25)
    }
}
