//! Streaming removal of a terminal's escape sequences (C# `TerminalTextCleaner`, AgentTerminal.cs:175-196): CSI sequences, OSC,
//! DCS, PM and APC strings (to BEL or ST), and every control character but newline and tab (and carriage return when asked), with a
//! sequence split between two reads removed whole. What an agent reads of a command and what a shell block shows go through it.

/// Where the cleaner stands between two reads.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum State {
    #[default]
    Text,
    /// After ESC.
    Escape,
    /// Inside `ESC [`, up to its final byte (`@`..`~`).
    Csi,
    /// Inside `ESC ]`, `ESC P`, `ESC ^` or `ESC _`, up to BEL or ST.
    Command,
    /// An ESC inside a command string: `\` ends it (ST).
    CommandEscape,
}

#[derive(Debug, Clone, Default)]
pub struct TerminalTextCleaner {
    state: State,
    keep_carriage_returns: bool,
}

impl TerminalTextCleaner {
    pub fn new() -> TerminalTextCleaner {
        TerminalTextCleaner::default()
    }

    /// A cleaner that keeps carriage returns (a shell block resolves their redraws itself).
    pub fn keeping_carriage_returns() -> TerminalTextCleaner {
        TerminalTextCleaner { state: State::Text, keep_carriage_returns: true }
    }

    /// Forgets a sequence it was inside (the text before was dropped).
    pub fn reset(&mut self) {
        self.state = State::Text;
    }

    pub fn clean(&mut self, input: &str) -> String {
        let mut result = String::with_capacity(input.len());
        for c in input.chars() {
            self.state = match self.state {
                State::Escape => match c {
                    '[' => State::Csi,
                    ']' | 'P' | '^' | '_' => State::Command,
                    _ => State::Text,
                },
                State::Csi if ('@'..='~').contains(&c) => State::Text,
                State::Csi => State::Csi,
                State::Command => match c {
                    '\u{7}' => State::Text,
                    '\u{1b}' => State::CommandEscape,
                    _ => State::Command,
                },
                State::CommandEscape => {
                    if c == '\\' {
                        State::Text
                    } else {
                        State::Command
                    }
                }
                State::Text => {
                    if c == '\u{1b}' {
                        State::Escape
                    } else {
                        // C# `char.IsControl`: the C0 and C1 controls and DEL.
                        if !c.is_control() || c == '\n' || c == '\t' || (self.keep_carriage_returns && c == '\r') {
                            result.push(c);
                        }
                        State::Text
                    }
                }
            };
        }
        result
    }
}
