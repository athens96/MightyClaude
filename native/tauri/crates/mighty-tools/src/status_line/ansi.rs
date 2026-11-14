//! ANSI colour and weight in a status line's output (C# `AnsiColorKind`, `AnsiColor`, `AnsiPalette`, `AnsiSegment`, `AnsiText`,
//! StatusLine.cs:8-165), as the Mac's `ANSIText` reads SGR: the 16 named colours, the 256-colour palette and 24-bit RGB, in the
//! semicolon and colon forms; OSC sequences and other CSI sequences are dropped.

use crate::setup::windows::parse_int;

/// Unset, one of the 16 named colours, a 256-colour palette index, or 24-bit RGB.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum AnsiColorKind {
    #[default]
    None,
    Standard,
    Palette,
    Rgb,
}

/// C# `AnsiColor(Kind, A, B, C)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct AnsiColor {
    pub kind: AnsiColorKind,
    pub a: i32,
    pub b: i32,
    pub c: i32,
}

impl AnsiColor {
    pub const DEFAULT: AnsiColor = AnsiColor { kind: AnsiColorKind::None, a: 0, b: 0, c: 0 };

    pub const fn standard(index: i32) -> AnsiColor {
        AnsiColor { kind: AnsiColorKind::Standard, a: index, b: 0, c: 0 }
    }

    /// 38;5;n / 48;5;n — the index clamped like the Mac's (`min(255, max(0, index))`).
    pub fn palette(index: i32) -> AnsiColor {
        AnsiColor { kind: AnsiColorKind::Palette, a: index.clamp(0, 255), b: 0, c: 0 }
    }

    /// 38;2;r;g;b / 48;2;r;g;b — each component clamped.
    pub fn rgb(r: i32, g: i32, b: i32) -> AnsiColor {
        AnsiColor { kind: AnsiColorKind::Rgb, a: r.clamp(0, 255), b: g.clamp(0, 255), c: b.clamp(0, 255) }
    }

    pub const BLACK: AnsiColor = AnsiColor::standard(0);
    pub const RED: AnsiColor = AnsiColor::standard(1);
    pub const GREEN: AnsiColor = AnsiColor::standard(2);
    pub const YELLOW: AnsiColor = AnsiColor::standard(3);
    pub const BLUE: AnsiColor = AnsiColor::standard(4);
    pub const MAGENTA: AnsiColor = AnsiColor::standard(5);
    pub const CYAN: AnsiColor = AnsiColor::standard(6);
    pub const WHITE: AnsiColor = AnsiColor::standard(7);
    pub const BRIGHT_BLACK: AnsiColor = AnsiColor::standard(8);
    pub const BRIGHT_RED: AnsiColor = AnsiColor::standard(9);
    pub const BRIGHT_GREEN: AnsiColor = AnsiColor::standard(10);
    pub const BRIGHT_YELLOW: AnsiColor = AnsiColor::standard(11);
    pub const BRIGHT_BLUE: AnsiColor = AnsiColor::standard(12);
    pub const BRIGHT_MAGENTA: AnsiColor = AnsiColor::standard(13);
    pub const BRIGHT_CYAN: AnsiColor = AnsiColor::standard(14);
    pub const BRIGHT_WHITE: AnsiColor = AnsiColor::standard(15);
}

const NAMED16: [(u8, u8, u8); 16] = [
    (0, 0, 0),
    (128, 0, 0),
    (0, 128, 0),
    (128, 128, 0),
    (0, 0, 128),
    (128, 0, 128),
    (0, 128, 128),
    (192, 192, 192),
    (128, 128, 128),
    (255, 0, 0),
    (0, 255, 0),
    (255, 255, 0),
    (0, 0, 255),
    (255, 0, 255),
    (0, 255, 255),
    (255, 255, 255),
];
const CUBE_LEVELS: [u8; 6] = [0, 95, 135, 175, 215, 255];

/// C# `AnsiPalette.ToRgb`: xterm's 256 colours (0-15 named, 16-231 the 6×6×6 cube, 232-255 greys), the index clamped.
pub fn palette_rgb(index: i32) -> (u8, u8, u8) {
    let index = index.clamp(0, 255) as usize;
    if index < 16 {
        return NAMED16[index];
    }
    if index < 232 {
        let i = index - 16;
        return (CUBE_LEVELS[i / 36], CUBE_LEVELS[i / 6 % 6], CUBE_LEVELS[i % 6]);
    }
    let grey = (8 + (index - 232) * 10) as u8;
    (grey, grey, grey)
}

/// C# `AnsiSegment`.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AnsiSegment {
    pub text: String,
    pub foreground: AnsiColor,
    pub background: AnsiColor,
    pub bold: bool,
    pub dim: bool,
    pub italic: bool,
    pub underline: bool,
}

#[derive(Default, Clone, Copy)]
struct Style {
    fg: AnsiColor,
    bg: AnsiColor,
    bold: bool,
    dim: bool,
    italic: bool,
    underline: bool,
}

impl Style {
    fn segment(&self, text: String) -> AnsiSegment {
        AnsiSegment {
            text,
            foreground: self.fg,
            background: self.bg,
            bold: self.bold,
            dim: self.dim,
            italic: self.italic,
            underline: self.underline,
        }
    }
}

/// C# `AnsiText.Parse`: a string with ANSI SGR sequences as styled segments. OSC sequences are stripped; a CSI that is not SGR is
/// skipped with its final byte; an escape of any other kind is kept as it is.
pub fn parse(text: &str) -> Vec<AnsiSegment> {
    let chars: Vec<char> = text.chars().collect();
    let mut segments = Vec::new();
    let mut style = Style::default();
    let mut pending = String::new();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '\u{1b}' && i + 1 < chars.len() {
            match chars[i + 1] {
                '[' => {
                    if !pending.is_empty() {
                        segments.push(style.segment(std::mem::take(&mut pending)));
                    }
                    i += 2;
                    let mut sequence = String::new();
                    while i < chars.len() && !matches!(chars[i], 'm' | 'A' | 'B' | 'C' | 'D' | 'H' | 'J' | 'K') {
                        sequence.push(chars[i]);
                        i += 1;
                    }
                    if i < chars.len() && chars[i] == 'm' {
                        i += 1;
                        apply_sgr(&sequence, &mut style);
                    } else {
                        // A non-SGR CSI: its final byte is skipped.
                        i += 1;
                    }
                }
                ']' => {
                    // OSC: skipped to its terminator (BEL or ESC \).
                    i += 2;
                    while i < chars.len() {
                        if chars[i] == '\u{7}' {
                            i += 1;
                            break;
                        }
                        if chars[i] == '\u{1b}' && i + 1 < chars.len() && chars[i + 1] == '\\' {
                            i += 2;
                            break;
                        }
                        i += 1;
                    }
                }
                _ => {
                    pending.push(chars[i]);
                    i += 1;
                }
            }
        } else {
            pending.push(chars[i]);
            i += 1;
        }
    }
    if !pending.is_empty() {
        segments.push(style.segment(pending));
    }
    segments
}

/// The Mac's `ANSIText.apply`: semicolon parameters make a flat code list, a colon group (`38:2::r:g:b`, `38:5:n`) collapses to one
/// extended colour in that list, and 38/48 then take the following one or three codes; a malformed or truncated extended colour
/// clears the colour (and takes the rest of the list).
fn apply_sgr(sequence: &str, style: &mut Style) {
    let mut codes: Vec<i32> = Vec::new();
    for token in sequence.split(';') {
        if token.contains(':') {
            let subs: Vec<Option<i32>> = token.split(':').map(|s| if s.is_empty() { None } else { parse_int(s) }).collect();
            let (Some(Some(kind)), Some(Some(mode))) = (subs.first(), subs.get(1)) else { continue };
            if subs.len() < 3 || (*kind != 38 && *kind != 48) {
                continue;
            }
            let numbers: Vec<i32> = subs[2..].iter().flatten().copied().collect();
            if *mode == 2 && numbers.len() >= 3 {
                codes.extend([*kind, 2]);
                codes.extend(&numbers[numbers.len() - 3..]);
            } else if *mode == 5 && !numbers.is_empty() {
                codes.extend([*kind, 5, numbers[0]]);
            }
        } else {
            codes.push(if token.is_empty() { 0 } else { parse_int(token).unwrap_or(-1) });
        }
    }
    if codes.is_empty() {
        *style = Style::default();
        return;
    }
    let mut position = 0;
    while position < codes.len() {
        let code = codes[position];
        position += 1;
        match code {
            0 => *style = Style::default(),
            1 => style.bold = true,
            2 => style.dim = true,
            3 => style.italic = true,
            4 => style.underline = true,
            22 => {
                style.bold = false;
                style.dim = false;
            }
            23 => style.italic = false,
            24 => style.underline = false,
            30..=37 => style.fg = AnsiColor::standard(code - 30),
            90..=97 => style.fg = AnsiColor::standard(code - 90 + 8),
            39 => style.fg = AnsiColor::DEFAULT,
            40..=47 => style.bg = AnsiColor::standard(code - 40),
            100..=107 => style.bg = AnsiColor::standard(code - 100 + 8),
            49 => style.bg = AnsiColor::DEFAULT,
            38 | 48 => {
                let mut color = AnsiColor::DEFAULT;
                if position < codes.len() && codes[position] == 5 && position + 1 < codes.len() {
                    color = AnsiColor::palette(codes[position + 1]);
                    position += 2;
                } else if position < codes.len() && codes[position] == 2 && position + 3 < codes.len() {
                    color = AnsiColor::rgb(codes[position + 1], codes[position + 2], codes[position + 3]);
                    position += 4;
                } else {
                    position = codes.len();
                }
                if code == 38 {
                    style.fg = color;
                } else {
                    style.bg = color;
                }
            }
            _ => {}
        }
    }
}
