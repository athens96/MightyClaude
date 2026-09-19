import Foundation
import Testing
@testable import MightyCore

private final class SpySink: TerminalPasteSink, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var pasted: [String] = []
    private(set) var enters = 0
    var pasteSucceeds = true
    func paste(text: String) -> Bool { lock.lock(); defer { lock.unlock() }; pasted.append(text); return pasteSucceeds }
    func sendEnter() -> Bool { lock.lock(); defer { lock.unlock() }; enters += 1; return true }
}

struct TerminalInputPolicyTests {
    @Test func installIsNeverRun() {
        let sink = SpySink()
        let install = StyleFixtures.bundled("ouroboros").manifest.install!
        // Everything that came from a manifest is pasted and left there (§1.5).
        let outcome = TerminalInputPolicy.apply(TerminalInput(text: install.command, autoRun: false), to: sink)
        #expect(outcome == .pasted && sink.pasted == [install.command] && sink.enters == 0)
        // Only a command the app wrote itself may run on its own.
        let app = SpySink()
        #expect(TerminalInputPolicy.apply(TerminalInput(text: "claude login", autoRun: true), to: app) == .pastedAndRan)
        #expect(app.pasted == ["claude login"] && app.enters == 1)
    }

    @Test func aLineBreakStopsThePasteAltogether() {
        for separator in ["\u{000A}", "\u{000D}", "\u{2028}", "\u{2029}"] {
            let sink = SpySink()
            let outcome = TerminalInputPolicy.apply(TerminalInput(text: "rm -rf /" + separator + "echo done", autoRun: true), to: sink)
            #expect(outcome == .refused(TerminalInputPolicy.newlineRefusal))
            #expect(sink.pasted.isEmpty && sink.enters == 0)
        }
        let failing = SpySink(); failing.pasteSucceeds = false
        #expect(TerminalInputPolicy.apply(TerminalInput(text: "ls", autoRun: true), to: failing) == .failed && failing.enters == 0)
    }

    @Test func bothBundledInstallCommandsAreSafeToPaste() {
        for id in ["ouroboros", "paperthin"] {
            let command = StyleFixtures.bundled(id).manifest.install?.command ?? ""
            #expect(!command.isEmpty && !TerminalInputPolicy.hasLineBreak(command) && !StyleText.containsBanned(command))
        }
    }
}
