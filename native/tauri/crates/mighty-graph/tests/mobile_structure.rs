//! `mighty.structure` against C# `MobileStructureVerification` (native/windows/MightyCode.Core.Tests/MobileStructureVerification.cs,
//! registered in Verification.cs:461-465): the desktop's structure view as the phone receives it, bounded for the wire, and the cache that
//! builds it only when the structure moved. The session route that sends it (MobileDesktopRouter) is not this crate's.

mod common;

use std::collections::HashSet;
use std::sync::Arc;

use common::*;
use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::json::Wire;
use mighty_graph::structure::mobile::{self, Built, MobileStructure, MobileStructureCache, Naming};
use mighty_graph::structure::{self, Inputs, StructureStyle, word};
use serde_json::Value;
use unicode_segmentation::UnicodeSegmentation;

fn bytes(text: Option<&str>) -> usize {
    text.map_or(usize::MAX, str::len)
}

fn json(value: &impl Wire) -> String {
    value.to_json()
}

fn free(runs: &[MightyGraphRun]) -> MobileStructure {
    mobile::make(&structure::make(runs, &Inputs::default(), None, &ko()), runs.len(), None, None, None, None).expect("a structure")
}

fn styled<'a>(style: &'a ManifestStyle, current: &str) -> Inputs<'a> {
    Inputs { style: Some(style as &dyn StructureStyle), current_phase: Some(phase(style, current)), ..Inputs::default() }
}

#[test]
fn carries_the_desktop_shape() {
    let flow = style("ouroboros");
    let mut runs = vec![
        asked("r1", "ooo interview app"),
        asked("r2", "more questions"),
        run(
            "r3",
            Some("ooo seed"),
            "running",
            vec![agent("a1", None, "running", None), agent("a2", Some("a1"), "completed", None)],
            None,
            None,
        ),
    ];
    runs[2].root_entries = Some(vec![said("m", "writing the seed"), read("t", "seed.yaml")]);
    let shape = structure::make(&runs, &styled(&flow, "seed"), None, &ko());
    let title = |id: &str| (id == "r3").then(|| "Seed".to_string());
    let wire = mobile::make(&shape, runs.len(), Some(&title), None, None, None).expect("a structure");
    assert!(
        wire.v == 1 && wire.shape == "phases" && wire.rows.is_none() && wire.omitted.is_none(),
        "a styled pane travels as versioned phases"
    );
    let columns = wire.columns.as_ref().unwrap();
    let desktop = shape.columns.as_ref().unwrap();
    assert!(
        columns.iter().map(|c| c.id.clone()).collect::<Vec<_>>() == desktop.iter().map(|c| c.id().to_string()).collect::<Vec<_>>()
            && columns.iter().map(|c| c.title.clone()).collect::<Vec<_>>()
                == desktop.iter().map(|c| c.phase.title.clone()).collect::<Vec<_>>()
            && columns.iter().map(|c| c.step.clone()).collect::<Vec<_>>()
                == desktop.iter().map(|c| word(c.step).to_string()).collect::<Vec<_>>(),
        "the phone gets the desktop's columns and their steps"
    );
    assert_eq!(
        columns.iter().map(|c| c.items.iter().map(|i| i.run_id.clone()).collect::<Vec<_>>()).collect::<Vec<_>>(),
        desktop.iter().map(|c| c.items.iter().map(|i| i.run_id.clone()).collect::<Vec<_>>()).collect::<Vec<_>>(),
        "each request stands where the desktop drew it"
    );
    let card = &columns.iter().find(|c| c.id == "seed").unwrap().items[0];
    let source = &desktop.iter().find(|c| c.id() == "seed").unwrap().items[0];
    assert!(
        card.ordinal == 3
            && card.title.as_deref() == Some("Seed")
            && card.status == source.status
            && card.finished == source.finished
            && card.request.as_deref() == source.request.as_ref().map(|r| r.text.as_str())
            && card.message.as_deref() == source.message.as_ref().map(|m| m.text.as_str()),
        "a card carries its number, title, status and texts"
    );
    assert!(
        card.activity.iter().map(|a| a.text.clone()).collect::<Vec<_>>()
            == source.activity.iter().map(|a| a.text.clone()).collect::<Vec<_>>()
            && card.tally.agents as usize == source.tally.agents
            && card.tally.tools as usize == source.tally.tools,
        "a card carries its tool calls and tally"
    );
    assert!(
        card.clusters.iter().map(|c| c.nodes.iter().map(|n| n.id.clone()).collect::<Vec<_>>()).collect::<Vec<_>>() == [["a1", "a2"]]
            && card.clusters[0].nodes.iter().map(|n| n.depth).collect::<Vec<_>>() == [0, 1]
            && columns.iter().find(|c| c.id == "interview").unwrap().items[0].title.is_none(),
        "a card carries its sub-agent trees; an unnamed request has no title"
    );

    let free_runs = [
        run("done", None, "completed", vec![], Some("All done\n\n```swift\nlet x = 1\n```"), None),
        status("failed", "error"),
        status("live", "running"),
    ];
    let sent = free(&free_runs);
    let rows = sent.rows.as_ref().unwrap();
    let drawn_shape = structure::make(&free_runs, &Inputs::default(), None, &ko());
    let drawn = drawn_shape.rows.as_ref().unwrap();
    assert!(
        sent.shape == "free"
            && sent.columns.is_none()
            && rows.iter().map(|r| r.item.run_id.clone()).collect::<Vec<_>>()
                == drawn.iter().map(|r| r.item.run_id.clone()).collect::<Vec<_>>()
            && rows.iter().map(|r| r.item.ordinal).collect::<Vec<_>>() == [1, 2, 3],
        "a free pane travels as the desktop's rows"
    );
    assert!(
        rows.iter().map(|r| format!("{}{}{}", r.request, r.execution, r.result)).collect::<Vec<_>>()
            == drawn.iter().map(|r| format!("{}{}{}", word(r.request), word(r.execution), word(r.result))).collect::<Vec<_>>()
            && rows[0].item.answer.as_deref() == drawn[0].item.answer.as_ref().map(|a| a.text.as_str()),
        "a row carries its three steps and its answer"
    );

    let compact = free(&[done("r1")]);
    let root: Value = serde_json::from_str(&json(&compact)).unwrap();
    assert!(
        root["v"] == 1 && root["shape"] == "free" && root.get("columns").is_none() && root.get("omitted").is_none(),
        "the wire shape is versioned and leaves out what is absent"
    );
    let row = &root["rows"][0];
    let mut keys: Vec<&str> = row.as_object().unwrap().keys().map(String::as_str).collect();
    keys.sort();
    assert!(
        keys == ["execution", "item", "request", "result"] && row["item"]["runId"] == "r1" && row["item"].get("omittedNodes").is_none(),
        "a row is its item and three steps"
    );
    assert_eq!(json(&MobileStructure::read(&root).unwrap()), json(&compact), "the wire shape reads back as it was written");

    assert!(
        mobile::make(&structure::make(&[], &Inputs::default(), None, &ko()), 0, None, None, None, None).is_none(),
        "nothing to draw is no structure"
    );
    let mut cache = MobileStructureCache::new();
    assert_eq!(
        *cache.structure("p", &[], &Inputs::default(), &Naming::default(), &ko()),
        Built { value: None, tag: 0 },
        "nothing to draw has the tag 0"
    );
    // A style with phases still shows its bar before the first request.
    let empty = cache.structure("q", &[], &styled(&flow, "goal"), &Naming::default(), &ko());
    let bar = empty.value.as_ref().and_then(|v| v.columns.as_ref()).expect("columns");
    assert!(
        !bar.is_empty() && bar.iter().all(|c| c.items.is_empty()) && bar[0].step == "current",
        "a style's phases travel before the first request"
    );
}

/// native/contracts/fixtures/mobile-structure.json, which the Mac's and the phone's tests read too: every field the phone reads is one
/// this type writes, and nothing more.
#[test]
fn shared_fixture_is_the_wire_shape() {
    let path = repo().join("native/contracts/fixtures/mobile-structure.json");
    let fixture: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    for name in ["styled", "free"] {
        let raw = fixture.get(name).unwrap_or_else(|| panic!("the fixture has no {name}"));
        let decoded = MobileStructure::read(raw).unwrap_or_else(|e| panic!("{name} does not decode: {e}"));
        let written: Value = serde_json::from_str(&json(&decoded)).unwrap();
        assert!(written == *raw, "{name} changed through the wire type: {}", json(&decoded));
    }
}

#[test]
fn bounds_and_budget() {
    let text = "가나다라 ".repeat(2_000);
    let worker =
        MightyGraphAgent { input: Some(text.clone()), entries: Some(vec![said("o", &text), read("t", &"x".repeat(900))]), ..sub("a") };
    let mut live = run("r", Some(&text), "running", vec![worker], None, None);
    live.root_entries = Some(vec![said("m", &text)]);
    let sent = free(&[live]);
    let item = &sent.rows.as_ref().unwrap()[0].item;
    let node = &item.clusters[0].nodes[0];
    assert!(
        bytes(item.request.as_deref()) <= mobile::REQUEST_BYTES
            && item.request.as_deref().unwrap().ends_with('…')
            && bytes(item.message.as_deref()) <= mobile::MESSAGE_BYTES,
        "a request and a message are capped under the desktop's bounds"
    );
    assert!(
        bytes(node.prompt.as_deref()) <= mobile::PROMPT_BYTES
            && bytes(node.output.as_deref()) <= mobile::OUTPUT_BYTES
            && bytes(node.activity.as_ref().map(|a| a.text.as_str())) <= mobile::ACTIVITY_BYTES,
        "a sub-agent's texts are capped"
    );
    // The answer is Markdown: a fence the cut leaves open is closed inside the cap.
    let fenced = free(&[run("d", None, "completed", vec![], Some(&format!("intro\n```\n{}", "code line\n".repeat(1_000))), None)]);
    let answer = fenced.rows.as_ref().unwrap()[0].item.answer.clone().unwrap();
    assert!(
        answer.len() <= mobile::ANSWER_BYTES && answer.ends_with("\n```") && structure::open_fence(&answer).is_none(),
        "a cut answer closes the fence it leaves open, inside its cap"
    );

    let many = free(&(1..=25).map(|i| done(&format!("r{i}"))).collect::<Vec<_>>());
    let newest = many.rows.as_ref().unwrap();
    assert!(
        newest.len() == mobile::MAXIMUM_ITEMS
            && newest[0].item.run_id == "r6"
            && newest[0].item.ordinal == 6
            && newest.last().unwrap().item.run_id == "r25"
            && many.omitted == Some(5),
        "only the newest requests travel, the rest counted"
    );

    // One chain eight deep, then wide siblings: 8 + 70 blocks.
    let chain: Vec<String> = (0..8).map(|i| format!("c{i}")).collect();
    let mut wide: Vec<MightyGraphAgent> =
        (0..8).map(|i| agent(&chain[i], if i == 0 { None } else { Some(&chain[i - 1]) }, "completed", None)).collect();
    wide.extend((0..70).map(|i| sub(&format!("w{i}"))));
    let crowded_sent = free(&[run("r", None, "completed", wide, None, None)]);
    let crowded = &crowded_sent.rows.as_ref().unwrap()[0].item;
    let nodes: Vec<_> = crowded.clusters.iter().flat_map(|c| &c.nodes).collect();
    assert!(
        nodes.len() == mobile::MAXIMUM_NODES
            && crowded.omitted_nodes == Some(78 - mobile::MAXIMUM_NODES as i32)
            && nodes.iter().map(|n| n.depth).max() == Some(mobile::MAXIMUM_DEPTH),
        "a request carries at most sixty nodes"
    );
    // The tally still counts the whole request.
    assert!(
        nodes.iter().take(8).map(|n| n.depth).collect::<Vec<_>>() == [0, 1, 2, 3, 4, 5, 6, 6] && crowded.tally.agents == 78,
        "a deep node is drawn at the depth cap; the tally counts the whole request"
    );

    // Each request near the per-text caps with sixty full nodes: far over the budget together.
    let quoted = "\"quoted\" ".repeat(600);
    let heavy = |id: &str| {
        let blocks = (0..60)
            .map(|i| MightyGraphAgent {
                input: Some(quoted.clone()),
                entries: Some(vec![said(&format!("o{i}"), &quoted)]),
                ..sub(&format!("{id}-{i}"))
            })
            .collect();
        run(id, Some(&quoted), "completed", blocks, Some(&quoted), None)
    };
    let six: Vec<MightyGraphRun> = (1..=6).map(|i| heavy(&format!("r{i}"))).collect();
    let budgeted = free(&six);
    let carried = budgeted.rows.as_ref().unwrap();
    let size = json(&budgeted).len();
    assert!(
        size <= mobile::BUDGET_BYTES && carried.last().unwrap().item.run_id == "r6" && carried.len() < six.len(),
        "the whole value stays under the budget: {size}"
    );
    assert!(
        carried.iter().map(|r| r.item.run_id.clone()).collect::<Vec<_>>()
            == six[six.len() - carried.len()..].iter().map(|r| r.id().to_string()).collect::<Vec<_>>()
            && budgeted.omitted == Some((six.len() - carried.len()) as i32),
        "the budget drops older requests first"
    );

    let trees = mobile::item_for(
        &structure::item_for(
            &run("r", None, "completed", vec![sub("a"), agent("b", Some("a"), "completed", None), sub("c")], None, None),
            0,
            None,
            None,
            &ko(),
        ),
        None,
        None,
        None,
    );
    let ids = |item: &mobile::MobileItem| {
        item.clusters.iter().map(|c| c.nodes.iter().map(|n| n.id.clone()).collect::<Vec<_>>()).collect::<Vec<_>>()
    };
    assert_eq!(ids(&trees), [vec!["a", "b"], vec!["c"]], "a request's trees travel root first");
    let two = mobile::keeping(&trees, 2);
    let none = mobile::keeping(&trees, 0);
    assert!(
        ids(&two) == [vec!["a", "b"]]
            && two.omitted_nodes == Some(1)
            && none.clusters.is_empty()
            && none.omitted_nodes == Some(3)
            && json(&mobile::keeping(&trees, 9)) == json(&trees),
        "a request keeps its first nodes, the rest counted as left out"
    );
    // One request whose sixty nodes alone are past the budget: it travels, trimmed to fit.
    let escaped = "\"\\".repeat(2_000);
    let blocks = (0..60)
        .map(|i| MightyGraphAgent {
            title: Some(escaped.clone()),
            input: Some(escaped.clone()),
            entries: Some(vec![said(&format!("o{i}"), &escaped), read(&format!("t{i}"), &escaped)]),
            ..sub(&format!("n{i}"))
        })
        .collect();
    let fitted = free(&[run("big", Some(&escaped), "completed", blocks, Some(&escaped), None)]);
    let sent = &fitted.rows.as_ref().unwrap()[0].item;
    let count: usize = sent.clusters.iter().map(|c| c.nodes.len()).sum();
    assert!(
        json(&fitted).len() <= mobile::BUDGET_BYTES && count > 0 && count < 60 && sent.omitted_nodes == Some(60 - count as i32),
        "a request over the budget alone keeps its first nodes: {count}"
    );

    let same = free(&[done("same"), done("other"), asked("same", "again")]);
    let same_rows = same.rows.as_ref().unwrap();
    assert!(
        same_rows.iter().map(|r| r.item.run_id.as_str()).collect::<Vec<_>>() == ["other", "same"]
            && same_rows.last().unwrap().item.ordinal == 3
            && same.omitted.is_none(),
        "a repeated run id travels once, as its newest request"
    );

    // A family emoji is one character of many scalars: never split.
    let family = "👨‍👩‍👧‍👦";
    let cut = mobile::capped(&family.repeat(40), 100, false, false);
    let elements: Vec<&str> = cut.graphemes(true).collect();
    assert!(
        cut.len() <= 100 && cut.ends_with('…') && elements.len() > 1 && elements[..elements.len() - 1].iter().all(|e| *e == family),
        "a cut falls between characters"
    );
    // A long fence early on: the shorter second cut still ends inside it, and its close fits.
    let fence = "`".repeat(40);
    let closed = mobile::capped(&format!("intro\n{fence}\n{}", "code\n".repeat(200)), 300, false, true);
    assert!(
        closed.len() <= 300 && closed.ends_with(&format!("\n{fence}")) && structure::open_fence(&closed).is_none(),
        "a cut closes the fence it leaves open, inside the cap"
    );
}

#[test]
fn cache_rebuilds_only_when_the_structure_moves() {
    let ko = ko();
    let plain = Naming::default();
    let mut cache = MobileStructureCache::new();
    let mut live = status("r", "running");
    live.root_entries = Some(vec![said("m", "reading")]);
    let first = cache.structure("p", std::slice::from_ref(&live), &Inputs::default(), &plain, &ko);
    assert!(
        cache.builds() == 1 && first.tag == mobile::tag(first.value.as_ref()) && (1..=999_999_999_999).contains(&first.tag),
        "a built value carries its tag"
    );
    // A streamed token that moves nothing drawn reuses the value.
    live.root_entries.as_mut().unwrap()[0].text = Some("reading the file".into());
    assert!(
        Arc::ptr_eq(&cache.structure("p", std::slice::from_ref(&live), &Inputs::default(), &plain, &ko), &first) && cache.builds() == 1,
        "a streamed token that moves nothing drawn reuses the value"
    );
    // A new block moves the structure.
    live.agents.as_mut().unwrap().push(sub("a"));
    let moved = cache.structure("p", std::slice::from_ref(&live), &Inputs::default(), &plain, &ko);
    assert!(
        moved.value.as_ref().and_then(|v| v.rows.as_ref()).map(|r| r[0].item.clusters.len()) == Some(1) && cache.builds() == 2,
        "a new block moves the structure"
    );
    // So does the style's key (its request titles), and another pane has its own.
    let other = Naming { key: "other", ..Naming::default() };
    cache.structure("p", std::slice::from_ref(&live), &Inputs::default(), &other, &ko);
    assert_eq!(cache.builds(), 3, "another key rebuilds");
    cache.structure("q", std::slice::from_ref(&live), &Inputs::default(), &plain, &ko);
    assert_eq!(cache.builds(), 4, "another pane has its own value");
    cache.retain(&HashSet::from(["q".to_string()]));
    cache.structure("p", std::slice::from_ref(&live), &Inputs::default(), &other, &ko);
    assert_eq!(cache.builds(), 5, "a forgotten pane is built again");

    let plan = style("claude-plan");
    let mut plans = MobileStructureCache::new();
    let runs = [run("r1", None, "completed", vec![], None, Some("s1"))];
    let planning = plans.structure("p", &runs, &styled(&plan, "plan"), &plain, &ko).value.clone();
    let history = [record("p1", "s1", "approvedAuto")];
    let approved =
        plans.structure("p", &runs, &Inputs { plan_history: Some(&history), ..styled(&plan, "execute") }, &plain, &ko).value.clone();
    assert!(
        planning.is_some()
            && approved.is_some()
            && json(planning.as_ref().unwrap()) != json(approved.as_ref().unwrap())
            && plans.builds() == 2,
        "the pane's phase and plans move the structure"
    );
    let execute = approved.unwrap().columns.unwrap().into_iter().find(|c| c.id == "execute").unwrap();
    assert_eq!(
        execute.items.iter().map(|i| i.run_id.as_str()).collect::<Vec<_>>(),
        ["r1"],
        "an approved plan's request travels under the executing phase"
    );
}

/// Not a C# check: the tag and the wire bytes are C#'s own. The numbers were read from MightyCode.Core (`MobileStructure.Tag` and
/// `JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json)`) for the shared fixture's two values and a value of escaped text.
#[test]
fn the_tag_and_the_bytes_are_csharps() {
    let path = repo().join("native/contracts/fixtures/mobile-structure.json");
    let fixture: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    for (name, tag, size) in [("styled", 573_448_318_928, 1_359), ("free", 554_612_256_375, 739)] {
        let decoded = MobileStructure::read(&fixture[name]).unwrap();
        assert!(mobile::tag(Some(&decoded)) == tag && json(&decoded).len() == size, "{name}: the tag and the size C# writes");
    }
    let korean = r#"{"v":1,"shape":"free","rows":[{"item":{"runId":"r1","ordinal":1,"title":"요청 <1> & 'a' + \"b\"","status":"completed","finished":true,"request":"한글 👨‍👩‍👧 \u0001 `x`","activity":[],"tally":{"agents":0,"tools":0},"clusters":[]},"request":"done","execution":"done","result":"done"}]}"#;
    let decoded = MobileStructure::read(&serde_json::from_str(korean).unwrap()).unwrap();
    let written = json(&decoded);
    // C# writes 389 bytes, every non-ASCII character and `<>&'+"` and the backtick escaped; the tag is a digest of those bytes.
    assert!(
        written.is_ascii() && written.len() == 389 && !written.contains(['<', '>', '&', '\'', '+', '`']),
        "escaped as Wire.Json escapes: {written}"
    );
    assert_eq!(mobile::tag(Some(&decoded)), 696_344_438_840, "the tag of escaped text");
}

// C# `RouteSendsTheStructureOnlyWhenAskedAndChanged` (Verification.cs:465) is not ported here: it drives `MobileDesktopRouter` over a
// `DesktopService` with a state folder on disk, the phone route's (mighty-remote's) to port.
