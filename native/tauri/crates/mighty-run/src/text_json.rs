//! JSON text as C#'s serializers write it: `Wire.Json` (compact, `JavaScriptEncoder.Default`: every non-ASCII character escaped,
//! mighty-core's `JsonOut`), and `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, compact or indented (`WriteIndented`, lines
//! ended by `Environment.NewLine`), which `ToolPermissions.InputDisplay`, `AgentQuestions.Request` and the attachment prompt use.
//!
//! The relaxed encoder keeps every character but `"` and `\`, the control characters (C0, DEL and C1), the line and paragraph
//! separators, private use and every character beyond the BMP (as its UTF-16 pair), which it escapes in upper-case hex.
//! Deviation: .NET also escapes the code points its Unicode tables leave unassigned; here they stay as they are.

use std::fmt::Write as _;

use mighty_core::json::JsonOut;
use serde_json::Value;

use crate::os;

/// `JsonSerializer.Serialize(value, Wire.Json)` of a JSON value (`JsonElement.WriteTo`): strings escaped again, numbers in
/// serde_json's form.
pub fn wire(value: &Value) -> String {
    let mut out = JsonOut::new();
    out.value(value);
    out.finish()
}

fn relaxed_keeps(c: char) -> bool {
    let code = c as u32;
    !(code < 0x20
        || (0x7f..=0x9f).contains(&code)
        || code == 0x2028
        || code == 0x2029
        || (0xe000..=0xf8ff).contains(&code)
        || code > 0xffff
        || c == '"'
        || c == '\\')
}

/// A string as JSON text (quotes included) with the relaxed encoder.
pub fn escape_relaxed_into(out: &mut String, value: &str) {
    out.push('"');
    for c in value.chars() {
        if relaxed_keeps(c) {
            out.push(c);
            continue;
        }
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            _ => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    let _ = write!(out, "\\u{:04X}", unit);
                }
            }
        }
    }
    out.push('"');
}

pub fn escape_relaxed(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    escape_relaxed_into(&mut out, value);
    out
}

/// A JSON value with the relaxed encoder, compact or indented by two spaces as `Utf8JsonWriter` indents (`"name": value`, an
/// empty object or array as `{}` / `[]`).
pub fn relaxed(value: &Value, indented: bool) -> String {
    let mut out = String::new();
    write_relaxed(&mut out, value, indented, 0);
    out
}

fn indent(out: &mut String, level: usize) {
    out.push_str(os::new_line());
    for _ in 0..level {
        out.push_str("  ");
    }
}

fn write_relaxed(out: &mut String, value: &Value, indented: bool, level: usize) {
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => number(out, n),
        Value::String(s) => escape_relaxed_into(out, s),
        Value::Array(items) => {
            out.push('[');
            if items.is_empty() {
                out.push(']');
                return;
            }
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                if indented {
                    indent(out, level + 1);
                }
                write_relaxed(out, item, indented, level + 1);
            }
            if indented {
                indent(out, level);
            }
            out.push(']');
        }
        Value::Object(map) => {
            out.push('{');
            if map.is_empty() {
                out.push('}');
                return;
            }
            for (index, (key, item)) in map.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                if indented {
                    indent(out, level + 1);
                }
                escape_relaxed_into(out, key);
                out.push(':');
                if indented {
                    out.push(' ');
                }
                write_relaxed(out, item, indented, level + 1);
            }
            if indented {
                indent(out, level);
            }
            out.push('}');
        }
    }
}

/// A number as `JsonElement.WriteTo` writes it: the literal (here serde_json's form of it, the same for integers).
fn number(out: &mut String, n: &serde_json::Number) {
    let _ = write!(out, "{n}");
}

/// Unicode's Format (Cf) characters of the BMP, which `ToolPermissions.InputDisplay` makes visible as `\uxxxx`.
pub fn is_format(c: char) -> bool {
    matches!(c as u32,
        0x00AD | 0x0600..=0x0605 | 0x061C | 0x06DD | 0x070F | 0x0890..=0x0891 | 0x08E2 | 0x180E | 0x200B..=0x200F | 0x202A..=0x202E
        | 0x2060..=0x2064 | 0x2066..=0x206F | 0xFEFF | 0xFFF9..=0xFFFB
        | 0x110BD | 0x110CD | 0x13430..=0x1343F | 0x1BCA0..=0x1BCA3 | 0x1D173..=0x1D17A | 0xE0001 | 0xE0020..=0xE007F)
}

/// A value read back from text C# parsed (`JsonDocument.Parse`): None for text that is no JSON.
pub fn parse(text: &str) -> Option<Value> {
    serde_json::from_str(text).ok()
}
