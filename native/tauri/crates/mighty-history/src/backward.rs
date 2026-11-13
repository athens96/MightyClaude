//! C# `JsonlBackwardReader` (SessionHistory.cs:360): complete lines of a JSONL file, newest first, read in blocks from an end offset
//! towards the start. A line still being written past the last newline is never returned, and a line longer than the limit is skipped
//! whole.

use std::fs::File;

use crate::files;

pub struct JsonlBackwardReader<'a> {
    file: &'a mut File,
    /// Bytes `[buffer_start, buffer_start + length)` of the file not returned yet.
    buffer: Vec<u8>,
    length: usize,
    buffer_start: i64,
    aligned: bool,
    block: usize,
    maximum: usize,
    /// Every line not yet returned ends before this offset.
    pub end: i64,
    /// A read failed: the reader stops as if the file began here.
    pub failed: bool,
}

fn last_newline(bytes: &[u8]) -> Option<usize> {
    memchr::memrchr(b'\n', bytes)
}

impl<'a> JsonlBackwardReader<'a> {
    pub fn new(file: &'a mut File, end: i64, maximum_line_bytes: usize) -> JsonlBackwardReader<'a> {
        JsonlBackwardReader::with_block(file, end, maximum_line_bytes, 256 * 1_024)
    }

    pub fn with_block(file: &'a mut File, end: i64, maximum_line_bytes: usize, block: usize) -> JsonlBackwardReader<'a> {
        JsonlBackwardReader {
            file,
            buffer: vec![],
            length: 0,
            buffer_start: end,
            aligned: false,
            block: block.max(1),
            maximum: maximum_line_bytes.max(1),
            end,
            failed: false,
        }
    }

    fn fill(&mut self) -> bool {
        if self.buffer_start <= 0 || self.failed {
            return false;
        }
        let count = (self.block as i64).min(self.buffer_start) as usize;
        let mut next = vec![0u8; count];
        if files::read_at(self.file, &mut next, self.buffer_start - count as i64) != count {
            self.failed = true;
            return false;
        }
        next.extend_from_slice(&self.buffer[..self.length]);
        self.buffer = next;
        self.length += count;
        self.buffer_start -= count as i64;
        true
    }

    /// Drops whatever follows the last newline: it is a line still being written.
    fn align(&mut self) {
        self.aligned = true;
        loop {
            if let Some(index) = last_newline(&self.buffer[..self.length]) {
                self.length = index + 1;
                self.end = self.buffer_start + index as i64 + 1;
                return;
            }
            self.length = 0;
            if !self.fill() {
                self.end = 0;
                return;
            }
        }
    }

    /// The previous complete line and its starting offset; None at the start.
    pub fn previous(&mut self) -> Option<(i64, Vec<u8>)> {
        if !self.aligned {
            self.align();
        }
        while self.end > 0 {
            // The buffer ends with the newline that closes the wanted line.
            let mut discarding = false;
            loop {
                let search_end = self.length.saturating_sub(1);
                let index = if search_end > 0 { last_newline(&self.buffer[..search_end]) } else { None };
                if let Some(index) = index {
                    let start = index + 1;
                    let line =
                        if discarding || search_end - start > self.maximum { vec![] } else { self.buffer[start..search_end].to_vec() };
                    let offset = self.buffer_start + start as i64;
                    self.length = start;
                    self.end = offset;
                    if discarding || line.is_empty() {
                        break;
                    }
                    return Some((offset, line));
                }
                if self.buffer_start == 0 || self.failed {
                    // The first line of the file (or of what could be read).
                    let line = if discarding || search_end == 0 || search_end > self.maximum {
                        vec![]
                    } else {
                        self.buffer[..search_end].to_vec()
                    };
                    let offset = self.buffer_start;
                    self.length = 0;
                    self.end = 0;
                    if self.failed {
                        return None;
                    }
                    return (!line.is_empty()).then_some((offset, line));
                }
                if self.length > self.maximum + 1 {
                    // Too long to keep: forget its bytes but remember the newline that ends it, then look for where it starts.
                    discarding = true;
                    self.buffer = vec![b'\n'];
                    self.length = 1;
                }
                if !self.fill() {
                    self.end = 0;
                    return None;
                }
            }
        }
        None
    }
}

#[cfg(test)]
mod tests {
    use std::io::Write;

    use super::*;

    fn file(text: &[u8]) -> (std::path::PathBuf, File) {
        let path = std::env::temp_dir().join(format!("mighty-backward-{}-{}", std::process::id(), rand_name()));
        std::fs::File::create(&path).unwrap().write_all(text).unwrap();
        let file = File::open(&path).unwrap();
        (path, file)
    }

    fn rand_name() -> u128 {
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
    }

    fn all(text: &[u8], maximum: usize, block: usize) -> Vec<(i64, String)> {
        let (path, mut f) = file(text);
        let size = files::length(&f);
        let mut reader = JsonlBackwardReader::with_block(&mut f, size, maximum, block);
        let mut lines = vec![];
        while let Some((offset, line)) = reader.previous() {
            lines.push((offset, String::from_utf8(line).unwrap()));
        }
        let _ = std::fs::remove_file(path);
        lines
    }

    #[test]
    fn lines_come_newest_first_without_the_one_being_written() {
        for block in [1, 2, 3, 7, 256] {
            assert_eq!(all(b"a\nbb\n\nccc\nhalf", 100, block), vec![(6, "ccc".into()), (2, "bb".into()), (0, "a".into())], "block {block}");
        }
    }

    #[test]
    fn a_line_past_the_limit_is_skipped_whole() {
        for block in [1, 3, 4, 256] {
            assert_eq!(all(b"ok\ntoolongline\nfine\n", 5, block), vec![(15, "fine".into()), (0, "ok".into())], "block {block}");
        }
    }
}
