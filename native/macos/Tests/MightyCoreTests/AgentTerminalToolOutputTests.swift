import Foundation
import Testing
@testable import MightyCore

/// The text run_in_terminal and read_latest_output hand the agent: escape
/// sequences stripped, CR LF and bare CR turned into LF. The pane and its
/// buffers stay raw, and the 64 KB read limit counts raw bytes.
struct AgentTerminalToolOutputTests {
    private func clean(_ raw: String) -> String {
        var cleaner = AgentTerminalOutputCleaner()
        return cleaner.clean(raw, final: true)
    }

    // MARK: Escape sequences

    @Test func csiColourAndCursorSequencesAreStripped() {
        #expect(clean("\u{1B}[1;31mred\u{1B}[0m plain\u{1B}[K\n") == "red plain\n")
        #expect(clean("\u{1B}[?25l\u{1B}[2J\u{1B}[H\u{1B}[38;5;208mx\u{1B}[m") == "x")
    }

    @Test func oscEndedByBelOrStringTerminatorIsStripped() {
        #expect(clean("\u{1B}]0;title\u{07}after") == "after")
        #expect(clean("\u{1B}]8;;https://example.com\u{1B}\\link\u{1B}]8;;\u{1B}\\ done") == "link done")
    }

    @Test func otherEscSequencesAndStringsAreStripped() {
        // Charset designation, keypad mode, index, DCS and APC strings.
        #expect(clean("\u{1B}(Babc\u{1B}=\u{1B}Dd\u{1B}P1$r0m\u{1B}\\e\u{1B}_hidden\u{1B}\\f") == "abcdef")
        // C1 CSI and OSC as single code points.
        #expect(clean("\u{9B}31mx\u{9D}t\u{07}y") == "xy")
    }

    @Test func textAfterAnInterruptedSequenceIsKept() {
        // A newline inside a CSI cuts it short and stays in the text.
        #expect(clean("a\u{1B}[3\nb") == "a\nb")
    }

    @Test func nonASCIITextIsKept() {
        #expect(clean("\u{1B}[32m한글 ✓ 🙂\u{1B}[0m\r\n") == "한글 ✓ 🙂\n")
    }

    // MARK: Line endings

    @Test func crlfBecomesLF() {
        #expect(clean("one\r\ntwo\r\n") == "one\ntwo\n")
    }

    @Test func bareCRProgressRedrawsBecomeLines() {
        #expect(clean("10%\r20%\r30%\r\ndone\n") == "10%\n20%\n30%\ndone\n")
        #expect(clean("\r\u{1B}[K50%\r\u{1B}[K100%\n") == "\n50%\n100%\n")
    }

    @Test func repeatedCRBeforeLFIsOneLineEnd() {
        #expect(clean("a\r\r\nb") == "a\nb")
    }

    @Test func trailingCRIsWrittenOnlyWhenTheOutputEnds() {
        var cleaner = AgentTerminalOutputCleaner()
        #expect(cleaner.clean("50%\r", final: false) == "50%")
        #expect(cleaner.clean("", final: true) == "\n")
    }

    // MARK: Pieces split across reads

    @Test func sequencesSplitAcrossReadsAreStrippedWhole() {
        var cleaner = AgentTerminalOutputCleaner()
        #expect(cleaner.clean("ab\u{1B}[3", final: false) == "ab")
        #expect(cleaner.clean("1mcd\u{1B}]0;ti", final: false) == "cd")
        #expect(cleaner.clean("tle\u{1B}", final: false) == "")
        #expect(cleaner.clean("\\ef", final: true) == "ef")
    }

    @Test func crlfSplitAcrossReadsIsOneLineEnd() {
        var cleaner = AgentTerminalOutputCleaner()
        #expect(cleaner.clean("x\r", final: false) == "x")
        #expect(cleaner.clean("\ny", final: true) == "\ny")
    }

    // MARK: Through the runner

    @Test func runResultIsCleanedForTheAgent() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextOutput = "\u{1B}[1mbuilt\u{1B}[0m\r\n\u{1B}]0;npm\u{07}ok\r\n"
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "npm run build")
        #expect(result.output == "built\nok\n")
        // The pane's own buffer, which the visible pane draws, stays raw.
        #expect(pane.readOutput(handle: result.handle, fromOffset: 0, maxBytes: 1024).output == pane.nextOutput)
    }

    @Test func readLatestOutputIsCleanedAndCarriesSplitsBetweenReads() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextOutput = "start\r"
        pane.nextRunning = true
        let runner = AgentTerminalRunner(pane: pane, clock: FakeAgentTerminalClock())
        let run = try await runner.runInTerminal(command: "npm run dev")
        #expect(run.status == .running)
        #expect(run.output == "start")
        pane.appendUserTyped(text: "\n\u{1B}[32mready\u{1B}[", handle: run.handle)
        let first = try #require(await runner.readLatestOutput(handle: run.handle))
        #expect(first.output == "\nready")
        pane.appendUserTyped(text: "0m\r40%\r", handle: run.handle)
        pane.finish(handle: run.handle, code: 0)
        let last = try #require(await runner.readLatestOutput(handle: run.handle))
        #expect(last.status == .done)
        #expect(last.output == "\n40%\n")
    }

    @Test func realPTYColourOutputReachesTheAgentPlainAndThePaneStaysRaw() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "printf '\\033[1;32mgreen\\033[0m\\n50%%\\r100%%\\n'")
        #expect(result.status == .done && result.exitCode == 0)
        #expect(result.output == "green\n50%\n100%\n")
        let raw = pane.readOutput(handle: result.handle, fromOffset: 0, maxBytes: 4_096).output
        #expect(raw.contains("\u{1B}[1;32mgreen\u{1B}[0m\r\n50%\r100%\r\n"))
    }

    @Test func readLimitCountsRawBytesBeforeCleaning() async throws {
        let pane = FakeAgentTerminalPane()
        // 4,000 coloured lines: 17 raw bytes each (68,000 in all), 7 once cleaned.
        let raw = String(repeating: "\u{1B}[31m" + "123456" + "\u{1B}[0m" + "\r\n", count: 4_000)
        #expect(raw.utf8.count == 68_000)
        pane.nextOutput = raw
        pane.nextRunning = true
        let runner = AgentTerminalRunner(pane: pane, clock: FakeAgentTerminalClock())
        let run = try await runner.runInTerminal(command: "colours")
        #expect(run.moreRemains)
        // The chunk took exactly 64 KB of raw terminal bytes, so less clean text.
        let consumedRaw = String(decoding: Data(raw.utf8).prefix(AgentTerminalRunner.readChunkBytes), as: UTF8.self)
        var reference = AgentTerminalOutputCleaner()
        #expect(run.output == reference.clean(consumedRaw, final: false))
        #expect(run.output.utf8.count < AgentTerminalRunner.readChunkBytes)
        var collected = run.output
        while true {
            let next = try #require(await runner.readLatestOutput(handle: run.handle))
            collected += next.output
            if !next.moreRemains { break }
        }
        pane.finish(handle: run.handle, code: 0)
        collected += try #require(await runner.readLatestOutput(handle: run.handle)).output
        #expect(collected == String(repeating: "123456\n", count: 4_000))
    }
}
