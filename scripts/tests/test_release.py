"""scripts/release.py without network, git pushes or real keys: python -m unittest scripts/tests/test_release.py"""
import argparse
import base64
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

spec = importlib.util.spec_from_file_location("release", Path(__file__).resolve().parents[1] / "release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
release.say = lambda message: None

LIVE_PAGE = """<body>
  <p>버전 0.4.47</p>
  <a class="button" href="0.4.47/MightyClaude-macos.zip">Mac</a>
  <a class="button" href="0.4.47/MightyClaude-windows-x64-setup.exe">Windows</a>
  <a class="button" href="android/MightyClaude-android.apk" download="MightyClaude-0.4.47.apk">Android</a>
  <p>0.4.35 이후 설치한 앱 위에 그대로 업데이트됩니다.</p>
</body>"""


class PageTests(unittest.TestCase):
    def test_moves_only_the_version_line_and_folder_links(self):
        page, old = release.replace_page_version(LIVE_PAGE, "0.4.48")
        self.assertEqual(old, "0.4.47")
        self.assertIn("<p>버전 0.4.48</p>", page)
        self.assertIn('href="0.4.48/MightyClaude-macos.zip"', page)
        self.assertIn('href="0.4.48/MightyClaude-windows-x64-setup.exe"', page)
        self.assertNotIn('href="0.4.47/', page)
        # Without a new APK the phone download keeps its name, and other text is untouched.
        self.assertIn('download="MightyClaude-0.4.47.apk"', page)
        self.assertIn("0.4.35 이후", page)
        self.assertEqual(release.page_apk_version(page), "0.4.47")

    def test_new_apk_renames_the_download(self):
        page, _ = release.replace_page_version(LIVE_PAGE, "0.4.48", apk_version="0.4.48")
        self.assertIn('download="MightyClaude-0.4.48.apk"', page)
        self.assertIn('href="android/MightyClaude-android.apk"', page)

    def test_refuses_a_page_without_the_old_version(self):
        with self.assertRaises(release.ReleaseError):
            release.replace_page_version(LIVE_PAGE.replace("<p>버전 0.4.47</p>", "<p>Mighty</p>"), "0.4.48")
        with self.assertRaises(release.ReleaseError):
            release.replace_page_version(LIVE_PAGE.replace('href="0.4.47/', 'href="old/'), "0.4.48")


class ManifestTests(unittest.TestCase):
    """A throwaway key signs through make-update-manifest.py's own path; the real key is never touched."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = self.temp.name
        for index, name in enumerate(release.MANIFEST_ROLES.values()):
            with open(os.path.join(self.folder, name), "wb") as f:
                f.write(bytes([index]) * (1000 + index))
        private = Ed25519PrivateKey.generate()
        seed = private.private_bytes(serialization.Encoding.Raw, serialization.PrivateFormat.Raw, serialization.NoEncryption())
        self.key = os.path.join(self.folder, "throwaway.key")
        with open(self.key, "w", encoding="utf-8") as f:
            f.write(base64.b64encode(seed).decode())
        self.public = base64.b64encode(private.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)).decode()
        self.notes = os.path.join(self.folder, "update-notes.md")
        with open(self.notes, "w", encoding="utf-8") as f:
            f.write("- 바뀐 점\n")
        self.out = os.path.join(self.folder, "latest.json")
        release.make_manifest("0.4.48", 1360, self.folder, self.key, self.notes, self.out)
        with open(self.out, encoding="utf-8") as f:
            self.envelope = json.load(f)

    def tearDown(self):
        self.temp.cleanup()

    def verify(self, envelope=None, **overrides):
        args = dict(version="0.4.48", build=1360, folder=self.folder, public_key=self.public)
        args.update(overrides)
        return release.verify_envelope(envelope or self.envelope, **args)

    def test_signed_manifest_matches_files(self):
        payload = self.verify()
        self.assertEqual(payload["version"], "0.4.48")
        self.assertEqual(payload["build"], 1360)
        self.assertEqual(payload["notes"], "- 바뀐 점")
        self.assertEqual(payload["macos"]["url"], f"{release.PUBLIC_BASE}/0.4.48/MightyClaude-macos.zip")
        self.assertEqual(payload["windows"]["arm64"]["size"], 1002)

    def test_wrong_public_key_fails(self):
        other = base64.b64encode(Ed25519PrivateKey.generate().public_key().public_bytes(
            serialization.Encoding.Raw, serialization.PublicFormat.Raw)).decode()
        with self.assertRaisesRegex(release.ReleaseError, "서명"):
            self.verify(public_key=other)
        with self.assertRaisesRegex(release.ReleaseError, "서명"):  # the real public key did not sign a throwaway manifest
            self.verify(public_key=release.PUBLIC_KEY)

    def test_tampered_payload_fails(self):
        payload = json.loads(base64.b64decode(self.envelope["payload"]))
        payload["macos"]["size"] += 1
        tampered = dict(self.envelope, payload=base64.b64encode(json.dumps(payload).encode()).decode())
        with self.assertRaisesRegex(release.ReleaseError, "서명"):
            self.verify(tampered)

    def test_changed_file_fails_size_and_sha(self):
        with open(os.path.join(self.folder, "MightyClaude-windows-x64.zip"), "ab") as f:
            f.write(b"x")
        with self.assertRaisesRegex(release.ReleaseError, "x64 size.*x64 sha256"):
            self.verify()

    def test_same_size_different_bytes_fails_sha(self):
        path = os.path.join(self.folder, "MightyClaude-macos.zip")
        with open(path, "r+b") as f:
            f.write(b"\xff")
        with self.assertRaisesRegex(release.ReleaseError, "macos sha256"):
            self.verify()

    def test_wrong_version_or_build_fails(self):
        with self.assertRaisesRegex(release.ReleaseError, "버전"):
            self.verify(version="0.4.49")
        with self.assertRaisesRegex(release.ReleaseError, "build"):
            self.verify(build=1)


class StateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        patcher = patch.object(release, "RELEASE_ROOT", self.temp.name)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(self.temp.cleanup)

    def test_steps_run_in_order(self):
        state = release.load_state("0.4.48")
        self.assertEqual(release.next_step(state), "check")
        with self.assertRaisesRegex(release.ReleaseError, "check, version, push, ci, download"):
            release.require_previous(state, "manifest")
        release.require_previous(state, "manifest", force=True)
        for step in release.STEPS[:5]:
            release.mark_done(state, step)
        release.require_previous(state, "manifest")
        self.assertEqual(release.next_step(release.load_state("0.4.48")), "manifest")

    def test_redoing_a_producer_reopens_later_steps(self):
        state = release.load_state("0.4.48")
        for step in release.STEPS[:8]:
            release.mark_done(state, step)
        release.mark_done(state, "manifest")
        self.assertEqual(release.next_step(state), "page")
        self.assertNotIn("upload", state["done"])
        release.mark_done(state, "check")  # re-checking keeps the work done since
        self.assertIn("manifest", state["done"])

    def test_publishing_steps_need_yes(self):
        state = release.load_state("0.4.48")
        state["data"]["commit"] = "abc"
        args = release.parser().parse_args(["push"])
        with patch.object(release, "sh", return_value=""), patch.object(release, "sh_ok", return_value=False):
            with self.assertRaisesRegex(release.ReleaseError, "--yes"):
                release.step_push(state, args)
        self.assertNotIn("push", state["done"])

    def fake_git_and_gh(self, conclusions, run_ids=(7, 8)):
        def sh(args, **_):
            if args[:2] == ["git", "rev-parse"]:
                return args[2]
            if args[:2] == ["git", "fetch"]:
                return ""
            if args[:2] == ["git", "rev-list"]:
                return "1360"
            if args[:2] == ["git", "show"]:
                return "0.4.48"
            if args[:3] == ["gh", "run", "list"]:
                workflow = args[args.index("--workflow") + 1]
                return json.dumps([{"databaseId": run_ids[0] if "macos" in workflow else run_ids[1], "status": "completed",
                                    "headBranch": "main", "conclusion": conclusions[workflow], "url": "u", "createdAt": "t"}])
            raise AssertionError(args)
        return patch.object(release, "sh", side_effect=sh)

    def test_ci_records_runs_and_build_number_or_names_the_failure(self):
        args = release.parser().parse_args(["ci"])
        state = release.load_state("0.4.48")
        state["data"]["commit"] = "c0ffee"
        with patch.object(release, "sh_ok", return_value=True):
            with self.fake_git_and_gh({"native-macos.yml": "success", "native-windows.yml": "failure"}):
                with self.assertRaisesRegex(release.ReleaseError, "windows: failure.*gh run view 8 --log-failed"):
                    release.step_ci(state, args)
            self.assertNotIn("ci", state["done"])
            with self.fake_git_and_gh({"native-macos.yml": "success", "native-windows.yml": "success"}):
                release.step_ci(state, args)
        self.assertEqual(state["data"]["runs"], {"macos": 7, "windows": 8})
        self.assertEqual((state["data"]["build"], state["data"]["buildCommit"]), (1360, "c0ffee"))
        self.assertIn("ci", state["done"])

    def test_ci_commit_must_already_be_on_origin_main(self):
        state = release.load_state("0.4.48")
        state["data"]["commit"] = "c0ffee"
        args = release.parser().parse_args(["ci", "--commit", "feature"])

        def on_main(command):
            return not (command[:3] == ["git", "merge-base", "--is-ancestor"] and command[3] == "feature")
        with patch.object(release, "sh_ok", side_effect=on_main), \
                self.fake_git_and_gh({"native-macos.yml": "success", "native-windows.yml": "success"}) as sh:
            with self.assertRaisesRegex(release.ReleaseError, "먼저 main에 push"):
                release.step_ci(state, args)
        self.assertFalse([c for c in sh.call_args_list if c.args[0][:2] == ["gh", "run"]])  # no waiting at all

    def test_ci_on_another_commit_reopens_download_and_redownloads(self):
        state = release.load_state("0.4.48")
        state["data"]["commit"] = "c0ffee"
        folder = release.release_dir("0.4.48")
        os.makedirs(folder, exist_ok=True)
        downloads = []

        def fake_download(run_id):
            def sh(args, **_):
                self.assertEqual(args[:3], ["gh", "run", "download"])
                downloads.append(args[3])
                target = args[args.index("-D") + 1]
                os.makedirs(target, exist_ok=True)
                for name in release.ARTIFACTS[args[args.index("-n") + 1]][1]:
                    if not name.endswith(".sha256"):
                        with open(os.path.join(target, name), "w", encoding="utf-8") as f:
                            f.write(f"{name} from run {run_id}")
                        if "windows" in name:
                            with open(os.path.join(target, name + ".sha256"), "w", encoding="ascii") as f:
                                f.write(release.digest(os.path.join(target, name)) + "  " + name + "\n")
                return ""
            return patch.object(release, "sh", side_effect=sh)

        ok = {"native-macos.yml": "success", "native-windows.yml": "success"}
        with patch.object(release, "sh_ok", return_value=True):
            with self.fake_git_and_gh(ok, run_ids=(1, 2)):
                release.step_ci(state, release.parser().parse_args(["ci"]))
            with fake_download("A"):
                release.step_download(state, release.parser().parse_args(["download"]))
            self.assertEqual(downloads, ["1", "2", "2"])
            release.mark_done(state, "manifest")
            old = state["data"]["files"]["MightyClaude-macos.zip"]["sha256"]
            # The same runs again: nothing reopens and nothing is fetched again.
            with self.fake_git_and_gh(ok, run_ids=(1, 2)):
                release.step_ci(state, release.parser().parse_args(["ci"]))
            self.assertIn("manifest", state["done"])
            with fake_download("A"):
                release.step_download(state, release.parser().parse_args(["download"]))
            self.assertEqual(len(downloads), 3)
            release.mark_done(state, "manifest")
            # A CI-fix commit: new runs, so download and manifest are open again and the files are replaced.
            with self.fake_git_and_gh(ok, run_ids=(3, 4)):
                release.step_ci(state, release.parser().parse_args(["ci", "--commit", "fix"]))
            self.assertEqual(state["data"]["buildCommit"], "fix")
            self.assertNotIn("download", state["done"])
            self.assertNotIn("manifest", state["done"])
            with fake_download("B"):
                release.step_download(state, release.parser().parse_args(["download"]))
        self.assertEqual(downloads[3:], ["3", "4", "4"])
        self.assertNotEqual(state["data"]["files"]["MightyClaude-macos.zip"]["sha256"], old)
        with open(os.path.join(folder, "MightyClaude-macos.zip"), encoding="utf-8") as f:
            self.assertIn("from run B", f.read())

    def test_publishing_steps_refuse_force(self):
        state = release.load_state("0.4.48")
        for step in ("push", "upload", "github"):
            with self.assertRaisesRegex(release.ReleaseError, "--force"):
                release.run_step(step, state, release.parser().parse_args([step, "--force", "--yes"]))

    def test_notes_template_fills_known_values_and_flags_the_rest(self):
        release.ensure_notes("0.4.48")
        with open(release.notes_path("0.4.48"), encoding="utf-8") as f:
            text = f.read()
        self.assertTrue(text.startswith("{{DATE}} · Mac build {{MAC_BUILD}} · Windows (beta) x64·ARM64 · Android {{ANDROID}}"))
        self.assertIn("<details open>\n<summary><b>한국어</b></summary>", text)
        filled = release.fill_notes(text, {"DATE": "2026-11-02", "MAC_BUILD": 1360, "SHA:MightyClaude-macos.zip": "ab" * 32})
        self.assertTrue(filled.startswith("2026-11-02 · Mac build 1360 ·"))
        self.assertIn("`" + "ab" * 32 + "`", filled)
        left = release.unfinished_notes(filled)
        self.assertIn(release.PLACEHOLDER, left)
        self.assertIn("{{ANDROID}}", left)
        self.assertNotIn("{{DATE}}", left)

    def test_notes_are_rendered_again_from_the_template_each_time(self):
        with open(release.notes_path("0.4.48"), "w", encoding="utf-8-sig") as f:  # a BOM, as Notepad writes
            f.write("{{DATE}} · Mac build {{MAC_BUILD}} · Android {{ANDROID}}\n{{SHA:MightyClaude-macos.zip}}\n{{ANDROID_ROW}}\n")
        state = release.load_state("0.4.48")
        state["data"].update(date="2026-11-02", build=1360, previousApk="0.4.47",
                             files={"MightyClaude-macos.zip": {"sha256": "a" * 64, "size": 1}})
        first = release.render_notes(state)
        self.assertTrue(first.startswith("2026-11-02 · Mac build 1360 · Android 0.4.47\n" + "a" * 64))
        self.assertIn("Android: 0.4.47 APK — https://github.com/athens96/MightyClaude/releases/tag/v0.4.47", first)
        state["data"].update(build=1362, files={"MightyClaude-macos.zip": {"sha256": "b" * 64, "size": 2}},
                             apk={"version": "0.4.48", "path": "x.apk", "sha256": "c" * 64, "size": 3})
        second = release.render_notes(state)
        self.assertNotIn("a" * 64, second)
        self.assertIn("Mac build 1362 · Android 0.4.48\n" + "b" * 64, second)
        self.assertIn("| MightyClaude-0.4.48.apk (Android) | `" + "c" * 64 + "` | 3 |", second)
        with open(release.rendered_notes_path("0.4.48"), encoding="utf-8") as f:
            self.assertEqual(f.read(), second)
        self.assertIn("{{SHA:MightyClaude-macos.zip}}", release.read_text(release.notes_path("0.4.48")))  # template untouched
        with open(release.notes_path("0.4.48"), "w", encoding="utf-8") as f:
            f.write("\n")
        with self.assertRaisesRegex(release.ReleaseError, "비었습니다"):
            release.render_notes(state)


LIVE_OLDER = json.dumps({"format": release.FORMAT, "version": "0.4.47", "payload": "", "signature": ""}).encode()


class PublishTests(unittest.TestCase):
    """upload and github on a made-up release folder; wrangler and gh are fakes that only record."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        patcher = patch.object(release, "RELEASE_ROOT", self.temp.name)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(self.temp.cleanup)
        self.folder = release.release_dir("0.4.48")
        os.makedirs(self.folder)
        files = {}
        for name in release.PACKAGES + ["latest.json", "index.html", "MightyClaude-0.4.48.apk"]:
            path = os.path.join(self.folder, name)
            with open(path, "w", encoding="utf-8") as f:
                f.write("{}" if name == "latest.json" else name)
            if name in release.PACKAGES:
                files[name] = {"sha256": release.digest(path), "size": os.path.getsize(path)}
        apk = os.path.join(self.folder, "MightyClaude-0.4.48.apk")
        self.state = {"version": "0.4.48", "done": {s: "x" for s in release.STEPS[:7]},
                      "data": {"build": 1360, "buildCommit": "fix", "files": files, "date": "2026-11-02",
                               "apk": {"version": "0.4.48", "path": apk, "sha256": release.digest(apk), "size": os.path.getsize(apk)}}}
        with open(release.notes_path("0.4.48"), "w", encoding="utf-8") as f:
            f.write("{{DATE}} · Mac build {{MAC_BUILD}} · Android {{ANDROID}}\n본문\n")
        payload = {"macos": {"sha256": "m"}, "windows": {"x64": {"sha256": "x"}, "arm64": {"sha256": "a"}}}
        verify = patch.object(release, "verify_envelope", return_value=payload)
        verify.start()
        self.addCleanup(verify.stop)
        self.calls = []

    def recording_sh(self, fail=lambda args: False, outputs=None):
        def sh(args, **_):
            self.calls.append(list(args))
            if fail(args):
                raise release.ReleaseError("fake failure")
            for prefix, output in (outputs or {}).items():
                if " ".join(args).startswith(prefix):
                    if isinstance(output, Exception):
                        raise output
                    return output
            return ""
        return patch.object(release, "sh", side_effect=sh)

    def upload(self, *flags, live=LIVE_OLDER):
        fetch = patch.object(release, "fetch", side_effect=live if isinstance(live, Exception) else (lambda url: live))
        with fetch, patch.object(release.time, "sleep"):
            release.step_upload(self.state, release.parser().parse_args(["upload"] + list(flags)))

    def targets(self):
        return [c[4] for c in self.calls if c[:4] == ["wrangler", "r2", "object", "put"]]

    def test_upload_without_yes_or_with_dry_run_sends_nothing(self):
        with self.recording_sh():
            with self.assertRaisesRegex(release.ReleaseError, "--yes"):
                self.upload()
            self.upload("--dry-run", "--yes")
        self.assertEqual(self.calls, [])
        self.assertNotIn("upload", self.state["done"])

    def test_upload_order_with_apk(self):
        with self.recording_sh():
            self.upload("--yes")
        self.assertEqual(self.targets()[5:], ["mightyclaude/mightyclaude/latest.json", "mightyclaude/mightyclaude/android/MightyClaude-android.apk",
                                              "mightyclaude/mightyclaude/index.html", "mightyclaude/mightyclaude/"])
        self.assertIn("upload", self.state["done"])

    def test_upload_stops_at_the_first_failure(self):
        with self.recording_sh(fail=lambda args: True):
            with self.assertRaisesRegex(release.ReleaseError, "세 번 실패"):
                self.upload("--yes")
        self.assertEqual(self.targets(), ["mightyclaude/mightyclaude/0.4.48/MightyClaude-macos.zip"] * 3)
        self.assertNotIn("upload", self.state["done"])

    def test_upload_refuses_an_unreadable_live_manifest_unless_allowed(self):
        with self.recording_sh():
            with self.assertRaisesRegex(release.ReleaseError, "--allow-unreadable-live"):
                self.upload("--yes", live=release.FetchError("offline"))
            self.assertEqual(self.calls, [])
            self.upload("--yes", "--allow-unreadable-live", live=release.FetchError("offline"))
        self.assertIn("upload", self.state["done"])

    def test_upload_refuses_a_newer_or_different_live_release(self):
        newer = json.dumps({"version": "0.4.49"}).encode()
        same = json.dumps({"version": "0.4.48", "payload": base64.b64encode(json.dumps(
            {"macos": {"sha256": "other"}, "windows": {}}).encode()).decode()}).encode()
        with self.recording_sh():
            with self.assertRaisesRegex(release.ReleaseError, "더 새 버전"):
                self.upload("--yes", live=newer)
            with self.assertRaisesRegex(release.ReleaseError, "--force-overwrite-live"):
                self.upload("--yes", live=same)
            self.assertEqual(self.calls, [])
            self.upload("--yes", "--force-overwrite-live", live=same)
        self.assertIn("upload", self.state["done"])

    def test_upload_refuses_files_over_the_wrangler_limit_or_changed(self):
        with self.recording_sh(), patch.object(release, "MAX_UPLOAD_BYTES", 10):
            with self.assertRaisesRegex(release.ReleaseError, "300 MiB"):
                self.upload("--dry-run")
        with open(os.path.join(self.folder, "MightyClaude-windows-x64-setup.exe"), "a", encoding="utf-8") as f:
            f.write("!")
        with self.recording_sh():
            with self.assertRaisesRegex(release.ReleaseError, "x64-setup.exe.*sha256"):
                self.upload("--dry-run")
        self.state["data"]["files"] = {}
        with self.recording_sh():
            with self.assertRaisesRegex(release.ReleaseError, "download"):
                self.upload("--dry-run")
        self.assertEqual(self.calls, [])

    def test_download_refuses_files_over_the_wrangler_limit(self):
        runs = {"macos": 1, "windows": 2}
        state = {"version": "0.4.48", "done": {},
                 "data": {"runs": runs, "sources": {a: runs[w] for a, (w, _) in release.ARTIFACTS.items()}}}
        for _, names in release.ARTIFACTS.values():
            for name in names:
                if name.endswith(".sha256"):
                    with open(os.path.join(self.folder, name), "w", encoding="ascii") as f:
                        f.write(release.digest(os.path.join(self.folder, name[:-len(".sha256")])) + "  x\n")
        with patch.object(release, "MAX_UPLOAD_BYTES", 10), self.recording_sh():
            with self.assertRaisesRegex(release.ReleaseError, "300 MiB"):
                release.step_download(state, release.parser().parse_args(["download"]))
        self.assertEqual(self.calls, [])  # the files were already there from the same runs
        self.assertNotIn("download", state["done"])

    def test_github_without_yes_only_shows_the_draft_then_publish(self):
        with self.recording_sh(outputs={"gh release view": release.ReleaseError("release not found")}):
            with self.assertRaisesRegex(release.ReleaseError, "--yes"):
                release.step_github(self.state, release.parser().parse_args(["github"]))
        self.assertEqual([c[:3] for c in self.calls], [["gh", "release", "view"]])
        self.assertNotIn("github", self.state["done"])
        with open(release.rendered_notes_path("0.4.48"), encoding="utf-8") as f:
            self.assertTrue(f.read().startswith("2026-11-02 · Mac build 1360 · Android 0.4.48"))

    def test_github_creates_a_draft_with_assets_then_publishes(self):
        with self.recording_sh(outputs={"gh release view": release.ReleaseError("release not found")}):
            release.step_github(self.state, release.parser().parse_args(["github", "--yes"]))
        create, publish = self.calls[1], self.calls[2]
        self.assertEqual(create[:5], ["gh", "release", "create", "v0.4.48", "--draft"])
        self.assertEqual(create[create.index("--target") + 1], "fix")
        self.assertEqual(create[create.index("--notes-file") + 1], release.rendered_notes_path("0.4.48"))
        self.assertEqual(sorted(os.path.basename(p) for p in create[create.index("--notes-file") + 2:]),
                         sorted(release.PACKAGES + ["MightyClaude-0.4.48.apk"]))
        self.assertEqual(publish, ["gh", "release", "edit", "v0.4.48", "--draft=false", "--latest"])
        self.assertIn("github", self.state["done"])

    def test_github_completes_the_assets_of_an_existing_release(self):
        existing = json.dumps({"isDraft": False, "assets": [{"name": n} for n in release.PACKAGES[:3]]})
        with self.recording_sh(outputs={"gh release view": existing}):
            with self.assertRaisesRegex(release.ReleaseError, "--yes"):
                release.step_github(self.state, release.parser().parse_args(["github"]))
            release.step_github(self.state, release.parser().parse_args(["github", "--yes"]))
        uploads = [c for c in self.calls if c[:3] == ["gh", "release", "upload"]]
        self.assertEqual(len(uploads), 1)
        self.assertEqual(sorted(os.path.basename(p) for p in uploads[0][4:]),
                         sorted(release.PACKAGES[3:] + ["MightyClaude-0.4.48.apk"]))
        self.assertFalse([c for c in self.calls if c[:3] in (["gh", "release", "create"], ["gh", "release", "edit"])])
        self.assertIn("github", self.state["done"])


class UploadPlanTests(unittest.TestCase):
    def test_dry_run_order_keys_types_and_cache(self):
        folder = os.path.join("release", "0.4.48")
        commands = [release.wrangler_command(*entry) for entry in release.upload_plan("0.4.48", folder, apk="app.apk")]
        self.assertEqual([c[4] for c in commands], [
            "mightyclaude/mightyclaude/0.4.48/MightyClaude-macos.zip",
            "mightyclaude/mightyclaude/0.4.48/MightyClaude-windows-x64.zip",
            "mightyclaude/mightyclaude/0.4.48/MightyClaude-windows-arm64.zip",
            "mightyclaude/mightyclaude/0.4.48/MightyClaude-windows-x64-setup.exe",
            "mightyclaude/mightyclaude/0.4.48/MightyClaude-windows-arm64-setup.exe",
            "mightyclaude/mightyclaude/latest.json",
            "mightyclaude/mightyclaude/android/MightyClaude-android.apk",
            "mightyclaude/mightyclaude/index.html",
            "mightyclaude/mightyclaude/",
        ])
        by_target = {c[4]: c for c in commands}
        for command in commands:
            self.assertEqual(command[:4], ["wrangler", "r2", "object", "put"])
            self.assertEqual(command[-1], "--remote")

        def option(target, flag):
            command = by_target["mightyclaude/mightyclaude/" + target]
            return command[command.index(flag) + 1] if flag in command else None
        self.assertEqual(option("0.4.48/MightyClaude-macos.zip", "--content-type"), "application/zip")
        self.assertEqual(option("0.4.48/MightyClaude-windows-x64-setup.exe", "--content-type"), "application/vnd.microsoft.portable-executable")
        self.assertIsNone(option("0.4.48/MightyClaude-macos.zip", "--cache-control"))
        self.assertEqual(option("android/MightyClaude-android.apk", "--content-type"), "application/vnd.android.package-archive")
        self.assertEqual(option("latest.json", "--content-type"), "application/json")
        self.assertEqual(option("latest.json", "--cache-control"), "no-cache")
        self.assertEqual(option("index.html", "--content-type"), "text/html; charset=utf-8")
        self.assertEqual(option("", "--file"), os.path.join(folder, "index.html"))
        self.assertEqual(option("", "--cache-control"), "no-cache")

    def test_without_apk_the_phone_key_is_left_alone(self):
        keys = [key for key, *_ in release.upload_plan("0.4.48", "f")]
        self.assertNotIn("mightyclaude/android/MightyClaude-android.apk", keys)
        self.assertEqual(len(keys), 8)


if __name__ == "__main__":
    unittest.main()
