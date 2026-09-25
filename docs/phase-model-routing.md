# Phase Model Routing

Machine-wide per-phase / per-role model settings for Claude, Codex, oh-my-claudecode and Ouroboros.

Replaces the old per-permission-mode model defaults (removed in the `interview_20260924_044823` batch).

## Overview

All values are one machine-wide set stored in `AppSnapshot.phaseModels` (`PhaseModelHardcodedConfig`).
There is no per-workspace or per-session scope for any knob on this screen.

A knob left at `"default"` adds no flag, env key, or file key when launching a CLI run.

## Phase summary rows

The settings screen shows four summary phases:

| Phase | Maps to |
|-------|---------|
| Planning | Claude `claudeOpusAlias`; omc: planner, architect, critic; Ouroboros: `clarification.default_model` |
| Execution | Claude `claudeMain`, `claudeSonnetAlias`; omc: executor |
| Review | Codex `codexReviewModel`; omc: codeReviewer, verifier; Ouroboros: `evaluation.semantic_model`, `consensus.judge_model`, `llm.qa_model` |
| Subagents | Claude `claudeSubagentDefault`; Codex `codexSubagentDefault` |

Choosing a model on a row writes it to every single-model knob mapped to that phase.
A row whose mapped knobs differ shows **혼합**.
Non-model knobs (Codex `plan_mode_reasoning_effort`, Ouroboros economics tier lists) are never changed by a row.

## Knob delivery

### Claude (hardcoded, per-run)
| Knob | Delivery |
|------|----------|
| `claudeMain` | `--model <value>` |
| `claudeOpusAlias` | `--settings env.ANTHROPIC_DEFAULT_OPUS_MODEL` |
| `claudeSonnetAlias` | `--settings env.ANTHROPIC_DEFAULT_SONNET_MODEL` |
| `claudeHaikuAlias` | `--settings env.ANTHROPIC_DEFAULT_HAIKU_MODEL` |
| `claudeSubagentDefault` | `--settings env.CLAUDE_CODE_SUBAGENT_MODEL` |

Session model (`session.model`) always wins over `claudeMain` when not `"default"`.

### Codex (hardcoded, per-run)
| Knob | Delivery |
|------|----------|
| `codexReviewModel` | `-c review_model="<value>"` |
| `codexSubagentDefault` | `-c agents.default_subagent_model="<value>"` |
| `codexPlanModeReasoningEffort` | `-c plan_mode_reasoning_effort="<value>"` (non-model knob) |

### omc (scanned, file-based)
Reads agent list from the installed oh-my-claudecode plugin.
Writes `agents.<camelCaseKey>.model` in `~/.config/claude-omc/config.jsonc` only.
File is re-read immediately before writing; backup written first; written atomically.

#### omc agent scan

The omc section exists only when omc is installed:

1. `<home>/.claude/plugins/installed_plugins.json` has a plugin id starting with `oh-my-claudecode@`
   with a `scope: "user"` record, and
2. that record's `installPath` contains `agents/*.md`.

The agent list is those file names turned into omc's camelCase keys
(`code-reviewer` → `codeReviewer`, `security-reviewer` → `securityReviewer`; single words unchanged).
Each agent's YAML frontmatter `model:` is only the **displayed default** (shown next to the agent name);
the value on screen comes from `agents.<key>.model` in `config.jsonc`, or `"default"` when the file has none.
A save writes only the agents whose value actually changed, so frontmatter defaults never reach
`config.jsonc`, and `~/.claude/agents` is never written.
If omc is not installed the section is absent.

### Ouroboros (scanned, file-based)
Reads model keys from `~/.ouroboros/config.yaml` (only `*_model` scalar keys at 2-level depth).
Rewrites only owned model keys; all other keys (including `orchestrator.cli_path`) preserved verbatim.
Backup written first; written atomically.

## Who writes the files

- **macOS** now writes both files: picking a phase row or a detail row calls `ModelSettingsFileStore`
  (`AppStore+PhaseModels.swift`), so omc agents land in `config.jsonc` and Ouroboros keys in `config.yaml`.
- **Windows** does the same from the Settings section `phaseModels` (Core `PhaseModelSection.cs`,
  `ModelSettingsFileStore.cs`, `OmcAgentCatalog.cs`; WinUI `MainWindow.Settings.cs`).

Both platforms re-read the file immediately before writing, back it up next to the original
(`config.mighty-backup-<time>-<id>.<ext>`), write atomically, change only owned model keys and keep
every other key (`orchestrator.cli_path` survives). An unparseable file is refused: nothing is written,
the file stays byte-identical, and the screen shows the localized `settings.phaseModels.fileError` message.

## Windows paths

The home directory is `%USERPROFILE%` (injectable for tests; the smoke run uses fixture values and reads no user file).

| File | Windows path | macOS path |
|------|--------------|------------|
| omc config | `%USERPROFILE%\.config\claude-omc\config.jsonc` | `~/.config/claude-omc/config.jsonc` |
| Ouroboros config | `%USERPROFILE%\.ouroboros\config.yaml` | `~/.ouroboros/config.yaml` |
| omc install record | `%USERPROFILE%\.claude\plugins\installed_plugins.json` | `~/.claude/plugins/installed_plugins.json` |

The Ouroboros section is shown only when `config.yaml` exists; its knobs are the `*_model` scalar keys found there.
Claude and Codex values are stored in `AppSnapshot.phaseModels` with the macOS field names and are delivered
per run exactly as on macOS (one merged `--settings` env JSON, `--model` only when the session model is `default`,
Codex `-c` flags).

The Windows section sits between the remote-connection slot and styles (macOS order), titled from
`settings.phaseModels.sectionTitle`, and uses the same `settings.phaseModels.*` locale keys as macOS.

## Registered model names

Names are stored in `AppSnapshot.modelDefaults.claude.registeredModels` / `.codex.registeredModels`
(the old `ModelDefaultsConfig` struct, preserved for backwards compatibility).
The macOS app no longer reads or writes `modeDefaults` entries.

## Storage layout

```
AppSnapshot {
    modelDefaults: ModelDefaultsConfig?  // kept for backwards compat; modeDefaults ignored by macOS
    phaseModels: PhaseModelHardcodedConfig?  // new: Claude/Codex run-time knobs
}
```

Old stored `modeDefaults` keys are neither read nor written by the macOS app but are left
in the saved state so that older builds still load it correctly.
