//! C# `PaneLayoutVerification.Run` (its tree checks; the persistence half is the store's, held by the parity corpus): reorders,
//! merges, splits, refusals, repairs and presets of a workspace's layout.

use mighty_core::layout::{groups, move_tab, normalize, preset, resize, sessions};
use mighty_core::model::PaneLayoutNode;

fn tabs(id: &str, ids: &[&str], selected: Option<&str>) -> PaneLayoutNode {
    PaneLayoutNode {
        id: Some(id.into()),
        session_ids: Some(ids.iter().map(|s| Some(s.to_string())).collect()),
        selected_session_id: selected.map(str::to_string),
        ..PaneLayoutNode::default()
    }
}

fn split(id: &str, a: PaneLayoutNode, b: PaneLayoutNode) -> PaneLayoutNode {
    PaneLayoutNode {
        id: Some(id.into()),
        kind: Some("split".into()),
        axis: Some("horizontal".into()),
        ratio: 0.4,
        children: Some(vec![Some(a), Some(b)]),
        ..PaneLayoutNode::default()
    }
}

fn ids(list: &[&str]) -> Vec<String> {
    list.iter().map(|s| s.to_string()).collect()
}

#[test]
fn the_layout_rules_hold() {
    let one = tabs("one", &["a", "b"], Some("b"));
    let two = tabs("two", &["c"], None);
    let root = split("split", one.clone(), two.clone());
    let normalized = normalize(Some(&root), &ids(&["a", "b", "c"]), Some("b")).unwrap();
    assert_eq!(groups(&normalized)[0].selected_session_id.as_deref(), Some("b"), "the selected tab is kept");

    let reorder = move_tab(&normalized, "a", "one", "center", Some(2));
    assert_eq!(groups(&reorder)[0].session_ids().collect::<Vec<_>>(), ["b", "a"], "a forward reorder counts the removal");
    let reorder = move_tab(&reorder, "a", "one", "center", Some(0));
    assert_eq!(groups(&reorder)[0].session_ids().collect::<Vec<_>>(), ["a", "b"], "a backward reorder");

    let merged = move_tab(&normalized, "c", "one", "center", Some(1));
    assert_eq!(merged.kind(), "tabs", "the emptied split is pruned");
    assert_eq!(merged.session_ids().collect::<Vec<_>>(), ["a", "c", "b"]);
    assert_eq!(merged.selected_session_id.as_deref(), Some("c"));
    for edge in ["left", "right", "top", "bottom"] {
        let divided = move_tab(&merged, "c", "one", edge, None);
        let axis = if matches!(edge, "left" | "right") { "horizontal" } else { "vertical" };
        assert_eq!((divided.kind(), divided.axis.as_deref()), ("split", Some(axis)), "a tab of the same group splits along {edge}");
        let side = divided.children().nth(if matches!(edge, "left" | "top") { 0 } else { 1 }).unwrap();
        assert_eq!(sessions(side), ["c"], "the split's direction for {edge}");
        let mut all = sessions(&divided);
        all.sort_unstable();
        assert_eq!(all, ["a", "b", "c"], "every pane once");
    }
    assert_eq!(move_tab(&two, "c", "two", "left", None), two, "a lone tab beside its own group changes nothing");
    assert_eq!(move_tab(&root, "a", "missing", "center", None), root, "a missing target keeps the tree");
    assert_eq!(move_tab(&root, "foreign", "one", "center", None), root, "a pane from elsewhere is refused");
    assert_eq!(resize(&root, "split", 5.0).ratio, 0.85);
    assert_eq!(resize(&root, "split", -2.0).ratio, 0.15);
    assert_eq!(resize(&root, "split", f64::NAN), root, "an invalid ratio changes nothing");

    let malformed = PaneLayoutNode {
        ratio: f64::INFINITY,
        children: Some(vec![Some(tabs("one", &["a", "a", "outside"], Some("outside"))), Some(tabs("one", &["a", "c"], None))]),
        ..root.clone()
    };
    let repaired = normalize(Some(&malformed), &ids(&["a", "b", "c"]), Some("b")).unwrap();
    assert_eq!(repaired.ratio, 0.5);
    assert_eq!(sessions(&repaired), ["a", "b", "c"], "foreign and repeated tabs gone, the missing one added");
    let group_ids: std::collections::HashSet<&str> = groups(&repaired).iter().map(|g| g.id()).collect();
    assert_eq!(group_ids.len(), 2, "a repeated node id is replaced");

    let pruned = normalize(Some(&root), &ids(&["c"]), None).unwrap();
    assert_eq!((pruned.kind(), sessions(&pruned)), ("tabs", vec!["c"]), "closing tabs collapses empty groups");
    assert!(normalize(Some(&root), &[], None).is_none(), "an empty workspace has no tree");

    let mut deep = one.clone();
    for depth in 0..15 {
        deep = PaneLayoutNode {
            id: Some(format!("depth-{depth}")),
            kind: Some("split".into()),
            children: Some(vec![Some(tabs(&format!("sibling-{depth}"), &[&format!("x{depth}")], None)), Some(deep)]),
            ..PaneLayoutNode::default()
        };
    }
    assert_eq!(move_tab(&deep, "a", "one", "right", None), deep, "a split past the depth limit keeps the whole tree");

    for name in ["grid", "columns", "tabs", "focus"] {
        let made = preset(&ids(&["a", "b", "c", "d", "e"]), name, Some("e")).unwrap();
        assert_eq!(sessions(&made), ["a", "b", "c", "d", "e"], "{name} keeps every pane in order");
        if matches!(name, "tabs" | "focus") {
            assert_eq!(made.selected_session_id.as_deref(), Some("e"), "{name} selects the active pane");
        }
    }
}
