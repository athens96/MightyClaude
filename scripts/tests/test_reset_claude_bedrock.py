import importlib.util
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "reset_bedrock", Path(__file__).resolve().parents[1] / "reset-claude-bedrock.py")
reset = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reset)


class ResetTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.home = Path(self.tmp.name)
        (self.home / ".claude").mkdir()
        self.shell = self.home / ".zshrc"
        self.settings = self.home / ".claude/settings.json"
        self.shell.write_text("# ordinary config\nexport PATH=/usr/bin\n"
            "export AWS_BEARER_TOKEN_BEDROCK='fixture-secret'\n"
            + reset.MARKER + "\nexport AWS_REGION=ap-northeast-2\n"
            "source <(openclaw completion --shell zsh)\n")
        self.settings.write_text(json.dumps({"env": {
            "CLAUDE_CODE_USE_BEDROCK": "1", "AWS_REGION": "ap-northeast-2",
            "AWS_BEARER_TOKEN_BEDROCK": "fixture-secret", "EDITOR": "vim",
            "AWS_ACCESS_KEY_ID": "unrelated-aws"},
            "model": "apac.anthropic.claude-example", "permissions": {"allow": []}}))

    def test_dry_run_never_writes_or_exposes_secrets(self):
        before = {p: p.read_bytes() for p in (self.shell, self.settings)}
        report = reset.run(self.home)
        self.assertNotIn("error", report)
        self.assertFalse(report["applied"])
        self.assertEqual(len(report["plannedChanges"]), 2)
        self.assertNotIn("fixture-secret", json.dumps(report))
        self.assertNotIn("unrelated-aws", json.dumps(report))
        for path, data in before.items():
            self.assertEqual(path.read_bytes(), data)
        self.assertEqual(list(self.home.glob(".claude-bedrock-reset-backup-*")), [])

    def test_apply_backup_preservation_and_idempotence(self):
        originals = [self.shell.read_bytes(), self.settings.read_bytes()]
        oauth = self.home / ".claude/.credentials.json"
        oauth.write_text("untouched-oauth")
        report = reset.run(self.home, apply=True)
        self.assertTrue(report["applied"], report)
        self.assertEqual(len(report["changedFiles"]), 2)
        self.assertEqual(self.shell.read_text(), "# ordinary config\nexport PATH=/usr/bin\n"
                         "source <(openclaw completion --shell zsh)\n")
        settings = json.loads(self.settings.read_text())
        self.assertEqual(settings, {"env": {"EDITOR": "vim", "AWS_ACCESS_KEY_ID": "unrelated-aws"},
                                    "permissions": {"allow": []}})
        self.assertEqual(oauth.read_text(), "untouched-oauth")
        backup = Path(report["backupDirectory"])
        self.assertEqual(stat.S_IMODE(backup.stat().st_mode), 0o700)
        copies = sorted(backup.iterdir())
        self.assertEqual([p.read_bytes() for p in copies], originals)
        self.assertTrue(all(stat.S_IMODE(p.stat().st_mode) == 0o600 for p in copies))
        second = reset.run(self.home, apply=True)
        self.assertTrue(second["applied"])
        self.assertEqual(second["changedFiles"], [])
        self.assertNotIn("backupDirectory", second)

    def test_ambiguous_shell_refuses_all_writes(self):
        for line in ("export AWS_BEARER_TOKEN_BEDROCK=fixture; echo other\n",
                     "export OTHER=x \\\nAWS_BEARER_TOKEN_BEDROCK=fixture\necho hello\n",
                     "export AWS_BEARER_TOKEN_BEDROCK=$(echo fixture)\n",
                     "export AWS_BEARER_TOKEN_BEDROCK='multi\nline'\n",
                     "export AWS_BEARER_TOKEN_BEDROCK=fixture \\\n next\n"):
            with self.subTest(line=line):
                self.shell.write_text(line)
                settings = self.settings.read_bytes()
                report = reset.run(self.home, apply=True)
                self.assertEqual(report["error"], "ambiguous_shell_assignment")
                self.assertEqual(report["changedFiles"], [])
                self.assertEqual(self.settings.read_bytes(), settings)

    def test_symlink_invalid_json_and_duplicate_keys_refuse(self):
        self.shell.unlink()
        self.shell.symlink_to(self.settings)
        self.assertEqual(reset.run(self.home, True)["error"], "symlink_refused")
        self.shell.unlink()
        for value in ("broken", '{"env": [], "model": "x"}', '{"env": {}, "env": {}}'):
            self.settings.write_text(value)
            report = reset.run(self.home, True)
            self.assertIn("error", report)
            self.assertEqual(report["changedFiles"], [])

    def test_unrelated_regions_models_and_credentials_preserved(self):
        raw = b'export AWS_REGION=us-east-1\nexport ANTHROPIC_MODEL=sonnet\n'
        self.assertEqual(reset.reset_shell(raw), (raw, []))
        raw = b'{"env":{"AWS_REGION":"us-east-1","AWS_PROFILE":"other"},"model":"sonnet"}'
        self.assertEqual(reset.reset_settings(raw), (raw, []))

    def test_concurrent_change_before_apply_is_refused(self):
        plan = reset.make_plan(self.home)
        self.shell.write_text("# newer edits\n")
        report = {"changedFiles": []}
        with self.assertRaises(reset.ResetError):
            reset.apply_plan(self.home, plan, report)
        self.assertEqual(self.shell.read_text(), "# newer edits\n")
        self.assertEqual(report["changedFiles"], [])
        self.assertNotIn("backupDirectory", report)

    def test_partial_failure_keeps_backups_and_reports_changed_file(self):
        real_replace = reset.os.replace
        def fail_second(source, destination):
            if Path(destination) == self.settings:
                raise PermissionError("fixture-secret")
            return real_replace(source, destination)
        with patch.object(reset.os, "replace", side_effect=fail_second):
            report = reset.run(self.home, True)
        self.assertTrue(report["partial"])
        self.assertFalse(report["applied"])
        self.assertEqual(report["changedFiles"], [".zshrc"])
        self.assertEqual(len(list(Path(report["backupDirectory"]).iterdir())), 2)
        self.assertNotIn("fixture-secret", json.dumps(report))
        self.assertEqual(list((self.home / ".claude").glob(".bedrock-reset-*")), [])

    def test_model_reset_clears_selectors_catalogs_and_all_documented_pins(self):
        model_settings = {
            "model": "opus", "fallbackModel": "sonnet", "advisorModel": "fable",
            "modelOverrides": {"claude-opus-5": "private-deployment"},
            "availableModels": ["opus", "sonnet"],
            "modelPicker": {"models": [{"model": "private-deployment", "name": "Custom"}]},
        }
        keep = {"switchModelsOnFlag": False, "effortLevel": "high",
                "permissions": {"allow": []}, "enabledPlugins": {"fixture": True},
                "customModelNotes": "preserve unrecognized keys"}
        keep_env = {"ANTHROPIC_API_KEY": "fixture-auth", "AWS_PROFILE": "work",
                    "MODEL_CACHE_PATH": "fixture-cache", "ANTHROPIC_BASE_URL": "fixture-gateway"}
        env = {key: "fixture-pin" for key in reset.ALL_MODEL_KEYS}
        env.update(keep_env)
        self.settings.write_text(json.dumps({**model_settings, **keep, "env": env}))
        self.shell.write_text("# user model defaults\n" + "".join(
            "export " + key + "='fixture-pin'\n" for key in sorted(reset.ALL_MODEL_KEYS))
            + "export MODEL_CACHE_PATH='fixture-cache'\n")
        before = self.settings.read_bytes()
        plan = reset.run(self.home, reset_models=True)
        self.assertEqual(self.settings.read_bytes(), before)
        self.assertEqual(plan["changedFiles"], [])
        self.assertTrue(plan["resetModels"])
        self.assertNotIn("fixture-pin", json.dumps(plan))
        report = reset.run(self.home, apply=True, reset_models=True)
        self.assertTrue(report["applied"], report)
        self.assertEqual(json.loads(self.settings.read_text()), {**keep, "env": keep_env})
        self.assertEqual(self.shell.read_text(),
                         "# user model defaults\nexport MODEL_CACHE_PATH='fixture-cache'\n")
        self.assertEqual(reset.run(self.home, True, reset_models=True)["changedFiles"], [])

    def test_plain_bedrock_reset_preserves_generic_model_definitions(self):
        data = json.dumps({"model": "opus", "availableModels": ["opus"],
            "modelOverrides": {"claude-opus-5": "private-deployment"},
            "modelPicker": [], "fallbackModel": "sonnet", "advisorModel": "fable",
            "env": {key: "generic-pin" for key in reset.ALL_MODEL_KEYS}}).encode()
        self.assertEqual(reset.reset_settings(data), (data, []))
        shell = b"export ANTHROPIC_DEFAULT_MODEL=opus\nexport ANTHROPIC_MODEL=sonnet\n"
        self.assertEqual(reset.reset_shell(shell), (shell, []))

    def test_ambiguous_model_assignment_refuses_reset(self):
        self.shell.write_text("export ANTHROPIC_DEFAULT_MODEL=opus; echo unrelated\n")
        before = self.settings.read_bytes()
        report = reset.run(self.home, True, reset_models=True)
        self.assertEqual(report["error"], "ambiguous_shell_assignment")
        self.assertEqual(report["changedFiles"], [])
        self.assertEqual(self.settings.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
