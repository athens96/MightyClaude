//! JSON exactly as C# `Wire.Json` (System.Text.Json, `JsonSerializerDefaults.Web`, nulls left out, compact) writes and reads it.
//!
//! Writing ([`JsonOut`]): property order is the caller's (each record writes its C# declaration order); strings are escaped as
//! `Utf8JsonWriter` escapes them with no encoder set, which is `JavaScriptEncoder.Default`: printable ASCII stays except
//! `" & ' + < > \` and the backtick, `\n \r \t \b \f \\` keep their short forms, everything else (controls, DEL, every non-ASCII
//! UTF-16 unit) is `\uXXXX` in upper-case hex; doubles are .NET's shortest round-trip text ([`format_double`]).
//!
//! Reading ([`Obj`] and the field readers): a property name matches without regard to case (the last match wins); a field that is
//! absent takes the C# initializer's value, a JSON null makes a reference-typed field null; numbers may come as strings
//! (`JsonNumberHandling.AllowReadingFromString`), but a bool or a string never comes from another type; any other mismatch fails the
//! whole read, as a `JsonException` does in C#.

use std::fmt::Write as _;

use indexmap::IndexMap;
use serde_json::{Map, Value};

/// Why a value does not read as the C# record (C# throws a `JsonException`, or a `NullReferenceException` where noted).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReadError(pub String);

impl std::fmt::Display for ReadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ReadError {}

const CRASH: &str = "crash: ";

impl ReadError {
    /// What C# meets with an exception other than a `JsonException` (a null it dereferences): it fails the load all the same, but
    /// no caller that catches `JsonException` catches it.
    pub fn crash(why: impl Into<String>) -> Self {
        ReadError(format!("{CRASH}{}", why.into()))
    }

    pub fn is_crash(&self) -> bool {
        self.0.starts_with(CRASH)
    }
}

pub type R<T> = Result<T, ReadError>;

pub fn fail<T>(why: impl Into<String>) -> R<T> {
    Err(ReadError(why.into()))
}

// ── writing ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// A compact JSON writer with `Utf8JsonWriter`'s output.
#[derive(Debug, Default)]
pub struct JsonOut {
    buf: String,
    /// The next value or name in the open container needs a comma first.
    comma: bool,
}

impl JsonOut {
    pub fn new() -> Self {
        JsonOut::default()
    }

    pub fn finish(self) -> String {
        self.buf
    }

    pub fn as_str(&self) -> &str {
        &self.buf
    }

    fn separate(&mut self) {
        if self.comma {
            self.buf.push(',');
        }
    }

    pub fn begin_object(&mut self) {
        self.separate();
        self.buf.push('{');
        self.comma = false;
    }

    pub fn end_object(&mut self) {
        self.buf.push('}');
        self.comma = true;
    }

    pub fn begin_array(&mut self) {
        self.separate();
        self.buf.push('[');
        self.comma = false;
    }

    pub fn end_array(&mut self) {
        self.buf.push(']');
        self.comma = true;
    }

    pub fn name(&mut self, name: &str) {
        self.separate();
        escape_into(&mut self.buf, name);
        self.buf.push(':');
        self.comma = false;
    }

    pub fn str(&mut self, value: &str) {
        self.separate();
        escape_into(&mut self.buf, value);
        self.comma = true;
    }

    pub fn int(&mut self, value: i64) {
        self.separate();
        let _ = write!(self.buf, "{value}");
        self.comma = true;
    }

    /// A double as `Utf8JsonWriter.WriteNumberValue(double)` writes it (C# throws on a value that is not finite; callers keep them
    /// out, and one that slips through is written as null rather than as invalid JSON).
    pub fn float(&mut self, value: f64) {
        self.separate();
        if value.is_finite() {
            self.buf.push_str(&format_double(value));
        } else {
            self.buf.push_str("null");
        }
        self.comma = true;
    }

    pub fn bool(&mut self, value: bool) {
        self.separate();
        self.buf.push_str(if value { "true" } else { "false" });
        self.comma = true;
    }

    pub fn null(&mut self) {
        self.separate();
        self.buf.push_str("null");
        self.comma = true;
    }

    /// JSON text written as it is (`WriteRawValue`).
    pub fn raw(&mut self, json: &str) {
        self.separate();
        self.buf.push_str(json);
        self.comma = true;
    }

    /// Any JSON value, as `JsonNode.WriteTo` writes one: strings escaped again, numbers in .NET's form.
    pub fn value(&mut self, value: &Value) {
        match value {
            Value::Null => self.null(),
            Value::Bool(b) => self.bool(*b),
            Value::Number(n) => {
                if let Some(i) = n.as_i64() {
                    self.int(i);
                } else if let Some(u) = n.as_u64() {
                    self.separate();
                    let _ = write!(self.buf, "{u}");
                    self.comma = true;
                } else {
                    self.float(n.as_f64().unwrap_or(f64::NAN));
                }
            }
            Value::String(s) => self.str(s),
            Value::Array(items) => {
                self.begin_array();
                for item in items {
                    self.value(item);
                }
                self.end_array();
            }
            Value::Object(map) => {
                self.begin_object();
                for (key, item) in map {
                    self.name(key);
                    self.value(item);
                }
                self.end_object();
            }
        }
    }

    // Properties as the C# records write them: a null (None) is left out (`JsonIgnoreCondition.WhenWritingNull`).

    pub fn prop_str(&mut self, name: &str, value: Option<&str>) {
        if let Some(value) = value {
            self.name(name);
            self.str(value);
        }
    }

    pub fn prop_bool(&mut self, name: &str, value: bool) {
        self.name(name);
        self.bool(value);
    }

    pub fn prop_opt_bool(&mut self, name: &str, value: Option<bool>) {
        if let Some(value) = value {
            self.prop_bool(name, value);
        }
    }

    pub fn prop_int(&mut self, name: &str, value: i64) {
        self.name(name);
        self.int(value);
    }

    pub fn prop_opt_int(&mut self, name: &str, value: Option<i64>) {
        if let Some(value) = value {
            self.prop_int(name, value);
        }
    }

    pub fn prop_float(&mut self, name: &str, value: f64) {
        self.name(name);
        self.float(value);
    }

    pub fn prop_opt_float(&mut self, name: &str, value: Option<f64>) {
        if let Some(value) = value {
            self.prop_float(name, value);
        }
    }

    pub fn prop<T: Wire>(&mut self, name: &str, value: Option<&T>) {
        if let Some(value) = value {
            self.name(name);
            value.write(self);
        }
    }

    pub fn prop_list<T: Wire>(&mut self, name: &str, value: Option<&Vec<T>>) {
        if let Some(list) = value {
            self.name(name);
            self.list(list);
        }
    }

    pub fn list<T: Wire>(&mut self, list: &[T]) {
        self.begin_array();
        for item in list {
            item.write(self);
        }
        self.end_array();
    }

    pub fn prop_strings(&mut self, name: &str, value: Option<&Vec<String>>) {
        if let Some(list) = value {
            self.name(name);
            self.strings(list);
        }
    }

    pub fn strings(&mut self, list: &[String]) {
        self.begin_array();
        for item in list {
            self.str(item);
        }
        self.end_array();
    }

    /// A `Dictionary<string, string>` (a null value written as null: the ignore condition does not reach dictionary values).
    pub fn prop_string_map(&mut self, name: &str, value: Option<&IndexMap<String, Option<String>>>) {
        if let Some(map) = value {
            self.name(name);
            self.begin_object();
            for (key, item) in map {
                self.name(key);
                match item {
                    Some(text) => self.str(text),
                    None => self.null(),
                }
            }
            self.end_object();
        }
    }

    pub fn prop_map<T: Wire>(&mut self, name: &str, value: Option<&IndexMap<String, T>>) {
        if let Some(map) = value {
            self.name(name);
            self.begin_object();
            for (key, item) in map {
                self.name(key);
                item.write(self);
            }
            self.end_object();
        }
    }
}

/// A record written and read as C# `Wire.Json` does (each implementation is the C# type's, in its property order).
pub trait Wire: Sized {
    /// A value that is not null (STJ hands a null to the property itself, never to the type's reader).
    fn read(value: &Value) -> R<Self>;
    fn write(&self, out: &mut JsonOut);

    fn to_json(&self) -> String {
        let mut out = JsonOut::new();
        self.write(&mut out);
        out.finish()
    }
}

/// Whether `Utf8JsonWriter` (no encoder: `JavaScriptEncoder.Default`) keeps an ASCII character as it is.
fn allowed(c: u8) -> bool {
    (0x20..0x7f).contains(&c) && !matches!(c, b'"' | b'&' | b'\'' | b'+' | b'<' | b'>' | b'\\' | b'`')
}

/// A string as JSON text (quotes included), escaped as `Utf8JsonWriter` escapes it.
pub fn escape_into(out: &mut String, value: &str) {
    out.push('"');
    for c in value.chars() {
        let code = c as u32;
        if code < 0x80 && allowed(code as u8) {
            out.push(c);
            continue;
        }
        match c {
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\\' => out.push_str("\\\\"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            _ => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    let _ = write!(out, "\\u{:04X}", unit);
                }
            }
        }
    }
    out.push('"');
}

pub fn escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    escape_into(&mut out, value);
    out
}

/// How many UTF-8 bytes `JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json)` gives for a string (the quotes and escapes
/// included): the measure of `StateStore.Normalize`'s text budget.
pub fn escaped_len(value: &str) -> usize {
    let mut len = 2;
    for c in value.chars() {
        let code = c as u32;
        len += if code < 0x80 && allowed(code as u8) {
            1
        } else if matches!(c, '\n' | '\r' | '\t' | '\\' | '\u{8}' | '\u{c}') {
            2
        } else {
            6 * c.len_utf16()
        };
    }
    len
}

/// The shortest digits that read back as `value` (positive, finite, not zero) and its decimal exponent, as .NET picks them: when
/// two strings of that length are equally near the double (its exact value lies halfway), the one ending in an even digit
/// (Dragon4's rule; Rust's own formatter takes the upper one).
fn shortest_digits(value: f64) -> (String, i32) {
    let text = format!("{:e}", value);
    let (mantissa, exponent) = text.split_once('e').unwrap_or((&text, "0"));
    let digits: String = mantissa.chars().filter(char::is_ascii_digit).collect();
    let exponent: i32 = exponent.parse().unwrap_or(0);
    // Two candidates of the same length can only both read back when there are at least 16 of them.
    if digits.len() < 16 {
        return (digits, exponent);
    }
    let n = digits.len();
    // The exact value's digits (a double's expansion ends within 767 digits), truncated to n, and what follows.
    let exact = format!("{:.1100e}", value);
    let (exact_mantissa, exact_exponent) = exact.split_once('e').unwrap_or((&exact, "0"));
    if exact_exponent.parse::<i32>().ok() != Some(exponent) {
        return (digits, exponent);
    }
    let all: String = exact_mantissa.chars().filter(char::is_ascii_digit).collect();
    let (head, rest) = all.split_at(n.min(all.len()));
    if !(rest.starts_with('5') && rest[1..].bytes().all(|b| b == b'0')) {
        return (digits, exponent);
    }
    // Halfway between `head` and `head + 1` in the last place.
    let lower = head.to_string();
    let mut upper: Vec<u8> = head.bytes().collect();
    let mut at = upper.len();
    loop {
        if at == 0 {
            return (digits, exponent);
        }
        at -= 1;
        if upper[at] == b'9' {
            upper[at] = b'0';
        } else {
            upper[at] += 1;
            break;
        }
    }
    let upper = String::from_utf8(upper).unwrap_or_default();
    let reads_back = |d: &str| format!("{}.{}e{}", &d[..1], &d[1..], exponent).parse::<f64>().ok() == Some(value);
    let even = |d: &str| d.as_bytes()[d.len() - 1].is_multiple_of(2);
    match (reads_back(&lower), reads_back(&upper)) {
        (true, true) => (if even(&lower) { lower } else { upper }, exponent),
        _ => (digits, exponent),
    }
}

/// A double as .NET Core 3.0+ formats it with no format (`double.ToString()`, `Utf8Formatter`): the shortest digits that read back
/// as the same double ([`shortest_digits`]), fixed-point unless the decimal exponent is 17 or more or below -4, then `d.dddE+XX`.
pub fn format_double(value: f64) -> String {
    if value.is_nan() {
        return "NaN".into();
    }
    if value.is_infinite() {
        return if value > 0.0 { "∞".into() } else { "-∞".into() };
    }
    let negative = value.is_sign_negative();
    if value == 0.0 {
        return if negative { "-0".into() } else { "0".into() };
    }
    let (digits, exponent) = shortest_digits(value.abs());
    let scale = exponent + 1;
    let mut out = String::new();
    if negative {
        out.push('-');
    }
    if !(-3..=17).contains(&scale) {
        out.push_str(&digits[..1]);
        if digits.len() > 1 {
            out.push('.');
            out.push_str(&digits[1..]);
        }
        let shown = scale - 1;
        let _ = write!(out, "E{}{:02}", if shown < 0 { '-' } else { '+' }, shown.unsigned_abs());
    } else if scale > 0 {
        let whole = scale as usize;
        if digits.len() <= whole {
            out.push_str(&digits);
            out.extend(std::iter::repeat_n('0', whole - digits.len()));
        } else {
            out.push_str(&digits[..whole]);
            out.push('.');
            out.push_str(&digits[whole..]);
        }
    } else {
        out.push_str("0.");
        out.extend(std::iter::repeat_n('0', (-scale) as usize));
        out.push_str(&digits);
    }
    out
}

// ── reading ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// A JSON object read as STJ reads a record's properties.
#[derive(Clone, Copy)]
pub struct Obj<'a> {
    map: &'a Map<String, Value>,
}

/// .NET `OrdinalIgnoreCase`: each UTF-16 unit upper-cased by the invariant culture's simple mapping.
pub fn same_ignoring_case(a: &str, b: &str) -> bool {
    if a.len() == b.len() && a.eq_ignore_ascii_case(b) {
        return true;
    }
    let upper = |c: char| {
        let mut it = c.to_uppercase();
        match (it.next(), it.next()) {
            (Some(u), None) => u,
            _ => c,
        }
    };
    let mut x = a.chars().map(upper);
    let mut y = b.chars().map(upper);
    loop {
        match (x.next(), y.next()) {
            (None, None) => return true,
            (Some(p), Some(q)) if p == q => {}
            _ => return false,
        }
    }
}

impl<'a> Obj<'a> {
    /// An object, or the error C# gives for any other token where a record is expected.
    pub fn of(value: &'a Value, what: &str) -> R<Obj<'a>> {
        match value {
            Value::Object(map) => Ok(Obj { map }),
            _ => fail(format!("{what} is not an object")),
        }
    }

    pub fn new(map: &'a Map<String, Value>) -> Obj<'a> {
        Obj { map }
    }

    /// A record property: the last property whose name matches without regard to case.
    pub fn get(&self, name: &str) -> Option<&'a Value> {
        let mut found = None;
        for (key, value) in self.map {
            if same_ignoring_case(key, name) {
                found = Some(value);
            }
        }
        found
    }

    /// `JsonElement.TryGetProperty` (a converter's own read): the exact name.
    pub fn exact(&self, name: &str) -> Option<&'a Value> {
        self.map.get(name)
    }

    /// `JsonElementExtensions.Text`: the exact property when it is a string.
    pub fn text(&self, name: &str) -> Option<&'a str> {
        self.exact(name).and_then(Value::as_str)
    }

    pub fn entries(&self) -> impl Iterator<Item = (&'a String, &'a Value)> {
        self.map.iter()
    }
}

/// `JsonElementExtensions.Text` on any value: the exact property of an object when it is a string.
pub fn text<'a>(value: &'a Value, name: &str) -> Option<&'a str> {
    value.as_object().and_then(|o| o.get(name)).and_then(Value::as_str)
}

/// `MetadataJson.Number`: a finite number property.
pub fn number(value: &Value, name: &str) -> Option<f64> {
    value.as_object().and_then(|o| o.get(name)).filter(|v| v.is_number()).and_then(Value::as_f64).filter(|n| n.is_finite())
}

/// `MetadataJson.Integer`: a number property that reads as a 64-bit integer.
pub fn integer(value: &Value, name: &str) -> Option<i64> {
    value.as_object().and_then(|o| o.get(name)).and_then(as_integer)
}

/// `MetadataJson.Flag`: the property is `true`.
pub fn flag(value: &Value, name: &str) -> bool {
    value.as_object().and_then(|o| o.get(name)) == Some(&Value::Bool(true))
}

/// A `string` property: absent takes `default`, null is null, anything but a string fails.
pub fn string(value: Option<&Value>, default: impl FnOnce() -> Option<String>) -> R<Option<String>> {
    match value {
        None => Ok(default()),
        Some(Value::Null) => Ok(None),
        Some(Value::String(s)) => Ok(Some(s.clone())),
        Some(other) => fail(format!("{} is not a string", kind(other))),
    }
}

pub fn opt_string(value: Option<&Value>) -> R<Option<String>> {
    string(value, || None)
}

/// A `bool` property: absent takes `default`; null or any other token fails.
pub fn boolean(value: Option<&Value>, default: bool) -> R<bool> {
    match value {
        None => Ok(default),
        Some(Value::Bool(b)) => Ok(*b),
        Some(other) => fail(format!("{} is not a bool", kind(other))),
    }
}

/// A `bool?` property.
pub fn opt_boolean(value: Option<&Value>) -> R<Option<bool>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Bool(b)) => Ok(Some(*b)),
        Some(other) => fail(format!("{} is not a bool", kind(other))),
    }
}

fn integer_text(text: &str) -> Option<i64> {
    let digits = text.strip_prefix('-').unwrap_or(text);
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    text.parse().ok()
}

/// A JSON number as an integer: an integer literal (serde_json reads `-0` as the float -0.0, which is the integer 0 here; a
/// literal `-0.0` is read the same, where C# refuses it).
pub fn as_integer(value: &Value) -> Option<i64> {
    match value {
        Value::Number(n) => n.as_i64().or_else(|| n.as_f64().filter(|f| *f == 0.0 && f.is_sign_negative()).map(|_| 0)),
        _ => None,
    }
}

/// A JSON number (or, with `AllowReadingFromString`, a string holding one) as a 64-bit integer within `[min, max]`.
fn whole(value: &Value, min: i64, max: i64) -> R<i64> {
    let number = match value {
        Value::Number(_) => as_integer(value),
        Value::String(s) => integer_text(s),
        _ => None,
    };
    match number {
        Some(n) if (min..=max).contains(&n) => Ok(n),
        _ => fail(format!("{} is not an integer in range", kind(value))),
    }
}

/// An `int` property.
pub fn int32(value: Option<&Value>, default: i32) -> R<i32> {
    match value {
        None => Ok(default),
        Some(v) => whole(v, i32::MIN as i64, i32::MAX as i64).map(|n| n as i32),
    }
}

/// An `int?` property.
pub fn opt_int32(value: Option<&Value>) -> R<Option<i32>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(v) => whole(v, i32::MIN as i64, i32::MAX as i64).map(|n| Some(n as i32)),
    }
}

/// A `long` property.
pub fn int64(value: Option<&Value>, default: i64) -> R<i64> {
    match value {
        None => Ok(default),
        Some(v) => whole(v, i64::MIN, i64::MAX),
    }
}

/// A `long?` property.
pub fn opt_int64(value: Option<&Value>) -> R<Option<i64>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(v) => whole(v, i64::MIN, i64::MAX).map(Some),
    }
}

fn real(value: &Value) -> R<f64> {
    let number = match value {
        Value::Number(n) => n.as_f64(),
        Value::String(s) if is_json_number(s) => s.parse::<f64>().ok(),
        _ => None,
    };
    match number {
        Some(n) if n.is_finite() => Ok(n),
        _ => fail(format!("{} is not a number", kind(value))),
    }
}

/// The JSON number grammar (what `AllowReadingFromString` accepts in a string).
fn is_json_number(text: &str) -> bool {
    let b = text.as_bytes();
    let mut i = 0;
    if b.get(i) == Some(&b'-') {
        i += 1;
    }
    match b.get(i) {
        Some(b'0') => i += 1,
        Some(c) if c.is_ascii_digit() => {
            while b.get(i).is_some_and(u8::is_ascii_digit) {
                i += 1;
            }
        }
        _ => return false,
    }
    if b.get(i) == Some(&b'.') {
        i += 1;
        let start = i;
        while b.get(i).is_some_and(u8::is_ascii_digit) {
            i += 1;
        }
        if i == start {
            return false;
        }
    }
    if matches!(b.get(i), Some(b'e' | b'E')) {
        i += 1;
        if matches!(b.get(i), Some(b'+' | b'-')) {
            i += 1;
        }
        let start = i;
        while b.get(i).is_some_and(u8::is_ascii_digit) {
            i += 1;
        }
        if i == start {
            return false;
        }
    }
    i == b.len()
}

/// A `double` property.
pub fn float64(value: Option<&Value>, default: f64) -> R<f64> {
    match value {
        None => Ok(default),
        Some(v) => real(v),
    }
}

/// A `double?` property.
pub fn opt_float64(value: Option<&Value>) -> R<Option<f64>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(v) => real(v).map(Some),
    }
}

/// A record property: null is null, an object reads as `T`, anything else fails.
pub fn record<T: Wire>(value: Option<&Value>) -> R<Option<T>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(v) => T::read(v).map(Some),
    }
}

/// A list property of records: null is null, an array reads element by element. A null element is kept as `None` for the caller
/// to drop where C# drops it.
pub fn list<T>(value: Option<&Value>, each: impl Fn(&Value) -> R<T>) -> R<Option<Vec<Option<T>>>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Array(items)) => {
            items.iter().map(|item| if item.is_null() { Ok(None) } else { each(item).map(Some) }).collect::<R<Vec<_>>>().map(Some)
        }
        Some(other) => fail(format!("{} is not an array", kind(other))),
    }
}

/// A list of records whose null elements C# never reaches without failing (a `NullReferenceException` in the code that walks
/// it): one null fails the read here, as the load fails there.
pub fn strict_list<T: Wire>(value: Option<&Value>, what: &str) -> R<Option<Vec<T>>> {
    match list(value, T::read)? {
        None => Ok(None),
        Some(items) => {
            items.into_iter().map(|item| item.ok_or_else(|| ReadError::crash(format!("a null {what}")))).collect::<R<Vec<_>>>().map(Some)
        }
    }
}

/// A list of records whose null elements the C# code skips before anything reads them.
pub fn skipping_list<T: Wire>(value: Option<&Value>) -> R<Option<Vec<T>>> {
    Ok(list(value, T::read)?.map(|items| items.into_iter().flatten().collect()))
}

/// A `List<string>`, a null element kept.
pub fn string_list(value: Option<&Value>) -> R<Option<Vec<Option<String>>>> {
    list(value, |item| match item {
        Value::String(s) => Ok(s.clone()),
        other => fail(format!("{} is not a string", kind(other))),
    })
}

/// A `Dictionary<string, T>`: each key once, the last value at the first key's place.
pub fn dictionary<T>(value: Option<&Value>, each: impl Fn(&Value) -> R<T>) -> R<Option<IndexMap<String, Option<T>>>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Object(map)) => {
            let mut out = IndexMap::with_capacity(map.len());
            for (key, item) in map {
                out.insert(key.clone(), if item.is_null() { None } else { Some(each(item)?) });
            }
            Ok(Some(out))
        }
        Some(other) => fail(format!("{} is not an object", kind(other))),
    }
}

/// A `Dictionary<string, string>`.
pub fn string_map(value: Option<&Value>) -> R<Option<IndexMap<String, Option<String>>>> {
    dictionary(value, |item| match item {
        Value::String(s) => Ok(s.clone()),
        other => fail(format!("{} is not a string", kind(other))),
    })
}

pub fn kind(value: &Value) -> &'static str {
    match value {
        Value::Null => "null",
        Value::Bool(_) => "a bool",
        Value::Number(_) => "a number",
        Value::String(_) => "a string",
        Value::Array(_) => "an array",
        Value::Object(_) => "an object",
    }
}

/// `JsonDocument.Parse`'s depth limit: 64 nested arrays and objects.
pub const MAXIMUM_DEPTH: usize = 64;

pub fn depth(value: &Value) -> usize {
    match value {
        Value::Array(items) => 1 + items.iter().map(depth).max().unwrap_or(0),
        Value::Object(map) => 1 + map.values().map(depth).max().unwrap_or(0),
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn doubles_print_as_dotnet() {
        let cases = [
            (252.0, "252"),
            (0.5, "0.5"),
            (-0.0, "-0"),
            (1e15, "1000000000000000"),
            (1e16, "10000000000000000"),
            (1e17, "1E+17"),
            (1e14, "100000000000000"),
            (f64::from_bits(0xC317_38B1_9F75_A801), "-1634065000000000.2"),
            (0.0001, "0.0001"),
            (0.00001, "1E-05"),
            (2.0 / 3.0, "0.6666666666666666"),
            (1e100, "1E+100"),
            (123456789012345680000.0, "1.2345678901234568E+20"),
            (-1.5e-7, "-1.5E-07"),
        ];
        for (value, text) in cases {
            assert_eq!(format_double(value), text, "{value}");
        }
    }

    #[test]
    fn strings_escape_as_dotnet() {
        assert_eq!(escape("a\"b<&>'+`\\"), "\"a\\u0022b\\u003C\\u0026\\u003E\\u0027\\u002B\\u0060\\\\\"");
        assert_eq!(escape("é😀\n\u{7f}\u{1}"), "\"\\u00E9\\uD83D\\uDE00\\n\\u007F\\u0001\"");
        assert_eq!(escaped_len("é😀\n\u{7f}\u{1}"), escape("é😀\n\u{7f}\u{1}").len());
    }

    #[test]
    fn writer_places_commas() {
        let mut out = JsonOut::new();
        out.begin_object();
        out.prop_str("a", Some("x"));
        out.prop_str("skip", None);
        out.name("b");
        out.begin_array();
        out.int(1);
        out.float(2.5);
        out.begin_object();
        out.end_object();
        out.end_array();
        out.prop_bool("c", true);
        out.end_object();
        assert_eq!(out.finish(), r#"{"a":"x","b":[1,2.5,{}],"c":true}"#);
    }

    #[test]
    fn names_match_without_case() {
        let value: Value = serde_json::from_str(r#"{"Theme":"light","theme":"dark","ſtatus":1}"#).unwrap();
        let obj = Obj::of(&value, "x").unwrap();
        assert_eq!(obj.get("theme"), Some(&Value::from("dark")));
        assert_eq!(obj.get("status"), Some(&Value::from(1)));
        assert_eq!(obj.exact("Theme"), Some(&Value::from("light")));
    }

    #[test]
    fn numbers_read_from_strings() {
        assert_eq!(int32(Some(&Value::from("42")), 0), Ok(42));
        assert!(int32(Some(&Value::from(1.5)), 0).is_err());
        assert!(int32(Some(&Value::Null), 0).is_err());
        assert_eq!(float64(Some(&Value::from("2.5")), 0.0), Ok(2.5));
        assert!(float64(Some(&Value::from("NaN")), 0.0).is_err());
        assert!(boolean(Some(&Value::from("true")), false).is_err());
    }
}
