//! The composer's "send to" targets (C# `AskTargets.cs`, `native/contracts/fixtures/ask-targets.json`): another CLI asked through
//! oh-my-claudecode's ask skill — which targets a pane offers, which one a send uses, the text it sends, and when a saved choice
//! counts as gone.

use std::collections::HashSet;

/// C# `AskTarget(Id, Label, Executable)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AskTarget {
    pub id: &'static str,
    pub label: &'static str,
    pub executable: &'static str,
}

/// C# `AskTargets.Skill`.
pub const SKILL: &str = "oh-my-claudecode:ask";

/// C# `AskTargets.All`: every target, in the menu's order. Neither claude (the default) nor muse is one.
pub const ALL: [AskTarget; 5] = [
    AskTarget { id: "codex", label: "Codex", executable: "codex" },
    AskTarget { id: "antigravity", label: "Antigravity", executable: "agy" },
    AskTarget { id: "gemini", label: "Gemini CLI", executable: "gemini" },
    AskTarget { id: "grok", label: "Grok", executable: "grok" },
    AskTarget { id: "cursor", label: "Cursor", executable: "cursor-agent" },
];

/// C# `AskTargets.Available`: none unless a Claude agent pane whose slash list has the skill; then every installed one.
pub fn available(is_claude_agent: bool, slash: &HashSet<String>, installed: &HashSet<String>) -> Vec<AskTarget> {
    if !is_claude_agent || !slash.contains(SKILL) {
        return vec![];
    }
    ALL.iter().copied().filter(|t| installed.contains(t.executable)).collect()
}

/// C# `AskTargets.Effective`: the target a send uses; None (Claude) when nothing is saved or the saved id is not offered.
pub fn effective(saved: Option<&str>, available: &[AskTarget]) -> Option<AskTarget> {
    let saved = saved?;
    available.iter().copied().find(|t| t.id == saved)
}

/// C# `AskTargets.FellBack`: a saved target is gone, judged only once the PATH read and the catalogue are both in.
pub fn fell_back(saved: Option<&str>, available: &[AskTarget], probed: bool, scanned: bool) -> bool {
    saved.is_some() && probed && scanned && effective(saved, available).is_none()
}

/// C# `AskTargets.Probed`: the installed set after a PATH read, and whether the read is conclusive.
pub fn probed(previous: &HashSet<String>, found: &HashSet<String>, login_shell: bool) -> (HashSet<String>, bool) {
    if !login_shell {
        return (previous.union(found).cloned().collect(), false);
    }
    if found.is_empty() && !previous.is_empty() {
        return (previous.clone(), false);
    }
    (found.clone(), true)
}

/// C# `AskTargets.Question`: the question inside a wrapped send; any other text unchanged (mighty-core's `ask_question`).
pub fn question(text: &str) -> &str {
    mighty_core::normalize::ask_question(text)
}

/// C# `AskTargets.Wrap`: the text unchanged for Claude or a slash command, the ask command around the trimmed text otherwise;
/// None when nothing should be sent.
pub fn wrap(text: &str, target: Option<AskTarget>) -> Option<String> {
    let Some(target) = target else { return Some(text.to_string()) };
    let trimmed = crate::slash::dotnet_trim(text);
    if trimmed.is_empty() {
        return None;
    }
    if trimmed.starts_with('/') {
        return Some(text.to_string());
    }
    Some(format!("/{SKILL} {} {trimmed}", target.id))
}
