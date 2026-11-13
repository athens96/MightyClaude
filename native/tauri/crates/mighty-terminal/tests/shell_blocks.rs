//! C# ShellBlockRunVerification.cs (the page's half is native/windows/web/src/conversation/shellBlocks.test.ts), under the names
//! Verification.cs registers: a transcript's shell block run — its key, its record's states, the saved field and its restore, the
//! output's cleaning and limit, and the host's check that the command is the one the reply shows. The service half ("the service
//! keeps the record with its pane across a relaunch") is `update_pane` here and in mighty-run's `tests/pane_updates.rs`.

use std::sync::Arc;

use mighty_core::Wire;
use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AppSnapshot, LogEntry, RunSession, ShellBlockRun, Workspace};
use mighty_core::normalize::normalize;
use mighty_terminal::shell_blocks::{
    self, MAXIMUM_RUNS, OUTPUT_LIMIT_BYTES, ShellBlockOutput, command, entry_id, finished, is_command, is_key, replace, runnable, shown,
    started, tail, update_pane,
};
use serde_json::{Value, json};

/// "shell block runs: the key is the entry and the block's place".
#[test]
fn the_key_is_the_entry_and_the_blocks_place() {
    assert!(entry_id(Some("entry-1#0")) == Some("entry-1") && entry_id(Some("a#b#12")).is_none(), "entry id and index");
    assert_eq!(entry_id(Some("e#3")), Some("e"), "a later block");
    for bad in [None, Some(""), Some("#0"), Some("e#"), Some("e#x"), Some("e#-1"), Some("e#12345"), Some("../e#0"), Some("e 1#0")] {
        assert!(!is_key(bad), "refused: {bad:?}");
    }
    assert!(
        is_command(Some("echo hi"))
            && !is_command(Some("  \n"))
            && !is_command(Some("a\0b"))
            && !is_command(Some(&"x".repeat(65_537)))
            && !is_command(None),
        "commands"
    );
}

/// "shell block runs: a record moves from running to completed, failed or stopped, and running again replaces it".
#[test]
fn the_record_moves_from_running_to_its_end() {
    let run = started("e#0", "ls", Some("t0"));
    assert!(
        run.state.as_deref() == Some("running")
            && run.exit_code.is_none()
            && run.output.as_deref() == Some("")
            && run.ended_at.is_none()
            && run.started_at.as_deref() == Some("t0"),
        "started"
    );
    let completed = finished(&run, Some(0), false, Some("t1"));
    assert!(
        completed.state.as_deref() == Some("completed") && completed.exit_code == Some(0) && completed.ended_at.as_deref() == Some("t1"),
        "exit 0 completes"
    );
    let failed = finished(&run, Some(2), false, None);
    assert!(failed.state.as_deref() == Some("failed") && failed.exit_code == Some(2), "another code fails");
    let signal = finished(&run, None, false, None);
    assert!(signal.state.as_deref() == Some("failed") && signal.exit_code.is_none(), "no code (a signal) fails");
    let stopped = finished(&run, Some(1), true, None);
    assert!(stopped.state.as_deref() == Some("stopped") && stopped.exit_code == Some(1), "the user's stop is stopped, whatever the code");
    let done = finished(&run, Some(0), false, None);
    assert_eq!(finished(&done, Some(1), false, None), done, "an ended record does not end again");

    let mut runs = replace(None, done);
    runs = replace(Some(&runs), started("e#1", "pwd", None));
    runs = replace(Some(&runs), started("e#0", "ls", None));
    assert!(
        runs.len() == 2
            && runs[0].key.as_deref() == Some("e#0")
            && runs[0].state.as_deref() == Some("running")
            && runs[1].key.as_deref() == Some("e#1"),
        "running again replaces the record in its place"
    );
    for index in 0..100 {
        runs = replace(Some(&runs), started(&format!("f#{index}"), "x", None));
    }
    assert!(runs.len() == MAXIMUM_RUNS && runs.last().unwrap().key.as_deref() == Some("f#99"), "the newest records are kept");
}

fn workspace() -> Workspace {
    Workspace { id: Some("w".into()), path: Some(std::env::temp_dir().display().to_string()), ..Workspace::default() }
}

fn pane(id: &str, kind: &str) -> RunSession {
    RunSession { id: Some(id.into()), workspace_id: Some("w".into()), kind: Some(kind.into()), ..RunSession::default() }
}

fn snapshot(sessions: Vec<RunSession>) -> AppSnapshot {
    AppSnapshot {
        workspaces: Some(vec![workspace()]),
        sessions: Some(sessions.into_iter().map(Arc::new).collect()),
        ..AppSnapshot::default()
    }
}

/// "shell block runs: the saved field is optional and a run restored running reads interrupted".
#[test]
fn the_field_is_optional_and_a_restored_run_is_interrupted() {
    let plain = pane("p", "claude");
    assert!(!plain.to_json().contains("shellRuns"), "none writes nothing");
    let with_runs = RunSession {
        shell_block_runs: Some(vec![
            started("e#0", "npm test", Some("t0")),
            finished(&started("e#1", "ls", Some("t0")), Some(0), false, Some("t1")),
        ]),
        ..plain.clone()
    };
    let json = with_runs.to_json();
    assert!(json.contains(r#""shellRuns":[{"key":"e#0","command":"npm test","state":"running""#), "saved as shellRuns: {json}");
    let mut node: Value = serde_json::from_str(&json).unwrap();
    node.as_object_mut().unwrap().remove("shellRuns");
    assert!(RunSession::read(&node).unwrap().shell_block_runs.is_none(), "an older save reads as none");
    node["shellRuns"] = json!([{"key":"e#0","command":"ls","state":"completed","exitCode":0,"output":"a","startedAt":"t"},{"key":7},{"key":"e#2","command":"ls","state":"exploded","output":"","startedAt":"t"}]);
    let damaged = RunSession::read(&node).unwrap();
    let normalized = normalize(&snapshot(vec![damaged]), true).unwrap();
    let kept = normalized.sessions()[0].shell_block_runs.clone().unwrap_or_default();
    assert!(
        kept.len() == 1 && kept[0].key.as_deref() == Some("e#0") && kept[0].state.as_deref() == Some("completed"),
        "a damaged record is dropped, the rest kept"
    );
    node["shellRuns"] = json!("nonsense");
    assert!(RunSession::read(&node).unwrap().shell_block_runs.is_none(), "a damaged field keeps the pane");

    let shell = RunSession { id: Some("s".into()), kind: Some("shell".into()), ..with_runs.clone() };
    let reread = RunSession::read(&serde_json::from_str::<Value>(&json).unwrap()).unwrap();
    let restored = normalize(&snapshot(vec![reread, shell]), true).unwrap();
    let agent = restored.session("p").unwrap().shell_block_runs.clone().unwrap();
    assert!(
        agent.len() == 2
            && agent[0].key.as_deref() == Some("e#0")
            && agent[0].state.as_deref() == Some("interrupted")
            && agent[0].ended_at.is_none()
            && agent[1].state.as_deref() == Some("completed")
            && agent[1].exit_code == Some(0),
        "restoring, running reads interrupted and an ended run stays: {agent:?}"
    );
    assert!(restored.session("s").unwrap().shell_block_runs.is_none(), "only an agent pane keeps them");
    let live = normalize(&snapshot(vec![with_runs]), false).unwrap();
    assert_eq!(
        live.sessions()[0].shell_block_runs.as_ref().unwrap()[0].state.as_deref(),
        Some("running"),
        "a normalize while the app runs leaves a running run running"
    );
}

/// "shell block runs: output is cleaned, its redraws resolved and its last 16 KB kept".
#[test]
fn the_output_is_cleaned_and_its_tail_kept() {
    let ascii = format!("{}{}", "a".repeat(10_000), "b".repeat(10_000));
    let kept = tail(&ascii);
    assert!(kept.len() == OUTPUT_LIMIT_BYTES && kept.ends_with('b'), "the last 16 KB");
    let wide = "한😀".repeat(4_000);
    let wide_tail = tail(&wide);
    assert!(wide_tail.len() <= OUTPUT_LIMIT_BYTES && wide.ends_with(&wide_tail), "counted in UTF-8, never a half pair");
    assert_eq!(tail("short"), "short", "short output as it is");
    let record = ShellBlockRun { output: Some(ascii.clone()), ..finished(&started("e#0", "x", Some("t")), Some(0), false, Some("t")) };
    let normalized = mighty_core::normalize::shell_block_runs(Some(&vec![record]), false).unwrap();
    assert_eq!(normalized[0].output.as_ref().unwrap().len(), OUTPUT_LIMIT_BYTES, "a saved record's output is held to the limit");

    let mut output = ShellBlockOutput::default();
    output.append("\u{1b}[32mgreen\u{1b}[0m\r\n");
    output.append("progress 10%\rprogress 50%");
    output.append("\r");
    output.append("progress 100%\r\ndone\u{1b}]0;title\u{7}\n");
    assert_eq!(output.text(), "green\nprogress 100%\ndone\n", "escapes removed and a carriage return's redraw resolved");
    let mut big = ShellBlockOutput::default();
    for _ in 0..200 {
        big.append(&format!("{}\n", "x".repeat(1_000)));
    }
    assert!(big.text().len() <= OUTPUT_LIMIT_BYTES, "live output keeps the tail");
}

/// "shell block runs: the host runs only a command the assistant reply shows".
#[test]
fn the_host_runs_only_what_the_reply_shows() {
    let reply = LogEntry::new(
        "r1".into(),
        "assistant",
        "Try:\n\n```console\n$ npm ci\nadded 3 packages\n$ npm test\n```\n\nThen `echo one` and:\n\n```sh\necho one\necho two\n```\n\n```ts\nconst a = 1;\n```\n\n> - ```bash\n>   ls\n>   ```\n\nPS> run me\n\n```sh\nstreaming".into(),
        "t".into(),
        None,
    );
    let request = LogEntry::new("u1".into(), "user", "```sh\nrm -rf x\n```".into(), "t".into(), None);
    let agent = RunSession { logs: Some(vec![request, reply.clone()]), ..pane("p", "claude") };
    assert_eq!(
        runnable(reply.text()),
        ["npm ci\nnpm test", "echo one\necho two", "ls"],
        "the page's blocks, in reading order, into quotes and lists, the open fence left out"
    );
    assert!(shown(&agent, "r1#1", "echo one\necho two"), "a block's command");
    assert!(shown(&agent, "r1#0", "npm ci\nnpm test"), "a console block's commands without their prompt");
    assert!(shown(&agent, "r1#2", "ls"), "a block in a quoted list");
    assert!(!shown(&agent, "r1#1", "echo one"), "only the whole block");
    assert!(!shown(&agent, "r1#1", "echo one\nnpm ci"), "a command forged from the reply's other lines");
    assert!(!shown(&agent, "r1#1", "added 3 packages"), "a console block's sample output");
    assert!(!shown(&agent, "r1#4", "run me"), "prose");
    assert!(!shown(&agent, "r1#0", "echo one\necho two"), "another block's command at the wrong place");
    assert!(!shown(&agent, "r1#3", "streaming"), "a block whose fence has not closed");
    assert!(!shown(&agent, "u1#0", "rm -rf x"), "never a request's block");
    assert!(!shown(&agent, "zz#0", "echo one"), "an unknown entry");
    let graph = RunSession {
        graph_runs: Some(vec![MightyGraphRun { id: Some("run".into()), root_entries: Some(vec![reply]), ..MightyGraphRun::default() }]),
        ..pane("g", "claude")
    };
    assert!(shown(&graph, "r1#1", "echo one\necho two"), "an entry the Mighty view shows from its run");

    assert!(
        command(Some("Bash title=x"), "ls\n\n").as_deref() == Some("ls")
            && command(Some("PwSh"), "dir").as_deref() == Some("dir")
            && command(Some("ts"), "ls").is_none()
            && command(None, "ls").is_none(),
        "languages"
    );
    assert!(
        command(Some("console"), "PS> dir\n> cd x\n% ls\nout").as_deref() == Some("dir\ncd x")
            && command(Some("console"), "only output").is_none(),
        "console prompts"
    );
    for hidden in [
        "\u{202E}",
        "\u{202A}",
        "\u{2066}",
        "\u{2069}",
        "\u{200B}",
        "\u{200F}",
        "\u{FEFF}",
        "\u{2060}",
        "\u{1b}[2K",
        "\r",
        "\u{0}",
        "\u{7F}",
        "\u{B}",
    ] {
        assert!(
            command(Some("sh"), &format!("echo safe{hidden} && rm -rf ~")).is_none(),
            "a hidden or reordering character gets no run: U+{:04X}",
            hidden.chars().next().unwrap() as u32
        );
    }
    assert_eq!(command(Some("sh"), "printf 'a\tb'\necho c").as_deref(), Some("printf 'a\tb'\necho c"), "tab and newline are fine");
    let trojan = RunSession {
        logs: Some(vec![LogEntry::new("r2".into(), "assistant", "```sh\necho ok \u{202E}; rm -rf ~\n```".into(), "t".into(), None)]),
        ..pane("t", "claude")
    };
    assert!(!shown(&trojan, "r2#0", "echo ok \u{202E}; rm -rf ~"), "the host refuses a bidi block");
}

/// More of the page's markdown, read as shellBlocks.test.ts reads it: ordered and nested lists, tables and headings stepped over,
/// a fence indented under its list item, a paragraph's lazy lines, a tilde fence.
#[test]
fn the_scan_reads_lists_tables_and_fences_as_the_page_does() {
    let text = "# Steps\n\n1. First\n   ```sh\n   npm ci\n   ```\n2. Then\n\n   ~~~bash\n   npm test\n   ~~~\n\n| a | b |\n|---|---|\n| `x` | y |\n\n---\n\n- item\n  - nested\n\n    ```zsh\n    ls -la\n    ```\nparagraph line\n```sh\nafter\n```";
    assert_eq!(runnable(text), ["npm ci", "npm test", "ls -la", "after"]);
    assert!(runnable(&"x".repeat(131_073)).is_empty(), "a reply too long to render has no buttons");
    assert_eq!(runnable("```sh\r\nwindows\r\n```"), ["windows"], "CRLF lines");
}

/// C# `DesktopService.UpdateShellBlockRun`'s change of one pane (ShellBlockRunVerification "the service keeps the record with its
/// pane"): the record replaced, nothing for a pane that is not an agent's, the same record no change, the output held to its limit.
#[test]
fn update_pane_keeps_the_record_with_its_pane() {
    let agent = pane("agent", "claude");
    let shell = pane("shell", "shell");
    let (agent, ended) = update_pane(&agent, "e#0", |_| Some(started("e#0", "ping localhost", None))).expect("started");
    assert!(!ended, "a running record is saved on the schedule");
    assert!(update_pane(&shell, "e#0", |_| Some(started("e#0", "ls", None))).is_none(), "a shell pane keeps none");
    assert!(update_pane(&agent, "e#0", |run| run.cloned()).is_none(), "the same record changes nothing");
    assert!(update_pane(&agent, "e#0", |_| Some(started("e#1", "ls", None))).is_none(), "another key changes nothing");
    let (agent, _) = update_pane(&agent, "e#0", |run| Some(ShellBlockRun { output: Some("x".repeat(40_000)), ..run.cloned().unwrap() }))
        .expect("output");
    let saved = &agent.shell_block_runs.as_ref().unwrap()[0];
    assert!(saved.state.as_deref() == Some("running") && saved.output.as_ref().unwrap().len() == OUTPUT_LIMIT_BYTES, "held to the limit");
    let (agent, ended) = update_pane(&agent, "e#0", |run| Some(finished(run.unwrap(), Some(0), false, None))).expect("ended");
    assert!(ended && agent.shell_block_runs.as_ref().unwrap()[0].state.as_deref() == Some("completed"), "an ended record is saved at once");
    let restored = normalize(&snapshot(vec![update_pane(&agent, "e#1", |_| Some(started("e#1", "ls", None))).unwrap().0]), true).unwrap();
    let runs = restored.sessions()[0].shell_block_runs.clone().unwrap();
    assert!(
        runs[0].state.as_deref() == Some("completed") && runs[1].state.as_deref() == Some("interrupted"),
        "after the quit a running record reads interrupted"
    );
    assert!(runs.iter().all(|run| shell_blocks::STATES.contains(&run.state.as_deref().unwrap_or(""))), "every state is one of the five");
}
