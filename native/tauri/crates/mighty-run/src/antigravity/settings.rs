//! C# `AntigravitySettingsText` and `AntigravitySettingsException` (AntigravityPermissions.cs:536-875, M/MightyCore/AntigravityPermissions.swift
//! AntigravitySettingsText): `permissions.allow` edited in the bytes of agy's settings, every other byte left as it was — other keys,
//! their order and values, unknown fields, whitespace, line endings. Rules are added after the last one in the style of the list (or of
//! the file, for a list it has to create, with its line endings), and removing one takes out exactly what adding it put in; [`pruning`]
//! then takes out the `permissions` or `allow` adding had to create, once empty.
//!
//! The file is read as `JsonDocument.Parse` reads it (no comments, no trailing commas, at most 64 levels, a UTF-8 byte order mark is not
//! JSON here). A string is decoded only where C# decodes one (the keys of the objects walked, the allow list's entries); one that
//! `GetString` cannot decode (a lone surrogate escape, bytes that are not UTF-8) makes C# throw a plain `InvalidOperationException`
//! there, which this port reads as `notJSON`.

use std::collections::HashSet;

use mighty_core::normalize;

use crate::strings;

/// C# `AntigravitySettingsException(kind, message)`: why agy's settings were left alone. `kind` is the Mac's case name (missingFolder,
/// unreadable, notJSON, notObject, permissionsNotObject, allowNotArray, writeFailed, badRule, onceOnly); the message is the user's words.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SettingsError {
    pub kind: String,
    pub message: String,
}

impl SettingsError {
    /// `AntigravitySettingsException.Of(kind, path?, error?)`.
    pub fn of(kind: &str, path: Option<&str>, error: Option<&str>) -> SettingsError {
        let mut values: Vec<(&str, &str)> = vec![];
        if let Some(path) = path {
            values.push(("path", path));
        }
        if let Some(error) = error {
            values.push(("error", error));
        }
        // 항상 허용 for a rule that may be allowed only once says what the phone and a host say.
        let key =
            if kind == "onceOnly" { "remote.error.antigravityOnceOnly".to_string() } else { format!("antigravity.settings.error.{kind}") };
        SettingsError { kind: kind.into(), message: strings::get_with(&key, &values) }
    }
}

impl std::fmt::Display for SettingsError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for SettingsError {}

type Result<T> = std::result::Result<T, SettingsError>;

/// What adding made that was not there: the file, `permissions` (with its allow) or `allow`.
pub const CREATED_FILE: &str = "file";
pub const CREATED_PERMISSIONS: &str = "permissions";
pub const CREATED_ALLOW: &str = "allow";

// ── reading as JsonDocument reads ───────────────────────────────────────────────────────────────────────────────────────────────

/// A value of the document: an object's members (each key's token range), an array's elements, a string's token range, or anything else.
enum Node {
    Object(Vec<((usize, usize), Node)>),
    Array(Vec<Node>),
    Text(usize, usize),
    Other,
}

/// `JsonDocumentOptions`' default `MaxDepth`.
const MAXIMUM_DEPTH: usize = 64;

fn space(value: u8) -> bool {
    matches!(value, 0x20 | 0x09 | 0x0A | 0x0D)
}

/// `JsonDocument.Parse(bytes)` with the default options: the whole input one value, or None.
fn document(bytes: &[u8]) -> Option<Node> {
    struct Reader<'a> {
        bytes: &'a [u8],
        at: usize,
    }
    impl Reader<'_> {
        fn skip(&mut self) {
            while self.at < self.bytes.len() && space(self.bytes[self.at]) {
                self.at += 1;
            }
        }
        fn string(&mut self) -> Option<(usize, usize)> {
            let start = self.at;
            if self.bytes.get(self.at) != Some(&b'"') {
                return None;
            }
            self.at += 1;
            loop {
                let byte = *self.bytes.get(self.at)?;
                match byte {
                    b'"' => {
                        self.at += 1;
                        return Some((start, self.at));
                    }
                    b'\\' => {
                        let escape = *self.bytes.get(self.at + 1)?;
                        match escape {
                            b'"' | b'\\' | b'/' | b'b' | b'f' | b'n' | b'r' | b't' => self.at += 2,
                            b'u' => {
                                let digits = self.bytes.get(self.at + 2..self.at + 6)?;
                                if !digits.iter().all(u8::is_ascii_hexdigit) {
                                    return None;
                                }
                                self.at += 6;
                            }
                            _ => return None,
                        }
                    }
                    0x00..=0x1F => return None,
                    _ => self.at += 1,
                }
            }
        }
        fn number(&mut self) -> Option<()> {
            let digits = |reader: &mut Reader| {
                let start = reader.at;
                while reader.bytes.get(reader.at).is_some_and(u8::is_ascii_digit) {
                    reader.at += 1;
                }
                reader.at > start
            };
            if self.bytes.get(self.at) == Some(&b'-') {
                self.at += 1;
            }
            match self.bytes.get(self.at) {
                Some(b'0') => self.at += 1,
                Some(b'1'..=b'9') => {
                    digits(self);
                }
                _ => return None,
            }
            if self.bytes.get(self.at) == Some(&b'.') {
                self.at += 1;
                if !digits(self) {
                    return None;
                }
            }
            if matches!(self.bytes.get(self.at), Some(b'e' | b'E')) {
                self.at += 1;
                if matches!(self.bytes.get(self.at), Some(b'+' | b'-')) {
                    self.at += 1;
                }
                if !digits(self) {
                    return None;
                }
            }
            Some(())
        }
        fn literal(&mut self, word: &[u8]) -> Option<()> {
            if self.bytes.get(self.at..self.at + word.len())? != word {
                return None;
            }
            self.at += word.len();
            Some(())
        }
        fn value(&mut self, depth: usize) -> Option<Node> {
            self.skip();
            match *self.bytes.get(self.at)? {
                b'{' => {
                    if depth >= MAXIMUM_DEPTH {
                        return None;
                    }
                    self.at += 1;
                    let mut members = vec![];
                    self.skip();
                    if self.bytes.get(self.at) == Some(&b'}') {
                        self.at += 1;
                        return Some(Node::Object(members));
                    }
                    loop {
                        self.skip();
                        let key = self.string()?;
                        self.skip();
                        if self.bytes.get(self.at) != Some(&b':') {
                            return None;
                        }
                        self.at += 1;
                        let value = self.value(depth + 1)?;
                        members.push((key, value));
                        self.skip();
                        match *self.bytes.get(self.at)? {
                            b',' => self.at += 1,
                            b'}' => {
                                self.at += 1;
                                return Some(Node::Object(members));
                            }
                            _ => return None,
                        }
                    }
                }
                b'[' => {
                    if depth >= MAXIMUM_DEPTH {
                        return None;
                    }
                    self.at += 1;
                    let mut elements = vec![];
                    self.skip();
                    if self.bytes.get(self.at) == Some(&b']') {
                        self.at += 1;
                        return Some(Node::Array(elements));
                    }
                    loop {
                        elements.push(self.value(depth + 1)?);
                        self.skip();
                        match *self.bytes.get(self.at)? {
                            b',' => self.at += 1,
                            b']' => {
                                self.at += 1;
                                return Some(Node::Array(elements));
                            }
                            _ => return None,
                        }
                    }
                }
                b'"' => self.string().map(|(start, end)| Node::Text(start, end)),
                b't' => self.literal(b"true").map(|_| Node::Other),
                b'f' => self.literal(b"false").map(|_| Node::Other),
                b'n' => self.literal(b"null").map(|_| Node::Other),
                _ => self.number().map(|_| Node::Other),
            }
        }
    }
    let mut reader = Reader { bytes, at: 0 };
    let value = reader.value(0)?;
    reader.skip();
    (reader.at == bytes.len()).then_some(value)
}

/// `JsonElement.GetString()` of the string token `bytes[start..end]` (quotes included): its escapes read, its UTF-8 decoded; None where
/// C#'s `GetString` throws (bytes that are not UTF-8, a lone surrogate escape).
fn decode(bytes: &[u8], start: usize, end: usize) -> Option<String> {
    let inner = bytes.get(start + 1..end.checked_sub(1)?)?;
    let mut units: Vec<u16> = Vec::with_capacity(inner.len());
    let mut at = 0;
    while at < inner.len() {
        if inner[at] == b'\\' {
            let escape = *inner.get(at + 1)?;
            let unit = match escape {
                b'"' => 0x22,
                b'\\' => 0x5C,
                b'/' => 0x2F,
                b'b' => 0x08,
                b'f' => 0x0C,
                b'n' => 0x0A,
                b'r' => 0x0D,
                b't' => 0x09,
                b'u' => {
                    let hex = std::str::from_utf8(inner.get(at + 2..at + 6)?).ok()?;
                    at += 4;
                    u16::from_str_radix(hex, 16).ok()?
                }
                _ => return None,
            };
            units.push(unit);
            at += 2;
            continue;
        }
        let run_end = inner[at..].iter().position(|b| *b == b'\\').map_or(inner.len(), |p| at + p);
        let run = std::str::from_utf8(&inner[at..run_end]).ok()?;
        units.extend(run.encode_utf16());
        at = run_end;
    }
    String::from_utf16(&units).ok()
}

/// `Last(value, key)`: the last member named `key`, every name read on the way (as `EnumerateObject` reads `Name`).
fn last<'a>(members: &'a [((usize, usize), Node)], data: &[u8], key: &str, path: &str) -> Result<Option<&'a Node>> {
    let mut found = None;
    for ((start, end), value) in members {
        let name = decode(data, *start, *end).ok_or_else(|| SettingsError::of("notJSON", Some(path), None))?;
        if name == key {
            found = Some(value);
        }
    }
    Ok(found)
}

/// `Rules`: the file's rules; refused when it is not an object, `permissions` is not an object or `allow` is not an array (entries that
/// are not strings are skipped). A UTF-8 byte order mark is not JSON here (agy writes none), as on the Mac.
pub fn rules(data: &[u8], path: &str) -> Result<Vec<String>> {
    let not_json = || SettingsError::of("notJSON", Some(path), None);
    if data.starts_with(&[0xEF, 0xBB, 0xBF]) {
        return Err(not_json());
    }
    let Some(top) = document(data) else { return Err(not_json()) };
    let Node::Object(members) = &top else { return Err(SettingsError::of("notObject", Some(path), None)) };
    let Some(permissions) = last(members, data, "permissions", path)? else { return Ok(vec![]) };
    let Node::Object(section) = permissions else { return Err(SettingsError::of("permissionsNotObject", Some(path), None)) };
    let Some(allow) = last(section, data, "allow", path)? else { return Ok(vec![]) };
    let Node::Array(elements) = allow else { return Err(SettingsError::of("allowNotArray", Some(path), None)) };
    let mut rules = vec![];
    for element in elements {
        if let Node::Text(start, end) = element {
            rules.push(decode(data, *start, *end).ok_or_else(not_json)?);
        }
    }
    Ok(rules)
}

// ── editing ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `NewFile`: a new settings file that holds only these rules.
pub fn new_file(rules: &[String]) -> Vec<u8> {
    format!("{{\n  \"permissions\": {{\n    \"allow\": {}\n  }}\n}}\n", array(rules, Some("    "), "  ", "\n")).into_bytes()
}

fn contains(data: &[u8], needle: &[u8]) -> bool {
    data.windows(needle.len()).any(|w| w == needle)
}

/// `Adding`: the rules not in the file yet, added. Returns the bytes, what was added and what had to be created for it.
pub fn adding(rules: &[String], data: &[u8], path: &str) -> Result<(Vec<u8>, Vec<String>, Vec<String>)> {
    let existing: HashSet<String> = self::rules(data, path)?.into_iter().collect();
    let mut seen = HashSet::new();
    let missing: Vec<String> = rules.iter().filter(|rule| !existing.contains(*rule) && seen.insert((*rule).clone())).cloned().collect();
    if missing.is_empty() {
        return Ok((data.to_vec(), vec![], vec![]));
    }
    let mut b = data.to_vec();
    let newline = if contains(data, b"\r\n") { "\r\n" } else { "\n" };
    let scan = Scanner { bytes: data, path };
    let top = scan.skip_whitespace(0);
    if top >= data.len() || data[top] != b'{' {
        return Err(SettingsError::of("notObject", Some(path), None));
    }
    let top_object = scan.object(top)?;
    let unit = scan
        .line_indent(top_object.members.first().map_or(-1, |m| m.key_start as isize))
        .filter(|found| !found.is_empty())
        .unwrap_or_else(|| "  ".into());
    let Some(permissions) = top_object.members.iter().rev().find(|m| m.key == "permissions") else {
        insert_member(&mut b, &scan, &top_object, Some(""), &unit, newline, |indent| {
            format!("\"permissions\": {}", allow_object(&missing, indent, &unit, newline))
        });
        return Ok((b, missing, vec![CREATED_PERMISSIONS.into()]));
    };
    let permissions_indent = scan.line_indent(permissions.key_start as isize);
    let section = scan.object(permissions.value_start)?;
    let Some(allow) = section.members.iter().rev().find(|m| m.key == "allow") else {
        insert_member(&mut b, &scan, &section, permissions_indent.as_deref(), &unit, newline, |indent| {
            format!("\"allow\": {}", array(&missing, indent, &unit, newline))
        });
        return Ok((b, missing, vec![CREATED_ALLOW.into()]));
    };
    let list = scan.array(allow.value_start)?;
    if let Some(last) = list.elements.last() {
        let after = if list.elements.len() > 1 { list.elements[list.elements.len() - 2].end } else { list.open };
        let from = scan.separator_end(last.start, after);
        let separator = &data[from..last.start];
        let mut inserted = vec![];
        for rule in &missing {
            inserted.push(b',');
            inserted.extend_from_slice(separator);
            inserted.extend(encoded(rule));
        }
        b.splice(last.end..last.end, inserted);
    } else {
        let rendered = array(&missing, scan.line_indent(allow.key_start as isize).as_deref(), &unit, newline);
        b.splice(list.open..list.close + 1, rendered.into_bytes());
    }
    Ok((b, missing, vec![]))
}

/// `Removing`: one occurrence of each of `rules` taken out of `permissions.allow` (the last one). Returns the bytes and what was removed.
pub fn removing(rules: &[String], data: &[u8], path: &str) -> Result<(Vec<u8>, Vec<String>)> {
    self::rules(data, path)?;
    let mut b = data.to_vec();
    let mut removed = vec![];
    let mut distinct: Vec<&String> = vec![];
    for rule in rules {
        if !distinct.contains(&rule) {
            distinct.push(rule);
        }
    }
    distinct.sort_by(|a, b| normalize::ordinal(a, b));
    for rule in distinct {
        let scan = Scanner { bytes: &b, path };
        let top = scan.object(scan.skip_whitespace(0))?;
        let Some(permissions) = top.members.iter().rev().find(|m| m.key == "permissions") else { break };
        let section = scan.object(permissions.value_start)?;
        let Some(allow) = section.members.iter().rev().find(|m| m.key == "allow") else { break };
        let list = scan.array(allow.value_start)?;
        let elements = &list.elements;
        let Some(index) = (0..elements.len()).rev().find(|at| scan.string(elements[*at]).as_deref() == Some(rule.as_str())) else {
            continue;
        };
        let range = if elements.len() == 1 {
            list.open + 1..list.close
        } else if index == elements.len() - 1 {
            elements[index - 1].end..elements[index].end
        } else {
            elements[index].start..elements[index + 1].start
        };
        let mut next = b.clone();
        next.drain(range);
        b = next;
        removed.push(rule.clone());
    }
    // Whatever is left must still read as the same kind of file.
    self::rules(&b, path)?;
    Ok((b, removed))
}

/// `Pruning`: takes out what adding created once it is empty — `permissions` when it holds nothing but an empty allow (permissions,
/// file), else an empty allow (allow). The flag: the file is now `{}`, which a created file is deleted for.
pub fn pruning(created: &HashSet<String>, data: &[u8], path: &str) -> Result<(Vec<u8>, bool)> {
    self::rules(data, path)?;
    let mut b = data.to_vec();
    let scan = Scanner { bytes: data, path };
    let top = scan.object(scan.skip_whitespace(0))?;
    let permission_members: Vec<usize> = (0..top.members.len()).filter(|i| top.members[*i].key == "permissions").collect();
    if permission_members.len() == 1 {
        let index = permission_members[0];
        let section = scan.object(top.members[index].value_start)?;
        let allows: Vec<usize> = (0..section.members.len()).filter(|i| section.members[*i].key == "allow").collect();
        let empty_allow = allows.len() == 1 && scan.array(section.members[allows[0]].value_start)?.elements.is_empty();
        if created.contains(CREATED_PERMISSIONS) || created.contains(CREATED_FILE) {
            if section.members.is_empty() || section.members.len() == 1 && empty_allow {
                remove_member(&mut b, &top, index);
            }
        } else if created.contains(CREATED_ALLOW) && empty_allow {
            remove_member(&mut b, &section, allows[0]);
        }
    }
    let after = Scanner { bytes: &b, path };
    let result = after.object(after.skip_whitespace(0))?;
    self::rules(&b, path)?;
    let empty = result.members.is_empty();
    Ok((b, empty))
}

/// `RemoveMember`: takes a member out with the separator that adding put before it; the only member leaves `{}`.
fn remove_member(b: &mut Vec<u8>, value: &ObjectSpan, index: usize) {
    let members = &value.members;
    if members.len() == 1 {
        b.splice(value.open..value.close + 1, b"{}".iter().copied());
    } else if index == members.len() - 1 {
        b.drain(members[index - 1].value_end..members[index].value_end);
    } else {
        b.drain(members[index].key_start..members[index + 1].key_start);
    }
}

// ── rendering ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `Encoded`: a rule as a JSON string, as the Mac writes it — non-ASCII and slashes as they are.
pub fn encoded(rule: &str) -> Vec<u8> {
    let mut text = String::from("\"");
    for c in rule.chars() {
        match c {
            '"' => text.push_str("\\\""),
            '\\' => text.push_str("\\\\"),
            '\u{8}' => text.push_str("\\b"),
            '\u{c}' => text.push_str("\\f"),
            '\n' => text.push_str("\\n"),
            '\r' => text.push_str("\\r"),
            '\t' => text.push_str("\\t"),
            c if (c as u32) < 0x20 => text.push_str(&format!("\\u{:04x}", c as u32)),
            c => text.push(c),
        }
    }
    text.push('"');
    text.into_bytes()
}

/// `Array`: a list on its own lines under `indent`, or on one line when the place it goes is one line (None).
pub fn array(rules: &[String], indent: Option<&str>, unit: &str, newline: &str) -> String {
    let items: Vec<String> = rules.iter().map(|rule| String::from_utf8(encoded(rule)).unwrap_or_default()).collect();
    match indent {
        None => format!("[{}]", items.join(", ")),
        Some(indent) => {
            let lines: Vec<String> = items.iter().map(|item| format!("{indent}{unit}{item}")).collect();
            format!("[{newline}{}{newline}{indent}]", lines.join(&format!(",{newline}")))
        }
    }
}

/// `AllowObject`.
pub fn allow_object(rules: &[String], indent: Option<&str>, unit: &str, newline: &str) -> String {
    match indent {
        None => format!("{{\"allow\": {}}}", array(rules, None, unit, "\n")),
        Some(indent) => format!(
            "{{{newline}{indent}{unit}\"allow\": {}{newline}{indent}}}",
            array(rules, Some(&format!("{indent}{unit}")), unit, newline)
        ),
    }
}

/// `InsertMember`: adds a member to `value` — after its last member, in that member's style, or as the only member of an empty object.
/// `render` gets the member's indent (None when it goes on one line).
fn insert_member(
    b: &mut Vec<u8>,
    scan: &Scanner,
    value: &ObjectSpan,
    indent_of_object: Option<&str>,
    unit: &str,
    newline: &str,
    render: impl Fn(Option<&str>) -> String,
) {
    if let Some(last) = value.members.last() {
        let previous_end = if value.members.len() > 1 { value.members[value.members.len() - 2].value_end } else { value.open };
        let separator = &scan.bytes[scan.separator_end(last.key_start, previous_end)..last.key_start];
        let mut inserted = vec![b','];
        inserted.extend_from_slice(separator);
        inserted.extend(render(scan.line_indent(last.key_start as isize).as_deref()).into_bytes());
        b.splice(last.value_end..last.value_end, inserted);
        return;
    }
    let text = match indent_of_object {
        Some(outer) => {
            format!("{{{newline}{outer}{unit}{}{newline}{outer}}}", render(Some(&format!("{outer}{unit}"))))
        }
        None => format!("{{{}}}", render(None)),
    };
    b.splice(value.open..value.close + 1, text.into_bytes());
}

// ── the scanner ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

struct Member {
    key: String,
    key_start: usize,
    value_start: usize,
    value_end: usize,
}

struct ObjectSpan {
    open: usize,
    close: usize,
    members: Vec<Member>,
}

#[derive(Clone, Copy)]
struct Element {
    start: usize,
    end: usize,
}

struct ListSpan {
    open: usize,
    close: usize,
    elements: Vec<Element>,
}

/// C# `AntigravitySettingsText.Scanner`: just enough of a JSON reader to find where things are. The bytes were already read as a document;
/// anything unexpected is notJSON.
struct Scanner<'a> {
    bytes: &'a [u8],
    path: &'a str,
}

impl Scanner<'_> {
    fn fail(&self) -> SettingsError {
        SettingsError::of("notJSON", Some(self.path), None)
    }

    fn skip_whitespace(&self, start: usize) -> usize {
        let mut i = start;
        while i < self.bytes.len() && space(self.bytes[i]) {
            i += 1;
        }
        i
    }

    /// `StringEnd`: the index after the closing quote of the string at `start`.
    fn string_end(&self, start: usize) -> Result<usize> {
        if start >= self.bytes.len() || self.bytes[start] != b'"' {
            return Err(self.fail());
        }
        let mut i = start + 1;
        while i < self.bytes.len() {
            if self.bytes[i] == b'\\' {
                i += 2;
                continue;
            }
            if self.bytes[i] == b'"' {
                return Ok(i + 1);
            }
            i += 1;
        }
        Err(self.fail())
    }

    /// `String(Element)`: an element's text when it is a string.
    fn string(&self, range: Element) -> Option<String> {
        if range.start >= self.bytes.len() || self.bytes[range.start] != b'"' {
            return None;
        }
        decode(self.bytes, range.start, range.end)
    }

    fn value_end(&self, start: usize) -> Result<usize> {
        if start >= self.bytes.len() {
            return Err(self.fail());
        }
        match self.bytes[start] {
            b'"' => self.string_end(start),
            b'{' => Ok(self.object(start)?.close + 1),
            b'[' => Ok(self.array(start)?.close + 1),
            _ => {
                let mut i = start;
                while i < self.bytes.len() && !matches!(self.bytes[i], b',' | b']' | b'}') && !space(self.bytes[i]) {
                    i += 1;
                }
                if i <= start {
                    return Err(self.fail());
                }
                Ok(i)
            }
        }
    }

    fn object(&self, open: usize) -> Result<ObjectSpan> {
        if open >= self.bytes.len() || self.bytes[open] != b'{' {
            return Err(self.fail());
        }
        let mut members = vec![];
        let mut i = self.skip_whitespace(open + 1);
        if i < self.bytes.len() && self.bytes[i] == b'}' {
            return Ok(ObjectSpan { open, close: i, members });
        }
        loop {
            let key_start = i;
            let key_end = self.string_end(key_start)?;
            let key = decode(self.bytes, key_start, key_end).ok_or_else(|| self.fail())?;
            i = self.skip_whitespace(key_end);
            if i >= self.bytes.len() || self.bytes[i] != b':' {
                return Err(self.fail());
            }
            let value_start = self.skip_whitespace(i + 1);
            let end = self.value_end(value_start)?;
            members.push(Member { key, key_start, value_start, value_end: end });
            i = self.skip_whitespace(end);
            if i >= self.bytes.len() {
                return Err(self.fail());
            }
            if self.bytes[i] == b'}' {
                return Ok(ObjectSpan { open, close: i, members });
            }
            if self.bytes[i] != b',' {
                return Err(self.fail());
            }
            i = self.skip_whitespace(i + 1);
        }
    }

    fn array(&self, open: usize) -> Result<ListSpan> {
        if open >= self.bytes.len() || self.bytes[open] != b'[' {
            return Err(SettingsError::of("allowNotArray", Some(self.path), None));
        }
        let mut elements = vec![];
        let mut i = self.skip_whitespace(open + 1);
        if i < self.bytes.len() && self.bytes[i] == b']' {
            return Ok(ListSpan { open, close: i, elements });
        }
        loop {
            let end = self.value_end(i)?;
            elements.push(Element { start: i, end });
            i = self.skip_whitespace(end);
            if i >= self.bytes.len() {
                return Err(self.fail());
            }
            if self.bytes[i] == b']' {
                return Ok(ListSpan { open, close: i, elements });
            }
            if self.bytes[i] != b',' {
                return Err(self.fail());
            }
            i = self.skip_whitespace(i + 1);
        }
    }

    /// `SeparatorEnd`: where the whitespace before `position` starts, after the comma (or bracket) that follows `after`.
    fn separator_end(&self, position: usize, after: usize) -> usize {
        let mut i = position;
        while i > after + 1 && space(self.bytes[i - 1]) {
            i -= 1;
        }
        i
    }

    /// `LineIndent`: the spaces and tabs before `position` when nothing else is on its line before it.
    fn line_indent(&self, position: isize) -> Option<String> {
        if position < 0 || position as usize > self.bytes.len() {
            return None;
        }
        let position = position as usize;
        let mut i = position;
        while i > 0 && matches!(self.bytes[i - 1], 0x20 | 0x09) {
            i -= 1;
        }
        if i != 0 && self.bytes[i - 1] != 0x0A {
            return None;
        }
        Some(String::from_utf8_lossy(&self.bytes[i..position]).into_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_document_reads_as_json_document_reads() {
        for good in [&b"{}"[..], b" {\"a\": [1, -2.5e3, true, false, null, \"x\\u00e9\"]}\r\n", b"[]", b"\"s\"", b"0"] {
            assert!(document(good).is_some(), "{}", String::from_utf8_lossy(good));
        }
        for bad in [
            &b""[..],
            b"{,}",
            b"{\"a\":1,}",
            b"[1,]",
            b"// note\n{}",
            b"{/* c */}",
            b"01",
            b"1.",
            b"{\"a\" 1}",
            b"{} {}",
            b"\"\x01\"",
            b"\"\\x\"",
            b"\"\\u12\"",
            b"tru",
            b"+1",
        ] {
            assert!(document(bad).is_none(), "{}", String::from_utf8_lossy(bad));
        }
        let deep = |n: usize| format!("{}{}", "[".repeat(n), "]".repeat(n));
        assert!(document(deep(64).as_bytes()).is_some(), "64 levels");
        assert!(document(deep(65).as_bytes()).is_none(), "65 levels");
    }

    #[test]
    fn a_string_decodes_as_get_string_decodes() {
        let text = br#""a\"b\\c\/\n\u00e9\ud83d\ude00""#;
        assert_eq!(decode(text, 0, text.len()).as_deref(), Some("a\"b\\c/\né😀"));
        let lone = br#""\ud800""#;
        assert_eq!(decode(lone, 0, lone.len()), None, "a lone surrogate");
        let broken = b"\"\xff\"";
        assert_eq!(decode(broken, 0, broken.len()), None, "not UTF-8");
    }
}
