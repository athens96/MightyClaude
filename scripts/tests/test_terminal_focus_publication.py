#!/usr/bin/env python3
"""Exercise the real session callback with inert terminal and window fixtures.

No PTY is created, no window is ordered or activated, and no clipboard or input
source is accessed. AppKit view containment is real; focus/window state is
controlled so every race can be replayed without depending on the user's UI.
"""

import json
from pathlib import Path
import platform
import re
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "native/macos/Sources/MightyClaude/LocalTerminalSession.swift"

FIXTURES = r"""
import AppKit
import Combine

@MainActor final class ApplicationFixture {
    var isActive = true
    var keyWindow: NSWindow?
    var modalWindow: NSWindow?
}
@MainActor let NSApp = ApplicationFixture()

@MainActor final class WindowFixture: NSWindow {
    var simulatedResponder: NSResponder?
    var simulatedKey = true
    var simulatedVisible = true
    var simulatedMiniaturized = false
    var simulatedSheet: NSWindow?
    override var firstResponder: NSResponder? { simulatedResponder }
    override var isKeyWindow: Bool { simulatedKey }
    override var isVisible: Bool { simulatedVisible }
    override var isMiniaturized: Bool { simulatedMiniaturized }
    override var attachedSheet: NSWindow? { simulatedSheet }
}

// These inert terminal interfaces keep the actual LocalTerminalSession logic
// under test while making it impossible to start Ghostty or read user content.
@MainActor class AppTerminalView: NSView {
    var configuration: TerminalSurfaceOptions?
    weak var delegate: AnyObject?
    var controller: TerminalController?
    var clipped = false
    override var visibleRect: NSRect { clipped ? .zero : bounds }
    func setSurfaceVisible(_ visible: Bool) {}
    func performBindingAction(_ action: String) -> Bool { false }
    func paste(text: String) -> Bool { false }
    func sendKey(_ key: TerminalKey) -> Bool { false }
}
@MainActor final class HostTerminalView: AppTerminalView {
    var readSelectedText: (() -> String?)?
}
enum TerminalKey { case enter }
@MainActor final class TerminalController { var lastConfigurationIssue: String? }
@MainActor final class TerminalSurface {
    func hasSelection() -> Bool { false }
    func readSelection() -> String? { nil }
}
struct TerminalGridMetrics {}
struct TerminalSurfaceOptions {
    enum Backend { case exec }
    init(backend: Backend, fontSize: Int, workingDirectory: String,
         envVars: [String: String], waitAfterCommand: Bool,
         resizeThrottleMilliseconds: Int) {}
}
protocol TerminalSurfaceTitleDelegate {}
protocol TerminalSurfacePwdDelegate {}
protocol TerminalSurfaceFocusDelegate {}
protocol TerminalSurfaceGridResizeDelegate {}
protocol TerminalSurfaceCloseDelegate {}
protocol TerminalSurfaceLifecycleDelegate {}
protocol TerminalSurfaceClipboardConfirmationDelegate {}
struct TerminalClipboardConfirmationRequest {
    enum Kind { case paste }
    let kind: Kind
    func respond(allow: Bool) {}
}
struct TerminalInput { var text: String }
protocol TerminalPasteSink {
    func paste(text: String) -> Bool
    func sendEnter() -> Bool
}
enum TerminalInputPolicy {
    enum Outcome { case pasted, pastedAndRan, refused(String), failed }
    static func apply(_ input: TerminalInput, to: some TerminalPasteSink) -> Outcome { .failed }
}

@MainActor final class Fixture {
    let window = WindowFixture(contentRect: NSRect(x: 0, y: 0, width: 300, height: 200),
                               styleMask: [.borderless], backing: .buffered, defer: true)
    let host = NSView(frame: NSRect(x: 0, y: 0, width: 300, height: 200))
    var publications = 0
    var terminal: LocalTerminalSession!
    var generation: UInt64 = 0

    init() {
        NSApp.isActive = true
        NSApp.keyWindow = window
        NSApp.modalWindow = nil
        window.contentView = host
        terminal = LocalTerminalSession(id: "isolated-focus-fixture", directory: "/tmp",
                                        controller: TerminalController(), smoke: true,
                                        statusChanged: { _ in }, focused: { [weak self] in
                                            self?.publications += 1
                                        }, closeRequested: {})
        generation = terminal.claimPresentation(host)
        terminal.view.frame = host.bounds
        host.addSubview(terminal.view)
        window.simulatedResponder = terminal.view
    }

    func cleanup() {
        terminal.dispose()
        NSApp.keyWindow = nil
        NSApp.modalWindow = nil
        window.contentView = nil
    }
}
"""

CHECKS = r"""
@main struct FocusRegressionChecks {
    @MainActor static func drain() async {
        await withCheckedContinuation { continuation in
            DispatchQueue.main.async { continuation.resume() }
        }
    }

    @MainActor static func main() async throws {
        var result: [String: Bool] = [:]

        func check(_ name: String, expected: Int = 0,
                   mutate: (Fixture) -> Void = { _ in }) async {
            let fixture = Fixture()
            fixture.terminal.terminalDidChangeFocus(true)
            mutate(fixture)
            await drain()
            result[name] = fixture.publications == expected
            fixture.cleanup()
        }

        await check("validFocusPublishesOnce", expected: 1)
        await check("blurCancelsQueuedFocus") { $0.terminal.terminalDidChangeFocus(false) }
        await check("newEditorCancelsQueuedFocus") {
            $0.window.simulatedResponder = NSTextView(frame: .zero)
        }
        await check("newWindowCancelsQueuedFocus") { $0.window.contentView = nil }
        await check("releaseCancelsQueuedFocus") {
            $0.terminal.releasePresentation($0.host, generation: $0.generation)
        }
        await check("sameHostRemountCancelsQueuedFocus") {
            _ = $0.terminal.claimPresentation($0.host)
        }
        await check("newHostRemountCancelsQueuedFocus") {
            let host = NSView(frame: $0.host.bounds)
            _ = $0.terminal.claimPresentation(host)
            $0.window.contentView = host
            host.addSubview($0.terminal.view)
        }
        await check("disposeCancelsQueuedFocus") { $0.terminal.dispose() }
        await check("surfaceDetachCancelsQueuedFocus") { $0.terminal.terminalDidDetachSurface() }
        await check("inactiveAppCancelsQueuedFocus") { _ in NSApp.isActive = false }
        await check("otherKeyWindowCancelsQueuedFocus") { _ in NSApp.keyWindow = nil }
        await check("nonKeyWindowCancelsQueuedFocus") { $0.window.simulatedKey = false }
        await check("hiddenWindowCancelsQueuedFocus") { $0.window.simulatedVisible = false }
        await check("minimizedWindowCancelsQueuedFocus") { $0.window.simulatedMiniaturized = true }
        await check("hiddenTerminalCancelsQueuedFocus") { $0.terminal.view.isHidden = true }
        await check("hiddenHostCancelsQueuedFocus") { $0.host.isHidden = true }
        await check("clippedTerminalCancelsQueuedFocus") { $0.terminal.view.clipped = true }
        await check("modalCancelsQueuedFocus") { NSApp.modalWindow = $0.window }
        await check("sheetCancelsQueuedFocus") { $0.window.simulatedSheet = $0.window }
        await check("repeatedTruePublishesOnlyLatest", expected: 1) {
            $0.terminal.terminalDidChangeFocus(true)
        }
        await check("blurThenRefocusPublishesOnlyLatest", expected: 1) {
            $0.terminal.terminalDidChangeFocus(false)
            $0.terminal.terminalDidChangeFocus(true)
        }
        await check("staleLeaseReleasePreservesCurrentFocus", expected: 1) {
            $0.terminal.releasePresentation($0.host, generation: $0.generation &- 1)
        }

        // A callback cannot activate/focus anything itself, even when the
        // responder changes without receiving Ghostty's matching false event.
        let fixture = Fixture()
        let editor = NSTextView(frame: .zero)
        fixture.terminal.terminalDidChangeFocus(true)
        fixture.window.simulatedResponder = editor
        await drain()
        result["callbackLeavesNewResponderUntouched"] = fixture.window.firstResponder === editor
        fixture.cleanup()

        let encoded = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
        print(String(decoding: encoded, as: UTF8.self))
    }
}
"""


@unittest.skipUnless(sys.platform == "darwin", "Requires AppKit on macOS")
class TerminalFocusPublicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix="mighty-terminal-focus-")
        cls.swiftc = subprocess.check_output(["xcrun", "--find", "swiftc"], text=True).strip()
        cls.sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
        cls.source = SOURCE.read_text()

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def run_checks(self, source: str, name: str) -> dict[str, bool]:
        path = Path(self.scratch.name)
        harness = path / f"{name}.swift"
        binary = path / name
        source = source.replace("import GhosttyTerminal\n", "").replace("import MightyCore\n", "")
        harness.write_text(FIXTURES + source + CHECKS)
        compile_result = subprocess.run(
            [self.swiftc, "-parse-as-library", "-sdk", self.sdk,
             "-target", f"{platform.machine()}-apple-macosx14.0",
             "-module-cache-path", str(path / "module-cache"),
             str(harness), "-o", str(binary)], text=True, capture_output=True, timeout=90,
        )
        self.assertEqual(compile_result.returncode, 0, compile_result.stderr)
        run = subprocess.run([str(binary)], text=True, capture_output=True, timeout=30)
        self.assertEqual(run.returncode, 0, run.stderr)
        return json.loads(run.stdout)

    def test_current_callback_cancels_stale_publication(self):
        result = self.run_checks(self.source, "current")
        self.assertEqual(len(result), 23)
        self.assertFalse([name for name, passed in result.items() if not passed], result)

    def test_original_callback_reproduces_races(self):
        # Keep all other production code unchanged. Restoring the original
        # callback must reproduce the races, proving this suite detects the bug.
        original, count = re.subn(
            r"    func terminalDidChangeFocus\(_ focused: Bool\) \{.*?(?=    func terminalDidClose)",
            "    func terminalDidChangeFocus(_ focused: Bool) { if focused { publish { $0.focused() } } }\n",
            self.source, count=1, flags=re.DOTALL,
        )
        self.assertEqual(count, 1)
        result = self.run_checks(original, "original-callback")
        for name in ["blurCancelsQueuedFocus", "newEditorCancelsQueuedFocus",
                     "sameHostRemountCancelsQueuedFocus", "releaseCancelsQueuedFocus",
                     "inactiveAppCancelsQueuedFocus", "repeatedTruePublishesOnlyLatest"]:
            self.assertFalse(result[name], name)
        self.assertTrue(result["validFocusPublishesOnce"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
