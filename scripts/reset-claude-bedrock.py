#!/usr/bin/env python3
"""Plan a narrow Claude Bedrock reset; --apply backs up and changes user files.

Does not log out Claude/Codex, touch ~/.aws, or execute shell startup files.
Run from a normal terminal: this workspace's sandbox cannot write user settings.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shlex
import stat
import subprocess
import tempfile

LIMIT = 1024 * 1024
BEDROCK_KEYS = frozenset({
    "AWS_BEARER_TOKEN_BEDROCK", "CLAUDE_CODE_USE_BEDROCK",
    "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "ANTHROPIC_BEDROCK_BASE_URL",
})
REGION_KEYS = frozenset({"AWS_REGION", "AWS_DEFAULT_REGION"})
MODEL_KEYS = frozenset({"ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
    "ANTHROPIC_DEFAULT_OPUS_MODEL"})
# Explicit model selectors/picker definitions from Claude Code's model-config
# and env-vars references; do not match arbitrary keys containing "MODEL".
PINNED_MODEL_KEYS = frozenset({"ANTHROPIC_DEFAULT_HAIKU_MODEL",
    "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
    "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_CUSTOM_MODEL_OPTION"})
ALL_MODEL_KEYS = MODEL_KEYS | PINNED_MODEL_KEYS | frozenset({
    "ANTHROPIC_DEFAULT_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL",
    "CLAUDE_CODE_SUBAGENT_MODEL_FORCE", "ANTHROPIC_SMALL_FAST_MODEL_AWS_REGION",
}) | frozenset(key + suffix for key in PINNED_MODEL_KEYS
    for suffix in ("_NAME", "_DESCRIPTION", "_SUPPORTED_CAPABILITIES"))
MODEL_SETTING_KEYS = frozenset({"model", "fallbackModel", "advisorModel",
    "modelOverrides", "availableModels", "modelPicker"})
MARKER = "# Bedrock: configure Seoul after credentials, before OpenClaw completion."
SHELL_FILES = (".zshenv", ".zprofile", ".zshrc", ".zlogin",
               ".bash_profile", ".bashrc", ".profile")
SETTINGS_FILES = (".claude/settings.json", ".claude/settings.local.json")
BEDROCK_MODEL = re.compile(r"^(?:(?:us|eu|apac|global)\.)?anthropic\.|^arn:aws[^:]*:bedrock:")
ASSIGNMENT = re.compile(
    r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z_0-9]*)="
    r"(?:[A-Za-z0-9_./:+,=@%-]*|'[^'\r\n]*'|\"[^\"$`\\\r\n]*\")"
    r"\s*(?:#.*)?$")


class ResetError(Exception):
    """Messages are fixed categories, never input or exception text."""


def safe_read(path, home):
    for parent in (path, *path.parents):
        if parent == home.parent:
            break
        if parent.is_symlink():
            raise ResetError("symlink_refused")
    try:
        before = path.lstat()
    except FileNotFoundError:
        return None
    if not stat.S_ISREG(before.st_mode):
        raise ResetError("nonregular_file_refused")
    fd = os.open(str(path), os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd, "rb") as handle:
        actual = os.fstat(handle.fileno())
        if (actual.st_dev, actual.st_ino) != (before.st_dev, before.st_ino):
            raise ResetError("concurrent_change")
        data = handle.read(LIMIT + 1)
    if len(data) > LIMIT:
        raise ResetError("file_too_large")
    return data, actual


def decode(data):
    try:
        return data.decode("utf-8")
    except UnicodeError:
        raise ResetError("invalid_utf8") from None


def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ResetError("duplicate_json_key")
        result[key] = value
    return result


def reset_settings(data, reset_models=False):
    try:
        settings = json.loads(decode(data), object_pairs_hook=no_duplicates)
    except (ValueError, TypeError):
        raise ResetError("invalid_json") from None
    if not isinstance(settings, dict) or not isinstance(settings.get("env", {}), dict):
        raise ResetError("invalid_settings_shape")
    env = settings.get("env", {})
    enabled = str(env.get("CLAUDE_CODE_USE_BEDROCK", "")).lower() in ("1", "true")
    removed = []
    for key in list(env):
        if key in BEDROCK_KEYS or (enabled and key in REGION_KEYS) or (
                reset_models and key in ALL_MODEL_KEYS) or (
                key in MODEL_KEYS and isinstance(env[key], str) and BEDROCK_MODEL.match(env[key])):
            del env[key]
            removed.append("env." + key)
    if reset_models:
        for key in sorted(MODEL_SETTING_KEYS):
            if key in settings:
                del settings[key]
                removed.append(key)
    elif isinstance(settings.get("model"), str) and BEDROCK_MODEL.match(settings["model"]):
        del settings["model"]
        removed.append("model")
    if not removed:
        return data, []
    return (json.dumps(settings, ensure_ascii=False, indent=2) + "\n").encode(), removed


def validate_shell(data):
    result = subprocess.run(["/bin/zsh", "-f", "-n"], input=data,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
    if result.returncode:
        raise ResetError("shell_syntax_invalid")


def reset_shell(data, reset_models=False):
    lines = decode(data).splitlines(keepends=True)
    output, removed = [], []
    marked_region = False
    candidates = BEDROCK_KEYS | (ALL_MODEL_KEYS if reset_models else MODEL_KEYS)
    reference = re.compile(r"\b(?:" + "|".join(sorted(candidates)) + r")\b")
    for index, line in enumerate(lines):
        stripped = line.strip()
        if stripped == MARKER:
            if marked_region:
                raise ResetError("ambiguous_shell_region")
            marked_region = True
            continue
        if marked_region:
            match = ASSIGNMENT.fullmatch(line.rstrip("\r\n"))
            if not match or match[1] not in REGION_KEYS:
                raise ResetError("ambiguous_shell_region")
            removed.append(match[1])
            marked_region = False
            continue
        if not stripped or stripped.startswith("#") or not reference.search(line):
            output.append(line)
            continue
        if index and lines[index - 1].rstrip("\r\n").endswith("\\"):
            raise ResetError("ambiguous_shell_assignment")
        match = ASSIGNMENT.fullmatch(line.rstrip("\r\n"))
        if not match or match[1] not in candidates:
            raise ResetError("ambiguous_shell_assignment")
        try:
            words = shlex.split(line, comments=True, posix=True)
            value = words[-1].split("=", 1)[1]
        except (ValueError, IndexError):
            raise ResetError("ambiguous_shell_assignment") from None
        key = match[1]
        if key in BEDROCK_KEYS or reset_models or BEDROCK_MODEL.match(value):
            removed.append(key)
        else:
            output.append(line)
    if marked_region:
        raise ResetError("ambiguous_shell_region")
    changed = "".join(output).encode()
    if changed != data:
        validate_shell(data)
        validate_shell(changed)
    return changed, removed


def make_plan(home, reset_models=False):
    home = Path(home).absolute()
    changes = []
    for relative in (*SHELL_FILES, *SETTINGS_FILES):
        path = home / relative
        original = safe_read(path, home)
        if original is None:
            continue
        data, info = original
        updated, removed = (reset_settings if relative in SETTINGS_FILES else reset_shell)(
            data, reset_models=reset_models)
        if updated != data:
            changes.append(dict(path=path, relative=relative, original=data,
                                updated=updated, info=info, removed=removed))
    return changes


def assert_unchanged(change, home):
    current = safe_read(change["path"], home)
    before = change["info"]
    if current is None:
        raise ResetError("concurrent_change")
    data, info = current
    if data != change["original"] or (
            info.st_dev, info.st_ino, info.st_mtime_ns, info.st_mode) != (
            before.st_dev, before.st_ino, before.st_mtime_ns, before.st_mode):
        raise ResetError("concurrent_change")


def apply_plan(home, changes, report):
    if not changes:
        return
    for change in changes:
        assert_unchanged(change, home)
    backup = Path(tempfile.mkdtemp(prefix=".claude-bedrock-reset-backup-", dir=str(home)))
    os.chmod(backup, 0o700)
    report["backupDirectory"] = str(backup)
    for index, change in enumerate(changes):
        fd = os.open(str(backup / (str(index) + "-" + change["path"].name)),
                     os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(fd, "wb") as handle:
            handle.write(change["original"])
            handle.flush()
            os.fsync(handle.fileno())
    for change in changes:
        assert_unchanged(change, home)
    for change in changes:
        fd, name = tempfile.mkstemp(prefix=".bedrock-reset-", dir=str(change["path"].parent))
        try:
            with os.fdopen(fd, "wb") as handle:
                handle.write(change["updated"])
                handle.flush()
                os.fchmod(handle.fileno(), stat.S_IMODE(change["info"].st_mode))
                os.fsync(handle.fileno())
            assert_unchanged(change, home)
            os.replace(name, str(change["path"]))
            report["changedFiles"].append(change["relative"])
        finally:
            if os.path.exists(name):
                os.unlink(name)


def run(home, apply=False, reset_models=False):
    report = {"applied": False, "changedFiles": [], "plannedChanges": [],
              "scope": "user_shell_and_claude_settings_only", "resetModels": reset_models}
    try:
        home = Path(home).absolute()
        changes = make_plan(home, reset_models=reset_models)
        report["plannedChanges"] = [{"file": item["relative"], "remove": item["removed"]}
                                    for item in changes]
        if apply:
            apply_plan(home, changes, report)
            report["applied"] = True
    except ResetError as error:
        report["error"] = str(error)
    except (OSError, subprocess.SubprocessError):
        report["error"] = "filesystem_or_validation_failed"
    report["partial"] = bool(report["changedFiles"]) and not report["applied"]
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="back up and apply the reset")
    parser.add_argument("--reset-models", action="store_true",
        help="also clear Claude model pins, aliases, overrides, and custom picker definitions")
    args = parser.parse_args()
    report = run(Path.home(), args.apply, reset_models=args.reset_models)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print("Existing processes retain their environment. Fully quit MightyClaude and old terminals.")
    print("Open a fresh terminal before configuring Claude Code again.")
    print("In the login terminal, also run: unset AWS_BEARER_TOKEN_BEDROCK CLAUDE_CODE_USE_BEDROCK AWS_REGION")
    if args.reset_models:
        print("Clear inherited Claude model pins in that terminal: unset " + " ".join(sorted(ALL_MODEL_KEYS)))
    return 1 if "error" in report else 0


if __name__ == "__main__":
    raise SystemExit(main())
