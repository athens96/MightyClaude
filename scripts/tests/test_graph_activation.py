#!/usr/bin/env python3
"""Check graph monitor/native activation ownership in an offscreen window.

Uses real NSApplication local-monitor and NSWindow dispatch, shadowing only
activation metadata to avoid activating or manipulating the user's windows.
No keyboard source, provider, input content, or clipboard is accessed.
"""
import json
from pathlib import Path
import platform
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "native/macos/Sources/MightyClaude/MightyGraphInteraction.swift"


def main():
    source = SOURCE.read_text()
    source = source[source.index("@MainActor\nfinal class MightyGraphInteractionProbe:"):]
    camera = (ROOT / "native/macos/Sources/MightyCore/MightyGraphCamera.swift").read_text()
    fixture = (ROOT / "scripts/tests/graph-activation-main.swift").read_text().replace("// PRODUCTION_CAMERA", camera)
    sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
    compiler = subprocess.check_output(["xcrun", "--find", "swiftc"], text=True).strip()
    with tempfile.TemporaryDirectory(prefix="mighty-graph-activation-") as temp:
        temp = Path(temp)
        def run(name, production):
            script, exe = temp / (name + ".swift"), temp / name
            script.write_text(fixture.replace("// PRODUCTION_PROBE", production))
            build = subprocess.run([compiler, "-parse-as-library", "-sdk", sdk, "-target", platform.machine() + "-apple-macosx14.0", "-module-cache-path", str(temp / "cache"), str(script), "-o", str(exe)], text=True, capture_output=True, timeout=90)
            if build.returncode: raise RuntimeError(build.stderr)
            result = subprocess.run([str(exe)], text=True, capture_output=True, timeout=15)
            if result.returncode: raise RuntimeError(result.stderr)
            return json.loads(result.stdout)
        current = run("current", source)
        assert all(current.values()), current
        original, count = re.subn(r"        guard NSApp.isActive, window.isKeyWindow, NSApp.keyWindow === window else \{\n            cancelInteraction\(\)\n            return event\n        }\n", "", source, count=1)
        assert count == 1
        baseline = run("original", original)
        for key in ["inactiveCanvasClickReachesWindow", "inactiveCanvasClickPreservesEditor", "nonKeyCanvasClickReachesWindow", "inactiveResizeClickReachesWindow", "inactiveOverlayResizeClickReachesWindow", "deactivationCancelsPanWithoutNotification", "lostKeyCancelsResizeWithoutNotification"]:
            assert baseline[key] is False, baseline
        assert baseline["activeCanvasStartsPan"] and baseline["activeResizeStillStarts"]
        print(json.dumps({"current": current, "original": baseline}, sort_keys=True))

if __name__ == "__main__": main()
