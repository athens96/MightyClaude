//! The value rules of C# `Wire` (MightyCode.Core/Models.cs) and a lenient field wrapper for the fields the C# side reads with a
//! lenient converter (a value of the wrong type loads as nothing there instead of failing the whole snapshot).

use serde::de::DeserializeOwned;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::Value;
use unicode_segmentation::UnicodeSegmentation;

/// The agents a pane may run (C# `Wire.Providers`).
pub const PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];

/// `Wire.Identifier`: `^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}\z`.
pub fn identifier(value: &str) -> bool {
    let bytes = value.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 128
        && bytes[0].is_ascii_alphanumeric()
        && bytes[1..].iter().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b':' | b'-'))
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
        && value.chars().count() <= 200
        && bytes[0].is_ascii_alphanumeric()
        && bytes[1..].iter().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b':' | b'/' | b'@' | b'[' | b']' | b'-'))
}

/// `Wire.Clean`: the control characters JSON text may not keep (all of C0 but tab, LF and CR, and DEL) removed, then cut to
/// `limit` UTF-16 units as C# counts them (never inside a surrogate pair here: the cut is on a character boundary).
pub fn clean(value: &str, limit: usize) -> String {
    let mut out = String::with_capacity(value.len().min(limit * 2));
    let mut units = 0;
    for c in value.chars() {
        let code = c as u32;
        if code <= 0x08 || code == 0x0b || code == 0x0c || (0x0e..=0x1f).contains(&code) || code == 0x7f {
            continue;
        }
        units += c.len_utf16();
        if units > limit {
            break;
        }
        out.push(c);
    }
    out
}

/// The first `maximum` text elements (grapheme clusters) of `value`, as C# `StringInfo` counts them.
pub fn text_elements(value: &str, maximum: usize) -> (&str, bool) {
    match value.grapheme_indices(true).nth(maximum) {
        Some((end, _)) => (&value[..end], true),
        None => (value, false),
    }
}

/// A field the C# side reads leniently (`LenientJsonConverter`, `LenientNullableBoolConverter`, ...): a value of the expected type
/// is `Valid`; any other JSON is kept as it was (`Invalid`), so it is neither lost on a round trip nor fails the snapshot.
#[derive(Debug, Clone, PartialEq)]
pub enum Lenient<T> {
    Valid(T),
    Invalid(Value),
}

impl<T> Lenient<T> {
    pub fn get(&self) -> Option<&T> {
        match self {
            Lenient::Valid(value) => Some(value),
            Lenient::Invalid(_) => None,
        }
    }
}

impl<'de, T: DeserializeOwned> Deserialize<'de> for Lenient<T> {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let value = Value::deserialize(deserializer)?;
        Ok(match T::deserialize(&value) {
            Ok(typed) => Lenient::Valid(typed),
            Err(_) => Lenient::Invalid(value),
        })
    }
}

impl<T: Serialize> Serialize for Lenient<T> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match self {
            Lenient::Valid(value) => value.serialize(serializer),
            Lenient::Invalid(value) => value.serialize(serializer),
        }
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
        assert_eq!(clean("a😀b", 2), "a");
    }

    #[test]
    fn lenient_keeps_the_wrong_type() {
        let valid: Lenient<bool> = serde_json::from_str("true").unwrap();
        assert_eq!(valid.get(), Some(&true));
        let invalid: Lenient<bool> = serde_json::from_str("\"yes\"").unwrap();
        assert_eq!(invalid.get(), None);
        assert_eq!(serde_json::to_string(&invalid).unwrap(), "\"yes\"");
    }
}
