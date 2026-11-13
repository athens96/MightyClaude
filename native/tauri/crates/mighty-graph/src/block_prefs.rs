//! Bounded remembered card sizes (C# `GraphBlockPreferences`, MightyCode.Core/GraphBlockPreferences.cs); zoom and expanded state stay
//! transient. The pane-owned normalize and the once-only move of sizes saved before the 실행 split are the state's own and live in
//! `mighty_core::normalize` (`block_sizes`, `splitting_execution`), re-exported here.

use indexmap::IndexMap;
use mighty_core::graph::{GraphBlockSize, MightyGraphRun};
use mighty_core::model::RunSession;
use mighty_core::wire;

/// `MaximumSizes`: places in a pane's saved sizes; a request and its 실행 block share one.
pub const MAXIMUM_SIZES: usize = 1_024;
/// `SplitLayout`.
pub const SPLIT_LAYOUT: i32 = mighty_core::graph::SPLIT_LAYOUT;

/// A pane's saved sizes as the state holds them.
pub type BlockSizes = IndexMap<String, Option<GraphBlockSize>>;

/// `SplittingExecution`: sizes saved before the 실행 split move once.
pub use mighty_core::normalize::splitting_execution;

/// `Normalize(values, runs)`: only nodes this pane owns, its recent turns first.
pub fn normalize_owned(values: Option<&BlockSizes>, runs: &[MightyGraphRun]) -> Option<BlockSizes> {
    mighty_core::normalize::block_sizes(values, runs)
}

/// `Clean`: an id of 1–512 UTF-16 units without a control character, and its size normalized for it.
fn clean(id: &str, size: Option<&GraphBlockSize>) -> Option<GraphBlockSize> {
    let length = wire::utf16_len(id);
    if length == 0 || length > 512 || id.chars().any(char::is_control) {
        return None;
    }
    size?.normalized_for(id)
}

/// `Normalize(values)`: the sizes that can be drawn, whoever owns them; a place holds two at most.
pub fn normalize(values: Option<&BlockSizes>) -> Option<BlockSizes> {
    let values = values?;
    let result: BlockSizes =
        values.iter().take(2 * MAXIMUM_SIZES).filter_map(|(id, size)| clean(id, size.as_ref()).map(|s| (id.clone(), Some(s)))).collect();
    (!result.is_empty()).then_some(result)
}

/// The sizes the layout draws by (`IReadOnlyDictionary<string, GraphBlockSize>`), nulls left out.
pub fn drawn(values: Option<&BlockSizes>) -> std::collections::HashMap<String, GraphBlockSize> {
    values.into_iter().flatten().filter_map(|(id, size)| size.map(|s| (id.clone(), s))).collect()
}

/// `Set`: the pane with block `id`'s size remembered, or forgotten with `None`. A .NET dictionary puts the entry added after a removal in the
/// removed one's place, so a size set again keeps its place, and one added at the bound takes the evicted first one's.
pub fn set(session: &RunSession, id: &str, size: Option<&GraphBlockSize>) -> RunSession {
    let mut next = normalize(session.graph_block_sizes.as_ref()).unwrap_or_default();
    let mut freed = next.shift_remove_full(id).map(|(at, _, _)| at);
    if let Some(normalized) = size.and_then(|s| s.normalized_for(id)) {
        if next.len() >= 2 * MAXIMUM_SIZES {
            next.shift_remove_index(0);
            freed = Some(0);
        }
        match freed {
            Some(at) if at <= next.len() => {
                next.shift_insert(at, id.to_string(), Some(normalized));
            }
            _ => {
                next.insert(id.to_string(), Some(normalized));
            }
        }
    }
    let mut pane = session.clone();
    pane.graph_block_sizes = normalize(Some(&next));
    pane
}
