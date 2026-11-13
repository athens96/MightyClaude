//! A workspace's layout of tab groups and binary splits (C# `PaneLayout`, MightyCode.Core/PaneLayout.cs).

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use crate::model::PaneLayoutNode;
use crate::wire;

/// The most panes the app holds, every workspace together.
pub const MAXIMUM_SESSIONS: usize = 128;

/// A layout node with C#'s defaults filled in; serialized as C# writes `PaneLayoutNode` (null fields left out).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Node {
    pub id: String,
    pub kind: String,
    pub session_ids: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub selected_session_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub axis: Option<String>,
    pub ratio: f64,
    pub children: Vec<Node>,
}

fn new_id() -> String {
    uuid::Uuid::new_v4().to_string()
}

impl Node {
    /// A saved node as C# deserializes it: a missing id is a new one, a missing kind is "tabs", a missing ratio is .5.
    pub fn from_raw(raw: &PaneLayoutNode) -> Node {
        Node {
            id: raw.id.clone().unwrap_or_else(new_id),
            kind: raw.kind.clone().unwrap_or_else(|| "tabs".into()),
            session_ids: raw.session_ids.clone().unwrap_or_default(),
            selected_session_id: raw.selected_session_id.clone(),
            axis: raw.axis.clone(),
            ratio: raw.ratio.unwrap_or(0.5),
            children: raw.children.as_deref().unwrap_or(&[]).iter().map(Node::from_raw).collect(),
        }
    }

    pub fn to_raw(&self) -> PaneLayoutNode {
        PaneLayoutNode {
            id: Some(self.id.clone()),
            kind: Some(self.kind.clone()),
            session_ids: Some(self.session_ids.clone()),
            selected_session_id: self.selected_session_id.clone(),
            axis: self.axis.clone(),
            ratio: Some(self.ratio),
            children: Some(self.children.iter().map(Node::to_raw).collect()),
            extra: Default::default(),
        }
    }

    fn tabs(ids: Vec<String>, active: Option<&str>) -> Node {
        let selected = match active {
            Some(a) if ids.iter().any(|id| id == a) => Some(a.to_string()),
            _ => ids.first().cloned(),
        };
        Node {
            id: new_id(),
            kind: "tabs".into(),
            session_ids: ids,
            selected_session_id: selected,
            axis: None,
            ratio: 0.5,
            children: vec![],
        }
    }

    fn split(a: Node, b: Node, axis: &str, ratio: f64) -> Node {
        Node {
            id: new_id(),
            kind: "split".into(),
            session_ids: vec![],
            selected_session_id: None,
            axis: Some(axis.into()),
            ratio,
            children: vec![a, b],
        }
    }
}

/// The tab groups of a layout, left to right and top to bottom.
pub fn groups(node: &Node) -> Vec<&Node> {
    if node.kind == "tabs" { vec![node] } else { node.children.iter().flat_map(groups).collect() }
}

pub fn clamp_ratio(ratio: f64) -> f64 {
    if ratio.is_finite() { ratio.clamp(0.15, 0.85) } else { 0.5 }
}

fn ordered(session_ids: &[String]) -> Vec<String> {
    let mut seen = HashSet::new();
    session_ids.iter().take(MAXIMUM_SESSIONS).filter(|id| wire::identifier(id) && seen.insert(id.as_str())).cloned().collect()
}

/// C# `PaneLayout.Normalize`: the saved tree with only the given panes, each once, nothing deeper than 16 or larger than 255
/// nodes, duplicate or invalid node ids replaced; a pane missing from it joins the first group; `active` selected.
pub fn normalize(root: Option<&Node>, session_ids: &[String], active: Option<&str>) -> Option<Node> {
    let ordered = ordered(session_ids);
    if ordered.is_empty() {
        return None;
    }
    let allowed: HashSet<&str> = ordered.iter().map(String::as_str).collect();
    let mut seen: HashSet<String> = HashSet::new();
    let mut nodes: HashSet<String> = HashSet::new();
    let mut count = 0;

    fn visit(
        node: &Node,
        depth: usize,
        allowed: &HashSet<&str>,
        seen: &mut HashSet<String>,
        nodes: &mut HashSet<String>,
        count: &mut usize,
    ) -> Option<Node> {
        if depth >= 16 {
            return None;
        }
        *count += 1;
        if *count > 255 {
            return None;
        }
        let id = if wire::identifier(&node.id) && nodes.insert(node.id.clone()) { node.id.clone() } else { new_id() };
        nodes.insert(id.clone());
        if node.kind == "tabs" {
            let ids: Vec<String> = node
                .session_ids
                .iter()
                .take(MAXIMUM_SESSIONS)
                .filter(|s| allowed.contains(s.as_str()) && seen.insert((*s).clone()))
                .cloned()
                .collect();
            if ids.is_empty() {
                return None;
            }
            let selected = match &node.selected_session_id {
                Some(s) if ids.contains(s) => s.clone(),
                _ => ids[0].clone(),
            };
            return Some(Node {
                id,
                kind: "tabs".into(),
                session_ids: ids,
                selected_session_id: Some(selected),
                axis: None,
                ratio: 0.5,
                children: vec![],
            });
        }
        if node.kind != "split" {
            return None;
        }
        let mut children: Vec<Node> =
            node.children.iter().take(2).filter_map(|c| visit(c, depth + 1, allowed, seen, nodes, count)).collect();
        match children.len() {
            0 => None,
            1 => children.pop(),
            _ => Some(Node {
                id,
                kind: "split".into(),
                session_ids: vec![],
                selected_session_id: None,
                axis: Some(if node.axis.as_deref() == Some("vertical") { "vertical" } else { "horizontal" }.into()),
                ratio: clamp_ratio(node.ratio),
                children,
            }),
        }
    }

    let result = root.and_then(|r| visit(r, 0, &allowed, &mut seen, &mut nodes, &mut count));
    let missing: Vec<String> = ordered.iter().filter(|id| !seen.contains(*id)).cloned().collect();
    let result = match result {
        None => Node::tabs(missing, active),
        Some(tree) if !missing.is_empty() => {
            let target = groups(&tree)[0].clone();
            let mut joined = target.clone();
            joined.session_ids.extend(missing);
            replace(&tree, &target.id, &joined)
        }
        Some(tree) => tree,
    };
    Some(match active {
        Some(a) => select(&result, a),
        None => result,
    })
}

/// C# `PaneLayout.Preset`: one group for focus, tabs and custom; otherwise the panes halved recursively, columns always side by
/// side, grid alternating.
pub fn preset(session_ids: &[String], layout: &str, active: Option<&str>) -> Option<Node> {
    let ids = ordered(session_ids);
    if ids.is_empty() {
        return None;
    }
    if matches!(layout, "focus" | "tabs" | "custom") {
        return Some(Node::tabs(ids, active));
    }
    fn build(values: &[String], depth: usize, layout: &str) -> Node {
        if values.len() == 1 {
            return Node::tabs(values.to_vec(), None);
        }
        let middle = values.len().div_ceil(2);
        let axis = if layout == "columns" || depth.is_multiple_of(2) { "horizontal" } else { "vertical" };
        Node::split(
            build(&values[..middle], depth + 1, layout),
            build(&values[middle..], depth + 1, layout),
            axis,
            middle as f64 / values.len() as f64,
        )
    }
    Some(build(&ids, 0, layout))
}

/// C# `PaneLayout.Select`: the group holding the pane shows it.
pub fn select(root: &Node, session_id: &str) -> Node {
    if root.kind == "tabs" {
        let mut node = root.clone();
        if node.session_ids.iter().any(|s| s == session_id) {
            node.selected_session_id = Some(session_id.to_string());
        }
        return node;
    }
    Node { children: root.children.iter().map(|c| select(c, session_id)).collect(), ..root.clone() }
}

/// C# `PaneLayout.Resize`.
pub fn resize(root: &Node, split_id: &str, ratio: f64) -> Node {
    if !ratio.is_finite() {
        return root.clone();
    }
    if root.id == split_id && root.kind == "split" {
        return Node { ratio: clamp_ratio(ratio), ..root.clone() };
    }
    Node { children: root.children.iter().map(|c| resize(c, split_id, ratio)).collect(), ..root.clone() }
}

fn replace(root: &Node, id: &str, replacement: &Node) -> Node {
    if root.id == id {
        return replacement.clone();
    }
    Node { children: root.children.iter().map(|c| replace(c, id, replacement)).collect(), ..root.clone() }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids(n: usize) -> Vec<String> {
        (1..=n).map(|i| format!("s{i}")).collect()
    }

    #[test]
    fn grid_halves_and_alternates() {
        let tree = preset(&ids(3), "grid", None).unwrap();
        assert_eq!(tree.kind, "split");
        assert_eq!(tree.axis.as_deref(), Some("horizontal"));
        assert!((tree.ratio - 2.0 / 3.0).abs() < 1e-9);
        assert_eq!(tree.children[0].axis.as_deref(), Some("vertical"));
        assert_eq!(groups(&tree).len(), 3);
        let columns = preset(&ids(4), "columns", None).unwrap();
        assert_eq!(columns.children[0].axis.as_deref(), Some("horizontal"));
    }

    #[test]
    fn tabs_select_the_active_pane() {
        let tree = preset(&ids(3), "tabs", Some("s2")).unwrap();
        assert_eq!(tree.selected_session_id.as_deref(), Some("s2"));
        let moved = select(&tree, "s3");
        assert_eq!(moved.selected_session_id.as_deref(), Some("s3"));
    }

    #[test]
    fn normalize_drops_gone_panes_and_adds_new_ones() {
        let tree = preset(&ids(2), "columns", None).unwrap();
        let mut now = ids(3);
        now.remove(0);
        let fixed = normalize(Some(&tree), &now, Some("s3")).unwrap();
        let all: Vec<&String> = groups(&fixed).iter().flat_map(|g| g.session_ids.iter()).collect();
        assert_eq!(all, vec!["s2", "s3"]);
        assert_eq!(fixed.kind, "tabs");
        assert_eq!(fixed.selected_session_id.as_deref(), Some("s3"));
    }

    #[test]
    fn normalize_keeps_valid_ids() {
        let tree = preset(&ids(2), "columns", None).unwrap();
        let again = normalize(Some(&tree), &ids(2), None).unwrap();
        assert_eq!(again.id, tree.id);
        assert_eq!(again.children[0].id, tree.children[0].id);
    }
}
