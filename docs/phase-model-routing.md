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

### Ouroboros (scanned, file-based)
Reads model keys from `~/.ouroboros/config.yaml` (only `*_model` scalar keys at 2-level depth).
Rewrites only owned model keys; all other keys (including `orchestrator.cli_path`) preserved verbatim.
Backup written first; written atomically.

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
