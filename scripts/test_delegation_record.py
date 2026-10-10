#!/usr/bin/env python3
"""Run scripts/delegation-record.py over the synthetic fixtures in
scripts/fixtures/delegation-record/: each case is built in a temp folder (its
git repository from repo.json, its profile and Claude folder with the
@{root} and @{commit:NAME} placeholders filled in, a REPO or REPO-<sub>
project folder named after repo/ or repo/<sub>) and judged read-only."""
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/delegation-record.py"
FIXTURES = ROOT / "scripts/fixtures/delegation-record"
GIT_ENV = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
GIT_ENV.update(GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_NOSYSTEM="1", GIT_AUTHOR_NAME="Fixture", GIT_AUTHOR_EMAIL="fixture@example.invalid",
               GIT_COMMITTER_NAME="Fixture", GIT_COMMITTER_EMAIL="fixture@example.invalid",
               GIT_AUTHOR_DATE="2026-10-10T00:00:00Z", GIT_COMMITTER_DATE="2026-10-10T00:00:00Z")


def git(repo, *args, stdin=None):
    return subprocess.run(["git", "-C", str(repo), *args], input=stdin, capture_output=True, text=True, env=GIT_ENV, check=True).stdout.strip()


def build_repo(spec, repo):
    """The repository repo.json describes; its commits by name."""
    repo.mkdir(parents=True)
    git(repo, "init", "-q", "--object-format=sha1", "-b", spec["head"])
    commits, trees = {}, {}
    for commit in spec["commits"]:
        files = dict(trees[commit["parents"][0]]) if commit["parents"] else {}
        files.update(commit["files"])
        git(repo, "read-tree", "--empty")
        for path, text in sorted(files.items()):
            blob = git(repo, "hash-object", "-w", "--stdin", stdin=text)
            git(repo, "update-index", "--add", "--cacheinfo", f"100644,{blob},{path}")
        tree = git(repo, "write-tree")
        parents = [arg for name in commit["parents"] for arg in ("-p", commits[name])]
        commits[commit["name"]] = git(repo, "commit-tree", tree, *parents, "-m", commit["message"])
        trees[commit["name"]] = files
    for branch, name in spec["branches"].items():
        git(repo, "update-ref", f"refs/heads/{branch}", commits[name])
    git(repo, "read-tree", "-u", "--reset", "HEAD")
    return commits


def claude_project_folder(path):
    return "".join(c if c.isascii() and c.isalnum() else "-" for c in path)


def materialize(case, root):
    """The case under `root`: repo/, worktrees/, profile/ and claude/."""
    spec = json.loads((case / "repo.json").read_text())
    commits = build_repo(spec, root / "repo")
    for name in spec.get("worktrees", []):
        (root / "worktrees" / name).mkdir(parents=True)

    def fill(text):
        text = text.replace("@{root}", str(root))
        return re.sub(r"@\{commit:([a-z0-9-]+)\}", lambda m: commits[m.group(1)], text)

    for folder in ("profile", "claude"):
        for source in sorted((case / folder).rglob("*")):
            relative = source.relative_to(case)
            repo = claude_project_folder(str(root / "repo"))
            parts = [repo + part[4:] if part == "REPO" or part.startswith("REPO-") else part for part in relative.parts]
            target = root.joinpath(*parts)
            if source.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            elif source.name != ".keep":
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(fill(source.read_text()))
            else:
                target.parent.mkdir(parents=True, exist_ok=True)


def judge(root, *extra):
    return subprocess.run([sys.executable, str(SCRIPT), "--profile", str(root / "profile"), "--repo", str(root / "repo"),
                           "--claude-dir", str(root / "claude"), *extra], capture_output=True, text=True)


def load_script():
    spec = importlib.util.spec_from_file_location("delegation_record", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DelegationRecordFixtureTests(unittest.TestCase):
    CASES = ["s1-like", "s2-like", "subfolder-workspace", "duplicate-notice-id", "deleted-unmerged-branch", "missing-panes", "no-transcript"]

    def test_every_case_is_listed_here(self):
        self.assertEqual(sorted(p.name for p in FIXTURES.iterdir() if p.is_dir()), sorted(self.CASES))

    def test_each_fixture_gets_its_expected_verdict_and_names_the_failed_rules(self):
        for name in self.CASES:
            with self.subTest(case=name), tempfile.TemporaryDirectory(prefix="mighty-record-") as folder:
                root = Path(folder).resolve()
                materialize(FIXTURES / name, root)
                expect = json.loads((FIXTURES / name / "expect.json").read_text())
                before = git(root / "repo", "for-each-ref", "--format=%(refname) %(objectname)")
                result = judge(root, "--json")
                self.assertEqual(result.returncode, {"passed": 0, "failed": 1, "invalid": 2}[expect["verdict"]], result.stdout + result.stderr)
                answer = json.loads(result.stdout)
                self.assertEqual(answer["verdict"], expect["verdict"], result.stdout)
                self.assertEqual(answer["failed"], expect["failed"], result.stdout)
                # Every rule is listed once, and judging changed no ref.
                names = [rule["rule"] for rule in answer["rules"]]
                self.assertEqual(len(names), len(set(names)))
                self.assertEqual(before, git(root / "repo", "for-each-ref", "--format=%(refname) %(objectname)"))
                text = judge(root)
                self.assertEqual(text.returncode, result.returncode)
                for rule in names:
                    self.assertEqual(sum(1 for line in text.stdout.splitlines() if f" {rule} " in line), 1, text.stdout)
                verdict = text.stdout.strip().splitlines()[-1]
                self.assertTrue(verdict.startswith(f"Verdict: {expect['verdict']}"), text.stdout)
                for rule in expect["failed"]:
                    self.assertIn(rule, verdict)
                for rule, details in expect.get("details", {}).items():
                    self.assertEqual(next(r["details"] for r in answer["rules"] if r["rule"] == rule), details, result.stdout)
                if expect["verdict"] == "invalid":
                    self.assertIn("panes missing", answer["reason"])

    def test_passing_transcripts_repeat_each_delivered_notice_id_beyond_its_one_prompt(self):
        # The CLI's queue-operation and last-prompt lines and an assistant's
        # quote repeat each id, and one notice is steered into a running turn,
        # so a pass shows that only the prompts the model received count.
        kinds = set()
        for name in self.CASES:
            case = FIXTURES / name
            if json.loads((case / "expect.json").read_text())["verdict"] != "passed":
                continue
            lines = [line for path in sorted((case / "claude").rglob("*.jsonl")) for line in path.read_text().splitlines()]
            notices = json.loads((case / "profile/delegation-state.json").read_text())["notices"]
            for notice in (notice for notice in notices if notice["lane"] == "delivered"):
                holding = [json.loads(line)["type"] for line in lines if notice["id"] in line]
                self.assertGreater(len(holding), 1, f"{name} {notice['id']}")
                kinds.update(holding)
        self.assertEqual(kinds, {"queue-operation", "user", "attachment", "assistant", "last-prompt"})

    def test_s1_and_s2_like_transcripts_quote_delivered_ids_in_lines_the_app_never_sent(self):
        # Task notifications as user lines (promptSource system, and sdk with
        # a task-notification origin) and as queued commands, and another
        # agent's message as a meta queued command, each quoting a delivered
        # notice id: the passes show that none of them counts as a prompt.
        def shape(entry):
            attachment = entry.get("attachment") or {}
            if entry["type"] == "user":
                return ("user", entry.get("promptSource"), (entry.get("origin") or {}).get("kind"))
            if attachment.get("type") == "queued_command":
                return ("queued_command", attachment.get("commandMode"), attachment.get("isMeta") is True, (attachment.get("origin") or {}).get("kind") == "peer")
            return (entry["type"],)

        expected = {("user", "system", "task-notification"), ("user", "sdk", "task-notification"),
                    ("queued_command", "prompt", True, True), ("queued_command", "task-notification", False, False)}
        for name in ("s1-like", "s2-like"):
            case = FIXTURES / name
            notices = json.loads((case / "profile/delegation-state.json").read_text())["notices"]
            delivered = [notice["id"] for notice in notices if notice["lane"] == "delivered"]
            lines = [line for path in sorted((case / "claude").rglob("*.jsonl")) for line in path.read_text().splitlines()]
            self.assertLessEqual(expected, {shape(json.loads(line)) for line in lines if any(nid in line for nid in delivered)}, name)


class TranscriptReadingTests(unittest.TestCase):
    record = load_script()

    def test_only_prompts_the_model_received_count_once_each(self):
        lines = [
            {"type": "queue-operation", "operation": "enqueue", "sessionId": "s", "content": "[n-1] first"},
            {"type": "queue-operation", "operation": "dequeue", "sessionId": "s"},
            {"type": "user", "uuid": "u1", "message": {"role": "user", "content": "[n-1] first"}},
            {"type": "user", "uuid": "u1", "message": {"role": "user", "content": "[n-1] first"}},
            {"type": "assistant", "uuid": "a1", "message": {"role": "assistant", "content": [{"type": "text", "text": "Quoting [n-1]."}]}},
            {"type": "user", "uuid": "t1", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": "x", "content": "[n-1]"}]}},
            {"type": "user", "uuid": "m1", "isMeta": True, "message": {"role": "user", "content": "[n-1] as meta"}},
            {"type": "user", "uuid": "c1", "isCompactSummary": True, "message": {"role": "user", "content": "Summary: [n-1] came."}},
            {"type": "user", "uuid": "s1", "isSidechain": True, "message": {"role": "user", "content": "[n-1] to a sub-agent"}},
            {"type": "attachment", "uuid": "q1", "attachment": {"type": "queued_command", "commandMode": "prompt", "prompt": [{"type": "text", "text": "[n-2] steered"}]}},
            {"type": "queue-operation", "operation": "remove", "reason": "absorbed_mid_turn", "sessionId": "s", "content": "[n-2] steered"},
            {"type": "attachment", "uuid": "h1", "attachment": {"type": "hook_success", "content": "[n-2]"}},
            {"type": "last-prompt", "lastPrompt": "[n-1] first", "leafUuid": "a1", "sessionId": "s"},
            {"type": "user", "message": {"role": "user", "content": [{"type": "image"}, {"type": "text", "text": "[n-3] with a picture"}]}},
            {"type": "user", "uuid": "u4", "message": {"role": "user", "content": "[n-4] one line\u2028still"}},
        ]
        broken = ['{"type": "user", "uuid": "u5", "message": {"role": "us', "[1, 2]", '"text"', "", "{}"]
        text = "\n".join([json.dumps(line, ensure_ascii=False) for line in lines] + broken) + "\n"
        self.assertEqual(self.record.prompts(text), ["[n-1] first", "[n-2] steered", "[n-3] with a picture", "[n-4] one line\u2028still"])

    def test_background_task_notifications_and_peer_messages_are_not_prompts(self):
        # Each line's text is its uuid too, so no line hides behind another's.
        def user(text, **fields):
            return {"type": "user", "uuid": text, "entrypoint": "sdk-cli", "message": {"role": "user", "content": text}, **fields}

        def queued(text, **fields):
            return {"type": "attachment", "uuid": text, "attachment": {"type": "queued_command", "prompt": text, **fields}}

        task, peer = {"kind": "task-notification"}, {"kind": "peer", "from": "agent-2"}
        skipped = [
            user("[n-1] task notification", promptSource="system", origin=task),
            user("[n-1] task notification from the sdk", promptSource="sdk", origin=task),
            user("[n-1] task notification without an origin", promptSource="system"),
            user("[n-1] peer message", promptSource="system", isMeta=True, origin=peer),
            user("[n-1] peer message from the sdk", promptSource="sdk", isMeta=True, origin=peer),
            user("[n-1] peer message that is not meta", promptSource="sdk", origin=peer),
            queued("[n-1] peer command", commandMode="prompt", isMeta=True, origin=peer),
            queued("[n-1] peer command that is not meta", commandMode="prompt", origin=peer),
            queued("[n-1] meta command", commandMode="prompt", isMeta=True),
            queued("[n-1] task command", commandMode="task-notification", isMeta=False, origin=task),
            queued("[n-1] task command without an origin", commandMode="task-notification", isMeta=False),
            queued("[n-1] task-origin command", commandMode="prompt", origin=task),
        ]
        counted = [
            user("[n-2] the app's prompt", promptSource="sdk", isMeta=False),
            user("[n-2] a line without a prompt source", isMeta=False),
            user("[n-2] a typed prompt", entrypoint="cli", promptSource="typed", origin={"kind": "human"}),
            queued("[n-2] the app's steer", commandMode="prompt", isMeta=False),
            queued("[n-2] a steer without a mode"),
        ]
        for line in skipped:
            with self.subTest(skipped=line["uuid"]):
                self.assertEqual(self.record.prompts(json.dumps(line)), [])
        for line in counted:
            with self.subTest(counted=line["uuid"]):
                self.assertEqual(self.record.prompts(json.dumps(line)), [line["uuid"]])
        text = "\n".join(json.dumps(line) for line in skipped[:6] + counted[:3] + skipped[6:] + counted[3:]) + "\n"
        self.assertEqual(self.record.prompts(text), [line["uuid"] for line in counted])

    def judged(self, rule, notices, *received):
        """`rule`'s failures for `notices` of children of one parent whose transcript's prompts are `received`."""
        record = self.record.Record.__new__(self.record.Record)
        record.failures = {name: [] for name in self.record.RULE_NAMES}
        record.notices = notices
        record.by_id = {notice["childId"]: {"id": notice["childId"], "parentSessionId": "p"} for notice in notices}
        record.transcripts = {"p": list(received)}
        getattr(record, "rule_" + rule.replace("-", "_"))()
        return record.failures[rule]

    def test_a_delivered_notice_is_in_exactly_one_prompt_and_a_held_one_in_none(self):
        notices = [{"id": "n-1", "childId": "c1", "lane": "delivered"}, {"id": "n-2", "childId": "c2", "lane": "held"}]
        self.assertEqual(self.judged("transcript-once", notices, "[n-1]", "Go on."), [])
        self.assertEqual(self.judged("transcript-once", notices, "[n-1] [n-1]", "Go on."), [])
        self.assertEqual(self.judged("transcript-once", notices, "[n-1]", "[n-1] again"), ["delivered notice n-1 is in 2 of the transcript's prompts, not 1"])
        self.assertEqual(self.judged("transcript-once", notices, "Go on."), ["delivered notice n-1 is in 0 of the transcript's prompts, not 1"])
        self.assertEqual(self.judged("transcript-once", notices, "[n-1]", "[n-2]"), ["held notice n-2 is in 1 of the transcript's prompts, not 0"])

    def test_delivered_notices_reach_the_model_oldest_first_by_prompt_and_then_within_it(self):
        notices = [{"id": f"n-{i}", "childId": f"c{i}", "lane": "delivered"} for i in (1, 2, 3)]
        self.assertEqual(self.judged("oldest-first", notices, "[n-1]", "[n-2]\n\n[n-3]\n\nFrom the Mac."), [])
        self.assertEqual(self.judged("oldest-first", notices, "First, [n-1]", "[n-2]", "[n-3]"), [])
        self.assertEqual(len(self.judged("oldest-first", notices, "[n-1]", "[n-3]\n\n[n-2]")), 1)
        self.assertEqual(len(self.judged("oldest-first", notices, "[n-2]\n\n[n-3]", "[n-1]")), 1)

    def test_a_child_pane_works_in_its_worktree_or_a_folder_inside_it(self):
        within = self.record.within
        for folder in ["/w/child", "/w/child/", "/w/child/app", "/w//child/./app/"]:
            self.assertTrue(within(folder, "/w/child"), folder)
        for folder in ["/w/childish", "/w/child-2/app", "/w", "/w/child/../other", "", None]:
            self.assertFalse(within(folder, "/w/child"), folder)
        self.assertTrue(within("/w/child/app", "/w/child/"))


class MalformedRecordTests(unittest.TestCase):
    """A delegation file the app did not write is not judged: the verdict is
    invalid, naming what is wrong, and the script never crashes."""

    def judge_s1_like_with(self, mutate, name="delegation-state.json"):
        with tempfile.TemporaryDirectory(prefix="mighty-record-") as folder:
            root = Path(folder).resolve()
            materialize(FIXTURES / "s1-like", root)
            path = root / "profile" / name
            file = json.loads(path.read_text())
            mutate(file)
            path.write_text(json.dumps(file))
            return judge(root, "--json")

    def test_a_field_of_the_wrong_type_is_invalid_and_named(self):
        cases = {
            "children[0].reportRevision": lambda f: f["children"][0].__setitem__("reportRevision", "2"),
            "notices[0].reportRevision": lambda f: f["notices"][0].__setitem__("reportRevision", "1"),
            "notices[1].receipt": lambda f: f["notices"][1].__setitem__("receipt", "steer"),
            "copies[0].revision": lambda f: f["copies"][0].__setitem__("revision", True),
            "children[1].id": lambda f: f["children"][1].__setitem__("id", ["c2"]),
        }
        for field, mutate in cases.items():
            with self.subTest(field=field):
                result = self.judge_s1_like_with(mutate)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertNotIn("Traceback", result.stderr)
                answer = json.loads(result.stdout)
                self.assertEqual(answer["verdict"], "invalid")
                self.assertIn(field, answer["reason"])

    def test_a_shape_no_check_foresaw_is_invalid_rather_than_a_crash(self):
        # A pane whose id is a list cannot key the pane table.
        result = self.judge_s1_like_with(lambda state: state["sessions"].append({"id": ["x"]}), name="workspace-state.json")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertNotIn("Traceback", result.stderr)
        answer = json.loads(result.stdout)
        self.assertEqual(answer["verdict"], "invalid")
        self.assertIn("unexpected shape", answer["reason"])
        self.assertIn(", line ", answer["reason"])

    def test_files_that_cannot_be_read_are_invalid_rather_than_a_crash(self):
        # With no git on the PATH the repository cannot be read.
        with tempfile.TemporaryDirectory(prefix="mighty-record-") as folder:
            root = Path(folder).resolve()
            materialize(FIXTURES / "s1-like", root)
            result = subprocess.run([sys.executable, str(SCRIPT), "--profile", str(root / "profile"), "--repo", str(root / "repo"),
                                     "--claude-dir", str(root / "claude"), "--json"], capture_output=True, text=True,
                                    env={**os.environ, "PATH": str(root / "no-such-bin")})
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertNotIn("Traceback", result.stderr)
        answer = json.loads(result.stdout)
        self.assertEqual(answer["verdict"], "invalid")
        self.assertIn("could not be read", answer["reason"])


if __name__ == "__main__":
    unittest.main()
