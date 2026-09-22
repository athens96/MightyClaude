#!/usr/bin/env python3
"""Exercise production pane-tab and coordinator code without activating a window.

The fixture owns an offscreen NSWindow and an isolated scripted event queue.
No events enter the user's application, keyboard source, or clipboard.
Requires macOS WindowServer access for NSEvent.window resolution.
"""

import argparse
import json
from pathlib import Path
import platform
import re
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "native/macos/Sources/MightyClaude/PaneDockDrag.swift"
MAIN = ROOT / "scripts/tests/pane-dock-input-main.swift"

ORIGINAL_MOUSE_DOWN = r"""    override func mouseDown(with event: NSEvent) {
        guard let window, let store, !store.hasModal else { return }
        store.selectSession(sessionId)
        let start = event.locationInWindow
        let coordinator = PaneDockDragCoordinator.shared
        var dragging = false
        var pushedCursor = false
        defer { coordinator.cancel(); if pushedCursor { NSCursor.pop() } }
        while self.window === window && window.isVisible {
            guard let next = window.nextEvent(matching: [.leftMouseDragged, .leftMouseUp, .keyDown], until: Date(timeIntervalSinceNow: 0.1), inMode: .eventTracking, dequeue: true) else {
                if !window.isKeyWindow { return }
                continue
            }
            if next.type == .keyDown { if next.keyCode == 53 { return }; continue }
            guard next.windowNumber == window.windowNumber else { return }
            if next.type == .leftMouseDragged {
                let point = next.locationInWindow
                if !dragging && hypot(point.x - start.x, point.y - start.y) >= 5 {
                    dragging = coordinator.begin(store: store, sessionId: sessionId, window: window)
                    guard dragging else { return }
                    NSCursor.closedHand.push(); pushedCursor = true
                }
                if dragging { coordinator.update(location: point, window: window) }
            } else {
                if dragging { coordinator.finish(location: next.locationInWindow, window: window) }
                else if event.clickCount == 2, paneDockVisibleRect.contains(convert(next.locationInWindow, from: nil)) {
                    store.beginRenameSession(sessionId)
                }
                return
            }
        }
    }
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--record-baseline", action="store_true")
    args = parser.parse_args()
    swiftc = subprocess.check_output(["xcrun", "--find", "swiftc"], text=True).strip()
    sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
    source = SOURCE.read_text()
    zones = (ROOT / "native/macos/Sources/MightyClaude/PaneDockView.swift").read_text()
    zones = zones[zones.index("enum PaneDockZone:"):zones.index("private enum PaneDockMetrics")]
    production = zones + source
    fixture = MAIN.read_text()
    with tempfile.TemporaryDirectory(prefix="mighty-pane-input-") as directory:
        directory = Path(directory)

        def run(name, implementation, *, legacy):
            harness = directory / f"{name}.swift"
            executable = directory / name
            harness.write_text(fixture.replace("// PRODUCTION_PANE_DOCK", implementation))
            build = subprocess.run([
                swiftc, "-parse-as-library", "-sdk", sdk,
                "-target", f"{platform.machine()}-apple-macosx14.0",
                "-module-cache-path", str(directory / "module-cache"),
                str(harness), "-o", str(executable),
            ], text=True, capture_output=True, timeout=90)
            if build.returncode:
                raise RuntimeError(build.stderr)
            result = subprocess.run([str(executable), "legacy" if legacy else "current"],
                                    text=True, capture_output=True, timeout=15)
            if result.returncode:
                raise RuntimeError(result.stderr)
            return json.loads(result.stdout)

        if args.record_baseline:
            print(json.dumps({"baseline": run("baseline", production, legacy=True)}, sort_keys=True))
            return
        current = run("current", production, legacy=False)
        assert all(current.values()), current
        original, count = re.subn(
            r"    override func mouseDown\(with event: NSEvent\) \{.*?(?=    override func mouseDragged|    override func menu)",
            lambda _: ORIGINAL_MOUSE_DOWN,
            production, count=1, flags=re.DOTALL,
        )
        assert count == 1
        negative = run("original-loop", original, legacy=True)
        for name in ["mouseDownDoesNotDrainEventQueue", "printableKeyRemainsAvailable",
                     "copyKeyRemainsAvailable", "nextClickRetainsBothEvents"]:
            assert negative[name] is False, negative
        assert negative["selectsOnPress"] is True
        print(json.dumps({"current": current, "originalLoop": negative}, sort_keys=True))


if __name__ == "__main__":
    main()
