//! A message a sub-agent sends the session that started it, as Claude Code hands it over (C# `AgentHandback`,
//! MightyCode.Core/AgentHandback.cs): `<agent-message from="<agent ID or name>">` … `</agent-message>`, alone or inside a
//! `<system-reminder>` with other text. Its final report (its hand-back) is framed: a preamble opening `[Subagent hand-back]` and ending
//! `The report follows:`, then the report with every line indented by two spaces. A message it sends while still running has the same
//! wrapper and no frame.

/// `AgentHandback.Opening`.
pub const OPENING: &str = "<agent-message";
/// `AgentHandback.Closing`.
pub const CLOSING: &str = "</agent-message>";
/// `AgentHandback.Frame`.
pub const FRAME: &str = "[Subagent hand-back]";
/// `AgentHandback.Marker`.
pub const MARKER: &str = "The report follows";
const MAXIMUM_MESSAGES: usize = 64;

/// One message: who sent it, its body, and whether it is a framed final report.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    pub from: String,
    pub body: String,
    pub is_handback: bool,
}

/// `AgentHandback.Parse`: every message in `text`, in order (AgentHandback.cs:24). A report's body is the indented text after the marker
/// with its indentation removed; a framed message without the marker, or an unframed one, is the whole text inside the tag.
pub fn parse(text: &str) -> Vec<Message> {
    let text = text.replace("\r\n", "\n");
    let mut found = vec![];
    let mut position = 0;
    while found.len() < MAXIMUM_MESSAGES {
        let Some(start) = text[position..].find(OPENING).map(|at| at + position) else { break };
        let rest = start + OPENING.len();
        if rest >= text.len() {
            break;
        }
        // `<agent-messages` and the like are not this tag.
        let next_char = text[rest..].chars().next().unwrap_or(' ');
        if next_char != '>' && !next_char.is_whitespace() {
            position = rest;
            continue;
        }
        let Some(tag_end) = text[rest..].find('>').map(|at| at + rest) else { break };
        let content = tag_end + 1;
        // The frame closes at the start of a line, before any next message; report lines are indented.
        let next = text[content..].find(&format!("\n{OPENING}")).map_or(text.len(), |at| at + content);
        let line_closing = format!("\n{CLOSING}");
        let (end, after) = if let Some(line_end) = text[content..next].find(&line_closing).map(|at| at + content) {
            (line_end, line_end + 1 + CLOSING.len())
        } else if let Some(anywhere) = text[content..].find(CLOSING).map(|at| at + content) {
            (anywhere, anywhere + CLOSING.len())
        } else {
            break;
        };
        let inner = &text[content..end];
        let framed = inner.contains(FRAME) || inner.contains(MARKER);
        found.push(Message {
            from: sender(&text[rest..tag_end]),
            body: if framed { body(inner) } else { inner.trim().to_string() },
            is_handback: framed,
        });
        position = after;
    }
    found
}

fn sender(attributes: &str) -> String {
    const KEY: &str = "from=\"";
    let Some(start) = attributes.find(KEY) else { return String::new() };
    let from = start + KEY.len();
    match attributes[from..].find('"') {
        Some(end) => attributes[from..from + end].to_string(),
        None => String::new(),
    }
}

fn body(inner: &str) -> String {
    let marked = format!("{MARKER}:");
    let (found, length) = match inner.find(&marked) {
        Some(at) => (at, marked.len()),
        None => match inner.find(MARKER) {
            Some(at) => (at, MARKER.len()),
            None => return inner.trim().to_string(),
        },
    };
    let report = &inner[found + length..];
    // The marker ends the preamble's line: what follows it there (the warning's advice) is not the report.
    let report = match report.find('\n') {
        Some(line_end) => &report[line_end + 1..],
        None => "",
    };
    report.split('\n').map(|line| line.strip_prefix("  ").unwrap_or(line)).collect::<Vec<_>>().join("\n").trim().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_framed_report_is_the_indented_text() {
        let text = "<agent-message from=\"a1\">\nnotes\n[Subagent hand-back] The report follows:\n  line one\n    two\n</agent-message>";
        let found = parse(text);
        assert_eq!(found, vec![Message { from: "a1".into(), body: "line one\n  two".into(), is_handback: true }]);
    }

    #[test]
    fn a_running_message_is_the_whole_text_and_lookalike_tags_are_not() {
        let found = parse("<agent-messages x><agent-message from=\"b\"> hi </agent-message>");
        assert_eq!(found, vec![Message { from: "b".into(), body: "hi".into(), is_handback: false }]);
    }
}
