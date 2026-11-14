//! The ACP channel's bound on an approval card (C# `AcpChannel.MaximumInputBytes`) holds before anything large is built from the request:
//! a crafted `session/request_permission` whose sorted copy and indented text would take hundreds of megabytes (gigabytes at the 32 MiB
//! frame cap) is refused while the memory the channel takes stays near the frame's own size. C#'s out-of-memory fails only the run; a
//! failed allocation here would end the app. A test binary of its own: it counts every allocation of the process.

use std::alloc::{GlobalAlloc, Layout, System};
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use mighty_run::acp::{AcpChannel, AcpHooks, AcpRequest};
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::strings;

/// The system's allocator, counting the bytes live and the most ever live.
struct Counting;

static LIVE: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);

// SAFETY: every call is passed to the system allocator unchanged; only the counters are added.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        // SAFETY: the caller's layout, as `GlobalAlloc::alloc` requires.
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() {
            let live = LIVE.fetch_add(layout.size(), Ordering::SeqCst) + layout.size();
            PEAK.fetch_max(live, Ordering::SeqCst);
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        // SAFETY: a block this allocator handed out, with its layout, as `GlobalAlloc::dealloc` requires.
        unsafe { System.dealloc(pointer, layout) };
        LIVE.fetch_sub(layout.size(), Ordering::SeqCst);
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

const MIB: usize = 1024 * 1024;

fn recording(name: &str) -> Vec<String> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures").join(name);
    std::fs::read_to_string(path).unwrap().lines().filter(|l| !l.trim().is_empty()).map(str::to_string).collect()
}

#[test]
fn a_crafted_approval_is_refused_before_its_sorted_copy_and_indented_text_are_built() {
    strings::set_language("ko");
    let warnings: Arc<Mutex<Vec<String>>> = Arc::default();
    let cards: Arc<Mutex<Vec<ToolPermissionRequest>>> = Arc::default();
    let writes: Arc<Mutex<Vec<String>>> = Arc::default();
    let written = writes.clone();
    let mut hooks = AcpHooks::writing(Arc::new(move |frame| written.lock().unwrap().push(frame.to_string())));
    let (warned, shown) = (warnings.clone(), cards.clone());
    hooks.warning = Arc::new(move |message| warned.lock().unwrap().push(message.to_string()));
    hooks.emit = Arc::new(move |card| shown.lock().unwrap().push(card));
    let channel = AcpChannel::new(
        AcpRequest { run_id: "pane".into(), workspace_path: "/tmp/project".into(), prompt_text: "hi".into(), ..AcpRequest::default() },
        hooks,
    );
    channel.start();
    let lines = recording("opencode-acp-answer.jsonl");
    channel.receive(&lines[0]);
    channel.receive(&lines[1]);
    // Half a million numbers fifty arrays deep: about 1 MB of JSON, some 35 MB once parsed (and as much again in the card's copy of
    // the input), more than 50 MB once indented. Refused on its compact text the channel takes about 70 MiB; building the sorted copy
    // and the indented text first took 200 MiB.
    let numbers = vec!["0"; 500_000].join(",");
    let frame = format!(
        "{{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"session/request_permission\",\"params\":{{\"sessionId\":\"ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw\",\"toolCall\":{{\"toolCallId\":\"call-9\",\"title\":\"edit\",\"kind\":\"edit\",\"rawInput\":{{\"data\":{}{numbers}{}}}}},\"options\":[{{\"optionId\":\"once\",\"kind\":\"allow_once\",\"name\":\"Allow\"}},{{\"optionId\":\"reject\",\"kind\":\"reject_once\",\"name\":\"Reject\"}}]}}}}",
        "[".repeat(50),
        "]".repeat(50)
    );
    let before = LIVE.load(Ordering::SeqCst);
    PEAK.store(before, Ordering::SeqCst);
    channel.receive(&frame);
    let grew = PEAK.load(Ordering::SeqCst).saturating_sub(before);
    assert!(
        cards.lock().unwrap().is_empty() && *warnings.lock().unwrap() == [strings::get("acp.channel.requestTooLarge")],
        "the request is refused and named: {:?}",
        warnings.lock().unwrap()
    );
    let answer = writes.lock().unwrap().last().cloned().unwrap_or_default();
    assert!(answer.contains("\"id\":9") && answer.contains("\"optionId\":\"reject\""), "and denied: {answer}");
    assert!(grew < 128 * MIB, "the channel took {} MiB for a 1 MB frame", grew / MIB);
}
