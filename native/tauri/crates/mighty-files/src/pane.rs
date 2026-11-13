//! The files pane in the pane list (C# `FilePaneKind`, WorkspaceFiles.cs:15-65): a pane of its own kind that runs nothing, one per
//! workspace (so its id follows from the workspace's), never saved (mighty-core's normalize and `stored` leave it out), placed left of
//! the group holding the current pane.

use mighty_core::layout;
use mighty_core::model::PaneLayoutNode;

/// C# `FilePaneKind.Kind`.
pub const KIND: &str = "files";
/// C# `FilePaneKind.Shortcut`: macOS ⇧⌘E with ⌘ mapped to Ctrl.
pub const SHORTCUT: &str = "Ctrl+Shift+E";
/// C# `PaneLayout.MaximumSessions`: the 129th pane is refused.
pub const MAXIMUM_SESSIONS: usize = 128;

/// C# `FilePaneKind.PaneId`: one files pane per workspace.
pub fn pane_id(workspace_id: &str) -> String {
    format!("{KIND}:{workspace_id}")
}

/// C# `FilePaneKind.IsFilePane`.
pub fn is_file_pane(kind: Option<&str>) -> bool {
    kind == Some(KIND)
}

/// C# `PaneLayout.HasRoom`: whether one more pane may be added.
pub fn has_room(session_count: usize) -> bool {
    session_count < MAXIMUM_SESSIONS
}

/// C# `FilePaneKind.Place`: where a newly added files pane goes: left of the group holding the current pane (as on macOS), or a tab
/// in that group when the layout has no room for a split.
pub fn place(tree: &PaneLayoutNode, pane_id: &str, current: Option<&str>) -> PaneLayoutNode {
    let groups = layout::groups(tree);
    let target = groups
        .iter()
        .find(|g| current.is_some_and(|c| c != pane_id && g.session_ids().any(|id| id == c)))
        .or_else(|| groups.iter().find(|g| g.session_ids().any(|id| id != pane_id)));
    let Some(target) = target else { return tree.clone() };
    let split = layout::move_tab(tree, pane_id, target.id(), "left", None);
    let alone = layout::groups(&split).iter().any(|g| {
        let ids: Vec<&str> = g.session_ids().collect();
        ids == [pane_id]
    });
    if alone { split } else { layout::move_tab(tree, pane_id, target.id(), "center", None) }
}
