#!/usr/bin/env python3
"""Exercise installer serialization without touching installed apps or LaunchServices."""
import os
from pathlib import Path
import subprocess
import signal
import tempfile
import time
import unittest


class InstallerTests(unittest.TestCase):
    def test_waiting_installers_cannot_replace_each_others_bundle(self):
        root = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory(prefix="mighty-install-test-") as folder:
            folder = Path(folder)
            bins = folder / "bin"
            bins.mkdir()
            gate = folder / "app-running"
            gate.touch()
            destination = folder / "Applications/MightyClaude.app"
            binary = destination / "Contents/MacOS/MightyClaude"
            binary.parent.mkdir(parents=True)
            binary.write_text("old")
            source = folder / "release/MightyClaude.app"
            source_binary = source / "Contents/MacOS/MightyClaude"
            source_binary.parent.mkdir(parents=True)
            source_binary.write_text("new")
            for name, body in {
                "ps": 'if [ -f "$FIXTURE_GATE" ]; then printf "%s\\n" "$MIGHTY_INSTALL_PATH/Contents/MacOS/MightyClaude"; fi',
                "codesign": "exit 0",
                "open": "exit 0",
                "lsregister": "exit 0",
            }.items():
                script = bins / name
                script.write_text("#!/bin/bash\n" + body + "\n")
                script.chmod(0o700)
            # Only replace OS integration targets and backup destination. The
            # actual lock/wait/backup/copy code is read from the shipping script.
            source_script = (root / "scripts/install-macos.sh").read_text()
            fixture_script = source_script.replace(
                "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
                str(bins / "lsregister"),
            ).replace("/tmp/MightyClaude-app-backup-", str(folder / "backup-"))
            installer = folder / "install.sh"
            installer.write_text(fixture_script)
            environment = os.environ.copy()
            environment.pop("MIGHTY_INSTALL_UNDER_LOCK", None)
            environment.update(PATH=str(bins) + ":/usr/bin:/bin:/usr/sbin:/sbin", TMPDIR=str(folder),
                               MIGHTY_INSTALL_PATH=str(destination), FIXTURE_GATE=str(gate))
            first_log = folder / "first.log"
            with first_log.open("w") as log:
                first = subprocess.Popen(["/bin/bash", str(installer), str(source)], env=environment,
                                         stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic() + 10
                    while "⌘Q" not in first_log.read_text() and first.poll() is None and time.monotonic() < deadline:
                        time.sleep(0.02)
                    self.assertIn("⌘Q", first_log.read_text())
                    second = subprocess.run(["/bin/bash", str(installer), str(source)], env=environment,
                                            capture_output=True, text=True, timeout=10)
                    self.assertEqual(second.returncode, 75, second.stderr)
                    self.assertEqual(binary.read_text(), "old")
                    gate.unlink()
                    self.assertEqual(first.wait(timeout=15), 0, first_log.read_text())
                finally:
                    if first.poll() is None:
                        gate.unlink(missing_ok=True)
                        first.terminate()
                        first.wait(timeout=10)
            self.assertEqual(binary.read_text(), "new")
            backups = list(folder.glob("backup-*/MightyClaude.app.bak/Contents/MacOS/MightyClaude"))
            self.assertEqual([p.read_text() for p in backups], ["old"])
            # Lock release allows the next update; backup names never collide.
            source_binary.write_text("newer")
            third = subprocess.run(["/bin/bash", str(installer), str(source)], env=environment,
                                   capture_output=True, text=True, timeout=15)
            self.assertEqual(third.returncode, 0, third.stderr)
            self.assertEqual(binary.read_text(), "newer")
            backups = list(folder.glob("backup-*/MightyClaude.app.bak/Contents/MacOS/MightyClaude"))
            self.assertEqual(sorted(p.read_text() for p in backups), ["new", "old"])

    def test_cancelled_waiter_cannot_install_and_lock_ignores_tmpdir(self):
        root = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory(prefix="mighty-install-cancel-test-") as temporary:
            folder = Path(temporary)
            bins = folder / "bin"
            bins.mkdir()
            gate = folder / "app-running"
            gate.touch()
            launched = folder / "launched"
            destination = folder / "Applications/MightyClaude.app"
            binary = destination / "Contents/MacOS/MightyClaude"
            binary.parent.mkdir(parents=True)
            binary.write_text("installed-original")
            obsolete = folder / "obsolete/MightyClaude.app"
            obsolete_binary = obsolete / "Contents/MacOS/MightyClaude"
            obsolete_binary.parent.mkdir(parents=True)
            obsolete_binary.write_text("cancelled-update")
            current = folder / "current/MightyClaude.app"
            current_binary = current / "Contents/MacOS/MightyClaude"
            current_binary.parent.mkdir(parents=True)
            current_binary.write_text("requested-update")
            for name, body in {
                "ps": 'if [ -f "$FIXTURE_GATE" ]; then printf "%s\\n" "$MIGHTY_INSTALL_PATH/Contents/MacOS/MightyClaude"; fi',
                "codesign": "exit 0",
                "open": 'printf "opened\\n" >> "$FIXTURE_LAUNCHED"',
                "lsregister": "exit 0",
            }.items():
                script = bins / name
                script.write_text("#!/bin/bash\n" + body + "\n")
                script.chmod(0o700)
            fixture_script = (root / "scripts/install-macos.sh").read_text().replace(
                "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
                str(bins / "lsregister"),
            ).replace("/tmp/MightyClaude-app-backup-", str(folder / "backup-"))
            installer = folder / "install.sh"
            installer.write_text(fixture_script)
            first_tmp = folder / "first-tmp"
            second_tmp = folder / "second-tmp"
            first_tmp.mkdir()
            second_tmp.mkdir()
            environment = os.environ.copy()
            environment.pop("MIGHTY_INSTALL_UNDER_LOCK", None)
            environment.update(PATH=str(bins) + ":/usr/bin:/bin:/usr/sbin:/sbin", TMPDIR=str(first_tmp),
                               MIGHTY_INSTALL_PATH=str(destination), FIXTURE_GATE=str(gate),
                               FIXTURE_LAUNCHED=str(launched))
            other_environment = dict(environment, TMPDIR=str(second_tmp))
            log_path = folder / "cancelled.log"
            with log_path.open("w") as log:
                waiter = subprocess.Popen(["/bin/bash", str(installer), str(obsolete)], env=environment,
                                          stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    deadline = time.monotonic() + 10
                    while "⌘Q" not in log_path.read_text() and waiter.poll() is None and time.monotonic() < deadline:
                        time.sleep(0.02)
                    self.assertIn("⌘Q", log_path.read_text())
                    # The destination, not the caller's temporary directory,
                    # determines the lock shared by independent launchers.
                    competing = subprocess.run(["/bin/bash", str(installer), str(current)], env=other_environment,
                                               capture_output=True, text=True, timeout=5)
                    self.assertEqual(competing.returncode, 75, competing.stderr)
                    # Cancel exactly the PID callers record, not its process
                    # group: a wrapper regression leaves a live copying child.
                    waiter.terminate()
                    waiter.wait(timeout=5)
                    gate.unlink()
                    deadline = time.monotonic() + 4
                    while time.monotonic() < deadline:
                        self.assertEqual(binary.read_text(), "installed-original")
                        self.assertFalse(launched.exists())
                        time.sleep(0.05)
                    self.assertEqual(list(folder.glob("backup-*")), [])
                    retry = subprocess.run(["/bin/bash", str(installer), str(current)], env=other_environment,
                                           capture_output=True, text=True, timeout=10)
                    self.assertEqual(retry.returncode, 0, retry.stderr)
                    self.assertEqual(binary.read_text(), "requested-update")
                    self.assertEqual(launched.read_text().splitlines(), ["opened"])
                    backups = list(folder.glob("backup-*/MightyClaude.app.bak/Contents/MacOS/MightyClaude"))
                    self.assertEqual([path.read_text() for path in backups], ["installed-original"])
                finally:
                    # Clean up only this isolated fixture's process group,
                    # including children if running against a broken baseline.
                    try:
                        os.killpg(waiter.pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                    waiter.wait(timeout=5)


if __name__ == "__main__":
    unittest.main(verbosity=2)
