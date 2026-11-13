//! SVG checks: whether bytes are an SVG document (C# `AgentImageSupport.SvgDocument`, AgentImages.cs:186), whether drawing one
//! could read anything but its own bytes (`FilePreviewClassifier.SvgLoadsExternalContent` and `SvgReferences`,
//! WorkspaceFiles.cs:582-731), its size (`SvgSize`, :548), and a bounded, self-contained document for a rasterizer
//! (`SafeSvgDocument`, SafeSvgDocument.cs).

use std::borrow::Cow;

use quick_xml::events::Event;
use quick_xml::name::ResolveResult;

use crate::images::{AgentImageError, MAXIMUM_IMAGE_BYTES};
use crate::slash::lower_invariant;
use crate::text::{self, TextEncodingKind};

/// `FilePreviewClassifier.MaximumVectorPoints`.
pub const MAXIMUM_VECTOR_POINTS: f64 = 10_000_000.0;

/// C# `AgentImageSupport.SvgDocument`: after an optional byte order mark, XML declaration, processing instructions, comments and
/// doctype, the first element is `<svg`.
pub fn svg_document(data: &[u8]) -> bool {
    let Some(decoded) = text::decode_text(&data[..data.len().min(65_536)], true) else { return false };
    let lowered = lower_invariant(&decoded.text);
    let mut rest = lowered.as_str();
    loop {
        rest = rest.trim_start().trim_start_matches('\u{FEFF}').trim_start();
        if rest.starts_with("<?") {
            let Some(end) = rest.find("?>") else { return false };
            rest = &rest[end + 2..];
        } else if rest.starts_with("<!--") {
            let Some(end) = rest.find("-->") else { return false };
            rest = &rest[end + 3..];
        } else if rest.starts_with("<!doctype") {
            let Some(end) = rest.find('>') else { return false };
            rest = &rest[end + 1..];
        } else {
            break;
        }
    }
    let Some(after) = rest.strip_prefix("<svg") else { return false };
    after.chars().next().is_none_or(|c| c.is_whitespace() || c == '>' || c == '/')
}

// ── SvgReferences ───────────────────────────────────────────────────────────────────────────────────────────────────────

fn find_all(needle: &[u8], text: &[u8]) -> Vec<usize> {
    let mut found = Vec::new();
    if needle.is_empty() || needle.len() > text.len() {
        return found;
    }
    let mut offset = 0;
    while offset + needle.len() <= text.len() {
        match text[offset..].windows(needle.len()).position(|w| w == needle) {
            Some(hit) => {
                found.push(offset + hit);
                offset += hit + 1;
            }
            None => break,
        }
    }
    found
}

fn is_space(value: u8) -> bool {
    matches!(value, 0x20 | 0x09 | 0x0A | 0x0D | 0x0C)
}

fn skip_space(text: &[u8], start: usize) -> usize {
    let mut index = start;
    while index < text.len() && is_space(text[index]) {
        index += 1;
    }
    index
}

/// `SvgReferences.IsLocal`: a `#fragment` or a `data:` URL that is not svg, xml or html.
fn is_local(text: &[u8], start: usize) -> bool {
    let mut index = skip_space(text, start);
    if index < text.len() && matches!(text[index], b'"' | b'\'') {
        index = skip_space(text, index + 1);
    }
    if index >= text.len() {
        return false;
    }
    if text[index] == b'#' {
        return true;
    }
    let scheme = b"data:";
    if text.len() - index <= scheme.len() || &text[index..index + scheme.len()] != scheme {
        return false;
    }
    let rest_start = index + scheme.len();
    let rest = &text[rest_start..rest_start + 256.min(text.len() - rest_start)];
    let Some(comma) = rest.iter().position(|b| *b == b',') else { return false };
    let media: Vec<u8> = rest[..comma].iter().copied().filter(|b| !is_space(*b)).collect();
    ["svg", "xml", "html"].iter().all(|word| find_all(word.as_bytes(), &media).is_empty())
}

/// `SvgReferences.ReadableDeclaration`: an XML declaration's encoding is one whose markup is plain ASCII (or the BOM's own).
fn readable_declaration(text: &[u8], bom: TextEncodingKind) -> bool {
    let start = skip_space(text, 0);
    let opening = b"<?xml";
    if text.len() - start < opening.len() || &text[start..start + opening.len()] != opening {
        return true;
    }
    let declaration = &text[start..start + 512.min(text.len() - start)];
    let closes = find_all(b"?>", declaration);
    let Some(&close) = closes.first() else { return false };
    let head = &declaration[..close];
    let keys = find_all(b"encoding", head);
    let Some(&key) = keys.first() else { return true };
    let mut index = skip_space(head, key + 8);
    if index >= head.len() || head[index] != b'=' {
        return false;
    }
    index = skip_space(head, index + 1);
    if index >= head.len() || !matches!(head[index], b'"' | b'\'') {
        return false;
    }
    let quote = head[index];
    let Some(end) = head[index + 1..].iter().position(|b| *b == quote).map(|p| p + index + 1) else { return false };
    let name = String::from_utf8_lossy(&head[index + 1..end]);
    let mut readable = vec!["utf-8", "utf8", "us-ascii", "ascii", "iso-8859-1", "latin1"];
    if matches!(bom, TextEncodingKind::Utf16Le | TextEncodingKind::Utf16Be) {
        readable.extend(["utf-16", "utf-16le", "utf-16be"]);
    }
    if matches!(bom, TextEncodingKind::Utf32Le | TextEncodingKind::Utf32Be) {
        readable.extend(["utf-32", "utf-32le", "utf-32be"]);
    }
    readable.contains(&name.as_ref())
}

fn hex_value(c: char) -> Option<u32> {
    c.to_digit(16)
}

/// `SvgReferences.Reference`: `&#NN;`, `&#xHH;` or one of the five predefined entities at `start` (after the `&`).
fn reference(chars: &[char], start: usize) -> Option<(char, usize)> {
    let mut index = start;
    if index < chars.len() && chars[index] == '#' {
        index += 1;
        let mut radix = 10;
        if index < chars.len() && matches!(chars[index], 'x' | 'X') {
            radix = 16;
            index += 1;
        }
        let (mut value, mut digits) = (0u64, 0);
        while index < chars.len() && hex_value(chars[index]).is_some_and(|d| d < radix) {
            value = (value * radix as u64 + hex_value(chars[index]).unwrap_or(0) as u64).min(0x11_0000);
            digits += 1;
            index += 1;
        }
        if digits == 0 || index >= chars.len() || chars[index] != ';' {
            return None;
        }
        return char::from_u32(value as u32).map(|c| (c, index + 1));
    }
    let mut name = String::new();
    let mut units = 0;
    while index < chars.len() && units < 5 && chars[index].is_alphabetic() {
        name.push(chars[index]);
        units += chars[index].len_utf16();
        index += 1;
    }
    if index >= chars.len() || chars[index] != ';' {
        return None;
    }
    let c = match lower_invariant(&name).as_str() {
        "amp" => '&',
        "lt" => '<',
        "gt" => '>',
        "quot" => '"',
        "apos" => '\'',
        _ => return None,
    };
    Some((c, index + 1))
}

/// `SvgReferences.DecodingCharacterReferences`: character references and the five entities replaced, as the parser reads them.
fn decoding_character_references(text: &str) -> Cow<'_, str> {
    if !text.contains('&') {
        return Cow::Borrowed(text);
    }
    let chars: Vec<char> = text.chars().collect();
    let mut output = String::with_capacity(text.len());
    let mut index = 0;
    while index < chars.len() {
        if chars[index] == '&'
            && let Some((value, next)) = reference(&chars, index + 1)
        {
            output.push(value);
            index = next;
        } else {
            output.push(chars[index]);
            index += 1;
        }
    }
    Cow::Owned(output)
}

/// C# `FilePreviewClassifier.SvgLoadsExternalContent` (WorkspaceFiles.cs:582): the deliberately conservative macOS rule.
pub fn svg_loads_external_content(data: &[u8]) -> bool {
    let Some(decoded) = text::decode_text(data, false) else { return true };
    let lowered = lower_invariant(&decoding_character_references(&decoded.text));
    let text = lowered.as_bytes();
    if text.contains(&b'\\') {
        return true;
    }
    for token in ["<!entity", "@import", "image-set(", "xml:base"] {
        if !find_all(token.as_bytes(), text).is_empty() {
            return true;
        }
    }
    for start in find_all(b"<!doctype", text) {
        let Some(end) = text[start..].iter().position(|b| *b == b'>').map(|p| p + start) else { return true };
        let doctype = &text[start..end];
        if ["system", "public", "["].iter().any(|word| !find_all(word.as_bytes(), doctype).is_empty()) {
            return true;
        }
    }
    if !readable_declaration(text, decoded.encoding) {
        return true;
    }
    for name in ["href", "src", "srcset"] {
        for start in find_all(name.as_bytes(), text) {
            let index = skip_space(text, start + name.len());
            if index >= text.len() || text[index] != b'=' {
                continue;
            }
            if !is_local(text, index + 1) {
                return true;
            }
        }
    }
    find_all(b"url(", text).into_iter().any(|start| !is_local(text, start + 4))
}

// ── size ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `FilePreviewClassifier.IsDrawable`.
pub fn is_drawable(width: f64, height: f64) -> bool {
    width.is_finite()
        && height.is_finite()
        && width > 0.0
        && height > 0.0
        && width <= MAXIMUM_VECTOR_POINTS
        && height <= MAXIMUM_VECTOR_POINTS
}

/// `double.TryParse(…, NumberStyles.Float, InvariantCulture)`.
fn parse_float(value: &str) -> Option<f64> {
    let trimmed = value.trim_matches(|c| matches!(c, ' ' | '\t' | '\n' | '\u{b}' | '\u{c}' | '\r'));
    // Rust also reads "inf"; .NET's invariant culture spells it "Infinity".
    if trimmed.to_ascii_lowercase().trim_start_matches(['+', '-']) == "inf" {
        return None;
    }
    trimmed.parse::<f64>().ok()
}

/// `SvgLength`: `^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(px|pt|pc|in|cm|mm)?$` in CSS pixels.
fn svg_length(value: &str) -> Option<f64> {
    let b = value.as_bytes();
    let mut at = 0;
    if at < b.len() && matches!(b[at], b'+' | b'-') {
        at += 1;
    }
    let digits_start = at;
    while at < b.len() && b[at].is_ascii_digit() {
        at += 1;
    }
    let int_digits = at - digits_start;
    if int_digits > 0 {
        if at < b.len() && b[at] == b'.' {
            at += 1;
            while at < b.len() && b[at].is_ascii_digit() {
                at += 1;
            }
        }
    } else {
        if at >= b.len() || b[at] != b'.' {
            return None;
        }
        at += 1;
        let fraction = at;
        while at < b.len() && b[at].is_ascii_digit() {
            at += 1;
        }
        if at == fraction {
            return None;
        }
    }
    if at < b.len() && matches!(b[at], b'e' | b'E') {
        let mut e = at + 1;
        if e < b.len() && matches!(b[e], b'+' | b'-') {
            e += 1;
        }
        let exponent = e;
        while e < b.len() && b[e].is_ascii_digit() {
            e += 1;
        }
        if e > exponent {
            at = e;
        }
    }
    let number: f64 = value[..at].parse().ok()?;
    let unit = value[at..].trim_start_matches(char::is_whitespace);
    let factor = match unit.to_ascii_lowercase().as_str() {
        "" => 1.0,
        "px" => 1.0,
        "pt" => 96.0 / 72.0,
        "pc" => 16.0,
        "in" => 96.0,
        "cm" => 96.0 / 2.54,
        "mm" => 96.0 / 25.4,
        _ => return None,
    };
    Some(number * factor)
}

/// The first `<svg\b[^>]*>` tag, matched ignoring case.
fn svg_tag(text: &str) -> Option<&str> {
    let b = text.as_bytes();
    let mut from = 0;
    while let Some(found) = find_ascii_ci(&b[from..], b"<svg") {
        let start = from + found;
        let after = start + 4;
        let word_boundary = text[after..].chars().next().is_none_or(|c| !(c.is_alphanumeric() || c == '_'));
        if word_boundary && let Some(close) = text[after..].find('>') {
            return Some(&text[start..after + close + 1]);
        }
        from = start + 1;
    }
    None
}

fn find_ascii_ci(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    haystack.windows(needle.len()).position(|w| w.eq_ignore_ascii_case(needle))
}

/// `\s{name}\s*=\s*(["'])\s*([^"']*?)\s*\1`, ignoring case: the attribute's value, trimmed.
fn attribute<'a>(tag: &'a str, name: &str) -> Option<&'a str> {
    for (at, c) in tag.char_indices() {
        if !c.is_whitespace() {
            continue;
        }
        let after = at + c.len_utf8();
        let Some(candidate) = tag.get(after..after + name.len()) else { continue };
        if !candidate.eq_ignore_ascii_case(name) {
            continue;
        }
        let rest = tag[after + name.len()..].trim_start_matches(char::is_whitespace);
        let Some(rest) = rest.strip_prefix('=') else { continue };
        let rest = rest.trim_start_matches(char::is_whitespace);
        let Some(quote) = rest.chars().next().filter(|q| matches!(q, '"' | '\'')) else { continue };
        let body = &rest[1..];
        let Some(end) = body.find(['"', '\'']) else { continue };
        if !body[end..].starts_with(quote) {
            continue;
        }
        return Some(body[..end].trim_matches(char::is_whitespace));
    }
    None
}

/// `\sviewBox\s*=\s*(["'])([^"']*)\1`, ignoring case.
fn view_box(tag: &str) -> Option<&str> {
    for (at, c) in tag.char_indices() {
        if !c.is_whitespace() {
            continue;
        }
        let after = at + c.len_utf8();
        let Some(candidate) = tag.get(after..after + 7) else { continue };
        if !candidate.eq_ignore_ascii_case("viewBox") {
            continue;
        }
        let rest = tag[after + 7..].trim_start_matches(char::is_whitespace);
        let Some(rest) = rest.strip_prefix('=') else { continue };
        let rest = rest.trim_start_matches(char::is_whitespace);
        let Some(quote) = rest.chars().next().filter(|q| matches!(q, '"' | '\'')) else { continue };
        let body = &rest[1..];
        let Some(end) = body.find(['"', '\'']) else { continue };
        if !body[end..].starts_with(quote) {
            continue;
        }
        return Some(&body[..end]);
    }
    None
}

/// C# `FilePreviewClassifier.SvgSize` (WorkspaceFiles.cs:548): the root element's size in CSS pixels; None when it does not say.
pub fn svg_size(data: &[u8]) -> Option<(f64, f64)> {
    let decoded = text::decode_text(&data[..data.len().min(65_536)], true)?;
    let tag = svg_tag(&decoded.text)?;
    let width = attribute(tag, "width").and_then(svg_length);
    let height = attribute(tag, "height").and_then(svg_length);
    if let (Some(w), Some(h)) = (width, height) {
        return Some((w, h));
    }
    let boxed = view_box(tag)?;
    let parts: Vec<&str> = boxed.split([' ', ',', '\t', '\n', '\r']).filter(|p| !p.is_empty()).collect();
    if parts.len() != 4 {
        return None;
    }
    let box_width = parse_float(parts[2])?;
    let box_height = parse_float(parts[3])?;
    Some((width.unwrap_or(box_width), height.unwrap_or(box_height)))
}

// ── SafeSvgDocument ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `SafeSvgDocument(Xml, Width, Height)`: a bounded, self-contained SVG for a native rasterizer.
#[derive(Debug, Clone, PartialEq)]
pub struct SafeSvgDocument {
    pub xml: String,
    pub width: f64,
    pub height: f64,
}

const SVG_NAMESPACE: &[u8] = b"http://www.w3.org/2000/svg";
const ACTIVE: [&str; 9] = ["script", "foreignobject", "iframe", "object", "embed", "set", "animate", "animatetransform", "animatemotion"];

/// Text as `XmlWriter` writes it: `&`, `<`, `>` escaped, and each line end as `Environment.NewLine` (NewLineHandling.Replace).
fn escape_text(out: &mut String, value: &str) {
    let newline = if std::path::MAIN_SEPARATOR == '\\' { "\r\n" } else { "\n" };
    for c in value.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '\n' => out.push_str(newline),
            _ => out.push(c),
        }
    }
}

/// XML 1.0's `Char`: what a document may hold at all.
fn xml_char(c: char) -> bool {
    matches!(c as u32, 0x9 | 0xA | 0xD | 0x20..=0xD7FF | 0xE000..=0xFFFD | 0x10000..=0x10FFFF)
}

/// The document's characters as `XmlReader` reads the bytes: the byte order mark's encoding, else the declaration's (one
/// `SvgReferences.ReadableDeclaration` let through: UTF-8, ASCII or Latin-1), else UTF-8. A byte its encoding cannot read is the
/// reader's error only once the reader gets there: what comes before it is read (and may be refused for itself) first. So is a
/// character XML does not allow. The second value is where the first such one is, in the text.
fn xml_source(data: &[u8]) -> Result<(String, Option<usize>), AgentImageError> {
    let text = xml_text(data)?;
    let (text, bad) = match text {
        Ok(text) => (text, None),
        Err((text, bad)) => (text, Some(bad)),
    };
    let invalid = text.char_indices().find(|(_, c)| !xml_char(*c)).map(|(at, _)| at);
    let first = match (bad, invalid) {
        (Some(a), Some(b)) => Some(a.min(b)),
        (a, b) => a.or(b),
    };
    Ok((text, first))
}

/// The decoded text, or a lossy one with where its first unreadable byte was.
fn xml_text(data: &[u8]) -> Result<Result<String, (String, usize)>, AgentImageError> {
    if text::from_byte_order_mark(data).is_some() {
        return text::decode_text(data, false).map(|d| Ok(d.text)).ok_or(AgentImageError::Undecodable);
    }
    let head = &data[..data.len().min(512)];
    let declared = head
        .strip_prefix(b"<?xml")
        .and_then(|rest| rest.windows(2).position(|w| w == b"?>").map(|end| String::from_utf8_lossy(&rest[..end]).to_ascii_lowercase()))
        .and_then(|decl| {
            decl.split_once("encoding").map(|(_, rest)| {
                rest.trim_start_matches([' ', '=', '"', '\'']).chars().take_while(|c| !matches!(c, '"' | '\'')).collect::<String>()
            })
        });
    Ok(match declared.as_deref() {
        Some("iso-8859-1" | "latin1") => Ok(data.iter().map(|b| *b as char).collect()),
        Some("us-ascii" | "ascii") => Ok(data.iter().map(|b| if *b < 0x80 { *b as char } else { '?' }).collect()),
        _ => match std::str::from_utf8(data) {
            Ok(text) => Ok(text.to_string()),
            Err(e) => Err((String::from_utf8_lossy(data).into_owned(), e.valid_up_to())),
        },
    })
}

/// An XML `Name` as a processing instruction's target must be one (ASCII rules, and any non-ASCII letter).
fn xml_name(name: &str) -> bool {
    let mut chars = name.chars();
    chars.next().is_some_and(|c| c.is_alphabetic() || c == '_' || c == ':')
        && chars.all(|c| c.is_alphanumeric() || matches!(c, '_' | ':' | '-' | '.' | '\u{B7}'))
}

/// The XML declaration as the reader takes it: `version="1.0"`, then an optional `encoding`, then an optional `standalone`
/// (`yes` or `no`), nothing else.
fn valid_declaration(body: &str) -> bool {
    if !body.starts_with(char::is_whitespace) {
        return false;
    }
    let mut rest = body.trim_start();
    let mut seen = Vec::new();
    while !rest.is_empty() {
        let Some(eq) = rest.find('=') else { return false };
        let name = rest[..eq].trim_end();
        let after = rest[eq + 1..].trim_start();
        let Some(quote) = after.chars().next().filter(|q| matches!(q, '"' | '\'')) else { return false };
        let Some(end) = after[1..].find(quote) else { return false };
        let value = &after[1..1 + end];
        seen.push((name.to_string(), value.to_string()));
        rest = after[end + 2..].trim_start();
    }
    let names: Vec<&str> = seen.iter().map(|(n, _)| n.as_str()).collect();
    let ordered = matches!(
        names.as_slice(),
        ["version"] | ["version", "encoding"] | ["version", "standalone"] | ["version", "encoding", "standalone"]
    );
    ordered && seen[0].1 == "1.0" && seen.iter().all(|(n, v)| n != "standalone" || v == "yes" || v == "no")
}

fn escape_attribute(out: &mut String, value: &str) {
    for c in value.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\t' => out.push_str("&#x9;"),
            '\n' => out.push_str("&#xA;"),
            '\r' => out.push_str("&#xD;"),
            _ => out.push(c),
        }
    }
}

/// An attribute's value as an XML parser gives it: literal white space made spaces (line ends first), references expanded.
fn attribute_value(raw: &str) -> Result<String, AgentImageError> {
    let normalized = raw.replace("\r\n", "\n").replace(['\t', '\n', '\r'], " ");
    unescape(&normalized)
}

fn unescape(raw: &str) -> Result<String, AgentImageError> {
    let mut out = String::with_capacity(raw.len());
    let mut rest = raw;
    while let Some(at) = rest.find('&') {
        out.push_str(&rest[..at]);
        let Some(end) = rest[at..].find(';') else { return Err(AgentImageError::Undecodable) };
        out.push(entity(&rest[at + 1..at + end])?);
        rest = &rest[at + end + 1..];
    }
    out.push_str(rest);
    Ok(out)
}

/// A reference's character; an undeclared entity is the parser's error.
fn entity(name: &str) -> Result<char, AgentImageError> {
    let value = match name {
        "amp" => Some('&'),
        "lt" => Some('<'),
        "gt" => Some('>'),
        "quot" => Some('"'),
        "apos" => Some('\''),
        _ => {
            if let Some(hex) = name.strip_prefix("#x") {
                u32::from_str_radix(hex, 16).ok().and_then(char::from_u32)
            } else if let Some(dec) = name.strip_prefix('#') {
                dec.parse::<u32>().ok().and_then(char::from_u32)
            } else {
                None
            }
        }
    };
    value.ok_or(AgentImageError::Undecodable)
}

impl SafeSvgDocument {
    /// C# `SafeSvgDocument.Parse` (SafeSvgDocument.cs:11): the XML read with no DTD, no outside resources, at most 100,000 nodes and
    /// 64 levels, no script, active or foreign element, no event attribute or `xml:base`; then canonicalized and sized.
    pub fn parse(data: &[u8]) -> Result<SafeSvgDocument, AgentImageError> {
        if data.is_empty() {
            return Err(AgentImageError::Empty);
        }
        if data.len() > MAXIMUM_IMAGE_BYTES {
            return Err(AgentImageError::TooLarge);
        }
        if svg_loads_external_content(data) {
            return Err(AgentImageError::ExternalSvg);
        }
        let (decoded, bad) = xml_source(data)?;
        let (source, bad) = match decoded.strip_prefix('\u{FEFF}') {
            Some(rest) => (rest, bad.map(|b| b.saturating_sub('\u{FEFF}'.len_utf8()))),
            None => (decoded.as_str(), bad),
        };
        let mut reader = quick_xml::NsReader::from_str(source);
        reader.config_mut().check_end_names = true;
        reader.config_mut().expand_empty_elements = false;
        reader.config_mut().trim_text(false);

        let mut out = String::new();
        let mut nodes = 0usize;
        let mut depth = 0usize;
        let mut root_seen = false;
        let mut root_done = false;
        let mut root: Option<(String, Vec<(String, String)>)> = None;
        // Text and the references inside it are one node to the C# reader.
        let mut in_text = false;
        let count = |depth: usize, nodes: &mut usize| -> Result<(), AgentImageError> {
            *nodes += 1;
            if *nodes > 100_000 || depth > 64 { Err(AgentImageError::TooLarge) } else { Ok(()) }
        };
        loop {
            let event = reader.read_event().map_err(|_| AgentImageError::Undecodable)?;
            // The reader got to a byte it cannot read, or a character XML does not allow.
            if bad.is_some_and(|b| reader.buffer_position() as usize > b) {
                return Err(AgentImageError::Undecodable);
            }
            let text_event = matches!(event, Event::Text(_) | Event::GeneralRef(_));
            if !text_event {
                in_text = false;
            }
            match event {
                Event::Decl(_) => {
                    // Only the document's first characters may declare it.
                    let body = source.strip_prefix("<?xml").and_then(|rest| rest.find("?>").map(|end| &rest[..end]));
                    if nodes > 0 || root_seen || !body.is_some_and(valid_declaration) {
                        return Err(AgentImageError::Undecodable);
                    }
                    count(0, &mut nodes)?
                }
                Event::DocType(_) => return Err(AgentImageError::Undecodable),
                Event::PI(ref pi) => {
                    // A target that is no XML name, or `xml` itself where no declaration may be, is the reader's error.
                    let target = String::from_utf8_lossy(pi.target());
                    let error = if !xml_name(&target) || target.eq_ignore_ascii_case("xml") {
                        AgentImageError::Undecodable
                    } else {
                        AgentImageError::ExternalSvg
                    };
                    return Err(error);
                }
                Event::Comment(_) => {}
                Event::Start(ref e) | Event::Empty(ref e) => {
                    let empty = matches!(event, Event::Empty(_));
                    if root_done {
                        return Err(AgentImageError::Undecodable);
                    }
                    count(depth, &mut nodes)?;
                    let (namespace, local) = reader.resolve_element(e.name());
                    match namespace {
                        ResolveResult::Unbound => {}
                        ResolveResult::Bound(ns) if ns.as_ref() == SVG_NAMESPACE => {}
                        ResolveResult::Bound(_) => return Err(AgentImageError::ExternalSvg),
                        ResolveResult::Unknown(_) => return Err(AgentImageError::Undecodable),
                    }
                    let local_name = String::from_utf8_lossy(local.as_ref()).into_owned();
                    if ACTIVE.contains(&lower_invariant(&local_name).as_str()) {
                        return Err(AgentImageError::ExternalSvg);
                    }
                    let name = String::from_utf8_lossy(e.name().as_ref()).into_owned();
                    let mut attributes = Vec::new();
                    for attribute in e.attributes().with_checks(true) {
                        let attribute = attribute.map_err(|_| AgentImageError::Undecodable)?;
                        let qualified = String::from_utf8_lossy(attribute.key.as_ref()).into_owned();
                        let attr_local = String::from_utf8_lossy(attribute.key.local_name().as_ref()).into_owned();
                        let raw = String::from_utf8_lossy(&attribute.value).into_owned();
                        attributes.push((qualified, attr_local, attribute_value(&raw)?));
                    }
                    if attributes.len() > 128 {
                        return Err(AgentImageError::TooLarge);
                    }
                    if attributes
                        .iter()
                        .any(|(q, l, _)| l.get(..2).is_some_and(|p| p.eq_ignore_ascii_case("on")) || q.eq_ignore_ascii_case("xml:base"))
                    {
                        return Err(AgentImageError::ExternalSvg);
                    }
                    if !root_seen {
                        root_seen = true;
                        root = Some((local_name.clone(), attributes.iter().map(|(q, _, v)| (q.clone(), v.clone())).collect()));
                    }
                    out.push('<');
                    out.push_str(&name);
                    for (qualified, _, value) in &attributes {
                        out.push(' ');
                        out.push_str(qualified);
                        out.push_str("=\"");
                        escape_attribute(&mut out, value);
                        out.push('"');
                    }
                    if empty {
                        out.push_str(" />");
                        if depth == 0 {
                            root_done = true;
                        }
                    } else {
                        out.push('>');
                        depth += 1;
                    }
                }
                Event::End(ref e) => {
                    depth = depth.checked_sub(1).ok_or(AgentImageError::Undecodable)?;
                    count(depth, &mut nodes)?;
                    out.push_str("</");
                    out.push_str(&String::from_utf8_lossy(e.name().as_ref()));
                    out.push('>');
                    if depth == 0 {
                        root_done = true;
                    }
                }
                Event::Text(ref t) => {
                    let raw = t.decode().map_err(|_| AgentImageError::Undecodable)?;
                    let value = raw.replace("\r\n", "\n").replace('\r', "\n");
                    if depth == 0 {
                        if !value.chars().all(|c| matches!(c, ' ' | '\t' | '\n' | '\r')) {
                            return Err(AgentImageError::Undecodable);
                        }
                        // The reader's Whitespace node outside the root: counted, never kept by the document.
                        count(0, &mut nodes)?;
                        continue;
                    }
                    if !in_text {
                        count(depth, &mut nodes)?;
                        in_text = true;
                    }
                    escape_text(&mut out, &value);
                }
                Event::GeneralRef(ref r) => {
                    if depth == 0 {
                        return Err(AgentImageError::Undecodable);
                    }
                    let name = r.decode().map_err(|_| AgentImageError::Undecodable)?;
                    let c = entity(&name)?;
                    if !in_text {
                        count(depth, &mut nodes)?;
                        in_text = true;
                    }
                    escape_text(&mut out, &c.to_string());
                }
                Event::CData(ref c) => {
                    if depth == 0 {
                        return Err(AgentImageError::Undecodable);
                    }
                    count(depth, &mut nodes)?;
                    out.push_str("<![CDATA[");
                    // Line ends read as LF and written as the new line, as in text.
                    let newline = if std::path::MAIN_SEPARATOR == '\\' { "\r\n" } else { "\n" };
                    out.push_str(&String::from_utf8_lossy(c).replace("\r\n", "\n").replace('\r', "\n").replace('\n', newline));
                    out.push_str("]]>");
                }
                Event::Eof => break,
            }
        }
        if depth != 0 || !root_done {
            return Err(AgentImageError::Undecodable);
        }
        let (root_name, root_attributes) = root.ok_or(AgentImageError::Undecodable)?;
        if root_name != "svg" {
            return Err(AgentImageError::Undecodable);
        }
        // Dimension attributes are tiny numbers, not drawing payloads.
        for name in ["width", "height", "viewBox"] {
            if root_attributes.iter().any(|(q, v)| q == name && text::utf16_len(v) > 512) {
                return Err(AgentImageError::TooManyPixels);
            }
        }
        let (width, height) = svg_size(out.as_bytes()).unwrap_or((300.0, 150.0));
        if !is_drawable(width, height) {
            return Err(AgentImageError::TooManyPixels);
        }
        Ok(SafeSvgDocument { xml: out, width, height })
    }
}
