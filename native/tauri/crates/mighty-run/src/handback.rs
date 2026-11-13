//! C# `AgentHandback` (AgentHandback.cs): a message a sub-agent sends the session that started it, `<agent-message from="…">` …
//! `</agent-message>`, and whether it is its framed final report (`[Subagent hand-back]` … `The report follows:`).

pub const OPENING: &str = "<agent-message";
pub const CLOSING: &str = "</agent-message>";
pub const FRAME: &str = "[Subagent hand-back]";
pub const MARKER: &str = "The report follows";
const MAXIMUM_MESSAGES: usize = 64;

/// One message: who sent it, its body, and whether it is a framed final report.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    pub from: String,
    pub body: String,
    pub is_handback: bool,
}

fn find(text: &str, needle: &str, from: usize) -> Option<usize> {
    text.get(from..)?.find(needle).map(|at| at + from)
}

/// `AgentHandback.Parse`: every message in `text`, in order.
pub fn parse(text: &str) -> Vec<Message> {
    let text = text.replace("\r\n", "\n");
    let mut found = Vec::new();
    let mut position = 0;
    while found.len() < MAXIMUM_MESSAGES {
        let Some(start) = find(&text, OPENING, position) else { break };
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
        let Some(tag_end) = find(&text, ">", rest) else { break };
        let content = tag_end + 1;
        // The frame closes at the start of a line, before any next message; report lines are indented.
        let next = find(&text, &format!("\n{OPENING}"), content).unwrap_or(text.len());
        let line_end = text[content..next].find(&format!("\n{CLOSING}")).map(|at| at + content);
        let (end, after) = match line_end {
            Some(line_end) => (line_end, line_end + 1 + CLOSING.len()),
            None => match find(&text, CLOSING, content) {
                Some(anywhere) => (anywhere, anywhere + CLOSING.len()),
                None => break,
            },
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
    let rest = &attributes[start + KEY.len()..];
    match rest.find('"') {
        Some(end) => rest[..end].to_string(),
        None => String::new(),
    }
}

fn body(inner: &str) -> String {
    let (found, length) = match inner.find(&format!("{MARKER}:")) {
        Some(at) => (Some(at), MARKER.len() + 1),
        None => (inner.find(MARKER), MARKER.len()),
    };
    let Some(found) = found else { return inner.trim().to_string() };
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
    fn a_framed_report_and_a_plain_message() {
        let text = "<agent-message from=\"a1\">\n[Subagent hand-back] done.\nThe report follows:\n  line one\n    indented\n</agent-message>\n<agent-messages x>\n<agent-message from=\"b\">hi there</agent-message>";
        let messages = parse(text);
        assert_eq!(messages.len(), 2);
        assert_eq!(messages[0], Message { from: "a1".into(), body: "line one\n  indented".into(), is_handback: true });
        assert_eq!(messages[1], Message { from: "b".into(), body: "hi there".into(), is_handback: false });
    }
}
