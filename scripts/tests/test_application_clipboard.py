#!/usr/bin/env python3
"""Build/run offscreen production clipboard routing checks with real Ghostty.

Requires an existing release dependency build. Windows are never activated and
diagnostics use counting actions; the general pasteboard is not accessed.
"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-only", action="store_true")
    parser.add_argument("--products-dir", type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    products = args.products_dir or repo / "native/macos/.build/out/Products/Release"
    source = repo / "native/macos/Sources"
    fixture = Path(tempfile.mkdtemp(prefix="mighty-clipboard-routing-"))
    # Extract complete production classes, excluding unrelated SwiftUI app
    # composition. Their source bodies are copied without rewriting behavior.
    for filename, original, boundary in [
        ("Selectable.swift", "SelectableTextView.swift", "/// The plain request"),
        ("TerminalFocus.swift", "LocalTerminalView.swift", "struct LocalTerminalPane: View"),
    ]:
        content = (source / "MightyClaude" / original).read_text()
        assert content.count(boundary) == 1
        (fixture / filename).write_text(content.split(boundary)[0])
    (fixture / "main.swift").write_text("""import AppKit
@main struct Main {
    @MainActor static func main() throws {
        _ = NSApplication.shared
        let report = ApplicationCopyDiagnostics.run()
        print(String(decoding: try JSONSerialization.data(withJSONObject: report,
            options: [.prettyPrinted, .sortedKeys]), as: UTF8.self))
        exit(report.values.allSatisfy { $0 } ? 0 : 1)
    }
}
""")
    executable = fixture / "ClipboardRouting"
    command = ["xcrun", "swiftc", "-parse-as-library", "-module-cache-path", str(fixture / "cache"),
               "-I", str(products), "-I", str(products / "include")]
    command += [str(fixture / name) for name in ["Selectable.swift", "TerminalFocus.swift", "main.swift"]]
    command += [str(source / "MightyClaude" / name) for name in [
        "HostTerminalView.swift", "ApplicationCopyRouter.swift", "ApplicationCopyDiagnostics.swift",
        "InputSessionRecoveryCoordinator.swift"]]
    # Compile the current shortcut policy as a local type, rather than testing
    # whichever version the existing dependency build happened to contain.
    command += [str(source / "MightyCore" / name) for name in ["ClipboardShortcut.swift", "TranscriptCopyClaim.swift"]]
    command += [str(products / name) for name in [
        "MightyCore.o", "GhosttyTerminal.o", "GhosttyKit.o", "MSDisplayLink.o", "libghostty.a"]]
    command += ["-lc++", "-framework", "Carbon", "-o", str(executable)]
    subprocess.run(command, env=os.environ.copy(), check=True)
    if args.build_only:
        print(executable)
    else:
        subprocess.run([str(executable)], check=True)


if __name__ == "__main__":
    main()
