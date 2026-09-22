#!/usr/bin/env python3
"""Run the production input monitor with passive, deterministic AppKit fixtures.

The monitor, recovery coordinator, and symptom detector are compiled from the
repository. Windows are never ordered/activated. A fake application and recovery
environment intercept activation/focus requests; no input source or pasteboard
is modified, and temporary diagnostics contain only fixture data.
"""

import json
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SOURCES = [
    ROOT / "native/macos/Sources/MightyCore/InputMethodSymptom.swift",
    ROOT / "native/macos/Sources/MightyClaude/InputSessionRecoveryCoordinator.swift",
    ROOT / "native/macos/Sources/MightyClaude/InputMethodDiagnostics.swift",
]

FIXTURES = r"""
import AppKit
import Carbon
import Foundation

@MainActor final class ApplicationFixture: NSObject {
    var isActive = false
    var isHidden = false
    var keyWindow: NSWindow?
    var mainWindow: NSWindow?
    var modalWindow: NSWindow?
    var activationRequests = 0
    func activationPolicy() -> NSApplication.ActivationPolicy { .regular }
    func activate() { activationRequests += 1 }
}
@MainActor let NSApp = ApplicationFixture()

@MainActor final class HealthWindow: NSWindow {
    var owner: NSResponder?
    var simulatedKey = false
    var responderChanges = 0
    var keyRequests = 0
    override var isVisible: Bool { true }
    override var isKeyWindow: Bool { simulatedKey }
    override var firstResponder: NSResponder? { owner }
    override func makeFirstResponder(_ responder: NSResponder?) -> Bool {
        responderChanges += 1
        owner = responder
        return true
    }
    override func makeKey() { keyRequests += 1 }
}

@MainActor class HealthEditor: NSTextView {
    override var inputContext: NSTextInputContext? { nil }
    override var visibleRect: NSRect { bounds }
}
// Only the monitor's explicit commit helper references this type. The passive
// tests use HealthEditor, and never load or replace the actual composer.
@MainActor final class ComposerTextView: HealthEditor {
    func prepareForSubmission() {}
}

final class HealthEvent: NSEvent {
    var owner: NSWindow?
    var simulatedCharacters: String? = "a"
    var simulatedFlags: NSEvent.ModifierFlags = []
    override var window: NSWindow? { owner }
    override var type: NSEvent.EventType { .keyDown }
    override var modifierFlags: NSEvent.ModifierFlags { simulatedFlags }
    override var keyCode: UInt16 { 0 }
    override var characters: String? { simulatedCharacters }
}

@MainActor final class RecoveryDriver {
    var activationRequests = 0
    var scheduled: [() -> Void] = []
    var clock: TimeInterval = 100
    var contextMatches = true
    var environment: InputSessionRecoveryCoordinator.Environment {
        .init(appIsActive: { NSApp.isActive }, keyWindow: { NSApp.keyWindow }, modalWindow: { nil },
              contextIsCurrent: { [weak self] _ in self?.contextMatches == true },
              activate: { [weak self] in self?.activationRequests += 1 },
              makeKey: { ($0 as? HealthWindow)?.simulatedKey = true; NSApp.keyWindow = $0 },
              makeFirstResponder: { $0.makeFirstResponder($1) },
              now: { [weak self] in self?.clock ?? 100 },
              schedule: { [weak self] _, action in self?.scheduled.append(action) })
    }
}

@MainActor final class Fixture {
    static let draft = "PRIVATE_DRAFT_MARKER_한글_12345"
    static let title = "PRIVATE_WINDOW_TITLE_MARKER"
    static let rawEvent = "PRIVATE_RAW_EVENT_MARKER"
    let window = HealthWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 120),
                              styleMask: .borderless, backing: .buffered, defer: true)
    let editor = HealthEditor(frame: NSRect(x: 0, y: 0, width: 320, height: 120))
    let event = HealthEvent()
    let driver = RecoveryDriver()
    let coordinator: InputSessionRecoveryCoordinator
    let monitor: InputMethodMonitor
    var notices: [InputMethodMonitor.Problem] = []
    var recoveries = 0
    var inKey = false
    var noticeDuringKey = false

    init(directory: URL? = nil) {
        coordinator = InputSessionRecoveryCoordinator(environment: driver.environment, observesNotifications: false)
        monitor = InputMethodMonitor(recoveryCoordinator: coordinator, contextIsCurrent: { [weak driver] _ in driver?.contextMatches == true })
        NSApp.isActive = false
        NSApp.keyWindow = window
        NSApp.mainWindow = window
        NSApp.modalWindow = nil
        editor.isEditable = true
        editor.string = Self.draft
        editor.setSelectedRange(NSRange(location: 3, length: 0))
        window.contentView = editor
        window.title = Self.title
        window.owner = editor
        event.owner = window
        monitor.dataDirectory = directory
        monitor.record(Self.rawEvent)
        monitor.onProblem = { [weak self] problem in
            guard let self else { return }
            self.noticeDuringKey = self.noticeDuringKey || self.inKey
            self.notices.append(problem)
        }
        monitor.onRecovered = { [weak self] in
            guard let self else { return }
            self.noticeDuringKey = self.noticeDuringKey || self.inKey
            self.recoveries += 1
        }
    }

    func key(asciiInsertCallback: Bool = false) {
        inKey = true
        monitor.keyBegan(event, in: editor)
        if asciiInsertCallback {
            monitor.noteInsert("a", replacementRange: NSRange(location: NSNotFound, length: 0),
                               source: "com.apple.keylayout.ABC", in: editor)
        }
        monitor.keyEnded()
        inKey = false
    }

    func cleanup() {
        monitor.onProblem = nil
        monitor.onRecovered = nil
        monitor.clearProblem()
        coordinator.retireEditor(editor)
        window.owner = nil
        window.contentView = nil
        NSApp.keyWindow = nil
        NSApp.mainWindow = nil
        NSApp.modalWindow = nil
    }
}
"""

CHECKS = r"""
@main struct MonitorRegressionChecks {
    @MainActor static func drain() async {
        // Snapshot persistence queues one additional UI notice. Two handoffs
        // cover it deterministically without a timing-dependent sleep.
        for _ in 0..<2 {
            await withCheckedContinuation { continuation in
                DispatchQueue.main.async { continuation.resume() }
            }
        }
    }

    @MainActor static func main() async throws {
        var result: [String: Bool] = [:]
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-monitor-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let fixture = Fixture(directory: root.appendingPathComponent("health"))
        let monitor = fixture.monitor
        let originalSource = InputMethodMonitor.currentInputSourceID()
        let originalSelection = fixture.editor.selectedRanges
        let originalResponderChanges = fixture.window.responderChanges
        let originalActivationRequests = NSApp.activationRequests
        let originalRecoveryRequests = fixture.driver.activationRequests

        fixture.key(asciiInsertCallback: true)
        await drain()
        result["firstUnhealthyKeyDoesNotRaiseNotice"] = monitor.problem == nil && fixture.notices.isEmpty
        result["firstUnhealthyKeyDoesNotPersist"] = !FileManager.default.fileExists(atPath: monitor.dataDirectory!.path)
        fixture.key(asciiInsertCallback: true)
        result["repeatedASCIIIsRecognized"] = monitor.problem?.reason == .unavailableSession
        result["noticeAndSnapshotWaitForInputReturn"] = fixture.notices.isEmpty && !FileManager.default.fileExists(atPath: monitor.dataDirectory!.path)
        await drain()
        result["repeatedASCIIShowsRecoveryNotice"] = fixture.notices.last?.reason == .unavailableSession && !fixture.noticeDuringKey
        let directory = monitor.dataDirectory!.appendingPathComponent("diagnostics")
        let files = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        result["oneSnapshotIsPersisted"] = files.count == 1
        guard let file = files.first else { throw CocoaError(.fileNoSuchFile) }
        let data = try Data(contentsOf: file)
        let snapshot = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let serialized = String(decoding: data, as: UTF8.self)
        result["automaticSnapshotOmitsTextAndEvents"] = snapshot["editorString"] == nil && snapshot["recentEvents"] == nil
            && snapshot["keyWindow"] == nil && snapshot["mainWindow"] == nil
            && ![Fixture.draft, Fixture.title, Fixture.rawEvent].contains(where: serialized.contains)
        result["snapshotDeclaresNoAutomaticRecovery"] = snapshot["automaticRecovery"] as? Bool == false
        result["noticeReceivesItsSnapshot"] = fixture.notices.last?.file?.standardizedFileURL.path == file.standardizedFileURL.path
            && monitor.problem?.file?.standardizedFileURL.path == file.standardizedFileURL.path
        let mode = try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as! NSNumber
        result["snapshotModeIs0600"] = mode.intValue == 0o600
        let directoryMode = try FileManager.default.attributesOfItem(atPath: directory.path)[.posixPermissions] as! NSNumber
        result["diagnosticsDirectoryModeIs0700"] = directoryMode.intValue == 0o700
        for _ in 0..<8 { fixture.key() }
        await drain()
        result["repeatedKeysRespectSnapshotCooldown"] = try FileManager.default.contentsOfDirectory(atPath: directory.path).count == 1
        result["passiveObservationPreservesTextAndSelection"] = fixture.editor.string == Fixture.draft && fixture.editor.selectedRanges == originalSelection
        result["passiveObservationPreservesResponder"] = fixture.window.owner === fixture.editor
            && fixture.window.responderChanges == originalResponderChanges && fixture.window.keyRequests == 0
        result["passiveObservationNeverRequestsRecoveryOrActivation"] = fixture.driver.activationRequests == originalRecoveryRequests
            && NSApp.activationRequests == originalActivationRequests && fixture.coordinator.state == .idle
        result["passiveObservationPreservesInputSource"] = InputMethodMonitor.currentInputSourceID() == originalSource

        for _ in 0..<75 {
            NotificationCenter.default.post(name: NSWindow.didResignKeyNotification, object: fixture.window,
                                            userInfo: ["draft": Fixture.draft, "key": Fixture.rawEvent, "otherAppName": "PRIVATE_OTHER_APP_MARKER"])
        }
        let history = monitor.snapshot(editor: fixture.editor)["recentLifecycleEvents"] as! [[String: Any]]
        let allowed: Set<String> = ["time", "event", "appIsActive", "keyWindowNumber", "frontmostIsOwnApplication",
                                    "currentInputSource", "windowNumber", "windowClass", "windowIsKey", "windowIsVisible"]
        result["lifecycleHistoryIsBounded"] = history.count == 60
        result["lifecycleHistoryIsMetadataOnly"] = history.allSatisfy { Set($0.keys).isSubset(of: allowed) }
        let historyJSON = String(decoding: try JSONSerialization.data(withJSONObject: history), as: UTF8.self)
        result["lifecycleHistoryOmitsDraftKeysAndOtherAppNames"] = ![Fixture.draft, Fixture.rawEvent, Fixture.title, "PRIVATE_OTHER_APP_MARKER"].contains(where: historyJSON.contains)
        fixture.cleanup()

        let noInsert = Fixture()
        noInsert.event.simulatedCharacters = nil
        noInsert.key(); noInsert.key()
        result["missingInsertNoticeIsDeferred"] = noInsert.notices.isEmpty
        await drain()
        result["missingInsertCallbacksStillRaiseNotice"] = noInsert.notices.last?.reason == .unavailableSession
        noInsert.cleanup()

        let dismissed = Fixture(directory: root.appendingPathComponent("dismissed"))
        dismissed.key(); dismissed.key()
        dismissed.monitor.clearProblem()
        await drain()
        result["dismissalCancelsQueuedNoticeAndFileAttachment"] = dismissed.monitor.problem == nil && dismissed.notices.isEmpty && dismissed.recoveries == 0
        dismissed.cleanup()

        let superseded = Fixture(directory: root.appendingPathComponent("superseded"))
        superseded.key(); superseded.key()
        let oldStamp = superseded.monitor.problem?.detectedAt
        superseded.monitor.clearProblem()
        superseded.monitor.dataDirectory = nil
        NSApp.isActive = true; superseded.window.simulatedKey = true
        superseded.key() // A healthy key resets the unavailable-session streak.
        NSApp.isActive = false; superseded.window.simulatedKey = false
        superseded.key(); superseded.key()
        let newStamp = superseded.monitor.problem?.detectedAt
        await drain()
        result["oldSnapshotCannotAttachToNewProblem"] = oldStamp != newStamp && superseded.monitor.problem?.detectedAt == newStamp
            && superseded.monitor.problem?.file == nil && superseded.notices.allSatisfy { $0.detectedAt == newStamp }
        superseded.cleanup()

        let recovered = Fixture()
        recovered.key(); recovered.key()
        recovered.inKey = true
        recovered.monitor.keyBegan(recovered.event)
        recovered.monitor.noteInsert("한", replacementRange: NSRange(location: 0, length: 2),
                                     source: "com.apple.inputmethod.Korean.2SetKorean", in: recovered.editor)
        recovered.monitor.keyEnded(); recovered.inKey = false
        result["composedReplacementRecoveryIsDeferred"] = recovered.notices.isEmpty && recovered.recoveries == 0
        await drain()
        result["compositionSupersedesPendingFailureNotice"] = recovered.monitor.problem == nil
            && recovered.notices.isEmpty && recovered.recoveries == 1 && !recovered.noticeDuringKey
        recovered.cleanup()

        let jamo = Fixture()
        jamo.editor.string = "ㅇㅏ"; jamo.editor.setSelectedRange(NSRange(location: 2, length: 0))
        for _ in 0..<2 {
            jamo.monitor.keyBegan(jamo.event)
            jamo.monitor.noteInsert("ㅏ", replacementRange: NSRange(location: NSNotFound, length: 0),
                                    source: "com.apple.inputmethod.Korean.2SetKorean", in: jamo.editor)
            jamo.monitor.keyEnded()
        }
        await drain()
        result["actualSymptomDetectorStillRecognizesUncombinedJamo"] = jamo.notices.last?.reason == .uncombinedJamo
        jamo.cleanup()

        let reconnected = Fixture()
        reconnected.key(); reconnected.key()
        reconnected.monitor.attemptRecovery(editor: reconnected.editor)
        reconnected.key(); reconnected.key()
        result["unhealthyKeysPreservePendingActivation"] = reconnected.monitor.problem?.recoveryState == .activating
        NSApp.isActive = true; reconnected.window.simulatedKey = true
        reconnected.coordinator.activationStateDidChange()
        reconnected.window.simulatedKey = false
        reconnected.key(); reconnected.key()
        result["unhealthyKeysPreservePendingVerification"] = reconnected.monitor.problem?.recoveryState == .verifying
        reconnected.window.simulatedKey = true
        reconnected.coordinator.activationStateDidChange()
        await drain()
        result["injectedRecoveryCanReachReconnected"] = reconnected.monitor.problem?.recoveryState == .reconnected
            && reconnected.driver.activationRequests == 1 && NSApp.activationRequests == originalActivationRequests
        NSApp.isActive = false; reconnected.window.simulatedKey = false
        reconnected.key()
        result["firstFreshUnhealthyKeyKeepsReconnectedState"] = reconnected.monitor.problem?.recoveryState == .reconnected
        reconnected.key()
        await drain()
        result["secondFreshUnhealthyKeyRaisesNewFailure"] = reconnected.monitor.problem?.reason == .unavailableSession
            && reconnected.monitor.problem?.recoveryState == .idle && reconnected.monitor.problem?.recoveryAttempts == 1
            && reconnected.notices.last?.recoveryState == .idle
        reconnected.cleanup()

        // Production default context check: nil is unavailable even when the
        // app, window, and editor all otherwise report healthy native focus.
        let missing = Fixture()
        NSApp.isActive = true; missing.window.simulatedKey = true
        let missingMonitor = InputMethodMonitor(recoveryCoordinator: missing.coordinator)
        for _ in 0..<2 { missingMonitor.keyBegan(missing.event, in: missing.editor); missingMonitor.keyEnded() }
        await drain()
        result["missingInputContextRaisesUnavailableSession"] = missing.editor.inputContext == nil
            && missingMonitor.problem?.reason == .unavailableSession
        result["missingInputContextObservationDoesNotMutateFocus"] = missing.window.responderChanges == 0
            && missing.window.owner === missing.editor && missing.editor.string == Fixture.draft
        missing.cleanup()

        let encoded = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
        print(String(decoding: encoded, as: UTF8.self))
    }
}
"""


@unittest.skipUnless(sys.platform == "darwin", "Requires AppKit on macOS")
class InputSessionMonitorTests(unittest.TestCase):
    def test_production_monitor_observation_and_notice_contracts(self):
        with tempfile.TemporaryDirectory(prefix="mighty-input-monitor-") as directory:
            scratch = Path(directory)
            swiftc = subprocess.check_output(["xcrun", "--find", "swiftc"], text=True).strip()
            sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
            source = "\n".join(path.read_text().replace("import MightyCore\n", "") for path in SOURCES)
            harness = scratch / "InputMonitorChecks.swift"
            harness.write_text(FIXTURES + source + CHECKS)
            binary = scratch / "input-monitor-checks"
            compiled = subprocess.run(
                [swiftc, "-parse-as-library", "-sdk", sdk, "-target", f"{platform.machine()}-apple-macosx14.0",
                 "-module-cache-path", str(scratch / "module-cache"), str(harness), "-o", str(binary)],
                capture_output=True, text=True, timeout=90,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=30)
            self.assertEqual(run.returncode, 0, run.stderr)
            result = json.loads(run.stdout)
            self.assertEqual(len(result), 33)
            self.assertFalse([name for name, passed in result.items() if not passed], result)


if __name__ == "__main__":
    unittest.main(verbosity=2)
