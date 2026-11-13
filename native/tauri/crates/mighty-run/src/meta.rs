//! C# `MetadataJson` and the `JsonElement.Text` extension (AgentActivity.cs:98-109, ProviderCatalog.cs:725-728): optional fields read
//! one by one from a parsed stream frame, a bad one read as nothing.
//!
//! `JsonElement.TryGetProperty` matches a name exactly and, for a name written twice, takes the last (serde_json keeps the last
//! value too). `TryGetInt64` takes an integer literal only (`1.0` and `1e3` are not), as `as_i64` does.

use serde_json::Value;

/// `MetadataJson.Property`: the field of an object, None for anything else (C#'s `default` element).
pub fn prop<'a>(value: &'a Value, key: &str) -> Option<&'a Value> {
    value.as_object().and_then(|object| object.get(key))
}

/// `JsonElement.Text(property)`: a string field, None when absent or of another kind.
pub fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    prop(value, key).and_then(Value::as_str)
}

/// [`text`] on an optional value.
pub fn text_of<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a str> {
    value.and_then(|v| text(v, key))
}

/// `MetadataJson.Integer`: a number written as an integer that fits a long.
pub fn integer(value: &Value, key: &str) -> Option<i64> {
    prop(value, key).and_then(Value::as_i64)
}

/// `MetadataJson.Number`: a finite number.
pub fn number(value: &Value, key: &str) -> Option<f64> {
    prop(value, key).filter(|v| v.is_number()).and_then(Value::as_f64).filter(|n| n.is_finite())
}

/// `MetadataJson.SafeInteger`: an integral number within JavaScript's safe range (decimal or exponent notation both count).
pub fn safe_integer(value: &Value, key: &str) -> Option<i64> {
    number(value, key).filter(|n| n.trunc() == *n && (-9_007_199_254_740_991.0..=9_007_199_254_740_991.0).contains(n)).map(|n| n as i64)
}

/// `MetadataJson.Flag`: the field is `true`.
pub fn flag(value: &Value, key: &str) -> bool {
    matches!(prop(value, key), Some(Value::Bool(true)))
}

/// `Property(value, key).ValueKind is Undefined or Null`.
pub fn absent_or_null(value: &Value, key: &str) -> bool {
    matches!(prop(value, key), None | Some(Value::Null))
}

/// The frame comes from the run's own session: no `parent_tool_use_id` (or a null one).
pub fn top_level(frame: &Value) -> bool {
    absent_or_null(frame, "parent_tool_use_id")
}

/// `JsonElement.GetRawText()` of a number as .NET keeps it: the literal; here serde_json's form of it (deviation: `1.50` reads `1.5`).
pub fn raw_number(value: &Value) -> String {
    value.to_string()
}
