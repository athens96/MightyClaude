import Foundation
import Testing
@testable import MightyCore

/// A command in an agent pane's terminal waiting for input becomes a card the
/// user answers from the pane or the phone.
@Suite struct TerminalPromptTests {
    @Test func yesNoQuestionsAndTheirDefault() throws {
        let eas = try #require(TerminalPromptDetector.detect("✔ Using remote Android credentials (Expo server)\n? Generate a new Android Keystore? › (Y/n)"))
        #expect(eas.kind == .yesNo && eas.defaultYes == true && eas.text == "? Generate a new Android Keystore? › (Y/n)")
        #expect(TerminalPromptDetector.detect("Overwrite the file? [y/N] ")?.defaultYes == false)
        let plain = try #require(TerminalPromptDetector.detect("Continue? (yes/no) "))
        #expect(plain.kind == .yesNo && plain.defaultYes == nil)
        #expect(TerminalPromptDetector.detect("Proceed (y/n)?")?.kind == .yesNo)
    }

    @Test func secretsTextAndNotPrompts() {
        #expect(TerminalPromptDetector.detect("Enter passphrase for key '/Users/me/.ssh/id_ed25519': ")?.kind == .secret)
        #expect(TerminalPromptDetector.detect("[sudo] password for me:")?.kind == .secret)
        #expect(TerminalPromptDetector.detect("비밀번호: ")?.kind == .secret)
        #expect(TerminalPromptDetector.detect("Project name:")?.kind == .text)
        #expect(TerminalPromptDetector.detect("? What is your app named? › my-app")?.kind == nil, "a prefilled answer is not a question mark at the end")
        #expect(TerminalPromptDetector.detect("? Package name ›")?.kind == .text)
        // Output that ended mid-line without asking, a line still loading, a finished line.
        #expect(TerminalPromptDetector.detect("Downloading 45%") == nil)
        #expect(TerminalPromptDetector.detect("Compressing project files...") == nil)
        #expect(TerminalPromptDetector.detect("Build finished.\n") == nil)
        #expect(TerminalPromptDetector.detect("") == nil)
    }

    @Test func aListMovedWithTheArrowKeys() throws {
        let tail = "? Select a build profile › - Use arrow-keys. Return to submit.\n❯   preview\n    production\n    development"
        let list = try #require(TerminalPromptDetector.detect(tail))
        #expect(list.kind == .choice && list.text.hasPrefix("? Select a build profile"))
        #expect(list.options == ["❯   preview", "production", "development"])
        // Lines without a marked choice are no list.
        #expect(TerminalPromptDetector.detect("? Done\n  a\n  b") == nil)
    }

    @Test func answersTypeWhatTheTerminalWouldGet() {
        #expect(TerminalPromptAnswer.yes.input == "y\r" && TerminalPromptAnswer.no.input == "n\r")
        #expect(TerminalPromptAnswer.text("my-app").input == "my-app\r")
        #expect(TerminalPromptAnswer.text("one\nrm -rf /").input == "one\r", "one answer is one line")
        #expect(TerminalPromptAnswer.key(.down).input == "\u{1B}[B" && TerminalPromptAnswer.key(.enter).input == "\r")
    }

    @Test func thePhonesAnswerIsReadStrictly() throws {
        #expect(MobileTerminalPromptAnswer(promptId: "h1:40", answer: "yes").terminalAnswer == .yes)
        #expect(MobileTerminalPromptAnswer(promptId: "h1:40", answer: "down").terminalAnswer == .key(.down))
        #expect(MobileTerminalPromptAnswer(promptId: "h1:40", answer: "text", text: "my-app").terminalAnswer == .text("my-app"))
        #expect(MobileTerminalPromptAnswer(promptId: "h1:40", answer: "text").terminalAnswer == nil)
        #expect(MobileTerminalPromptAnswer(promptId: "h1:40", answer: "text", text: String(repeating: "a", count: 4_097)).terminalAnswer == nil)
        #expect(MobileTerminalPromptAnswer(promptId: "h1:40", answer: "rm -rf").terminalAnswer == nil)
        #expect(MobileTerminalPromptAnswer(promptId: "", answer: "yes").terminalAnswer == nil)
        #expect(MobileTerminalPromptAnswer(promptId: "a\nb", answer: "yes").terminalAnswer == nil)
        // The card travels to the phone as plain JSON.
        let prompt = TerminalPrompt(id: "h1:40", handle: "h1", text: "Continue? (Y/n)", kind: .yesNo, defaultYes: true)
        let json = try #require(String(data: JSONEncoder().encode(prompt), encoding: .utf8))
        #expect(json.contains(#""kind":"yesNo""#) && json.contains(#""defaultYes":true"#))
    }

    @Test func theMonitorWaitsForQuietOutputAndHidesAnAnsweredPrompt() {
        let pane = FakePromptPane()
        var clock = Date(timeIntervalSince1970: 1_000)
        let monitor = TerminalPromptMonitor(quietSeconds: 0.8, now: { clock })
        pane.output["h1"] = "\u{1B}[32m✔\u{1B}[0m Ready\r\n? Generate a new Android Keystore? › (Y/n)"
        #expect(monitor.poll(pane) == nil, "just printed: not quiet yet")
        clock += 1
        let prompt = monitor.poll(pane)
        #expect(prompt?.kind == .yesNo && prompt?.handle == "h1" && prompt?.text == "? Generate a new Android Keystore? › (Y/n)")

        // Answered: hidden while nothing new is printed, back as a new prompt after more output.
        monitor.answered(prompt!)
        clock += 5
        #expect(monitor.poll(pane) == nil)
        pane.output["h1", default: ""] += "y\r\nPassword: "
        _ = monitor.poll(pane); clock += 1
        let next = monitor.poll(pane)
        #expect(next?.kind == .secret && next?.id != prompt?.id)

        // A command that ended waits for nothing.
        pane.running = []
        #expect(monitor.poll(pane) == nil)
    }
}

/// An agent terminal pane whose output a test writes.
private final class FakePromptPane: AgentTerminalPane, @unchecked Sendable {
    var output: [String: String] = [:]
    var running: [String]?
    var typed: [String] = []

    func launch(command: String, handle: String) async throws {}
    func readOutput(handle: String, fromOffset: Int, maxBytes: Int) -> (output: String, dropped: Bool, moreRemains: Bool, nextOffset: Int) {
        let bytes = Array((output[handle] ?? "").utf8)
        let start = min(fromOffset, bytes.count)
        return (String(decoding: bytes[start...], as: UTF8.self), false, false, bytes.count)
    }
    func isRunning(handle: String) -> Bool { runningHandles().contains(handle) }
    func exitCode(handle: String) -> Int32? { nil }
    func terminationSignal(handle: String) -> Int32? { nil }
    func runningHandles() -> [String] { running ?? output.keys.sorted() }
    func sendSIGINT(handle: String) {}
    func sendSIGTERM(handle: String) {}
    func sendSIGKILL(handle: String) {}
    func appendUserTyped(text: String, handle: String) { typed.append(text) }
}
