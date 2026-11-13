//! A child's output read as text the way C# reads it: `new StreamReader(stream, Encoding.UTF8)` (ChildProcess.cs:157-158), which
//! looks for a byte order mark first (a UTF-8 one is skipped; a UTF-16 or UTF-32 one switches the decoding) and replaces each
//! invalid UTF-8 sequence by U+FFFD, a sequence cut between two reads joined first.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Encoding {
    Utf8,
    Utf16 { big_endian: bool },
    Utf32 { big_endian: bool },
}

/// Bytes in, text out, as `StreamReader` decodes them.
#[derive(Debug)]
pub(crate) struct TextDecoder {
    /// None until the first bytes decided the encoding.
    encoding: Option<Encoding>,
    pending: Vec<u8>,
}

impl TextDecoder {
    pub(crate) fn new() -> TextDecoder {
        TextDecoder { encoding: None, pending: Vec::new() }
    }

    /// The text `bytes` complete; what a later read finishes is kept.
    pub(crate) fn push(&mut self, bytes: &[u8]) -> String {
        self.pending.extend_from_slice(bytes);
        if self.encoding.is_none() && !self.detect(false) {
            return String::new();
        }
        self.decode(false)
    }

    /// The end of the stream: anything left is decoded, an unfinished sequence as U+FFFD.
    pub(crate) fn finish(&mut self) -> String {
        if self.encoding.is_none() {
            self.detect(true);
        }
        self.decode(true)
    }

    /// Decides the encoding from a byte order mark (`StreamReader.DetectEncoding`); false while too few bytes have come to tell.
    fn detect(&mut self, end: bool) -> bool {
        let (encoding, mark) = match self.pending.as_slice() {
            [0xFE, 0xFF, ..] => (Encoding::Utf16 { big_endian: true }, 2),
            [0xFF, 0xFE, 0, 0, ..] => (Encoding::Utf32 { big_endian: false }, 4),
            [0xFF, 0xFE, _, _, ..] => (Encoding::Utf16 { big_endian: false }, 2),
            [0xFF, 0xFE] | [0xFF, 0xFE, _] if !end => return false,
            [0xFF, 0xFE, ..] => (Encoding::Utf16 { big_endian: false }, 2),
            [0xEF, 0xBB, 0xBF, ..] => (Encoding::Utf8, 3),
            [0xEF] | [0xEF, 0xBB] if !end => return false,
            [0, 0, 0xFE, 0xFF, ..] => (Encoding::Utf32 { big_endian: true }, 4),
            [0] | [0, 0] | [0, 0, 0xFE] if !end => return false,
            [] if !end => return false,
            _ => (Encoding::Utf8, 0),
        };
        self.pending.drain(..mark);
        self.encoding = Some(encoding);
        true
    }

    fn decode(&mut self, end: bool) -> String {
        match self.encoding.unwrap_or(Encoding::Utf8) {
            Encoding::Utf8 => self.utf8(end),
            Encoding::Utf16 { big_endian } => self.units(2, end, |unit| {
                let pair = [unit[0], unit[1]];
                u32::from(if big_endian { u16::from_be_bytes(pair) } else { u16::from_le_bytes(pair) })
            }),
            Encoding::Utf32 { big_endian } => self.units(4, end, |unit| {
                let quad = [unit[0], unit[1], unit[2], unit[3]];
                if big_endian { u32::from_be_bytes(quad) } else { u32::from_le_bytes(quad) }
            }),
        }
    }

    fn utf8(&mut self, end: bool) -> String {
        let mut text = String::new();
        let mut rest: &[u8] = &self.pending;
        loop {
            match std::str::from_utf8(rest) {
                Ok(valid) => {
                    text.push_str(valid);
                    rest = &[];
                    break;
                }
                Err(error) => {
                    let (valid, after) = rest.split_at(error.valid_up_to());
                    // `valid_up_to` marks a prefix that is valid UTF-8.
                    text.push_str(std::str::from_utf8(valid).unwrap_or_default());
                    match error.error_len() {
                        Some(length) => {
                            text.push('\u{FFFD}');
                            rest = &after[length..];
                        }
                        None if end => {
                            text.push('\u{FFFD}');
                            rest = &[];
                            break;
                        }
                        None => {
                            rest = after;
                            break;
                        }
                    }
                }
            }
        }
        let keep = rest.len();
        let consumed = self.pending.len() - keep;
        self.pending.drain(..consumed);
        text
    }

    /// UTF-16 or UTF-32: code units of `size` bytes, surrogate pairs joined, anything unpaired or out of range as U+FFFD.
    fn units(&mut self, size: usize, end: bool, read: impl Fn(&[u8]) -> u32) -> String {
        let whole = self.pending.len() / size * size;
        let values: Vec<u32> = self.pending[..whole].chunks_exact(size).map(&read).collect();
        let mut text = String::new();
        let mut index = 0;
        let mut consumed_units = values.len();
        while index < values.len() {
            let value = values[index];
            if size == 2 && (0xD800..0xDC00).contains(&value) {
                match values.get(index + 1) {
                    Some(low) if (0xDC00..0xE000).contains(low) => {
                        text.push(char::from_u32(0x10000 + ((value - 0xD800) << 10) + (low - 0xDC00)).unwrap_or('\u{FFFD}'));
                        index += 2;
                        continue;
                    }
                    None if !end => {
                        consumed_units = index;
                        break;
                    }
                    _ => {}
                }
            }
            text.push(char::from_u32(value).unwrap_or('\u{FFFD}'));
            index += 1;
        }
        self.pending.drain(..consumed_units * size);
        if end && !self.pending.is_empty() {
            self.pending.clear();
            text.push('\u{FFFD}');
        }
        text
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn decode_in(chunks: &[&[u8]]) -> String {
        let mut decoder = TextDecoder::new();
        let mut text: String = chunks.iter().map(|chunk| decoder.push(chunk)).collect();
        text.push_str(&decoder.finish());
        text
    }

    #[test]
    fn utf8_split_between_reads_is_joined_and_invalid_bytes_are_replaced() {
        let korean = "안녕".as_bytes();
        assert_eq!(decode_in(&[&korean[..2], &korean[2..]]), "안녕");
        assert_eq!(decode_in(&[b"a\xFFb"]), "a\u{FFFD}b");
        assert_eq!(decode_in(&[b"a\xE2\x82"]), "a\u{FFFD}", "an unfinished sequence at the end is one replacement");
    }

    #[test]
    fn byte_order_marks_decide_the_encoding() {
        assert_eq!(decode_in(&[b"\xEF\xBB", b"\xBFhi"]), "hi");
        assert_eq!(decode_in(&[b"\xFF\xFEh\0i\0"]), "hi");
        assert_eq!(decode_in(&[b"\xFE\xFF\0h\0i"]), "hi");
        assert_eq!(decode_in(&[b"\xFF\xFE\0\0h\0\0\0"]), "h");
        assert_eq!(decode_in(&[b""]), "");
        assert_eq!(decode_in(&[b"\0x"]), "\0x", "no mark: UTF-8");
    }
}
