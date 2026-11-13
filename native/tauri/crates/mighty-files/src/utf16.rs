//! C#'s string rules where they count UTF-16 units: ordinal order, `IndexOf`, `LastIndexOf`, substrings by unit.

use std::cmp::Ordering;

/// `string.CompareOrdinal`: by UTF-16 code units.
pub fn ordinal(a: &str, b: &str) -> Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

/// `char.ToUpperInvariant` of one UTF-16 unit, as `OrdinalIgnoreCase` compares (a unit with no one-to-one upper case is itself).
pub fn upper_unit(unit: u16) -> u16 {
    match char::from_u32(unit as u32) {
        Some(c) if c.is_ascii() => c.to_ascii_uppercase() as u16,
        Some(c) => {
            let mut upper = c.to_uppercase();
            match (upper.next(), upper.next()) {
                (Some(u), None) if (u as u32) <= 0xFFFF => u as u16,
                _ => unit,
            }
        }
        None => unit,
    }
}

/// `text.IndexOf(pattern, from, StringComparison.Ordinal)` over units.
pub fn index_of(text: &[u16], pattern: &[u16], from: usize) -> Option<usize> {
    if pattern.is_empty() {
        return (from <= text.len()).then_some(from);
    }
    if text.len() < pattern.len() {
        return None;
    }
    (from..=text.len() - pattern.len()).find(|&at| text[at..at + pattern.len()] == *pattern)
}

/// `text.IndexOf(unit, from)`.
pub fn index_of_unit(text: &[u16], unit: u16, from: usize) -> Option<usize> {
    text.get(from..)?.iter().position(|u| *u == unit).map(|at| at + from)
}

/// The units as a string (a lone surrogate becomes U+FFFD, as `Wire.Json` writes it).
pub fn string(units: &[u16]) -> String {
    String::from_utf16_lossy(units)
}

pub fn units(text: &str) -> Vec<u16> {
    text.encode_utf16().collect()
}

pub fn is_high_surrogate(unit: u16) -> bool {
    (0xD800..=0xDBFF).contains(&unit)
}
