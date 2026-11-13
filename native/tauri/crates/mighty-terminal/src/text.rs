//! Text measured as C# measures it (a string's `Length`, UTF-16 units) and output bytes decoded as C#'s `StreamReader` decodes them.

use mighty_core::wire::utf16_len;

/// The longest start of `text` that is at most `units` UTF-16 units, never splitting a character (C# `text[..units]` would split a
/// surrogate pair; here the pair is left out whole).
pub fn first_units(text: &str, units: usize) -> &str {
    let mut used = 0;
    for (at, c) in text.char_indices() {
        if used + c.len_utf16() > units {
            return &text[..at];
        }
        used += c.len_utf16();
    }
    text
}

/// The longest end of `text` that is at most `units` UTF-16 units, never splitting a character (C# `text[^units..]`).
pub fn last_units(text: &str, units: usize) -> &str {
    let mut used = 0;
    for (at, c) in text.char_indices().rev() {
        if used + c.len_utf16() > units {
            return &text[at + c.len_utf8()..];
        }
        used += c.len_utf16();
    }
    text
}

/// Text that keeps its last `limit` UTF-16 units as more is added (C#'s `StringBuilder` cut from the front past a limit).
#[derive(Debug, Default, Clone)]
pub struct BoundedText {
    text: String,
    units: usize,
}

impl BoundedText {
    /// Adds `more`; true when the front had to go to keep `limit`.
    pub fn push(&mut self, more: &str, limit: usize) -> bool {
        self.text.push_str(more);
        self.units += utf16_len(more);
        if self.units <= limit {
            return false;
        }
        let kept = last_units(&self.text, limit);
        let cut = self.text.len() - kept.len();
        self.units = utf16_len(kept);
        self.text.drain(..cut);
        true
    }

    pub fn as_str(&self) -> &str {
        &self.text
    }

    pub fn units(&self) -> usize {
        self.units
    }

    pub fn is_empty(&self) -> bool {
        self.text.is_empty()
    }

    pub fn clear(&mut self) {
        self.text.clear();
        self.units = 0;
    }

    /// The text, leaving it empty.
    pub fn take(&mut self) -> String {
        self.units = 0;
        std::mem::take(&mut self.text)
    }

    /// Takes the first `units` UTF-16 units (never splitting a character).
    pub fn take_front(&mut self, units: usize) -> String {
        let front = first_units(&self.text, units).to_string();
        self.units -= utf16_len(&front);
        self.text.drain(..front.len());
        front
    }
}

/// A terminal's output bytes as text, chunk by chunk (C# `StreamReader(output, new UTF8Encoding(false))`): a character split
/// between two chunks is decoded whole, and bytes that are no UTF-8 read as U+FFFD, one for each maximal invalid sequence.
#[derive(Debug, Default)]
pub struct Utf8Decoder {
    held: Vec<u8>,
}

impl Utf8Decoder {
    pub fn decode(&mut self, bytes: &[u8]) -> String {
        self.held.extend_from_slice(bytes);
        let mut text = String::new();
        let mut rest: &[u8] = &self.held;
        loop {
            match std::str::from_utf8(rest) {
                Ok(valid) => {
                    text.push_str(valid);
                    rest = &[];
                    break;
                }
                Err(error) => {
                    let (valid, after) = rest.split_at(error.valid_up_to());
                    // The bytes before the error are UTF-8, as the error says.
                    text.push_str(std::str::from_utf8(valid).unwrap_or_default());
                    match error.error_len() {
                        Some(bad) => {
                            text.push('\u{FFFD}');
                            rest = &after[bad..];
                        }
                        // A character that goes on in the next chunk.
                        None => {
                            rest = after;
                            break;
                        }
                    }
                }
            }
        }
        self.held = rest.to_vec();
        text
    }

    /// What is left at the end of the output: an unfinished character reads as U+FFFD.
    pub fn finish(&mut self) -> String {
        if self.held.is_empty() {
            return String::new();
        }
        self.held.clear();
        "\u{FFFD}".to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn units_never_split_a_character() {
        assert_eq!(first_units("ab😀c", 3), "ab");
        assert_eq!(first_units("ab😀c", 4), "ab😀");
        assert_eq!(last_units("a😀bc", 3), "bc");
        assert_eq!(last_units("a😀bc", 4), "😀bc");
        assert_eq!(last_units("short", 400), "short");
        let mut bounded = BoundedText::default();
        assert!(!bounded.push("한글", 4));
        assert!(bounded.push("abc", 4));
        assert_eq!((bounded.as_str(), bounded.units()), ("글abc", 4));
        assert_eq!(bounded.take_front(2), "글a");
        assert_eq!((bounded.as_str(), bounded.units()), ("bc", 2));
    }

    #[test]
    fn a_character_split_between_chunks_is_decoded_whole() {
        let mut decoder = Utf8Decoder::default();
        let bytes = "한글".as_bytes();
        assert_eq!(decoder.decode(&bytes[..2]), "");
        assert_eq!(decoder.decode(&bytes[2..4]), "한");
        assert_eq!(decoder.decode(&bytes[4..]), "글");
        assert_eq!(decoder.decode(b"a\xFFb"), "a\u{FFFD}b");
        assert_eq!(decoder.decode(&"😀".as_bytes()[..3]), "");
        assert_eq!(decoder.finish(), "\u{FFFD}");
        assert_eq!(decoder.finish(), "");
    }
}
