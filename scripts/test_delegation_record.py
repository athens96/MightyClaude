#!/usr/bin/env python3
"""Run scripts/delegation-record.py over the synthetic fixtures in
scripts/fixtures/delegation-record/: each case is built in a temp folder (its
git repository from repo.json, its profile and Claude folder with the
@{root} and @{commit:NAME} placeholders filled in) and judged read-only."""
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
            parts = [claude_project_folder(str(root / "repo")) if part == "REPO" else part for part in relative.parts]
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


class DelegationRecordFixtureTests(unittest.TestCase):
    CASES = ["s1-like", "s2-like", "duplicate-notice-id", "deleted-unmerged-branch", "missing-panes", "no-transcript"]

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
                if expect["verdict"] == "invalid":
                    self.assertIn("panes missing", answer["reason"])


if __name__ == "__main__":
    unittest.main()
