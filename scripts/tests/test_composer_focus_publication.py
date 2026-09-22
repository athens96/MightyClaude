#!/usr/bin/env python3
"""Exercise SessionPaneView's exact native-focus callback without activating UI.

This is a callback contract with an inert store, not a SwiftUI/AppStore end-to-end
check. Native AppKit view containment and hidden-ancestor behavior are real.
The negative control reinstates the former Bool.onChange delivery semantics.
"""
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'native/macos/Sources/MightyClaude/SessionPaneView.swift'

FIXTURE = r'''
import AppKit
import Foundation
@MainActor final class FixtureWindow: NSWindow {
    var owner: NSResponder?
    override var firstResponder: NSResponder? { owner }
}
struct SessionFixture { var id = "composer-pane"; var workspaceId = "workspace" }
struct SnapshotFixture { var activeWorkspaceId: String? = "workspace"; var activeSessionId: String? = "other-pane" }
struct GroupFixture { var selectedSessionId: String? = "composer-pane" }
@MainActor final class LayoutFixture {
    var current: GroupFixture? = GroupFixture()
    func group(containing id: String) -> GroupFixture? { current }
}
@MainActor final class StoreFixture {
    var snapshot = SnapshotFixture()
    var layout = LayoutFixture()
    var selections: [String] = []
    func layoutForWorkspace(_ id: String) -> LayoutFixture? { layout }
    func selectSession(_ id: String) { selections.append(id); snapshot.activeSessionId = id }
}
@MainActor final class ControllerFixture { weak var editor: NSTextView? }
@MainActor final class CallbackFixture {
    let window = FixtureWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 100), styleMask: .borderless, backing: .buffered, defer: true)
    let host = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 100))
    let editor = NSTextView(frame: NSRect(x: 0, y: 0, width: 320, height: 100))
    let store = StoreFixture()
    let composerInput = ControllerFixture()
    var session = SessionFixture()
    var composerFocused = false
    init() {
        window.isReleasedWhenClosed = false
        window.contentView = host; host.addSubview(editor)
        window.owner = editor; composerInput.editor = editor
    }
    func cleanup() { window.owner = nil; editor.removeFromSuperview(); window.contentView = nil; window.close() }
    // EXACT_CALLBACK
    func deliver(_ focused: Bool) { composerFocusChanged(focused) }
}
'''

CHECKS = r'''
@main struct Main {
    @MainActor static func main() throws {
        let app = NSApplication.shared
        let wasActive = app.isActive
        let keyWindow = app.keyWindow
        var report: [String: Bool] = [:]
        do {
            let f = CallbackFixture(); defer { f.cleanup() }
            f.deliver(true)
            report["initialTrueSelectsOwningPane"] = f.store.snapshot.activeSessionId == f.session.id && f.store.selections.count == 1
            f.store.snapshot.activeSessionId = "different-pane"
            f.deliver(true)
            report["repeatedTrueReselectsAfterActivePaneChanged"] = f.store.snapshot.activeSessionId == f.session.id && f.store.selections.count == 2
            f.deliver(true)
            report["eachValidNativeTrueIsDelivered"] = f.store.selections.count == 3
            let before = f.store.selections.count
            f.deliver(false)
            report["falseOnlyUpdatesFocusFlag"] = !f.composerFocused && f.store.selections.count == before
            report["callbackDoesNotChangeNativeResponder"] = f.window.firstResponder === f.editor
        }
        func rejects(_ name: String, _ arrange: (CallbackFixture) -> Void) {
            let f = CallbackFixture(); defer { f.cleanup() }
            arrange(f); f.deliver(true)
            report[name] = f.store.selections.isEmpty && f.store.snapshot.activeSessionId == "other-pane"
        }
        rejects("differentWorkspaceCannotSelect") { $0.store.snapshot.activeWorkspaceId = "other-workspace" }
        rejects("unselectedTabCannotSelect") { $0.store.layout.current?.selectedSessionId = "other-pane" }
        rejects("missingPaneGroupCannotSelect") { $0.store.layout.current = nil }
        rejects("hiddenEditorCannotSelect") { $0.editor.isHidden = true }
        rejects("hiddenAncestorCannotSelect") { $0.host.isHidden = true }
        rejects("detachedEditorCannotSelect") { $0.editor.removeFromSuperview() }
        rejects("newResponderPreventsStaleSelection") { $0.window.owner = $0.host }
        rejects("missingControllerEditorCannotSelect") { $0.composerInput.editor = nil }
        report["applicationActivationAndKeyWindowUntouched"] = app.isActive == wasActive && app.keyWindow === keyWindow
        print(String(decoding: try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys]), as: UTF8.self))
    }
}
'''


def extract_callback(source):
    marker = '    private func composerFocusChanged(_ focused: Bool) {'
    start = source.index(marker)
    opening = source.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        if source[end] == '{': depth += 1
        elif source[end] == '}': depth -= 1
        end += 1
    return source[start:end]


@unittest.skipUnless(sys.platform == 'darwin', 'Requires native macOS AppKit')
class ComposerFocusPublicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix='mighty-composer-focus-')
        cls.callback = extract_callback(SOURCE.read_text())
        cls.env = os.environ.copy()
        cls.env.setdefault('DEVELOPER_DIR', '/Library/Developer/CommandLineTools')

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def run_checks(self, name, callback):
        folder = Path(self.scratch.name)
        source = folder / (name + '.swift')
        binary = folder / name
        source.write_text(FIXTURE.replace('    // EXACT_CALLBACK', callback) + CHECKS)
        result = subprocess.run(['xcrun', 'swiftc', '-parse-as-library', '-target', platform.machine() + '-apple-macos14.0', '-module-cache-path', str(folder / 'module-cache'), str(source), '-o', str(binary)], env=self.env, text=True, capture_output=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stderr)
        executed = subprocess.run([str(binary)], text=True, capture_output=True, timeout=20)
        self.assertEqual(executed.returncode, 0, executed.stderr)
        return json.loads(executed.stdout)

    def test_exact_current_callback_handles_repeated_focus(self):
        report = self.run_checks('current', self.callback)
        self.assertEqual(len(report), 14)
        self.assertFalse([name for name, passed in report.items() if not passed], report)

    def test_former_boolean_change_delivery_loses_repeated_true(self):
        # SwiftUI onChange delivers only when the Bool value changes. Recreate
        # that boundary around the otherwise identical production callback.
        baseline = self.callback.replace('        composerFocused = focused', '        guard composerFocused != focused else { return }\n        composerFocused = focused', 1)
        self.assertNotEqual(baseline, self.callback)
        report = self.run_checks('former-onchange', baseline)
        failed = {name for name, passed in report.items() if not passed}
        self.assertEqual(failed, {'repeatedTrueReselectsAfterActivePaneChanged', 'eachValidNativeTrueIsDelivered'})


if __name__ == '__main__':
    unittest.main(verbosity=2)
