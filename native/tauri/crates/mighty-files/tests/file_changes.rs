//! C# `FileChangesVerification` (Verification.cs:1210-1215): native/contracts/fixtures/file-diff.json is the diff the Mac
//! (FileDiffTests) is held to as well; the rest are the capture, the store and the git base. The phone's route (:1216,
//! `ThePhoneAsksForChangesOnlyWithTheCapabilitysArguments`) is the phone's (W8).

mod common;

use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::Arc;

use common::{Temp, fixture, write};
use mighty_files::changes::{self, FileOriginalStore};
use mighty_files::diff::{self, Hunk};
use mighty_files::patch::{self, FileEdit, PatchHunk};
use mighty_run::parser::{OutputParser, ParserHooks};
use serde_json::{Value, json};

fn hunks_json(hunks: &[Hunk]) -> Value {
    Value::Array(hunks.iter().map(Hunk::to_json).collect())
}

/// The fixture's hunks as `Wire.Json` writes them back (noNewline only when true).
fn fixture_hunks(value: &Value) -> Value {
    Value::Array(value.as_array().unwrap().iter().map(|h| Hunk::from_json(h).expect("a fixture hunk").to_json()).collect())
}

fn generated(spec: &Value, side: &str) -> String {
    let mut text = String::new();
    for segment in spec[side].as_array().unwrap() {
        let (count, pattern, first) = (segment[0].as_u64().unwrap(), segment[1].as_str().unwrap(), segment[2].as_u64().unwrap());
        for n in first..first + count {
            text.push_str(&pattern.replace("{n}", &n.to_string()));
            text.push('\n');
        }
    }
    text
}

/// Verification.cs:1210 "file changes: the diff follows native/contracts/fixtures/file-diff.json and its caps".
#[test]
fn the_shared_cases_hold() {
    let fixture = fixture("file-diff.json");
    assert!(
        fixture["context"] == diff::CONTEXT
            && fixture["maximumSideBytes"] == diff::MAXIMUM_SIDE_BYTES
            && fixture["maximumSideLines"] == diff::MAXIMUM_SIDE_LINES
            && fixture["maximumOutputLines"] == diff::MAXIMUM_OUTPUT_LINES,
        "the fixture's caps are the engine's"
    );
    for item in fixture["cases"].as_array().unwrap() {
        let name = item["name"].as_str().unwrap();
        let (old, new) = match item.get("generate") {
            Some(spec) => (generated(spec, "old"), generated(spec, "new")),
            None => (item["old"].as_str().unwrap().to_string(), item["new"].as_str().unwrap().to_string()),
        };
        let result = diff::compute(&old, &new);
        let expect = &item["expect"];
        if expect.is_null() {
            assert!(result.is_none(), "{name}: expected no diff");
            continue;
        }
        let result = result.unwrap_or_else(|| panic!("{name}: expected a diff"));
        assert!(result.added == expect["added"] && result.removed == expect["removed"], "{name}: +{} -{}", result.added, result.removed);
        assert!(result.truncated == expect["truncated"] && result.unchanged == expect["unchanged"], "{name}: truncated, unchanged");
        match expect.get("hunks") {
            Some(hunks) => assert_eq!(hunks_json(&result.hunks), fixture_hunks(hunks), "{name}: hunks"),
            None => {
                assert_eq!(result.hunks.len() as u64, expect["hunkCount"].as_u64().unwrap(), "{name}: hunks");
                assert_eq!(
                    result.hunks.iter().map(|h| h.lines.len()).sum::<usize>() as u64,
                    expect["lineCount"].as_u64().unwrap(),
                    "{name}: lines"
                );
                let (first, hunk) = (&expect["firstHunk"], &result.hunks[0]);
                assert!(
                    hunk.old_start == first["oldStart"]
                        && hunk.old_lines == first["oldLines"]
                        && hunk.new_start == first["newStart"]
                        && hunk.new_lines == first["newLines"],
                    "{name}: first hunk @@ -{},{} +{},{}",
                    hunk.old_start,
                    hunk.old_lines,
                    hunk.new_start,
                    hunk.new_lines
                );
                let last = result.hunks.last().unwrap().lines.last().unwrap();
                assert!(last.op.to_string() == expect["lastLine"]["op"] && last.text == expect["lastLine"]["text"], "{name}: last line");
                if let Some(context) = expect.get("contextLines") {
                    let count: usize = result.hunks.iter().map(|h| h.lines.iter().filter(|l| l.op == ' ').count()).sum();
                    assert_eq!(count as u64, context.as_u64().unwrap(), "{name}: context lines");
                }
            }
        }
    }
    // Beyond the fixture: the caps, and an edit script too long for Myers' trace.
    assert!(diff::compute(&"a".repeat(diff::MAXIMUM_SIDE_BYTES + 1), "").is_none(), "a side over 1 MiB has no diff");
    assert!(diff::compute(&"x\n".repeat(diff::MAXIMUM_SIDE_LINES + 1), "").is_none(), "a side over 20,000 lines has no diff");
    let old: String = (0..3000).map(|n| format!("a{n}\n")).collect();
    let new: String = (0..3000).map(|n| format!("b{n}\n")).collect();
    let many = diff::compute(&old, &new).unwrap();
    assert!(
        many.removed == 3000
            && many.added == 3000
            && many.truncated
            && many.hunks.iter().map(|h| h.lines.len()).sum::<usize>() == diff::MAXIMUM_OUTPUT_LINES
            && many.hunks[0].lines.iter().take(3000).all(|l| l.op == '-'),
        "past the edit distance the middle reads as removed then added"
    );
    let cut = diff::cut(&diff::compute("a\nb\nc\n", "a\nB\nc\n").unwrap().hunks, 2);
    assert!(
        cut.len() == 1
            && (cut[0].old_start, cut[0].old_lines, cut[0].new_start, cut[0].new_lines) == (1, 2, 1, 1)
            && cut[0].lines.len() == 2
    );
}

/// Verification.cs:1211 "file changes: cuts and reversed structured patches follow native/contracts/fixtures/file-diff.json".
#[test]
fn the_cuts_and_reverse_patches_follow_the_fixture() {
    let fixture = fixture("file-diff.json");
    for item in fixture["cuts"].as_array().unwrap() {
        let name = item["name"].as_str().unwrap();
        let computed = diff::compute(item["old"].as_str().unwrap(), item["new"].as_str().unwrap()).unwrap();
        let cut = diff::cut(&computed.hunks, item["lines"].as_u64().unwrap() as usize);
        assert_eq!(hunks_json(&cut), fixture_hunks(&item["expect"]), "cut {name}");
    }
    for item in fixture["reversePatch"].as_array().unwrap() {
        let name = item["name"].as_str().unwrap();
        let hunks = patch::parse(&item["hunks"]).expect("the fixture's hunks parse");
        let original = patch::reverse_apply(item["current"].as_str().unwrap(), &hunks);
        assert_eq!(original.as_deref(), item["expect"].as_str(), "reverse patch {name}");
    }
    assert!(
        patch::parse(&json!([{"oldStart": 1}])).is_none() && patch::parse(&json!("x")).is_none(),
        "a malformed structuredPatch is none"
    );
}

fn store(directory: &Path, name: &str) -> FileOriginalStore {
    FileOriginalStore::new(directory.join(name), changes::MAXIMUM_BYTES, common::handles())
}

fn assistant(id: &str, tool: &str, path: &Path) -> String {
    json!({"type": "assistant", "message": {"id": format!("m-{id}"), "content": [{"type": "tool_use", "id": id, "name": tool, "input": {"file_path": path}}]}})
        .to_string()
}

fn result(id: &str, tool_use_result: Value, error: bool) -> String {
    json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": id, "is_error": error, "content": "ok"}]}, "tool_use_result": tool_use_result})
        .to_string()
}

fn patched(path: &Path, hunks: Value, old_string: &str) -> Value {
    json!({"filePath": path, "oldString": old_string, "newString": "TWO", "originalFile": null, "contentNotInModelContext": true,
        "structuredPatch": hunks, "userModified": false, "replaceAll": false})
}

fn read(path: &Path, text: &str) {
    std::fs::write(path, text).unwrap();
}

/// Verification.cs:1212 "file changes: a run keeps each file's first original, a created file reads as all added" — through the run
/// engine's own parser and the host's hook (`FileEdit::from_result`, `FileOriginalStore::capture`).
#[tokio::test]
async fn the_parser_keeps_each_files_first_original_of_the_run() {
    let directory = Temp::new("changes-parser");
    let root = directory.path().join("workspace");
    std::fs::create_dir_all(&root).unwrap();
    let file = root.join("a.txt");
    read(&file, "v3\n");
    let originals = Arc::new(store(directory.path(), "originals"));
    let (hooked, root_text) = (originals.clone(), root.to_string_lossy().into_owned());
    let hooks = ParserHooks {
        file_original: Some(Arc::new(move |path: &str, tool: &str, value: &Value| {
            hooked.capture("pane", "run-1", &root_text, path, &FileEdit::from_result(tool, value));
        })),
        ..ParserHooks::default()
    };
    let parser = OutputParser::new("claude", "ns", hooks);
    parser.parse(&assistant("t1", "Edit", &file));
    parser.parse(&result("t1", json!({"filePath": file, "originalFile": "v1\n", "structuredPatch": []}), false));
    parser.parse(&assistant("t2", "Edit", &file));
    parser.parse(&result("t2", json!({"filePath": file, "originalFile": "v2\n"}), false));
    let found = originals.find("pane", Some("run-1"), "a.txt", None).unwrap();
    assert!(found.text == "v1\n" && !found.created, "the first original of the run is kept; a later edit never replaces it");
    // A Read's echo, a failed edit and a sub-agent's edit of the transcript shape.
    for (name, text) in [("b.txt", "x\n"), ("c.txt", "x\n"), ("d.txt", "new\n")] {
        read(&root.join(name), text);
    }
    parser.parse(&assistant("t3", "Read", &root.join("b.txt")));
    parser.parse(&result("t3", json!({"filePath": root.join("b.txt"), "originalFile": "read\n"}), false));
    parser.parse(&assistant("t4", "Edit", &root.join("c.txt")));
    parser.parse(&result("t4", json!({"filePath": root.join("c.txt"), "originalFile": "failed\n"}), true));
    parser.parse(&assistant("t5", "MultiEdit", &root.join("d.txt")));
    parser.parse(
        &json!({"type": "user", "parent_tool_use_id": "agent-1", "message": {"content": [{"type": "tool_result", "tool_use_id": "t5", "content": "ok"}]},
            "toolUseResult": {"filePath": root.join("d.txt"), "originalFile": "old\n"}})
        .to_string(),
    );
    assert!(originals.find("pane", Some("run-1"), "b.txt", None).is_none(), "a Read's result is no original");
    assert!(originals.find("pane", Some("run-1"), "c.txt", None).is_none(), "a failed edit is no original");
    assert_eq!(
        originals.find("pane", Some("run-1"), "d.txt", None).map(|o| o.text).as_deref(),
        Some("old\n"),
        "a sub-agent's MultiEdit counts"
    );
    // A Write that created its file.
    read(&root.join("new.txt"), "one\ntwo\n");
    parser.parse(&assistant("t6", "Write", &root.join("new.txt")));
    parser.parse(&result(
        "t6",
        json!({"type": "create", "filePath": root.join("new.txt"), "content": "one\ntwo\n", "originalFile": null}),
        false,
    ));
    let created = originals.find("pane", Some("run-1"), "new.txt", None).unwrap();
    assert!(created.text.is_empty() && created.created, "a created file's original is empty and marked created");
    // What the CLI really sends for most edits: no originalFile, a structuredPatch to undo against the file on disk.
    let e = root.join("e.txt");
    read(&e, "one\r\nTWO\r\nthree\r\n");
    let two_to_two = json!([{"oldStart": 1, "oldLines": 3, "newStart": 1, "newLines": 3, "lines": [" one", "-two", "+TWO", " three"]}]);
    parser.parse(&assistant("t7", "Edit", &e));
    parser.parse(&result("t7", patched(&e, two_to_two.clone(), "two"), false));
    let undone = originals.find("pane", Some("run-1"), "e.txt", None).unwrap();
    assert!(undone.text == "one\r\ntwo\r\nthree\r\n" && !undone.created, "a structuredPatch is undone against the CRLF file on disk");
    read(&e, "ONE\r\nTWO\r\nthree\r\n");
    parser.parse(&assistant("t8", "Edit", &e));
    parser.parse(&result(
        "t8",
        patched(&e, json!([{"oldStart": 1, "oldLines": 1, "newStart": 1, "newLines": 1, "lines": ["-one", "+ONE"]}]), "two"),
        false,
    ));
    assert_eq!(
        originals.find("pane", Some("run-1"), "e.txt", None).unwrap().text,
        "one\r\ntwo\r\nthree\r\n",
        "a confirming later patch keeps the first"
    );
    // A patch that does not fit the file settles the path: a later edit with the text never poses as its first original.
    let f = root.join("f.txt");
    read(&f, "x\n");
    parser.parse(&assistant("t9", "Edit", &f));
    parser.parse(&result("t9", patched(&f, two_to_two.clone(), "two"), false));
    parser.parse(&assistant("t10", "Edit", &f));
    parser.parse(&result("t10", json!({"filePath": f, "originalFile": "later\n"}), false));
    assert!(
        originals.find("pane", Some("run-1"), "f.txt", None).is_none(),
        "a patch that does not fit is refused, and the path stays refused"
    );
    // A Write that updated a file without its original is unknown, never a created file.
    let g = root.join("g.txt");
    read(&g, "g\n");
    parser.parse(&assistant("t11", "Write", &g));
    parser.parse(&result("t11", json!({"type": "update", "filePath": g, "content": "g\n", "originalFile": null}), false));
    assert!(originals.find("pane", Some("run-1"), "g.txt", None).is_none(), "a Write update without its original or a patch");
    // A Write update with a structuredPatch and no original is undone like an Edit.
    let w = root.join("w.txt");
    read(&w, "w1\nW2\n");
    parser.parse(&assistant("t13", "Write", &w));
    parser.parse(&result(
        "t13",
        json!({"type": "update", "filePath": w, "content": "w1\nW2\n", "originalFile": null,
            "structuredPatch": [{"oldStart": 1, "oldLines": 2, "newStart": 1, "newLines": 2, "lines": [" w1", "-w2", "+W2"]}]}),
        false,
    ));
    let write_update = originals.find("pane", Some("run-1"), "w.txt", None).unwrap();
    assert!(write_update.text == "w1\nw2\n" && !write_update.created, "a Write update's structuredPatch is undone");
    // An Edit with an empty oldString and no patch, or one that does more than add, is unknown.
    read(&root.join("i.txt"), "i\n");
    read(&root.join("j.txt"), "J\n");
    parser.parse(&assistant("t14", "Edit", &root.join("i.txt")));
    parser.parse(&result("t14", json!({"filePath": root.join("i.txt"), "oldString": "", "newString": "i", "originalFile": null}), false));
    parser.parse(&assistant("t15", "Edit", &root.join("j.txt")));
    parser.parse(&result(
        "t15",
        patched(&root.join("j.txt"), json!([{"oldStart": 1, "oldLines": 1, "newStart": 1, "newLines": 1, "lines": ["-j", "+J"]}]), ""),
        false,
    ));
    assert!(
        originals.find("pane", Some("run-1"), "i.txt", None).is_none() && originals.find("pane", Some("run-1"), "j.txt", None).is_none()
    );
    // An Edit with an empty oldString made its file.
    let h = root.join("h.txt");
    read(&h, "made\n");
    parser.parse(&assistant("t12", "Edit", &h));
    parser.parse(&result(
        "t12",
        patched(&h, json!([{"oldStart": 0, "oldLines": 0, "newStart": 1, "newLines": 2, "lines": ["+made", "+more", "\\ No newline at end of file"]}]), ""),
        false,
    ));
    let made = originals.find("pane", Some("run-1"), "h.txt", None).unwrap();
    assert!(made.text.is_empty() && made.created, "an Edit with an empty oldString made its file");
    // Refusals are kept with the run: a restart does not forget them.
    let root_text = root.to_string_lossy().into_owned();
    assert!(!store(directory.path(), "originals").capture(
        "pane",
        "run-1",
        &root_text,
        &f.to_string_lossy(),
        &FileEdit::text(Some("again\n"))
    ));
    let processes = processes();
    let handles = common::handles();
    let created =
        changes::read(Some(&originals), processes.as_ref(), handles.as_ref(), &root_text, "new.txt", Some("pane"), None).await.unwrap();
    assert!(
        created.base == "run"
            && created.run_id.as_deref() == Some("run-1")
            && created.created == Some(true)
            && created.added == 2
            && created.removed == 0,
        "a created file reads as all added against its run"
    );
    assert!(created.hunks.len() == 1 && created.hunks[0].lines.iter().all(|l| l.op == '+'));
    let changed = changes::read(Some(&originals), processes.as_ref(), handles.as_ref(), &root_text, "a.txt", Some("pane"), Some("run-1"))
        .await
        .unwrap();
    assert!(changed.base == "run" && changed.added == 1 && changed.removed == 1);
    assert_eq!(
        changed.hunks[0].lines.iter().map(|l| format!("{}{}", l.op, l.text)).collect::<Vec<_>>(),
        ["-v1", "+v3"],
        "first original against disk"
    );
    let wire = changed.to_json();
    assert!(
        wire["base"] == "run" && wire["runId"] == "run-1" && wire.get("headSha").is_none() && wire["hunks"][0]["lines"][0]["op"] == "-",
        "the change set's wire shape"
    );
}

/// The run engine's process host where git runs (Windows), else a fake that starts nothing.
fn processes() -> Arc<dyn mighty_platform::ProcessHost> {
    #[cfg(windows)]
    {
        Arc::new(mighty_platform_windows::WindowsProcessHost::new())
    }
    #[cfg(not(windows))]
    {
        Arc::new(mighty_platform::unsupported::Unsupported)
    }
}

fn edit(old_start: usize, new_start: usize, lines: &[&str]) -> FileEdit {
    let hunk = PatchHunk {
        old_start,
        old_lines: lines.iter().filter(|l| !l.starts_with('+')).count(),
        new_start,
        new_lines: lines.iter().filter(|l| !l.starts_with('-')).count(),
        lines: lines.iter().map(|l| l.to_string()).collect(),
    };
    FileEdit { original: None, created: false, patch: Some(vec![hunk]) }
}

/// Verification.cs:1213 "file changes: a later edit already on disk never leaves a wrong original".
#[test]
fn a_later_edit_already_on_disk_never_leaves_a_wrong_original() {
    let directory = Temp::new("changes-later");
    let root = directory.path().join("workspace");
    std::fs::create_dir_all(&root).unwrap();
    let originals = store(directory.path(), "originals");
    let file = root.join("a.txt");
    let (root_text, path) = (root.to_string_lossy().into_owned(), file.to_string_lossy().into_owned());
    let original = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\n";
    let line2 = edit(1, 1, &[" l1", "-l2", "+L2", " l3", " l4", " l5"]);
    let line11 = edit(8, 8, &[" l8", " l9", " l10", "-l11", "+L11", " l12"]);
    let line11_twice = edit(8, 8, &[" l8", " l9", " l10", "-l11", "+L11a", "+L11b", " l12"]);
    let line2_again = edit(1, 1, &[" l1", "-L2", "+M2", " l3", " l4", " l5"]);
    let capture = |run: &str, change: &FileEdit| originals.capture("pane", run, &root_text, &path, change);
    let text = |run: &str| originals.find("pane", Some(run), "a.txt", None).map(|o| o.text);

    read(&file, &original.replace("l2\n", "L2\n"));
    capture("in-order", &line2);
    read(&file, &original.replace("l2\n", "L2\n").replace("l11\n", "L11\n"));
    capture("in-order", &line11);
    assert_eq!(text("in-order").as_deref(), Some(original), "edits read in order keep the first original");

    read(&file, &original.replace("l2\n", "L2\n").replace("l11\n", "L11\n"));
    capture("below", &line2);
    capture("below", &line11);
    assert!(text("below").is_none_or(|t| t == original), "a later edit below never leaves a wrong original");
    assert!(text("below").is_none(), "the original that took the later edit in is dropped");

    read(&file, &original.replace("l2\n", "M2\n").replace("l11\n", "L11a\nL11b\n"));
    capture("above", &line11_twice);
    capture("above", &edit(1, 1, &[" l1", "-l2", "+M2", " l3", " l4", " l5"]));
    assert!(text("above").is_none_or(|t| t == original), "a later edit above never leaves a wrong original");

    read(&file, &format!("l0\n{original}").replace("l11\n", "L11\n"));
    capture("shifted", &line11);
    assert!(text("shifted").is_none(), "a patch whose lines moved is refused at once");

    read(&file, &original.replace("l2\n", "L2\n").replace("l11\n", "L11\n"));
    capture("with-text", &line2);
    capture("with-text", &FileEdit { original: Some(original.replace("l2\n", "L2\n")), ..line11.clone() });
    assert!(text("with-text").is_none(), "an originalFile that is not the file the last edit left drops a rebuilt original");
    read(&file, &original.replace("l2\n", "L2\n"));
    capture("text-ok", &line2);
    read(&file, &original.replace("l2\n", "M2\n"));
    capture("text-ok", &FileEdit { original: Some(original.replace("l2\n", "L2\n")), ..line2_again });
    read(&file, &original.replace("l2\n", "N2\n"));
    capture("text-ok", &edit(1, 1, &[" l1", "-M2", "+N2", " l3", " l4", " l5"]));
    assert_eq!(text("text-ok").as_deref(), Some(original), "edits confirmed by their originalFile and their patches keep the first");

    read(&file, &original.replace("l2\n", "L2\n"));
    let second = root.join("b.txt");
    read(&second, "b\n");
    let third = root.join("c.txt");
    read(&third, "c\n");
    assert!(
        capture("unknown", &line2)
            && originals.capture("pane", "unknown", &root_text, &second.to_string_lossy(), &FileEdit::text(Some("b-kept\n")))
    );
    capture("unknown", &FileEdit::unknown());
    assert!(text("unknown").is_none() && !capture("unknown", &line2), "an unconfirmed later edit leaves the path refused");
    assert!(originals.capture("pane", "unknown", &root_text, &third.to_string_lossy(), &FileEdit::text(Some("c-kept\n"))));
    assert_eq!(originals.find("pane", Some("unknown"), "b.txt", None).map(|o| o.text).as_deref(), Some("b-kept\n"));
    assert_eq!(originals.find("pane", Some("unknown"), "c.txt", None).map(|o| o.text).as_deref(), Some("c-kept\n"), "each its own blob");
}

/// Verification.cs:1214 "file changes: the originals keep their caps, prune with the graph and survive a restart; paths stay in the
/// workspace".
#[test]
fn the_store_keeps_its_caps_and_survives_a_restart() {
    let directory = Temp::new("changes-store");
    let root = directory.path().join("workspace");
    std::fs::create_dir_all(&root).unwrap();
    let root_text = root.to_string_lossy().into_owned();
    let originals_dir = directory.path().join("originals");
    let originals = store(directory.path(), "originals");
    let touch = |name: &str| {
        let path = root.join(name);
        read(&path, "now\n");
        path.to_string_lossy().into_owned()
    };
    let text = |value: &str| FileEdit::text(Some(value));
    assert!(!originals.capture("pane", "run-1", &root_text, &touch("big.txt"), &text(&"x".repeat(changes::MAXIMUM_ORIGINAL_BYTES + 1))));
    assert!(
        !originals.capture("pane", "run-1", &root_text, &touch("big.txt"), &text("small\n")),
        "a refused path stays refused in its run"
    );
    for n in 0..changes::MAXIMUM_FILES_PER_RUN {
        assert!(originals.capture("pane", "run-2", &root_text, &touch(&format!("f{n}.txt")), &text("o\n")), "file {n} of the run is kept");
    }
    assert!(!originals.capture("pane", "run-2", &root_text, &touch("f64.txt"), &text("o\n")), "the 65th file of a run is refused");
    let megabyte = "m".repeat(changes::MAXIMUM_ORIGINAL_BYTES);
    for n in 0..16 {
        assert!(originals.capture("pane", "run-3", &root_text, &touch(&format!("m{n}.txt")), &text(&megabyte)), "megabyte {n} is kept");
    }
    assert!(!originals.capture("pane", "run-3", &root_text, &touch("m16.txt"), &text("o\n")), "a run past 16 MiB keeps nothing more");
    assert!(
        originals.capture("other", "run-1", &root_text, &touch("a.txt"), &text("o\n"))
            && originals.capture("pane", "run-4", &root_text, &touch("a.txt"), &text("first\n"))
            && originals.capture("pane", "run-5", &root_text, &touch("a.txt"), &text("second\n")),
        "runs of panes keep their own"
    );
    // Reopened as after a restart: the newest run that captured the path wins when none is named.
    let reopened = store(directory.path(), "originals");
    let newest = reopened.find("pane", None, "a.txt", None).unwrap();
    assert!(newest.run_id == "run-5" && newest.text == "second\n", "the originals survive a restart and the newest run answers");
    assert_eq!(reopened.find("pane", Some("run-4"), "a.txt", None).map(|o| o.text).as_deref(), Some("first\n"));
    assert!(reopened.find("pane", Some("run-9"), "a.txt", None).is_none() && reopened.find("nobody", None, "a.txt", None).is_none());
    let mut temporaries = Vec::new();
    find_tmp(&originals_dir, &mut temporaries);
    assert!(temporaries.is_empty(), "no temporary file is left behind");
    reopened.retain("pane", &HashSet::from(["run-4".to_string()]));
    assert_eq!(reopened.runs("pane"), ["run-4"], "runs the graph let go are dropped");
    reopened.forget("other");
    assert!(reopened.find("other", None, "a.txt", None).is_none(), "a closed pane's originals go");
    reopened.capture("gone", "run-1", &root_text, &touch("a.txt"), &text("o\n"));
    reopened.prune(&HashMap::from([("pane".to_string(), HashSet::from(["run-4".to_string(), "run-7".to_string()]))]));
    assert!(
        reopened.find("gone", None, "a.txt", None).is_none() && reopened.find("pane", Some("run-4"), "a.txt", None).is_some(),
        "a start keeps what the state names"
    );
    // Paths outside the workspace never reach the store.
    let outside = directory.path().join("outside.txt");
    read(&outside, "x");
    assert!(
        !reopened.capture("pane", "run-6", &root_text, &outside.to_string_lossy(), &text("x"))
            && !reopened.capture("pane", "run-6", &root_text, "../outside.txt", &text("x"))
            && !reopened.capture("pane", "run-6", &root_text, &root.join("missing.txt").to_string_lossy(), &text("x")),
        "a path outside the workspace, or no file, is refused"
    );
    assert!(!reopened.capture("../pane", "run-6", &root_text, &touch("a.txt"), &text("x")), "a malformed pane id is refused");
    // An original is read only under the folder it was captured in (a delegated child's worktree is another folder).
    let worktree = directory.path().join("worktree");
    write(&worktree, "a.txt", b"x\n");
    let worktree_text = worktree.to_string_lossy().into_owned();
    assert!(
        reopened.find("pane", Some("run-4"), "a.txt", Some(&root_text)).is_some()
            && reopened.find("pane", Some("run-4"), "a.txt", Some(&worktree_text)).is_none()
    );
    write(&worktree, "b.txt", b"x\n");
    assert!(
        !reopened.capture("pane", "run-4", &worktree_text, &worktree.join("b.txt").to_string_lossy(), &text("x")),
        "a run never mixes roots"
    );
    // A notice run's id stands for the run it came from.
    assert_eq!(
        reopened.find("pane", Some(&mighty_graph::notice::run_id("run-4", 1)), "a.txt", None).map(|o| o.run_id).as_deref(),
        Some("run-4")
    );
    reopened.retain("pane", &HashSet::from([mighty_graph::notice::run_id("run-4", 2)]));
    assert_eq!(reopened.runs("pane"), ["run-4"], "a kept notice run keeps the run it came from");
    // Each pane keeps the originals of its newest 20 runs.
    for n in 0..changes::MAXIMUM_RUNS_PER_PANE + 2 {
        assert!(reopened.capture("many", &format!("r{n}"), &root_text, &touch("a.txt"), &text("o\n")), "run {n} kept");
    }
    let many = reopened.runs("many");
    assert!(many.len() == changes::MAXIMUM_RUNS_PER_PANE && many[0] == format!("r{}", changes::MAXIMUM_RUNS_PER_PANE + 1));
    assert!(!many.contains(&"r0".to_string()) && !many.contains(&"r1".to_string()), "a pane keeps its newest 20 runs");
    // The whole store keeps its budget, the oldest runs going first.
    let small = FileOriginalStore::new(directory.path().join("small"), 3 * 1_000, common::handles());
    for n in 0..5 {
        assert!(small.capture(&format!("p{n}"), "run", &root_text, &touch("a.txt"), &text(&"z".repeat(1_000))), "pane {n} kept");
    }
    assert!(small.runs("p0").is_empty() && small.runs("p1").is_empty() && small.runs("p4").len() == 1 && small.runs("p2").len() == 1);
    // A crash's temporary files go at the next start.
    let stale = originals_dir.join("x.tmp");
    read(&stale, "x");
    reopened.prune(&HashMap::from([("pane".to_string(), HashSet::from(["run-4".to_string()]))]));
    let mut temporaries = Vec::new();
    find_tmp(&originals_dir, &mut temporaries);
    assert!(!stale.exists() && temporaries.is_empty(), "a start removes stale temporary files");
    let twice = mighty_core::model::RunSession { id: Some("twice".into()), ..Default::default() };
    assert!(
        FileOriginalStore::kept_runs_of([&twice, &twice]).contains_key("twice"),
        "a pane id that appears twice does not stop the start"
    );
}

fn find_tmp(folder: &Path, out: &mut Vec<std::path::PathBuf>) {
    for item in std::fs::read_dir(folder).into_iter().flatten().flatten() {
        let path = item.path();
        if path.is_dir() {
            find_tmp(&path, out);
        } else if path.extension().is_some_and(|e| e == "tmp") {
            out.push(path);
        }
    }
}

/// git in a folder, as the C# check runs it (its own author, no signing, no line-end conversion).
#[cfg(windows)]
#[allow(clippy::disallowed_methods, reason = "the check's own git, no child of the app")]
fn git(folder: &Path, arguments: &[&str]) {
    let output = std::process::Command::new("git")
        .args([
            "-c",
            "user.name=Fixture",
            "-c",
            "user.email=fixture@example.invalid",
            "-c",
            "commit.gpgsign=false",
            "-c",
            "core.autocrlf=false",
        ])
        .args(arguments)
        .current_dir(folder)
        .output()
        .expect("git runs");
    assert!(output.status.success(), "git {arguments:?}: {}", String::from_utf8_lossy(&output.stderr));
}

#[cfg(windows)]
#[allow(clippy::disallowed_methods, reason = "the check's own git, no child of the app")]
fn inside_repository(folder: &Path) -> bool {
    std::process::Command::new("git")
        .args(["rev-parse", "--is-inside-work-tree"])
        .current_dir(folder)
        .output()
        .is_ok_and(|o| o.status.success() && String::from_utf8_lossy(&o.stdout).trim() == "true")
}

/// Verification.cs:1215 "file changes: without a run original HEAD is the base, untracked all added, no repository no diff" — git runs
/// through the Windows process host (the Mac's is not written yet).
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn without_a_run_original_gits_head_is_the_base() {
    let directory = Temp::new("changes-git");
    let repository = directory.path().join("repo");
    let root = repository.join("app");
    std::fs::create_dir_all(&root).unwrap();
    git(&repository, &["init", "-q"]);
    read(&root.join("tracked.txt"), "one\ntwo\nthree\n");
    std::fs::write(root.join("binary.dat"), [1, 0, 2]).unwrap();
    git(&repository, &["add", "."]);
    git(&repository, &["commit", "-q", "-m", "fixture"]);
    read(&root.join("tracked.txt"), "one\r\nTWO\r\nthree\r\n");
    read(&root.join("untracked.txt"), "a\nb\n");
    std::fs::write(root.join("binary.dat"), [1, 0, 3]).unwrap();
    let originals = store(directory.path(), "originals");
    let (processes, handles) = (processes(), common::handles());
    let root_text = root.to_string_lossy().into_owned();
    let changes_of = |path: &'static str, session: Option<&'static str>, run: Option<&'static str>, folder: String| {
        let (processes, handles, originals) = (processes.clone(), handles.clone(), &originals);
        async move { changes::read(Some(originals), processes.as_ref(), handles.as_ref(), &folder, path, session, run).await }
    };
    let head = changes_of("tracked.txt", Some("pane"), None, root_text.clone()).await.unwrap();
    assert!(
        head.base == "head"
            && head.added == 1
            && head.removed == 1
            && head.created.is_none()
            && head.head_sha.as_ref().is_some_and(|s| s.len() == 7)
    );
    assert_eq!(head.hunks[0].lines.iter().map(|l| format!("{}{}", l.op, l.text)).collect::<Vec<_>>(), [" one", "-two", "+TWO", " three"]);
    let other_case = changes_of("TRACKED.txt", None, None, root_text.clone()).await.unwrap();
    assert!(
        other_case.base == "head" && other_case.created.is_none() && other_case.added == 1,
        "another case reads HEAD's file by the disk's spelling"
    );
    assert!(
        changes::not_in_head("fatal: path 'app/a.txt' does not exist in 'HEAD'")
            && changes::not_in_head("fatal: path 'a.txt' exists on disk, but not in 'HEAD'")
    );
    assert!(
        !changes::not_in_head("fatal: not a valid object name") && !changes::not_in_head(""),
        "only git's not-in-HEAD words make a file untracked"
    );
    let untracked = changes_of("untracked.txt", None, None, root_text.clone()).await.unwrap();
    assert!(
        untracked.base == "head" && untracked.created == Some(true) && untracked.added == 2 && untracked.removed == 0,
        "an untracked file reads as all added"
    );
    assert!(changes_of("binary.dat", None, None, root_text.clone()).await.is_none(), "a binary file has no diff");
    assert!(
        changes_of("../outside.txt", None, None, root_text.clone()).await.is_none()
            && changes_of("missing.txt", None, None, root_text.clone()).await.is_none()
    );
    // A run's original wins over HEAD, and a named run that captured nothing falls back to HEAD.
    originals.capture("pane", "run-1", &root_text, &root.join("tracked.txt").to_string_lossy(), &FileEdit::text(Some("one\nthree\n")));
    let run = changes_of("tracked.txt", Some("pane"), None, root_text.clone()).await.unwrap();
    assert!(run.base == "run" && run.added == 1 && run.removed == 0, "the run's original wins over HEAD");
    assert_eq!(
        changes_of("tracked.txt", Some("pane"), Some("run-2"), root_text.clone()).await.unwrap().base,
        "head",
        "a run that captured nothing"
    );
    let unborn = directory.path().join("unborn");
    std::fs::create_dir_all(&unborn).unwrap();
    git(&unborn, &["init", "-q"]);
    read(&unborn.join("a.txt"), "x\n");
    let fresh = changes_of("a.txt", None, None, unborn.to_string_lossy().into_owned()).await.unwrap();
    assert!(
        fresh.base == "head" && fresh.head_sha.is_none() && fresh.created == Some(true) && fresh.added == 1,
        "no commit yet reads as all added"
    );
    let plain = directory.path().join("plain");
    write(&plain, "a.txt", b"x\n");
    // The temporary folder is in no repository on a usual machine; one under a checkout cannot show this.
    if !inside_repository(&plain) {
        let plain_text = plain.to_string_lossy().into_owned();
        assert!(changes_of("a.txt", None, None, plain_text.clone()).await.is_none(), "a folder in no repository has no diff");
        assert!(changes::git_head(processes.as_ref(), &plain_text, "a.txt", diff::MAXIMUM_SIDE_BYTES).await.is_none());
    }
}
