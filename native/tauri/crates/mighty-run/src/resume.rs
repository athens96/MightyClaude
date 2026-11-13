//! C# `ClaudeResume` (ClaudeResume.cs): a Claude pane whose saved conversation Claude Code does not have forgets it, so its next
//! request starts a new conversation instead of failing for ever.

use mighty_core::model::RunSession;

/// What Claude Code prints when `--resume` names no conversation it has.
pub const MISSING_CONVERSATION: &str = "No conversation found with session ID: ";

/// `string.Contains(value, StringComparison.OrdinalIgnoreCase)`: each character compared upper-cased (simple mapping).
pub fn contains_ignore_case(text: &str, value: &str) -> bool {
    fn upper(c: char) -> char {
        let mut mapped = c.to_uppercase();
        match (mapped.next(), mapped.next()) {
            (Some(one), None) => one,
            _ => c,
        }
    }
    let needle: Vec<char> = value.chars().map(upper).collect();
    if needle.is_empty() {
        return true;
    }
    let hay: Vec<char> = text.chars().map(upper).collect();
    hay.windows(needle.len()).any(|window| window == needle.as_slice())
}

/// `ClaudeResume.Lost`: a Claude pane holding a resume id whose last run (after the last request) logged Claude Code's refusal of
/// exactly that id.
pub fn lost(pane: &RunSession) -> bool {
    if pane.kind() != "claude" || pane.provider() != "claude" {
        return false;
    }
    let Some(id) = pane.resume_id.as_deref().filter(|id| !id.is_empty()) else { return false };
    let logs = pane.logs();
    let start = logs.iter().rposition(|log| log.kind() == "user").map(|i| i + 1).unwrap_or(0);
    let refusal = format!("{MISSING_CONVERSATION}{id}");
    logs[start..].iter().any(|log| matches!(log.kind(), "error" | "output") && contains_ignore_case(log.text(), &refusal))
}
