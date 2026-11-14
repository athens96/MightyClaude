//! C# `AcpElicitationForm` (AgentQuestions.cs:70-221): an ACP `elicitation/create` form (https://agentclientprotocol.com, the MCP
//! elicitation schema) as the question card's questions. Each property is one question: a string with `oneOf` / `enum` is a single
//! choice, an array of those a multiple choice, a plain string a typed answer, a boolean a yes or no. opencode adds `<key>_custom` (a
//! plain string) beside a choice whose answer may be typed: that is the card's own "write your answer" row, not a question. Numbers and
//! nested objects are not questions the card asks; such a form is declined.

use std::cmp::Ordering;
use std::collections::{HashMap, HashSet};

use indexmap::IndexMap;
use serde_json::{Map, Value};

use crate::questionnaire::agent;
use crate::strings;
use crate::{Result, RunError};

/// `AcpElicitationForm.Kind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Single,
    Multiple,
    Text,
    Boolean,
}

/// `AcpElicitationForm.Field`: one property; `values` the value each shown label stands for; `custom_key` where a typed answer goes
/// (its own property, or the field itself when the agent takes any string for a choice).
#[derive(Debug, Clone, PartialEq)]
pub struct Field {
    pub key: String,
    pub question: String,
    pub kind: Kind,
    pub values: HashMap<String, Value>,
    pub custom_key: Option<String>,
}

/// C# `AcpElicitationForm`.
#[derive(Debug, Clone, PartialEq)]
pub struct AcpElicitationForm {
    pub fields: Vec<Field>,
    pub items: Vec<agent::Item>,
}

fn text(node: Option<&Value>) -> Option<&str> {
    node.and_then(Value::as_str)
}

/// A choice: its label, its description and the value it stands for.
type Choice = (String, String, Value);

impl AcpElicitationForm {
    /// `Read`: the form, or None when the card cannot ask it. `free_choices`: the agent reads a typed answer in place of a choice
    /// (Hermes's clarify does; opencode does only where it sent the `_custom` property).
    pub fn read(parameters: &Map<String, Value>, free_choices: bool) -> Option<AcpElicitationForm> {
        if text(parameters.get("mode")).unwrap_or("form") != "form" {
            return None;
        }
        let Some(Value::Object(schema)) = parameters.get("requestedSchema") else { return None };
        if text(schema.get("type")).unwrap_or("object") != "object" {
            return None;
        }
        let Some(Value::Object(properties)) = schema.get("properties") else { return None };
        if properties.is_empty() {
            return None;
        }
        let message = agent::text(text(parameters.get("message")), 8_192, false);
        // A JSON object has no order here; `q0, q1, … q10` sort as numbers do.
        let mut keys: Vec<&String> = properties.keys().collect();
        keys.sort_by(|a, b| numeric_order(a, b));
        let plain_string = |property: Option<&Value>| match property {
            Some(Value::Object(value)) => {
                text(value.get("type")) == Some("string")
                    && [value.get("enum"), value.get("oneOf"), value.get("anyOf")].iter().all(|v| matches!(v, None | Some(Value::Null)))
            }
            _ => false,
        };
        let empty = Map::new();
        let custom_keys: HashSet<&String> = keys
            .iter()
            .copied()
            .filter(|key| {
                let Some(base) = key.strip_suffix("_custom") else { return false };
                plain_string(properties.get(*key))
                    && match properties.get(base) {
                        Some(Value::Object(choice)) => {
                            choices(choice).is_some()
                                || choices(match choice.get("items") {
                                    Some(Value::Object(items)) => items,
                                    _ => &empty,
                                })
                                .is_some()
                        }
                        _ => false,
                    }
            })
            .collect();
        let mut fields = Vec::new();
        let mut items = Vec::new();
        for key in keys.iter().copied().filter(|key| !custom_keys.contains(key)) {
            if key.len() > 256 {
                return None;
            }
            let Some(Value::Object(property)) = properties.get(key) else { return None };
            let title = agent::text(text(property.get("title")), 256, true);
            let description = agent::text(text(property.get("description")), 8_192, false);
            // opencode: the title is the question's short header and the description its text. Hermes: the property says nothing and
            // the form's message is the question.
            let question = description
                .clone()
                .or_else(|| if properties.len() - custom_keys.len() == 1 { message.clone() } else { None })
                .or_else(|| title.clone())?;
            let header = description.as_ref().and(title.clone()).unwrap_or_else(|| strings::get("questionnaire.header.agent"));
            let (kind, options): (Kind, Vec<Choice>) = match text(property.get("type")) {
                Some("string") => match choices(property) {
                    Some(single) => (Kind::Single, single),
                    None => (Kind::Text, Vec::new()),
                },
                Some("array") => {
                    let items = match property.get("items") {
                        Some(Value::Object(items)) => items,
                        _ => &empty,
                    };
                    (Kind::Multiple, choices(items)?)
                }
                Some("boolean") => (
                    Kind::Boolean,
                    vec![
                        (strings::get("questionnaire.option.yes"), String::new(), Value::Bool(true)),
                        (strings::get("questionnaire.option.no"), String::new(), Value::Bool(false)),
                    ],
                ),
                _ => return None,
            };
            if options.iter().map(|o| o.0.as_str()).collect::<HashSet<_>>().len() != options.len() {
                return None;
            }
            let suffixed = format!("{key}_custom");
            let custom = if custom_keys.contains(&suffixed) {
                Some(suffixed)
            } else if kind == Kind::Text || free_choices && kind != Kind::Boolean {
                Some(key.clone())
            } else {
                None
            };
            fields.push(Field {
                key: key.clone(),
                question: question.clone(),
                kind,
                values: options.iter().map(|(label, _, value)| (label.clone(), value.clone())).collect(),
                custom_key: custom,
            });
            items.push(agent::Item {
                header,
                question,
                multi_select: kind == Kind::Multiple,
                options: options.into_iter().map(|(label, description, _)| (label, description)).collect(),
            });
        }
        (!fields.is_empty()).then_some(AcpElicitationForm { fields, items })
    }

    /// `Content`: the `content` of an `accept` answer. A typed answer where the agent takes only its choices is refused, and the card
    /// stays answerable.
    pub fn content(&self, answers: &IndexMap<String, (Vec<String>, String)>) -> Result<Value> {
        let mut content = Map::new();
        for field in &self.fields {
            let Some((picked, typed)) = answers.get(&field.question) else {
                return Err(RunError::Invalid(strings::get("questionnaire.error.missingAnswer")));
            };
            let picked: Vec<Value> = picked.iter().filter_map(|label| field.values.get(label).cloned()).collect();
            if !typed.is_empty() {
                let Some(custom) = &field.custom_key else {
                    return Err(RunError::Invalid(strings::get("questionnaire.error.choicesOnly")));
                };
                if *custom != field.key {
                    content.insert(custom.clone(), Value::String(typed.clone()));
                    if field.kind == Kind::Multiple && !picked.is_empty() {
                        content.insert(field.key.clone(), Value::Array(picked));
                    }
                    continue;
                }
                let value = if field.kind == Kind::Multiple {
                    let mut all = picked;
                    all.push(Value::String(typed.clone()));
                    Value::Array(all)
                } else {
                    Value::String(typed.clone())
                };
                content.insert(field.key.clone(), value);
                continue;
            }
            let Some(first) = picked.first().cloned() else {
                return Err(RunError::Invalid(strings::get("questionnaire.error.missingAnswer")));
            };
            content.insert(field.key.clone(), if field.kind == Kind::Multiple { Value::Array(picked) } else { first });
        }
        Ok(Value::Object(content))
    }
}

/// `Choices`: the choices of a `oneOf` / `anyOf` list of `{const, title, description}` or of an `enum` (with `enumNames`); None when
/// the schema offers none.
fn choices(schema: &Map<String, Value>) -> Option<Vec<Choice>> {
    let usable = |value: &Value| value.is_string() || value.is_number();
    let shown = |value: &Value| match value {
        Value::String(text) => text.clone(),
        other => other.to_string(),
    };
    let list = match schema.get("oneOf") {
        Some(value) if !value.is_null() => Some(value),
        _ => schema.get("anyOf"),
    };
    if let Some(Value::Array(list)) = list
        && !list.is_empty()
    {
        let mut result = Vec::new();
        for entry in list {
            let Value::Object(choice) = entry else { return None };
            let value = choice.get("const").filter(|v| usable(v))?;
            let label = agent::text(Some(&text(choice.get("title")).map(str::to_string).unwrap_or_else(|| shown(value))), 1_024, true)?;
            result.push((label, agent::text(text(choice.get("description")), 8_192, false).unwrap_or_default(), value.clone()));
        }
        return Some(result);
    }
    if let Some(Value::Array(values)) = schema.get("enum")
        && !values.is_empty()
    {
        let names = schema.get("enumNames").and_then(Value::as_array);
        let mut result = Vec::new();
        for (index, value) in values.iter().enumerate() {
            if !usable(value) {
                return None;
            }
            let name = names.and_then(|n| n.get(index)).and_then(Value::as_str);
            let label = agent::text(Some(&name.map(str::to_string).unwrap_or_else(|| shown(value))), 1_024, true)?;
            result.push((label, String::new(), value.clone()));
        }
        return Some(result);
    }
    None
}

/// `NumericOrder`: keys as numbers sort (`q2` before `q10`), the Mac's `compare(_:options: .numeric)`, on UTF-16 units as C# compares.
pub fn numeric_order(x: &str, y: &str) -> Ordering {
    let a: Vec<u16> = x.encode_utf16().collect();
    let b: Vec<u16> = y.encode_utf16().collect();
    let digit = |unit: u16| (u16::from(b'0')..=u16::from(b'9')).contains(&unit);
    let trim = |run: &[u16]| -> Vec<u16> { run.iter().position(|u| *u != u16::from(b'0')).map_or(Vec::new(), |at| run[at..].to_vec()) };
    let (mut i, mut j) = (0, 0);
    while i < a.len() && j < b.len() {
        if digit(a[i]) && digit(b[j]) {
            let (si, sj) = (i, j);
            while i < a.len() && digit(a[i]) {
                i += 1;
            }
            while j < b.len() && digit(b[j]) {
                j += 1;
            }
            let (left, right) = (trim(&a[si..i]), trim(&b[sj..j]));
            if left.len() != right.len() {
                return left.len().cmp(&right.len());
            }
            let digits = left.cmp(&right);
            if digits != Ordering::Equal {
                return digits;
            }
            continue;
        }
        if a[i] != b[j] {
            return a[i].cmp(&b[j]);
        }
        i += 1;
        j += 1;
    }
    (a.len() - i).cmp(&(b.len() - j))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keys_sort_as_numbers() {
        let mut keys = vec!["q10", "q2", "q0", "q1", "a", "q02"];
        keys.sort_by(|a, b| numeric_order(a, b));
        assert_eq!(keys, ["a", "q0", "q1", "q2", "q02", "q10"]);
    }
}
