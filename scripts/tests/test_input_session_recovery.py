#!/usr/bin/env python3
"""Run production AppKit recovery contracts in an invisible, isolated bundle.

Requires macOS WindowServer access, but never activates the bundle or displays
a window. --build-only prints the executable for an independently approved run.
"""
import argparse
import os
import pathlib
import plistlib
import subprocess
import tempfile
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    repository = pathlib.Path(__file__).resolve().parents[2]
    fixture = pathlib.Path(tempfile.mkdtemp(prefix="mighty-input-recovery-"))
    bundle = fixture / "InputRecovery.app"
    executable = bundle / "Contents/MacOS/InputRecovery"
    executable.parent.mkdir(parents=True)
    (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps({
        "CFBundleIdentifier": "dev.mightyclaude.diagnostic.inputrecovery." + uuid.uuid4().hex,
        "CFBundleExecutable": executable.name,
        "CFBundlePackageType": "APPL",
        "NSPrincipalClass": "NSApplication",
        "LSUIElement": True,
    }))
    environment = os.environ.copy()
    environment["CLANG_MODULE_CACHE_PATH"] = str(fixture / "module-cache")
    environment["SWIFTPM_MODULECACHE_OVERRIDE"] = str(fixture / "module-cache")
    sources = repository / "native/macos/Sources/MightyClaude"
    subprocess.run([
        "xcrun", "swiftc", "-parse-as-library",
        str(sources / "InputSessionRecoveryCoordinator.swift"),
        str(sources / "InputSessionRecoveryDiagnostics.swift"),
        str(repository / "scripts/tests/input-session-recovery-main.swift"),
        "-o", str(executable),
    ], env=environment, check=True)
    if args.build_only:
        print(executable)
    else:
        subprocess.run([str(executable)], check=True)


if __name__ == "__main__":
    main()
