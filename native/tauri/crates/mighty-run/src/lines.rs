//! C# `OutputParser.LinesAsync` (OutputParser.cs:102-116): a child's output as lines, each at most a number of UTF-8 bytes; a longer
//! one is skipped up to its line break and heard of once. Empty lines are left out. The stream is read as UTF-8 (`StreamReader`):
//! a byte-order mark at its start is dropped, an invalid byte reads as U+FFFD.

use mighty_platform::process::ChildReader;
use tokio::io::AsyncReadExt;

/// Reads lines from a child's pipe.
pub struct Lines {
    reader: Option<ChildReader>,
    maximum: usize,
    buffer: Vec<u8>,
    line: Vec<u8>,
    at: usize,
    filled: usize,
    dropping: bool,
    started: bool,
    ended: bool,
}

/// What the next read gave.
#[derive(Debug, PartialEq, Eq)]
pub enum Next {
    Line(String),
    /// A line longer than the limit was skipped (once per such line).
    TooLong,
    End,
}

/// A line buffer grown past this is let go after its line (`RetainedLineCapacity`).
const RETAINED_LINE_CAPACITY: usize = 1_048_576;

impl Lines {
    pub fn new(reader: Option<ChildReader>, maximum_line_bytes: usize) -> Lines {
        Lines {
            reader,
            maximum: maximum_line_bytes,
            buffer: vec![0u8; 8192],
            line: Vec::new(),
            at: 0,
            filled: 0,
            dropping: false,
            started: false,
            ended: false,
        }
    }

    fn reset(&mut self) {
        if self.line.capacity() > RETAINED_LINE_CAPACITY {
            self.line = Vec::new();
        } else {
            self.line.clear();
        }
    }

    fn take_line(&mut self) -> String {
        let text = String::from_utf8_lossy(&self.line).into_owned();
        self.reset();
        text
    }

    /// The next line, a skipped one, or the end of the output (a read error ends it too).
    pub async fn next(&mut self) -> Next {
        loop {
            while self.at < self.filled {
                let byte = self.buffer[self.at];
                self.at += 1;
                if byte == b'\n' {
                    let was_dropping = self.dropping;
                    self.dropping = false;
                    if !was_dropping && !self.line.is_empty() {
                        return Next::Line(self.take_line());
                    }
                    self.reset();
                    continue;
                }
                if self.dropping {
                    continue;
                }
                self.line.push(byte);
                if self.line.len() > self.maximum {
                    self.dropping = true;
                    self.reset();
                    return Next::TooLong;
                }
            }
            if self.ended {
                if !self.dropping && !self.line.is_empty() {
                    return Next::Line(self.take_line());
                }
                self.reset();
                return Next::End;
            }
            let Some(reader) = self.reader.as_mut() else {
                self.ended = true;
                continue;
            };
            match reader.read(&mut self.buffer).await {
                Ok(0) | Err(_) => {
                    self.ended = true;
                }
                Ok(read) => {
                    self.at = 0;
                    self.filled = read;
                    if !self.started {
                        self.started = true;
                        if self.buffer[..read].starts_with(&[0xEF, 0xBB, 0xBF]) {
                            self.at = 3;
                        }
                    }
                }
            }
        }
    }
}
