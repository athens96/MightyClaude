#!/usr/bin/env python3
"""Judge a delegation run from what it left on disk (macOS app, read-only).

    python3 scripts/delegation-record.py --profile DIR --repo DIR --claude-dir DIR [--json]

--profile     the app profile folder: workspace-state.json and delegation-state.json
--repo        the parent pane's git checkout
--claude-dir  the Claude config folder whose projects/ holds the transcripts

The rules are the on-disk ones the headless scenarios S1 and S2 check, for
any run: the delegation file, the panes it names, the repository and the
parent's Claude-format transcript. Each rule is listed once with its result.
Nothing is written: git runs only read commands, without optional locks.

Verdicts: passed (exit 0), failed (exit 1, naming each failed rule), invalid
(exit 2) when the record cannot be judged: a profile file is missing or
unreadable, it holds no child, the repository is not a git checkout, or a
pane the record names is missing (each child's parent pane, and the pane of
every child that is still open).
"""
import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

DELEGATION_FILE = "delegation-state.json"
STATE_FILE = "workspace-state.json"
FILE_CAP = 4 * 1024 * 1024
COPY_CAP = 64 * 1024
TRUNCATION_MARKER = "\n\n[truncated at 64 KiB: the full file stays in the child's worktree until cleanup]\n"
OPEN_CHILDREN_CAP = 3
FOLLOW_UP_CAP = 2
MODES = ["plan", "manual", "acceptEdits", "auto", "fullAccess"]
STATES = ["creating", "running", "waiting", "reported", "ended", "interrupted", "merged", "failed", "closed", "discarded"]
CLOSED_STATES = {"failed", "closed", "discarded"}
LANES = {"pending", "held", "delivered"}
ROUTES = {"steer", "queue"}
NOTICE_KINDS = {"reported", "ended_without_report", "failed_to_start"}
MERGE_KINDS = {"tool_fast_forward", "card_fast_forward", "card_merge_commit"}

RULES = [
    ("file-cap", "the delegation file is at most 4 MiB and each TASK/REPORT copy at most 64 KiB, a cut copy ending in the truncation marker"),
    ("unique-ids", "child, notice and follow-up ids are each unique"),
    ("child-records", "each child has its parent link, branch mighty/<id>, base commit, a known starting mode and a known state"),
    ("one-layer", "no child is the parent of another child"),
    ("width-cap", "a parent has at most 3 open children"),
    ("follow-up-cap", "a child has at most 2 follow-ups"),
    ("pane-links", "parent and child panes are Claude panes, and each child pane carries its parent link and worktree"),
    ("one-notice-per-revision", "a notice names a child and a revision it reached, one reported notice per revision, one for each open child's current report"),
    ("receipts", "delivered notices and follow-ups carry one receipt (time, steer or queue, run); pending and held ones none"),
    ("merge-records", "each merge record lands its child head: a fast-forward to it, or one merge commit of the old parent head and it"),
    ("merged-in-parent", "a merged child has a merge record of its reported head, and that head is in its parent branch"),
    ("unmerged-kept", "work that is not merged keeps its branch; only a closed child's merged branch may be gone"),
    ("discarded-gone", "a discarded child's branch and worktree are gone"),
    ("parent-transcript", "each parent pane's Claude-format transcript is found"),
    ("transcript-once", "each delivered notice id is in its parent's transcript exactly once, a held or pending one not at all"),
    ("oldest-first", "a parent's delivered notices reach its transcript oldest first"),
]
RULE_NAMES = [name for name, _ in RULES]


class Invalid(Exception):
    pass


def claude_project_folder(path):
    """The folder name Claude gives a working folder under projects/."""
    return "".join(c if c.isascii() and c.isalnum() else "-" for c in path)


def read_json(path, what):
    if not path.is_file():
        raise Invalid(f"{what} is missing: {path.name}")
    try:
        with path.open("rb") as handle:
            return json.load(handle)
    except (OSError, ValueError) as error:
        raise Invalid(f"{what} is unreadable: {error}")


class Git:
    def __init__(self, repo):
        self.repo = repo
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_OPTIONAL_LOCKS="0", GIT_CONFIG_NOSYSTEM="1", GIT_TERMINAL_PROMPT="0")

    def run(self, *args):
        result = subprocess.run(["git", "-C", str(self.repo), *args], capture_output=True, text=True, env=self.env)
        return result.returncode, result.stdout.strip()

    def commit(self, ref):
        code, out = self.run("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}")
        return out if code == 0 and out else None

    def branch(self, name):
        return self.commit(f"refs/heads/{name}") if name else None

    def is_ancestor(self, older, newer):
        return self.run("merge-base", "--is-ancestor", older, newer)[0] == 0

    def parents(self, commit):
        code, out = self.run("rev-list", "--parents", "-n", "1", commit)
        return out.split()[1:] if code == 0 else None


class Record:
    def __init__(self, profile, repo, claude_dir):
        self.profile, self.repo, self.claude_dir = profile, repo, claude_dir
        self.failures = {name: [] for name in RULE_NAMES}
        path = profile / DELEGATION_FILE
        self.file_bytes = path.stat().st_size if path.is_file() else 0
        self.file = read_json(path, "the delegation file")
        state = read_json(profile / STATE_FILE, "the workspace state file")
        if not isinstance(self.file, dict) or self.file.get("version") != 1:
            raise Invalid("the delegation file is not a version 1 delegation file")
        self.children = self.list_of("children")
        self.notices = self.list_of("notices")
        self.follow_ups = self.list_of("followUps")
        self.merges = self.list_of("merges")
        self.copies = self.list_of("copies")
        if not self.children:
            raise Invalid("the delegation file holds no child")
        if not isinstance(state, dict):
            raise Invalid("the workspace state file is unreadable")
        self.workspaces = {w.get("id"): w for w in state.get("workspaces") or [] if isinstance(w, dict)}
        self.panes = {s.get("id"): s for s in state.get("sessions") or [] if isinstance(s, dict)}
        self.by_id = {c.get("id"): c for c in self.children}
        missing = sorted({c.get("parentSessionId") for c in self.children if c.get("parentSessionId") not in self.panes} - {None})
        missing += sorted(c.get("id") for c in self.children if c.get("state") not in CLOSED_STATES and c.get("id") not in self.panes)
        if missing:
            raise Invalid("panes missing from the workspace state file: " + ", ".join(map(str, missing)))
        if not repo.is_dir() or Git(repo).run("rev-parse", "--git-dir")[0] != 0:
            raise Invalid(f"the repository is not a git checkout: {repo.name}")
        self.git = Git(repo)

    def list_of(self, key):
        value = self.file.get(key)
        if not isinstance(value, list) or not all(isinstance(item, dict) for item in value):
            raise Invalid(f"the delegation file's {key} is not a list of records")
        return value

    def fail(self, rule, detail):
        self.failures[rule].append(detail)

    def check(self):
        for name in RULE_NAMES:
            getattr(self, "rule_" + name.replace("-", "_"))()
        return self.failures

    def rule_file_cap(self):
        if self.file_bytes > FILE_CAP:
            self.fail("file-cap", f"the delegation file takes {self.file_bytes} bytes")
        for copy in self.copies:
            text = copy.get("text") or ""
            stored = len(json.dumps(text, ensure_ascii=False).encode()) - 2
            if stored > COPY_CAP:
                self.fail("file-cap", f"{copy.get('kind')} copy of {copy.get('childId')} takes {stored} bytes")
            if copy.get("truncated") and not text.endswith(TRUNCATION_MARKER):
                self.fail("file-cap", f"cut {copy.get('kind')} copy of {copy.get('childId')} lacks the truncation marker")

    def rule_unique_ids(self):
        for label, records in (("child", self.children), ("notice", self.notices), ("follow-up", self.follow_ups)):
            seen = set()
            for record in records:
                if record.get("id") in seen:
                    self.fail("unique-ids", f"{label} id {record.get('id')} appears more than once")
                seen.add(record.get("id"))

    def rule_child_records(self):
        for child in self.children:
            cid = child.get("id")
            for key in ("id", "parentSessionId", "worktreePath", "parentBranch", "baseCommit", "requestKey"):
                if not isinstance(child.get(key), str) or not child.get(key):
                    self.fail("child-records", f"child {cid} has no {key}")
            if child.get("branch") != f"mighty/{cid}":
                self.fail("child-records", f"child {cid} is on branch {child.get('branch')}, not mighty/{cid}")
            if child.get("startingMode") not in MODES:
                self.fail("child-records", f"child {cid} has an unknown starting mode {child.get('startingMode')}")
            if child.get("state") not in STATES:
                self.fail("child-records", f"child {cid} has an unknown state {child.get('state')}")
            if child.get("state") in ("reported", "merged") and (not child.get("reportHead") or (child.get("reportRevision") or 0) < 1):
                self.fail("child-records", f"child {cid} is {child.get('state')} with no reported revision and head")

    def rule_one_layer(self):
        for child in self.children:
            if child.get("parentSessionId") in self.by_id:
                self.fail("one-layer", f"child {child.get('id')} has child {child.get('parentSessionId')} as its parent")

    def rule_width_cap(self):
        open_by_parent = {}
        for child in self.children:
            if child.get("state") not in CLOSED_STATES:
                open_by_parent.setdefault(child.get("parentSessionId"), []).append(child.get("id"))
        for parent, ids in open_by_parent.items():
            if len(ids) > OPEN_CHILDREN_CAP:
                self.fail("width-cap", f"parent {parent} has {len(ids)} open children")

    def rule_follow_up_cap(self):
        for child in self.children:
            count = sum(1 for f in self.follow_ups if f.get("childId") == child.get("id"))
            if count > FOLLOW_UP_CAP or (child.get("followUpCount") or 0) > FOLLOW_UP_CAP:
                self.fail("follow-up-cap", f"child {child.get('id')} has {max(count, child.get('followUpCount') or 0)} follow-ups")

    def rule_pane_links(self):
        for parent in sorted({c.get("parentSessionId") for c in self.children}, key=str):
            pane = self.panes.get(parent)
            if pane and (pane.get("kind"), pane.get("provider")) != ("claude", "claude"):
                self.fail("pane-links", f"parent pane {parent} is not a Claude pane")
        for child in self.children:
            pane = self.panes.get(child.get("id"))
            if not pane:
                continue
            if (pane.get("kind"), pane.get("provider")) != ("claude", "claude"):
                self.fail("pane-links", f"child pane {child.get('id')} is not a Claude pane")
            if pane.get("parentSessionId") != child.get("parentSessionId"):
                self.fail("pane-links", f"child pane {child.get('id')} links parent {pane.get('parentSessionId')}, not {child.get('parentSessionId')}")
            if pane.get("workingFolder") != child.get("worktreePath"):
                self.fail("pane-links", f"child pane {child.get('id')} works in another folder than its worktree")

    def rule_one_notice_per_revision(self):
        seen = set()
        for notice in self.notices:
            child = self.by_id.get(notice.get("childId"))
            if notice.get("kind") not in NOTICE_KINDS or child is None:
                self.fail("one-notice-per-revision", f"notice {notice.get('id')} has kind {notice.get('kind')} for child {notice.get('childId')}")
                continue
            if (notice.get("reportRevision") or 0) > (child.get("reportRevision") or 0):
                self.fail("one-notice-per-revision", f"notice {notice.get('id')} names revision {notice.get('reportRevision')}, beyond its child's")
            if notice.get("kind") == "reported":
                key = (notice.get("childId"), notice.get("reportRevision"))
                if key in seen:
                    self.fail("one-notice-per-revision", f"child {key[0]} has more than one notice for revision {key[1]}")
                seen.add(key)
        for child in self.children:
            revision = child.get("reportRevision") or 0
            if child.get("state") not in CLOSED_STATES and revision >= 1 and (child.get("id"), revision) not in seen:
                self.fail("one-notice-per-revision", f"child {child.get('id')} has no notice for its revision {revision}")

    def rule_receipts(self):
        for label, records in (("notice", self.notices), ("follow-up", self.follow_ups)):
            for record in records:
                lane, receipt = record.get("lane"), record.get("receipt")
                if lane not in LANES:
                    self.fail("receipts", f"{label} {record.get('id')} is in an unknown lane {lane}")
                elif lane == "delivered":
                    if not isinstance(receipt, dict) or not receipt.get("time") or receipt.get("route") not in ROUTES or not receipt.get("runId"):
                        self.fail("receipts", f"delivered {label} {record.get('id')} has no complete receipt")
                elif receipt is not None:
                    self.fail("receipts", f"{lane} {label} {record.get('id')} already has a receipt")

    def rule_merge_records(self):
        for merge in self.merges:
            cid, kind = merge.get("childId"), merge.get("kind")
            pre, merged, head = (self.git.commit(merge.get(k) or "") for k in ("preMergeCommit", "mergedCommit", "childHead"))
            if kind not in MERGE_KINDS or not all((pre, merged, head)):
                self.fail("merge-records", f"merge of {cid} ({kind}) names commits the repository lacks")
            elif kind == "card_merge_commit":
                if self.git.parents(merged) != [pre, head]:
                    self.fail("merge-records", f"merge commit of {cid} does not have the old parent head and the child head as parents")
            elif merged != head or not self.git.is_ancestor(pre, head):
                self.fail("merge-records", f"{kind} of {cid} is not a fast-forward to the child head")

    def rule_merged_in_parent(self):
        for child in self.children:
            if child.get("state") != "merged":
                continue
            head = child.get("reportHead")
            if not any(m.get("childId") == child.get("id") and m.get("childHead") == head for m in self.merges):
                self.fail("merged-in-parent", f"merged child {child.get('id')} has no merge record of its reported head")
            tip = self.git.branch(child.get("parentBranch"))
            if not head or not tip or not self.git.is_ancestor(head, tip):
                self.fail("merged-in-parent", f"merged child {child.get('id')}'s head is not in {child.get('parentBranch')}")

    def rule_unmerged_kept(self):
        for child in self.children:
            state = child.get("state")
            if state in ("discarded", "failed", "creating") or self.git.branch(child.get("branch")):
                continue
            last = child.get("closedHead") or child.get("reportHead") or child.get("baseCommit")
            tip = self.git.branch(child.get("parentBranch"))
            merged = bool(last and tip and self.git.commit(last) and self.git.is_ancestor(last, tip))
            if state != "closed" or not merged:
                self.fail("unmerged-kept", f"{state} child {child.get('id')}'s branch {child.get('branch')} is gone" + ("" if merged else " and its work is not merged"))

    def rule_discarded_gone(self):
        for child in self.children:
            if child.get("state") != "discarded":
                continue
            if self.git.branch(child.get("branch")):
                self.fail("discarded-gone", f"discarded child {child.get('id')} still has branch {child.get('branch')}")
            if child.get("worktreePath") and Path(child["worktreePath"]).exists():
                self.fail("discarded-gone", f"discarded child {child.get('id')} still has its worktree")

    def transcript(self, parent):
        """The parent pane's transcript text, or None when none is found."""
        pane = self.panes.get(parent) or {}
        folder = pane.get("workingFolder") or (self.workspaces.get(pane.get("workspaceId")) or {}).get("path") or ""
        projects = self.claude_dir / "projects"
        names = {claude_project_folder(folder), claude_project_folder(os.path.realpath(folder))} if folder else set()
        resume = pane.get("resumeId")
        found = []
        for name in sorted(names):
            directory = projects / name
            if resume:
                found += [p for p in [directory / f"{resume}.jsonl"] if p.is_file()]
            elif directory.is_dir():
                candidates = sorted(directory.glob("*.jsonl"))
                found += candidates if len(candidates) == 1 else []
        if not found and resume and projects.is_dir():
            found = sorted(projects.glob(f"*/{resume}.jsonl"))
        if not found:
            return None
        try:
            return found[0].read_text(encoding="utf-8")
        except OSError:
            return None

    def rule_parent_transcript(self):
        self.transcripts = {}
        for parent in sorted({c.get("parentSessionId") for c in self.children}, key=str):
            text = self.transcript(parent)
            if text is None:
                self.fail("parent-transcript", f"no Claude transcript for parent pane {parent} under {self.claude_dir.name}/projects")
            else:
                self.transcripts[parent] = text

    def parent_of(self, notice):
        return (self.by_id.get(notice.get("childId")) or {}).get("parentSessionId")

    def rule_transcript_once(self):
        for notice in self.notices:
            text = self.transcripts.get(self.parent_of(notice))
            if text is None or not notice.get("id"):
                continue
            count = text.count(notice["id"])
            expected = 1 if notice.get("lane") == "delivered" else 0
            if count != expected:
                self.fail("transcript-once", f"{notice.get('lane')} notice {notice['id']} is in the transcript {count} times, not {expected}")

    def rule_oldest_first(self):
        for parent, text in self.transcripts.items():
            delivered = [n["id"] for n in self.notices if n.get("lane") == "delivered" and n.get("id") and self.parent_of(n) == parent]
            positions = [text.find(nid) for nid in delivered if text.count(nid) == 1]
            if positions != sorted(positions):
                self.fail("oldest-first", f"parent {parent}'s delivered notices reach its transcript out of order")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Judge a delegation run from what it left on disk (read-only).")
    parser.add_argument("--profile", required=True, type=Path, help="app profile folder (workspace-state.json, delegation-state.json)")
    parser.add_argument("--repo", required=True, type=Path, help="the parent pane's git checkout")
    parser.add_argument("--claude-dir", required=True, type=Path, help="Claude config folder holding projects/")
    parser.add_argument("--json", action="store_true", help="print the result as JSON")
    args = parser.parse_args(argv)
    try:
        failures = Record(args.profile, args.repo, args.claude_dir).check()
        failed = [name for name in RULE_NAMES if failures[name]]
        verdict, reason = ("failed" if failed else "passed"), None
    except Invalid as error:
        failures, failed, verdict, reason = None, [], "invalid", str(error)
    if args.json:
        rules = [{"rule": name, "checks": text, "result": "skipped" if failures is None else ("failed" if failures[name] else "passed"),
                  "details": [] if failures is None else failures[name]} for name, text in RULES]
        print(json.dumps({"verdict": verdict, "failed": failed, "reason": reason, "rules": rules}, indent=2))
    else:
        print(f"Delegation record: {len(RULES)} rules")
        for name, text in RULES:
            mark = "skip" if failures is None else ("FAIL" if failures[name] else "pass")
            print(f"  {mark}  {name:<24} {text}")
            for detail in (failures or {}).get(name, []):
                print(f"        - {detail}")
        print(f"Verdict: {verdict}" + (f" ({', '.join(failed)})" if failed else "") + (f": {reason}" if reason else ""))
    return {"passed": 0, "failed": 1, "invalid": 2}[verdict]


if __name__ == "__main__":
    sys.exit(main())
