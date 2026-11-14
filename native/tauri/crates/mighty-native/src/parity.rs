//! What makes a push comparable with C#'s capture (plan W6 §1.8): the `native-diff` example and the golden tests normalize both
//! sides with these.
//!
//! - every object's keys sorted: WebView2 hands `PostWebMessageAsJson`'s message to C#'s page through Chromium's values, whose
//!   dictionaries are sorted maps, so the capture never sees the order C# wrote (the one thing a capture cannot compare);
//! - every `id` number renumbered by its first appearance (C#'s ids count over the run, Rust's follow key paths);
//! - each mask text replaced by `<masked>` inside every string, and a font list's `items` emptied (this PC's fonts);
//! - written again with C#'s `Wire.Json` writer, so only escaping differences go.

use mighty_core::json::JsonOut;
use serde_json::Value;

/// Every object's keys in sorted order.
pub fn sort_keys(value: &mut Value) {
    match value {
        Value::Object(map) => {
            map.values_mut().for_each(sort_keys);
            map.sort_keys();
        }
        Value::Array(items) => items.iter_mut().for_each(sort_keys),
        _ => {}
    }
}

/// Every `id` number renumbered by its first appearance, from 1.
pub fn renumber(value: &mut Value) {
    fn walk(value: &mut Value, seen: &mut Vec<i64>) {
        match value {
            Value::Object(map) => {
                for (key, item) in map.iter_mut() {
                    if key == "id"
                        && let Some(id) = item.as_i64()
                    {
                        let at = seen.iter().position(|s| *s == id).unwrap_or_else(|| {
                            seen.push(id);
                            seen.len() - 1
                        });
                        *item = Value::from(at as i64 + 1);
                    } else {
                        walk(item, seen);
                    }
                }
            }
            Value::Array(items) => items.iter_mut().for_each(|item| walk(item, seen)),
            _ => {}
        }
    }
    walk(value, &mut vec![]);
}

/// Each of `masks` replaced by `<masked>` in every string; a pop-up whose name says it lists font families has its `items` emptied.
pub fn mask(value: &mut Value, masks: &[String]) {
    match value {
        Value::String(text) => {
            for m in masks.iter().filter(|m| !m.is_empty()) {
                *text = text.replace(m.as_str(), "<masked>");
            }
        }
        Value::Object(map) => {
            let fonts = map.get("t").and_then(Value::as_str) == Some("select")
                && map.get("name").and_then(Value::as_str).is_some_and(|n| n.to_lowercase().contains("font") || n.contains("글꼴"));
            if fonts {
                map.insert("items".into(), Value::Array(vec![]));
            }
            map.values_mut().for_each(|item| mask(item, masks));
        }
        Value::Array(items) => items.iter_mut().for_each(|item| mask(item, masks)),
        _ => {}
    }
}

/// `value` sorted, renumbered and masked.
pub fn normalize(value: &Value, masks: &[String]) -> Value {
    let mut value = value.clone();
    sort_keys(&mut value);
    renumber(&mut value);
    mask(&mut value, masks);
    value
}

/// `value` as C#'s `Wire.Json` writes it.
pub fn text(value: &Value) -> String {
    let mut out = JsonOut::new();
    out.value(value);
    out.finish()
}

/// Section `index` of a pushed view (`{kind, id, place, view:{sections:[…]}}`, or the view itself), normalized on its own (its ids
/// from 1), as text: a box's golden compares with the box alone.
pub fn section(push: &Value, index: usize) -> Option<String> {
    let view = push.get("view").unwrap_or(push);
    let section = view.get("sections")?.get(index)?;
    Some(text(&normalize(section, &[])))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn keys_sort_ids_count_from_one_and_masks_apply() {
        let push = json!({"view": {"sections": [{"node": {"t": "button", "id": 41, "c": [{"t": "text", "text": "C:\\p\\x"}, {"id": 41}, {"id": 7}]}}]}});
        assert_eq!(
            text(&normalize(&push, &["C:\\p".into()])),
            r#"{"view":{"sections":[{"node":{"c":[{"t":"text","text":"\u003Cmasked\u003E\\x"},{"id":1},{"id":2}],"id":1,"t":"button"}}]}}"#
        );
        assert_eq!(section(&push, 0).unwrap(), r#"{"node":{"c":[{"t":"text","text":"C:\\p\\x"},{"id":1},{"id":2}],"id":1,"t":"button"}}"#);
        assert!(section(&push, 1).is_none());
    }
}
