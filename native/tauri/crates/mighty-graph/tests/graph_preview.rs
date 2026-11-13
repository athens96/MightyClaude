//! Remembered block sizes (C# GraphPreviewVerification, MightyCode.Core.Tests/GraphPreviewVerification.cs). Its other check,
//! LocalHtmlReadsOnlyItsOwnBoundedDirectory, is the HTML preview's (`LocalHtmlDocument`), not the graph's.

mod view_support;

use std::collections::HashSet;

use indexmap::IndexMap;
use mighty_core::Wire;
use mighty_core::graph::{GraphBlockSize, node_id};
use mighty_core::json::JsonOut;
use mighty_core::model::RunSession;
use mighty_graph::block_prefs;
use mighty_graph::layout::{self, Options};
use view_support::*;

/// "GraphPreviewVerification RememberedGraphSizesDriveLayoutAndNormalize".
#[test]
fn remembered_graph_sizes_drive_layout_and_normalize() {
    let r = run("run", "request", "completed");
    let request = node_id(r.id(), "request");
    let pane = block_prefs::set(&RunSession::default(), &request, Some(&GraphBlockSize { width: 777.0, height: 333.0 }));
    let options = Options { block_sizes: Some(block_prefs::drawn(pane.graph_block_sizes.as_ref())), ..Options::default() };
    let made = layout::make(&[r], "", false, &HashSet::new(), &options, &ko());
    let frame = made.node(&request).unwrap().frame;
    check(frame.w == 777.0 && frame.h == 333.0, "saved dimensions control graph layout and edge positions");
    let mut out = JsonOut::new();
    pane.write(&mut out);
    let restored = RunSession::read(&serde_json::from_str(&out.finish()).unwrap()).unwrap();
    check(
        restored.graph_block_sizes.as_ref().and_then(|s| s.get(&request).copied().flatten())
            == Some(GraphBlockSize { width: 777.0, height: 333.0 }),
        "custom sizes survive state serialization",
    );
    let pane = block_prefs::set(&pane, &request, None);
    check(pane.graph_block_sizes.is_none(), "reset removes the remembered override");
    let corrupt: IndexMap<String, Option<GraphBlockSize>> = IndexMap::from([
        (request.clone(), Some(GraphBlockSize { width: f64::NAN, height: 100.0 })),
        ("other".to_string(), Some(GraphBlockSize { width: 1.0, height: 99999.0 })),
    ]);
    let normalized = block_prefs::normalize(Some(&corrupt));
    check(
        normalized
            .as_ref()
            .is_some_and(|n| n.len() == 1 && n.get("other").copied().flatten() == Some(GraphBlockSize { width: 300.0, height: 1200.0 })),
        "corrupt saved preferences never reach layout",
    );
}
