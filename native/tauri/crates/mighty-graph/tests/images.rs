//! The Mighty graph's picture preview block (C# MightyGraphImagesVerification, MightyCode.Core.Tests/MightyGraphImagesVerification.cs):
//! which steps get one, and where the layout attaches it and the other blocks that hang beside the flow. The image path rule
//! (`AgentImagePaths.Locate`) is the host's; here a stand-in reads a set of files under a workspace root as it does.

mod view_support;

use std::collections::HashSet;
use std::sync::Mutex;

use mighty_core::graph::{MightyGraphRun, node_id};
use mighty_core::model::{AgentImageRef, LogEntry};
use mighty_graph::camera::{self, GraphRect};
use mighty_graph::images::{self, AgentImageLocation, ImageLocator};
use mighty_graph::layout::{self, Gallery, Options};
use mighty_graph::{follow, view_model};
use view_support::*;

/// A workspace whose files are a set of names: `Locate` as C# reads it for a relative path inside the root, a data URI and a web address.
struct Files {
    root: String,
    names: Mutex<HashSet<String>>,
}

impl Files {
    fn new(root: &str) -> Self {
        Files { root: root.into(), names: Mutex::new(HashSet::new()) }
    }
    fn write(&self, name: &str) {
        self.names.lock().unwrap().insert(name.into());
    }
    fn delete(&self, name: &str) {
        self.names.lock().unwrap().remove(name);
    }
}

impl ImageLocator for Files {
    fn locate(&self, source: &str, root: Option<&str>) -> AgentImageLocation {
        let value = source.trim();
        if value.starts_with("data:") {
            let Some(comma) = value.find(',') else { return AgentImageLocation::Refused };
            return AgentImageLocation::Inline { media_type: "image/png".into(), base64: value[comma + 1..].into() };
        }
        if value.starts_with("http://") || value.starts_with("https://") {
            return AgentImageLocation::Remote { url: value.into() };
        }
        let picture = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"].iter().any(|e| value.to_lowercase().ends_with(e));
        if root != Some(self.root.as_str()) || value.split(['/', '\\']).any(|p| p == "..") || !picture || !self.names.lock().unwrap().contains(value) {
            return AgentImageLocation::Refused;
        }
        AgentImageLocation::File { path: format!("{}/{value}", self.root), root: self.root.clone() }
    }
}

fn markdown(text: &str) -> LogEntry {
    entry("md", "assistant", text, "2026-01-01T00:00:00Z", Some("claude"))
}

fn stored(hash: char) -> AgentImageRef {
    AgentImageRef {
        hash: Some(std::iter::repeat_n(hash, 64).collect()),
        media_type: Some("image/png".into()),
        width: 4,
        height: 4,
        bytes: 10,
        source: Some("Read".into()),
        path: None,
    }
}

fn picture(images: Vec<AgentImageRef>) -> LogEntry {
    let mut e = entry("pic", "image", "그림", "2026-01-01T00:00:00Z", Some("claude"));
    e.images = Some(images.into_iter().map(Some).collect());
    e
}

fn intersects(a: &GraphRect, b: &GraphRect) -> bool {
    a.x < b.max_x() && b.x < a.max_x() && a.y < b.max_y() && b.y < a.max_y()
}

fn gallery(run: &str, step: &str, count: usize) -> Gallery {
    Gallery { run_id: run.into(), step: step.into(), count }
}

/// "mighty images: the preview attaches to the step that produced the pictures" (TheGraphPreviewAttachesToTheStepThatProducedThePictures).
#[test]
fn the_graph_preview_attaches_to_the_step_that_produced_the_pictures() {
    let files = Files::new("C:/workspace-preview");
    files.write("plot.png");
    let picture_ref = stored('c');
    let mut child = agent("graph-child", "Look", "look", "completed");
    child.entries = Some(vec![picture(vec![picture_ref.clone(), picture_ref.clone()])]);
    let quiet = agent("graph-quiet", "Quiet", "x", "completed");
    let mut r = with_final(with_agents(run("run-1", "show", "completed"), vec![child, quiet]), "done");
    r.root_entries = Some(vec![markdown("![plot](plot.png) ![web](https://example.com/x.png) ![out](../outside.png)")]);
    let root = Some(files.root.as_str());
    let galleries = images::galleries(std::slice::from_ref(&r), root, 0, &files);
    check(
        galleries == vec![gallery("run-1", "request", 1), gallery("run-1", "agent:graph-child", 1)],
        format!("one gallery per step with pictures: {galleries:?}"),
    );
    check(
        images::items(&r, "agent:graph-child", root, &files).iter().map(|i| i.id.clone()).collect::<Vec<_>>()
            == vec![format!("hash:{}", picture_ref.hash.clone().unwrap())],
        "the agent's stored picture",
    );
    let located = images::items(&r, images::REQUEST_STEP, root, &files);
    check(located.len() == 1, "one request picture");
    let located = &located[0];
    check(
        located.id.starts_with("file:")
            && located.id.ends_with("plot.png")
            && located.stored.is_none()
            && matches!(located.located, Some(AgentImageLocation::File { .. }))
            && located.alt == "plot"
            && located.source == "plot.png",
        format!("the request's Markdown picture: {located:?}"),
    );
    check(
        images::items(&r, "agent:graph-quiet", root, &files).is_empty()
            && images::items(&r, "agent:nobody", root, &files).is_empty()
            && images::items(&r, "elsewhere", root, &files).is_empty(),
        "a step without pictures has none",
    );

    let runs = vec![r.clone()];
    let made = layout::make(&runs, "", false, &HashSet::new(), &Options { galleries: galleries.clone(), ..Options::default() }, &ko());
    let step_id = layout::node_id(&r, "agent:graph-child");
    let gallery_id = layout::node_id(&r, &format!("{}agent:graph-child", layout::IMAGES_SUFFIX));
    let step = made.node(&step_id).unwrap().clone();
    let preview = made.node(&gallery_id).unwrap().clone();
    check(
        preview.kind == layout::IMAGES_KIND && layout::is_auxiliary(&preview.kind) && camera::is_auxiliary(&gallery_id) && !layout::is_resizable_kind(&preview.kind),
        "an attachment, not a place for the camera",
    );
    check(
        preview.frame.y == step.frame.y && preview.frame.x > step.frame.max_x() && preview.frame.w == layout::IMAGES_WIDTH,
        "beside the step that produced it",
    );
    check(
        !made.edges.iter().any(|e| e.source == gallery_id || e.target == gallery_id) && made.size.w >= preview.frame.max_x() - made.origin_x,
        "no edge, and the canvas reaches it",
    );
    let plain = layout::make(&runs, "", false, &HashSet::new(), &Options::default(), &ko());
    check(plain.nodes.iter().all(|node| made.node(&node.id).unwrap().frame == node.frame), "no other card moves");
    check(
        layout::images_height(1, true) == layout::images_height(1, false) && layout::images_height(9, true) > layout::images_height(9, false),
        "unfolding grows it only with more to show",
    );
    check(
        layout::visible_images(20, false) == 6
            && layout::visible_images(20, true) == 12
            && layout::images_height(1, false) == 38.0 + 24.0 + 88.0
            && layout::images_height(9, false) == 38.0 + 24.0 + 2.0 * 88.0 + 8.0,
        "two rows folded, four unfolded",
    );
    let request = made.node(&layout::node_id(&r, &format!("{}request", layout::IMAGES_SUFFIX))).unwrap();
    check(!intersects(&request.frame, &preview.frame), "two previews never overlap");
    let block = view_model::blocks(&made, &runs, "", "Claude", true, None, None, &ko()).into_iter().find(|b| b.id == gallery_id).unwrap();
    check(
        block.kind == layout::IMAGES_KIND
            && block.record_id.as_deref() == Some("agent:graph-child")
            && block.title == ko().get("graph.images.title")
            && follow::target_id(&made, &runs) == Some(layout::node_id(&r, "result")),
        "the preview's block",
    );
}

/// "mighty images: a step's pictures are worked out once until it changes" (AStepsPicturesAreWorkedOutOnceUntilItChanges).
#[test]
fn a_steps_pictures_are_worked_out_once_until_it_changes() {
    let files = Files::new("C:/workspace-memo");
    let root = Some(files.root.as_str());
    files.write("plot.png");
    let id = format!("run-memo-{}", mighty_core::wire::new_id());
    let memo_run = |status: &str, more: Vec<LogEntry>| -> MightyGraphRun {
        let mut r = run(&id, "show", status);
        let mut entries = vec![markdown("![plot](plot.png) ![later](later.png)")];
        entries.extend(more);
        r.root_entries = Some(entries);
        r
    };
    check(images::items(&memo_run("running", vec![]), "request", root, &files).len() == 1, "the picture that is there");
    files.delete("plot.png");
    files.write("later.png");
    check(
        images::items(&memo_run("running", vec![]), "request", root, &files).len() == 1
            && images::galleries(&[memo_run("running", vec![])], root, 0, &files).first().map(|g| g.count) == Some(1),
        "an unchanged step is not looked up again",
    );
    let settled = images::items(&memo_run("completed", vec![]), "request", root, &files);
    check(settled.len() == 1 && settled[0].id.ends_with("later.png"), "a settled step is worked out again");
    check(
        images::galleries(&[memo_run("completed", vec![picture(vec![stored('a')])])], root, 0, &files).first().map(|g| g.count) == Some(2),
        "a new entry is a change",
    );
    let stream = format!("run-stream-{}", mighty_core::wire::new_id());
    files.write("first.png");
    let streaming = |status: &str, text: &str| -> MightyGraphRun {
        let mut r = run(&stream, "show", status);
        r.root_entries = Some(vec![markdown(text)]);
        r
    };
    check(images::items(&streaming("running", "![first](first.png)"), "request", root, &files).len() == 1, "the first picture as it streams in");
    files.delete("first.png");
    files.write("second.png");
    let grown = images::items(&streaming("running", "![first](first.png) and ![second](second.png)"), "request", root, &files);
    check(
        grown.len() == 2 && grown[0].id.ends_with("first.png") && grown[1].id.ends_with("second.png"),
        "a source already found is not looked up again; a new one is",
    );
    let done = images::items(&streaming("completed", "![first](first.png) and ![second](second.png)"), "request", root, &files);
    check(done.len() == 1 && done[0].id.ends_with("second.png"), "settled, every source is looked up again");
}

/// "mighty images: the attachments hang off the execution block and stack" (TheAttachmentsHangOffTheExecutionBlockAndStack).
#[test]
fn the_attachments_hang_off_the_execution_block_and_stack() {
    let r = with_final(run("one", "request", "completed"), "done");
    let runs = vec![r.clone()];
    let galleries = vec![
        gallery("one", images::REQUEST_STEP, 2),
        gallery("gone", images::REQUEST_STEP, 3),
        gallery("one", "agent:nobody", 1),
        gallery("one", images::REQUEST_STEP, 0),
    ];
    let executions: Vec<(String, String)> =
        [("one", "job-1"), ("one", "job-2"), ("gone", "job-3"), ("one", "job-1")].iter().map(|(a, b)| (a.to_string(), b.to_string())).collect();
    let expanded: HashSet<String> = HashSet::from([node_id("one", &format!("{}job-2", layout::EXECUTION_SUFFIX))]);
    let options = Options {
        plan_records: vec![("one".into(), "answered".into())],
        splits_execution: true,
        galleries: galleries.clone(),
        executions: executions.clone(),
        ..Options::default()
    };
    let made = layout::make(&runs, "", false, &expanded, &options, &ko());
    let frame = |suffix: &str| made.node(&node_id("one", suffix)).unwrap().frame;
    let (work, pictures, first, second, record) = (
        frame(layout::WORK_SUFFIX),
        frame(&format!("{}request", layout::IMAGES_SUFFIX)),
        frame(&format!("{}job-1", layout::EXECUTION_SUFFIX)),
        frame(&format!("{}job-2", layout::EXECUTION_SUFFIX)),
        frame(&format!("{}answered", layout::PLAN_RECORD_SUFFIX)),
    );
    check(
        pictures.y == work.y
            && pictures.x == work.max_x() + 8.0
            && first.y == work.y
            && first.x == pictures.max_x() + 8.0
            && first.w == layout::EXECUTION_WIDTH
            && first.h == 290.0,
        "the pictures, then the first execution, beside the 실행 block",
    );
    check(
        second.y == first.max_y() + 8.0 && second.h == layout::execution_height(true) && layout::execution_height(true) == 470.0 && record.y == second.max_y() + 8.0,
        "executions stack and the answered plan follows",
    );
    check(
        made.nodes.iter().filter(|n| n.kind == layout::EXECUTION_KIND).count() == 2 && made.nodes.iter().filter(|n| n.kind == layout::IMAGES_KIND).count() == 1,
        "a request that is gone, a step that is not there, an empty gallery and a repeated execution add nothing",
    );
    check(
        made.size.w >= first.max_x() + 24.0 - made.origin_x && made.size.h >= record.max_y() + 24.0 - made.origin_y,
        "the canvas reaches every attachment",
    );
    let plain = layout::make(&runs, "", false, &HashSet::new(), &Options { splits_execution: true, ..Options::default() }, &ko());
    check(
        plain.nodes.iter().all(|node| made.node(&node.id).unwrap().frame == node.frame) && made.edges.len() == plain.edges.len(),
        "nothing in the flow moves and no edge is added",
    );
    let execution_id = node_id("one", &format!("{}job-2", layout::EXECUTION_SUFFIX));
    let execution = view_model::blocks(&made, &runs, "", "Claude", true, None, None, &ko()).into_iter().find(|b| b.id == execution_id).unwrap();
    check(
        execution.kind == layout::EXECUTION_KIND
            && execution.record_id.as_deref() == Some("job-2")
            && camera::is_auxiliary(&execution.id)
            && !camera::is_auxiliary(&node_id("one", layout::WORK_SUFFIX)),
        "the execution's block, its key in the record slot",
    );
    let shared = layout::make(&runs, "", false, &HashSet::new(), &Options { galleries, executions, ..Options::default() }, &ko());
    let request = shared.nodes.iter().find(|n| n.kind == "request").unwrap().frame;
    check(
        shared.nodes.iter().find(|n| n.kind == layout::IMAGES_KIND).unwrap().frame.y == request.y
            && shared.nodes.iter().find(|n| n.kind == layout::EXECUTION_KIND).unwrap().frame.y == request.y,
        "without a 실행 block they hang off the request",
    );
}
