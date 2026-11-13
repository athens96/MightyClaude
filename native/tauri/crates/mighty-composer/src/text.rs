//! Text decoding as C# reads it: `TextEncodings` and `FilePreviewClassifier.DecodeText` (WorkspaceFiles.cs:335-399, 470-490) for
//! the picture checks, `File.ReadAllText(path, UTF8)` for the slash catalogue, and UTF-16 cuts as `string[..n]` makes them.

/// C# `TextEncodingKind` (WorkspaceFiles.cs:335).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TextEncodingKind {
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Utf32Be,
    Cp949,
}

/// C# `TextEncodings.ByteOrderMark`.
pub fn byte_order_mark(kind: TextEncodingKind) -> &'static [u8] {
    match kind {
        TextEncodingKind::Utf8Bom => &[0xEF, 0xBB, 0xBF],
        TextEncodingKind::Utf16Le => &[0xFF, 0xFE],
        TextEncodingKind::Utf16Be => &[0xFE, 0xFF],
        TextEncodingKind::Utf32Le => &[0xFF, 0xFE, 0x00, 0x00],
        TextEncodingKind::Utf32Be => &[0x00, 0x00, 0xFE, 0xFF],
        _ => &[],
    }
}

/// C# `TextEncodings.FromByteOrderMark`: UTF-32 before UTF-16, whose LE mark it starts with.
pub fn from_byte_order_mark(data: &[u8]) -> Option<TextEncodingKind> {
    [TextEncodingKind::Utf32Le, TextEncodingKind::Utf32Be, TextEncodingKind::Utf8Bom, TextEncodingKind::Utf16Le, TextEncodingKind::Utf16Be]
        .into_iter()
        .find(|kind| data.starts_with(byte_order_mark(*kind)))
}

/// C# `TextEncodings.TailSlack`: bytes a cut sample may leave of a character at its end.
fn tail_slack(kind: TextEncodingKind) -> usize {
    if kind == TextEncodingKind::Cp949 { 1 } else { 3 }
}

/// C# `DecodedText`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DecodedText {
    pub text: String,
    pub encoding: TextEncodingKind,
}

fn strict(data: &[u8], kind: TextEncodingKind) -> Option<String> {
    match kind {
        TextEncodingKind::Utf8 | TextEncodingKind::Utf8Bom => std::str::from_utf8(data).ok().map(str::to_string),
        TextEncodingKind::Utf16Le | TextEncodingKind::Utf16Be => {
            let (pairs, rest) = data.as_chunks::<2>();
            if !rest.is_empty() {
                return None;
            }
            let units =
                pairs.iter().map(|p| if kind == TextEncodingKind::Utf16Le { u16::from_le_bytes(*p) } else { u16::from_be_bytes(*p) });
            char::decode_utf16(units).collect::<Result<String, _>>().ok()
        }
        TextEncodingKind::Utf32Le | TextEncodingKind::Utf32Be => {
            let (quads, rest) = data.as_chunks::<4>();
            if !rest.is_empty() {
                return None;
            }
            quads
                .iter()
                .map(|p| char::from_u32(if kind == TextEncodingKind::Utf32Le { u32::from_le_bytes(*p) } else { u32::from_be_bytes(*p) }))
                .collect::<Option<String>>()
        }
        TextEncodingKind::Cp949 => cp949(data),
    }
}

/// .NET's code page 949 (`CodePagesEncodingProvider`), strict: WHATWG EUC-KR (`encoding_rs`) for the pairs both read, and what
/// .NET reads besides (checked byte for byte against it, tests/parity.rs): 0x80 and 0xFF alone as U+0080 and U+F8F7, and the two
/// user-defined rows C9A1-C9FE and FEA1-FEFE as U+E000-U+E05D and U+E05E-U+E0BB.
fn cp949(data: &[u8]) -> Option<String> {
    let mut out = String::with_capacity(data.len());
    let mut at = 0;
    while at < data.len() {
        let lead = data[at];
        match lead {
            0x00..=0x7F => {
                out.push(lead as char);
                at += 1;
            }
            0x80 => {
                out.push('\u{80}');
                at += 1;
            }
            0xFF => {
                out.push('\u{F8F7}');
                at += 1;
            }
            _ => {
                let trail = *data.get(at + 1)?;
                if matches!(lead, 0xC9 | 0xFE) && (0xA1..=0xFE).contains(&trail) {
                    let base = if lead == 0xC9 { 0xE000 } else { 0xE05E };
                    out.push(char::from_u32(base + (trail - 0xA1) as u32)?);
                } else {
                    out.push_str(&encoding_rs::EUC_KR.decode_without_bom_handling_and_without_replacement(&data[at..at + 2])?);
                }
                at += 2;
            }
        }
    }
    Some(out)
}

fn decode(data: &[u8], kind: TextEncodingKind, sample: bool) -> Option<String> {
    let most = if sample { tail_slack(kind).min(data.len()) } else { 0 };
    (0..=most).find_map(|drop| strict(&data[..data.len() - drop], kind))
}

/// C# `FilePreviewClassifier.DecodeText`: the byte order mark's encoding, else UTF-8, else CP949; None when none decodes cleanly
/// (or a NUL shows binary). With `sample` the data may end inside a character.
pub fn decode_text(data: &[u8], sample: bool) -> Option<DecodedText> {
    if let Some(marked) = from_byte_order_mark(data) {
        return decode(&data[byte_order_mark(marked).len()..], marked, sample).map(|text| DecodedText { text, encoding: marked });
    }
    if data.contains(&0) {
        return None;
    }
    [TextEncodingKind::Utf8, TextEncodingKind::Cp949]
        .into_iter()
        .find_map(|kind| decode(data, kind, sample).map(|text| DecodedText { text, encoding: kind }))
}

/// `File.ReadAllText(path, Encoding.UTF8)`: a byte order mark chooses UTF-8, UTF-16 or UTF-32 (as `StreamReader` detects them),
/// else UTF-8; bytes that do not decode become U+FFFD.
pub fn read_all_text(data: &[u8]) -> String {
    let lossy16 = |bytes: &[u8], le: bool| {
        let units: Vec<u16> = bytes
            .chunks(2)
            .map(|p| {
                if p.len() < 2 {
                    0xFFFD
                } else if le {
                    u16::from_le_bytes([p[0], p[1]])
                } else {
                    u16::from_be_bytes([p[0], p[1]])
                }
            })
            .collect();
        char::decode_utf16(units).map(|c| c.unwrap_or('\u{FFFD}')).collect::<String>()
    };
    let lossy32 = |bytes: &[u8], le: bool| {
        bytes
            .chunks(4)
            .map(|p| {
                if p.len() < 4 {
                    return '\u{FFFD}';
                }
                let v = if le { u32::from_le_bytes([p[0], p[1], p[2], p[3]]) } else { u32::from_be_bytes([p[0], p[1], p[2], p[3]]) };
                char::from_u32(v).unwrap_or('\u{FFFD}')
            })
            .collect::<String>()
    };
    if data.starts_with(&[0xFE, 0xFF]) {
        return lossy16(&data[2..], false);
    }
    if data.starts_with(&[0xFF, 0xFE, 0, 0]) {
        return lossy32(&data[4..], true);
    }
    if data.starts_with(&[0xFF, 0xFE]) {
        return lossy16(&data[2..], true);
    }
    if data.starts_with(&[0, 0, 0xFE, 0xFF]) {
        return lossy32(&data[4..], false);
    }
    let body = data.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(data);
    String::from_utf8_lossy(body).into_owned()
}

/// C# `s[..Math.Min(s.Length, n)]`: the first `n` UTF-16 units. A cut through a surrogate pair leaves its high half, which
/// `Wire.Json` writes as U+FFFD; here it is U+FFFD.
pub fn prefix_utf16(value: &str, n: usize) -> String {
    let mut out = String::new();
    let mut units = 0;
    for c in value.chars() {
        let width = c.len_utf16();
        if units + width > n {
            if width == 2 && units < n {
                out.push('\u{FFFD}');
            }
            break;
        }
        units += width;
        out.push(c);
    }
    out
}

/// C# `string.Length`.
pub fn utf16_len(value: &str) -> usize {
    value.encode_utf16().count()
}
