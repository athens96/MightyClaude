//! The value rules of C# `Wire` (MightyCode.Core/Models.cs): identifiers, model names, `Clean`, new ids, text elements.

use std::sync::Mutex;

use unicode_segmentation::UnicodeSegmentation;

/// The agents a pane may run (C# `Wire.Providers`).
pub const PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];
/// `Wire.Efforts`, `Wire.PiThinkingLevels`, `Wire.CodexEfforts`.
pub const EFFORTS: [&str; 5] = ["low", "medium", "high", "xhigh", "max"];
pub const PI_THINKING_LEVELS: [&str; 6] = ["off", "minimal", "low", "medium", "high", "xhigh"];
pub const CODEX_EFFORTS: [&str; 6] = ["low", "medium", "high", "xhigh", "max", "ultra"];

/// `Wire.RunEfforts`: the efforts a pane of `provider` may be set to besides "default".
pub fn run_efforts(provider: &str) -> &'static [&'static str] {
    match provider {
        "pi" => &PI_THINKING_LEVELS,
        "codex" => &CODEX_EFFORTS,
        _ => &EFFORTS,
    }
}

/// `Wire.Identifier`: `^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}\z`.
pub fn identifier(value: &str) -> bool {
    let bytes = value.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 128
        && bytes[0].is_ascii_alphanumeric()
        && bytes[1..].iter().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b':' | b'-'))
}

pub fn is_identifier(value: Option<&str>) -> bool {
    value.is_some_and(identifier)
}

/// `Wire.SessionIdentifier`: an identifier, or opencode's `ses_<base62>`.
pub fn session_identifier(value: &str) -> bool {
    identifier(value)
        || value.strip_prefix("ses_").is_some_and(|rest| (1..=124).contains(&rest.len()) && rest.bytes().all(|b| b.is_ascii_alphanumeric()))
}

/// `Wire.Model`: 1–200 characters, `^[a-zA-Z0-9][a-zA-Z0-9._:/@\[\]-]*\z`.
pub fn model(value: &str) -> bool {
    let bytes = value.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 200
        && bytes[0].is_ascii_alphanumeric()
        && bytes[1..].iter().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b':' | b'/' | b'@' | b'[' | b']' | b'-'))
}

pub fn is_model(value: Option<&str>) -> bool {
    value.is_some_and(model)
}

/// `Wire.Clean`: the control characters JSON text may not keep (C0 but tab, LF and CR, and DEL) removed, then the first `limit`
/// UTF-16 units. A cut through a surrogate pair leaves its high half in C#, which `Wire.Json` writes as U+FFFD; here it is U+FFFD.
pub fn clean(value: &str, limit: usize) -> String {
    let mut out = String::with_capacity(value.len().min(limit.saturating_mul(3)));
    let mut units = 0;
    for c in value.chars() {
        let code = c as u32;
        if code <= 0x08 || code == 0x0b || code == 0x0c || (0x0e..=0x1f).contains(&code) || code == 0x7f {
            continue;
        }
        let width = c.len_utf16();
        if units + width > limit {
            if width == 2 && units < limit {
                out.push('\u{FFFD}');
            }
            break;
        }
        units += width;
        out.push(c);
    }
    out
}

/// `Wire.Clean(value)` with its default limit, 32768.
pub fn clean_default(value: Option<&str>) -> String {
    clean(value.unwrap_or(""), 32768)
}

/// The first `maximum` text elements (extended grapheme clusters) of `value`, as C# `StringInfo` counts them, and whether it cut.
pub fn text_elements(value: &str, maximum: usize) -> (&str, bool) {
    match value.grapheme_indices(true).nth(maximum) {
        Some((end, _)) => (&value[..end], true),
        None => (value, false),
    }
}

/// UTF-16 length, C#'s `string.Length`.
pub fn utf16_len(value: &str) -> usize {
    value.encode_utf16().count()
}

static FIXED_IDS: Mutex<Option<u64>> = Mutex::new(None);

/// `Wire.Id()`: a new GUID; a test may make them count instead.
pub fn new_id() -> String {
    if let Ok(mut fixed) = FIXED_IDS.lock()
        && let Some(next) = fixed.as_mut()
    {
        *next += 1;
        return format!("00000000-0000-4000-8000-{:012x}", *next);
    }
    uuid::Uuid::new_v4().to_string()
}

/// Makes `new_id` count from zero (Some) or random again (None).
pub fn set_counting_ids(counting: bool) {
    if let Ok(mut fixed) = FIXED_IDS.lock() {
        *fixed = counting.then_some(0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn identifiers_follow_wire() {
        assert!(identifier("6e0722dc-9e7b-4542-b404-7a7ef7360e0f"));
        assert!(identifier("files:w1"));
        assert!(!identifier(""));
        assert!(!identifier("-lead"));
        assert!(!identifier("has space"));
        assert!(!identifier(&"a".repeat(129)));
        assert!(session_identifier("ses_abc123"));
        assert!(!session_identifier("_ses"));
        assert!(model("claude-sonnet-4-5[1m]"));
        assert!(!model(" default"));
    }

    #[test]
    fn clean_drops_controls_and_cuts() {
        assert_eq!(clean("a\u{0}b\tc\u{7f}d", 100), "ab\tcd");
        assert_eq!(clean("abcdef", 3), "abc");
        assert_eq!(clean("a😀b", 2), "a\u{FFFD}");
        assert_eq!(clean("a😀b", 3), "a😀");
    }
}
