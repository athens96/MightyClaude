//! A workspace's layout of tab groups and binary splits (C# `PaneLayout`, MightyCode.Core/PaneLayout.cs), on the saved
//! [`PaneLayoutNode`] itself.

use std::collections::HashSet;

use serde::ser::{Serialize, SerializeStruct, Serializer};

use crate::model::PaneLayoutNode;
use crate::wire::{self, new_id};

/// The most panes the app holds, every workspace together.
pub const MAXIMUM_SESSIONS: usize = 128;

impl Serialize for PaneLayoutNode {
    /// The page's layout model reads the node as `Wire.Json` writes it (nulls left out).
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut node = serializer.serialize_struct("PaneLayoutNode", 7)?;
        if let Some(id) = &self.id {
            node.serialize_field("id", id)?;
        }
        if let Some(kind) = &self.kind {
            node.serialize_field("kind", kind)?;
        }
        if let Some(ids) = &self.session_ids {
            node.serialize_field("sessionIds", ids)?;
        }
        if let Some(selected) = &self.selected_session_id {
            node.serialize_field("selectedSessionId", selected)?;
        }
        if let Some(axis) = &self.axis {
            node.serialize_field("axis", axis)?;
        }
        node.serialize_field("ratio", &self.ratio)?;
        if let Some(children) = &self.children {
            node.serialize_field("children", children)?;
        }
        node.end()
    }
}

/// `new() { SessionIds = ids, SelectedSessionId = ... }`: a tab group with a new id.
fn tab_node(id: String, ids: Vec<String>, selected: Option<String>) -> PaneLayoutNode {
    PaneLayoutNode {
        id: Some(id),
        kind: Some("tabs".into()),
        session_ids: Some(ids.into_iter().map(Some).collect()),
        selected_session_id: selected,
        axis: None,
        ratio: 0.5,
        children: Some(vec![]),
    }
}

/// C# `Tabs(ids, active)`.
fn tabs(ids: Vec<String>, active: Option<&str>) -> PaneLayoutNode {
    let selected = match active {
        Some(a) if ids.iter().any(|id| id == a) => Some(a.to_string()),
        _ => ids.first().cloned(),
    };
    tab_node(new_id(), ids, selected)
}

/// C# `Split(a, b, axis, ratio)`.
fn split(a: PaneLayoutNode, b: PaneLayoutNode, axis: &str, ratio: f64) -> PaneLayoutNode {
    PaneLayoutNode {
        id: Some(new_id()),
        kind: Some("split".into()),
        session_ids: Some(vec![]),
        selected_session_id: None,
        axis: Some(axis.into()),
        ratio,
        children: Some(vec![Some(a), Some(b)]),
    }
}

/// C# `PaneLayout.Groups`: the tab groups, left to right and top to bottom.
pub fn groups(node: &PaneLayoutNode) -> Vec<&PaneLayoutNode> {
    if node.kind() == "tabs" { vec![node] } else { node.children().flat_map(groups).collect() }
}

/// C# `PaneLayout.Sessions`.
pub fn sessions(node: &PaneLayoutNode) -> Vec<&str> {
    groups(node).into_iter().flat_map(|g| g.session_ids()).collect()
}

/// C# `PaneLayout.ClampRatio`.
pub fn clamp_ratio(ratio: f64) -> f64 {
    if ratio.is_finite() { ratio.clamp(0.15, 0.85) } else { 0.5 }
}

/// `sessionIds.Take(128).Where(Wire.Identifier).Distinct()`.
fn ordered(session_ids: &[String]) -> Vec<String> {
    let mut seen = HashSet::new();
    session_ids.iter().take(MAXIMUM_SESSIONS).filter(|id| wire::identifier(id) && seen.insert(id.as_str())).cloned().collect()
}

/// C# `PaneLayout.Normalize`: the saved tree with only the given panes, each once, nothing deeper than 16 or larger than 255
/// nodes, duplicate or invalid node ids replaced; a pane missing from it joins the first group; `active` selected.
pub fn normalize(root: Option<&PaneLayoutNode>, session_ids: &[String], active: Option<&str>) -> Option<PaneLayoutNode> {
    let ordered = ordered(session_ids);
    if ordered.is_empty() {
        return None;
    }
    struct Walk<'a> {
        allowed: HashSet<&'a str>,
        seen: HashSet<String>,
        nodes: HashSet<String>,
        count: usize,
    }
    fn visit(walk: &mut Walk, node: Option<&PaneLayoutNode>, depth: usize) -> Option<PaneLayoutNode> {
        let node = node?;
        if depth >= 16 {
            return None;
        }
        walk.count += 1;
        if walk.count > 255 {
            return None;
        }
        let id = match node.id.as_deref() {
            Some(id) if wire::identifier(id) && walk.nodes.insert(id.to_string()) => id.to_string(),
            _ => new_id(),
        };
        walk.nodes.insert(id.clone());
        if node.kind() == "tabs" && node.kind.is_some() {
            let mut ids = vec![];
            for session in node.session_ids.iter().flatten().take(MAXIMUM_SESSIONS) {
                if let Some(session) = session
                    && walk.allowed.contains(session.as_str())
                    && walk.seen.insert(session.clone())
                {
                    ids.push(session.clone());
                }
            }
            if ids.is_empty() {
                return None;
            }
            let selected = match &node.selected_session_id {
                Some(s) if ids.contains(s) => s.clone(),
                _ => ids[0].clone(),
            };
            return Some(tab_node(id, ids, Some(selected)));
        }
        if node.kind.as_deref() != Some("split") {
            return None;
        }
        let mut children: Vec<PaneLayoutNode> =
            node.children.iter().flatten().take(2).filter_map(|c| visit(walk, c.as_ref(), depth + 1)).collect();
        match children.len() {
            0 => None,
            1 => children.pop(),
            _ => Some(PaneLayoutNode {
                id: Some(id),
                kind: Some("split".into()),
                session_ids: Some(vec![]),
                selected_session_id: None,
                axis: Some(if node.axis.as_deref() == Some("vertical") { "vertical" } else { "horizontal" }.into()),
                ratio: clamp_ratio(node.ratio),
                children: Some(children.into_iter().map(Some).collect()),
            }),
        }
    }
    let mut walk = Walk { allowed: ordered.iter().map(String::as_str).collect(), seen: HashSet::new(), nodes: HashSet::new(), count: 0 };
    let result = visit(&mut walk, root, 0);
    let missing: Vec<String> = ordered.iter().filter(|id| !walk.seen.contains(*id)).cloned().collect();
    let result = match result {
        None => tabs(missing, active),
        Some(tree) if !missing.is_empty() => {
            let target = groups(&tree)[0].clone();
            let mut joined = target.clone();
            joined.session_ids.get_or_insert_with(Vec::new).extend(missing.into_iter().map(Some));
            replace(&tree, target.id(), &joined)
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
pub fn preset(session_ids: &[String], layout: &str, active: Option<&str>) -> Option<PaneLayoutNode> {
    let ids = ordered(session_ids);
    if ids.is_empty() {
        return None;
    }
    if matches!(layout, "focus" | "tabs" | "custom") {
        return Some(tabs(ids, active));
    }
    fn build(values: &[String], depth: usize, layout: &str) -> PaneLayoutNode {
        if values.len() == 1 {
            return tabs(values.to_vec(), None);
        }
        let middle = values.len().div_ceil(2);
        let axis = if layout == "columns" || depth.is_multiple_of(2) { "horizontal" } else { "vertical" };
        let a = build(&values[..middle], depth + 1, layout);
        let b = build(&values[middle..], depth + 1, layout);
        split(a, b, axis, middle as f64 / values.len() as f64)
    }
    Some(build(&ids, 0, layout))
}

fn map_children(root: &PaneLayoutNode, each: impl Fn(&PaneLayoutNode) -> PaneLayoutNode) -> PaneLayoutNode {
    PaneLayoutNode { children: root.children.as_ref().map(|c| c.iter().map(|n| n.as_ref().map(&each)).collect()), ..root.clone() }
}

/// C# `PaneLayout.Select`: the group holding the pane shows it.
pub fn select(root: &PaneLayoutNode, session_id: &str) -> PaneLayoutNode {
    if root.kind() == "tabs" && root.kind.is_some() {
        let mut node = root.clone();
        if node.session_ids().any(|s| s == session_id) {
            node.selected_session_id = Some(session_id.to_string());
        }
        return node;
    }
    map_children(root, |c| select(c, session_id))
}

/// C# `PaneLayout.Resize`.
pub fn resize(root: &PaneLayoutNode, split_id: &str, ratio: f64) -> PaneLayoutNode {
    if !ratio.is_finite() {
        return root.clone();
    }
    if root.id() == split_id && root.kind() == "split" {
        return PaneLayoutNode { ratio: clamp_ratio(ratio), ..root.clone() };
    }
    map_children(root, |c| resize(c, split_id, ratio))
}

/// C# `PaneLayout.Replace`.
pub fn replace(root: &PaneLayoutNode, id: &str, replacement: &PaneLayoutNode) -> PaneLayoutNode {
    if root.id.as_deref() == Some(id) {
        return replacement.clone();
    }
    map_children(root, |c| replace(c, id, replacement))
}

/// C# `PaneLayout.Remove`.
pub fn remove(root: &PaneLayoutNode, session_id: &str) -> Option<PaneLayoutNode> {
    if root.kind() == "tabs" {
        let ids: Vec<String> = root.session_ids().filter(|id| *id != session_id).map(str::to_string).collect();
        if ids.is_empty() {
            return None;
        }
        let selected = match &root.selected_session_id {
            Some(s) if ids.contains(s) => Some(s.clone()),
            _ => Some(ids[0].clone()),
        };
        return Some(PaneLayoutNode {
            session_ids: Some(ids.into_iter().map(Some).collect()),
            selected_session_id: selected,
            ..root.clone()
        });
    }
    let mut children: Vec<PaneLayoutNode> = root.children().filter_map(|c| remove(c, session_id)).collect();
    match children.len() {
        0 => None,
        1 => children.pop(),
        _ => Some(PaneLayoutNode { children: Some(children.into_iter().map(Some).collect()), ..root.clone() }),
    }
}

/// C# `PaneLayout.Move`: one tab moved to a group (`center`, at `index` counted before its removal) or beside it.
pub fn move_tab(root: &PaneLayoutNode, session_id: &str, target_group_id: &str, edge: &str, index: Option<i64>) -> PaneLayoutNode {
    let all = groups(root);
    let source = all.iter().find(|g| g.session_ids().any(|s| s == session_id)).copied();
    let target = all.iter().find(|g| g.id() == target_group_id).copied();
    let (Some(source), Some(target)) = (source, target) else { return root.clone() };
    if !matches!(edge, "center" | "left" | "right" | "top" | "bottom") {
        return root.clone();
    }
    if source.id() == target.id() {
        if edge == "center" {
            let current: Vec<&str> = target.session_ids().collect();
            let old = current.iter().position(|s| *s == session_id).map(|i| i as i64).unwrap_or(-1);
            let mut slot = index.unwrap_or(current.len() as i64).clamp(0, current.len() as i64);
            if old < slot {
                slot -= 1;
            }
            let mut ids: Vec<String> = current.iter().filter(|s| **s != session_id).map(|s| s.to_string()).collect();
            let at = slot.clamp(0, ids.len() as i64) as usize;
            ids.insert(at, session_id.to_string());
            let changed = PaneLayoutNode {
                session_ids: Some(ids.into_iter().map(Some).collect()),
                selected_session_id: Some(session_id.into()),
                ..target.clone()
            };
            return replace(root, target.id(), &changed);
        }
        if source.session_ids().count() == 1 {
            return root.clone();
        }
    }
    let Some(remaining) = remove(root, session_id) else { return root.clone() };
    let Some(target) = groups(&remaining).into_iter().find(|g| g.id() == target_group_id).cloned() else { return root.clone() };
    if edge == "center" {
        let mut ids: Vec<String> = target.session_ids().map(str::to_string).collect();
        let at = index.unwrap_or(ids.len() as i64).clamp(0, ids.len() as i64) as usize;
        ids.insert(at, session_id.to_string());
        let changed = PaneLayoutNode {
            session_ids: Some(ids.into_iter().map(Some).collect()),
            selected_session_id: Some(session_id.into()),
            ..target.clone()
        };
        return replace(&remaining, target.id(), &changed);
    }
    let tab = tabs(vec![session_id.to_string()], None);
    let before = matches!(edge, "left" | "top");
    let axis = if matches!(edge, "left" | "right") { "horizontal" } else { "vertical" };
    let pair = if before { split(tab, target.clone(), axis, 0.5) } else { split(target.clone(), tab, axis, 0.5) };
    let result = replace(&remaining, target.id(), &pair);
    fn depth(node: &PaneLayoutNode) -> usize {
        node.children().map(|c| 1 + depth(c)).max().unwrap_or(0)
    }
    fn count(node: &PaneLayoutNode) -> usize {
        1 + node.children().map(count).sum::<usize>()
    }
    if depth(&result) >= 16 || count(&result) > 255 { root.clone() } else { result }
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
        assert_eq!(tree.kind(), "split");
        assert_eq!(tree.axis.as_deref(), Some("horizontal"));
        assert!((tree.ratio - 2.0 / 3.0).abs() < 1e-9);
        assert_eq!(tree.children().next().unwrap().axis.as_deref(), Some("vertical"));
        assert_eq!(groups(&tree).len(), 3);
        let columns = preset(&ids(4), "columns", None).unwrap();
        assert_eq!(columns.children().next().unwrap().axis.as_deref(), Some("horizontal"));
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
        let all = sessions(&fixed);
        assert_eq!(all, vec!["s2", "s3"]);
        assert_eq!(fixed.kind(), "tabs");
        assert_eq!(fixed.selected_session_id.as_deref(), Some("s3"));
    }

    #[test]
    fn normalize_keeps_valid_ids() {
        let tree = preset(&ids(2), "columns", None).unwrap();
        let again = normalize(Some(&tree), &ids(2), None).unwrap();
        assert_eq!(again.id, tree.id);
        assert_eq!(again.children().next().unwrap().id, tree.children().next().unwrap().id);
    }

    #[test]
    fn move_beside_splits_the_group() {
        let tree = preset(&ids(2), "tabs", None).unwrap();
        let group = tree.id().to_string();
        let moved = move_tab(&tree, "s2", &group, "right", None);
        assert_eq!(moved.kind(), "split");
        assert_eq!(sessions(&moved), vec!["s1", "s2"]);
    }
}
