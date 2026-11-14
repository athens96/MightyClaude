//! C# CodexApprovalVerification (MightyCode.Core.Tests/CodexApprovalVerification.cs): Codex's app-server approval channel driven by
//! its callbacks, and a RunManager run against a stand-in app-server (`mighty-fake-cli --codex-approval-fixture`, C#'s
//! `--codex-approval-fixture`) over the Windows ProcessHost.

mod common;

use std::sync::{Arc, Mutex};

use mighty_core::model::RunSettings;
use mighty_run::codex_approval::{CodexApprovalChannel, CodexHooks, MAXIMUM_PENDING, absolute_path};
use mighty_run::model::StartRunRequest;
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::plan_mode::{TodoProgressTracker, todos};
use mighty_run::{RunError, providers};
use serde_json::{Value, json};

fn request(resume: Option<&str>) -> StartRunRequest {
    let mut value = StartRunRequest::new("codex-pane", "workspace", "claude", "fixture prompt")
        .with_provider("codex")
        .with_settings(RunSettings { permission_mode: "onRequest".into(), network_access: true, ..RunSettings::default() });
    value.resume_id = resume.map(str::to_string);
    value
}

#[derive(Default)]
struct Lists {
    writes: Vec<String>,
    events: Vec<String>,
    failures: Vec<String>,
    warnings: Vec<String>,
    requests: Vec<ToolPermissionRequest>,
    done: bool,
}

struct Fixture {
    lists: Arc<Mutex<Lists>>,
    channel: CodexApprovalChannel,
}

impl Fixture {
    fn new(resume: Option<&str>) -> Fixture {
        let lists: Arc<Mutex<Lists>> = Arc::default();
        let (w, e, r, wa, f, d) = (lists.clone(), lists.clone(), lists.clone(), lists.clone(), lists.clone(), lists.clone());
        let hooks = CodexHooks {
            write: Arc::new(move |text| w.lock().unwrap().writes.push(text.to_string())),
            consume: Arc::new(move |frame| e.lock().unwrap().events.push(frame.to_string())),
            emit: Arc::new(move |value| r.lock().unwrap().requests.push(value)),
            activity: Arc::new(|_, _| {}),
            warning: Arc::new(move |message| wa.lock().unwrap().warnings.push(message.to_string())),
            fail: Arc::new(move |message| f.lock().unwrap().failures.push(message.to_string())),
            completed: Arc::new(move || d.lock().unwrap().done = true),
        };
        Fixture { channel: CodexApprovalChannel::new(&request(resume), "C:\\work", "fixture prompt", vec![], hooks), lists }
    }
    fn writes(&self) -> Vec<String> {
        self.lists.lock().unwrap().writes.clone()
    }
    fn last(&self) -> Value {
        common::json(self.writes().last().expect("a write"))
    }
    fn requests(&self) -> Vec<ToolPermissionRequest> {
        self.lists.lock().unwrap().requests.clone()
    }
    fn last_request(&self) -> ToolPermissionRequest {
        self.requests().last().expect("a request").clone()
    }
    fn done(&self) -> bool {
        self.lists.lock().unwrap().done
    }
    fn reply(&self, result: Value) {
        let id = self.last()["id"].clone();
        self.channel.receive(&json!({"id": id, "result": result}).to_string());
    }
    fn ready(&self) {
        self.channel.start();
        self.reply(json!({}));
        self.reply(json!({"thread": {"id": "thread-1"}}));
        self.reply(json!({"turn": {"id": "turn-1"}}));
    }
    fn ask(&self, id: i64, thread: &str, turn: &str, kind: &str, decisions: Option<Value>) {
        let mut parameters =
            json!({"threadId": thread, "turnId": turn, "itemId": "item-1", "command": "git fetch origin", "cwd": "C:\\work", "kind": kind});
        if let Some(decisions) = decisions {
            parameters["availableDecisions"] = decisions;
        }
        self.channel.receive(&json!({"id": id, "method": "item/commandExecution/requestApproval", "params": parameters}).to_string());
    }
    fn ask_plain(&self, id: i64) {
        self.ask(id, "thread-1", "turn-1", "command", None);
    }
    fn notice(&self, method: &str, parameters: Value) {
        self.channel.receive(&json!({"method": method, "params": parameters}).to_string());
    }
}

/// `Reject`: stale or unsupported consent is refused as an `InvalidOperationException`.
fn rejected(result: mighty_run::Result<()>) -> bool {
    matches!(result, Err(RunError::State(_)))
}

/// CodexApprovalVerification.cs:28 `SettingsAndHandshake`.
#[test]
fn settings_and_handshake() {
    common::korean();
    let request = request(None).validate().unwrap();
    assert!(
        providers::normalize_settings("codex", request.settings.as_ref()) == request.settings(),
        "onRequest and network setting must survive restore"
    );
    assert!(
        !providers::capabilities("codex", Some("0.153.3")).permission_modes.iter().any(|m| m == "onRequest")
            && !providers::capabilities("codex", Some("0.153.4-preview")).permission_modes.iter().any(|m| m == "onRequest"),
        "old/prerelease CLIs do not advertise approval support"
    );
    assert!(
        providers::capabilities("codex", Some("0.153.4")).permission_modes.iter().any(|m| m == "onRequest"),
        "stable supported CLI advertises approvals"
    );
    assert!(providers::permission_modes("codex", false).contains(&"onRequest"), "Codex menu advertises approvals");
    for provider in ["claude", "gemini"] {
        let mut other = request.clone();
        other.provider = provider.into();
        assert!(matches!(other.validate(), Err(RunError::Invalid(_))), "Only Codex accepts onRequest");
    }
    let arguments = providers::arguments(&request, "unused", None).unwrap();
    let has = |value: &str| arguments.iter().any(|a| a == value);
    assert!(has("app-server") && has("stdio://") && !has("exec") && !has("-"), "approval runs use app-server, not exec");
    assert!(
        has("approval_policy=\"on-request\"") && has("sandbox_mode=\"workspace-write\""),
        "workspace sandbox and approval policy are explicit"
    );
    let manual = providers::arguments(&request.clone().with_settings(RunSettings::default()), "unused", None).unwrap();
    assert!(manual.iter().any(|a| a == "approval_policy=\"never\""), "manual retains its previous boundary");
    let fixture = Fixture::new(None);
    fixture.channel.start();
    fixture.channel.start();
    assert!(fixture.writes().len() == 1 && fixture.last()["method"] == "initialize", "start writes one handshake only");
    fixture.reply(json!({}));
    assert_eq!(fixture.last()["method"], "thread/start", "thread starts after initialization");
    fixture.reply(json!({"thread": {"id": "thread-1"}}));
    let turn = fixture.last()["params"].clone();
    assert!(turn["approvalPolicy"] == "on-request" && turn["approvalsReviewer"] == "user", "the user remains approval reviewer");
    assert_eq!(turn["sandboxPolicy"]["networkAccess"], true, "network permission reaches the actual turn");
    assert_eq!(turn["sandboxPolicy"]["writableRoots"][0], "C:\\work", "the only writable root is the current workspace");
    fixture.reply(json!({"turn": {"id": "turn-1"}}));
    assert!(fixture.channel.initialized() && !fixture.done(), "initialization is not completion");
}

/// CodexApprovalVerification.cs:62 `ApprovalScopeAndOnceOnly`.
#[test]
fn approval_scope_and_once_only() {
    common::korean();
    let f = Fixture::new(None);
    f.ready();
    f.ask(1, "other", "turn-1", "command", None);
    assert!(f.requests().is_empty() && f.last().get("error").is_some(), "cross-thread approval is rejected");
    f.ask(2, "thread-1", "old-turn", "command", None);
    assert!(f.requests().is_empty(), "stale turn is rejected");
    f.ask(3, "thread-1", "turn-1", "command", Some(json!(["accept", "decline", "acceptForSession"])));
    let requests = f.requests();
    assert_eq!(requests.len(), 1);
    let ask = requests[0].clone();
    assert!(ask.can_allow && ask.input_json.contains("git fetch origin"), "the complete command is displayed before consent");
    f.channel.respond(&ask.id, true).unwrap();
    assert_eq!(f.last()["result"]["decision"], "accept", "allow grants only this call");
    assert!(rejected(f.channel.respond(&ask.id, true)), "Stale or unsupported consent was accepted.");
    f.ask(4, "thread-1", "turn-1", "network", None);
    assert!(!f.last_request().can_allow, "network bundles cannot become one-time command grants");
    assert!(rejected(f.channel.respond(&f.last_request().id, true)), "Stale or unsupported consent was accepted.");
    f.channel.respond(&f.last_request().id, false).unwrap();
    f.ask(5, "thread-1", "turn-1", "command", Some(json!(["acceptForSession", "decline"])));
    assert!(!f.last_request().can_allow, "session grant is never treated as accept");
    f.channel.cancel_all();
    assert!(rejected(f.channel.respond(&f.last_request().id, true)), "Stale or unsupported consent was accepted.");
}

/// CodexApprovalVerification.cs:80 `FileChangesRequireCompleteDiff`.
#[test]
fn file_changes_require_complete_diff() {
    common::korean();
    let f = Fixture::new(None);
    f.ready();
    f.notice(
        "item/started",
        json!({"threadId": "thread-1", "turnId": "turn-1", "item": {"id": "file-1", "type": "fileChange", "changes": [{"path": "C:\\work\\hello.txt", "diff": "+hello", "kind": {"type": "add"}}]}}),
    );
    let ask = |id: i64, grant_root: Option<&str>| {
        let mut parameters = json!({"threadId": "thread-1", "turnId": "turn-1", "itemId": "file-1"});
        if let Some(root) = grant_root {
            parameters["grantRoot"] = json!(root);
        }
        f.channel.receive(&json!({"id": id, "method": "item/fileChange/requestApproval", "params": parameters}).to_string());
    };
    ask(1, None);
    assert!(f.last_request().can_allow && f.last_request().input_json.contains("hello"), "the exact previously observed diff is displayed");
    f.channel.respond(&f.last_request().id, true).unwrap();
    ask(2, Some("C:\\"));
    assert!(!f.last_request().can_allow, "grantRoot cannot grant lasting directory access");
    f.notice(
        "item/completed",
        json!({"threadId": "thread-1", "turnId": "turn-1", "item": {"id": "file-1", "type": "fileChange", "changes": []}}),
    );
    assert_eq!(f.last_request().state, "cancelled", "completed items cancel stale approvals");
    ask(3, None);
    assert!(!f.last_request().can_allow, "a missing diff cannot be approved");
}

/// CodexApprovalVerification.cs:96 `CancellationMalformedAndBounds`.
#[test]
fn cancellation_malformed_and_bounds() {
    common::korean();
    let f = Fixture::new(None);
    f.ready();
    f.ask_plain(1);
    f.notice("serverRequest/resolved", json!({"threadId": "thread-1", "requestId": 1}));
    assert_eq!(f.last_request().state, "cancelled", "server resolution removes the pending question");
    f.ask_plain(1);
    assert!(f.channel.failed(), "duplicate ids fail closed");
    let eof = Fixture::new(None);
    eof.ready();
    eof.channel.flush();
    assert!(eof.channel.failed() && !eof.done(), "EOF is not success");
    let timeout = Fixture::new(None);
    timeout.channel.start();
    timeout.channel.initialization_timed_out();
    assert!(timeout.channel.failed(), "handshake timeout fails closed");
    let bad = Fixture::new(None);
    bad.channel.receive("[]");
    assert!(bad.channel.failed(), "malformed envelope fails closed");
    let resume = Fixture::new(Some("expected-thread"));
    resume.channel.start();
    resume.reply(json!({}));
    resume.reply(json!({"thread": {"id": "other-thread"}}));
    assert!(resume.channel.failed(), "resume cannot silently choose another thread");
    let many = Fixture::new(None);
    many.ready();
    for id in 1..=17 {
        many.ask_plain(id);
    }
    assert!(many.requests().len() == MAXIMUM_PENDING && many.last()["result"]["decision"] == "decline", "pending approvals are bounded");
    assert!(
        absolute_path(Some("C:\\work")) && !absolute_path(Some("C:work")),
        "drive-relative paths are not full command working directories"
    );
}

/// CodexApprovalVerification.cs:112 `CompletionAndLegacyEvents`.
#[test]
fn completion_and_legacy_events() {
    common::korean();
    let f = Fixture::new(None);
    f.ready();
    let events = || f.lists.lock().unwrap().events.clone();
    f.notice(
        "item/completed",
        json!({"threadId": "thread-1", "turnId": "turn-1", "item": {"id": "msg", "type": "agentMessage", "text": "result"}}),
    );
    assert!(
        events().iter().any(|e| e.contains("agent_message") && e.contains("result")),
        "agent message reaches the existing transcript parser"
    );
    f.notice(
        "item/started",
        json!({"threadId": "thread-1", "turnId": "turn-1", "item": {"id": "spawn", "type": "collabAgentToolCall", "tool": "spawnAgent", "senderThreadId": "thread-1", "receiverThreadIds": ["child"], "status": "inProgress"}}),
    );
    assert!(
        events().iter().any(|e| e.contains("spawn_agent") && e.contains("receiver_thread_ids")),
        "graph receives normalized collaboration items"
    );
    f.notice(
        "thread/tokenUsage/updated",
        json!({"threadId": "thread-1", "turnId": "turn-1", "tokenUsage": {"last": {"inputTokens": 12, "outputTokens": 4}}}),
    );
    // The turn's plan reaches the checklist as exec's todo_list does (the first unfinished step is the current one).
    f.notice(
        "turn/plan/updated",
        json!({"threadId": "thread-1", "turnId": "turn-1", "plan": [{"step": "Implement", "status": "completed"}, {"step": "Build", "status": "inProgress"}, {"step": "Deploy", "status": "pending"}]}),
    );
    f.notice(
        "turn/plan/updated",
        json!({"threadId": "thread-1", "turnId": "other", "plan": [{"step": "Not this turn", "status": "pending"}]}),
    );
    let mut tracker = TodoProgressTracker::new(None);
    let mut progress = None;
    for event in events() {
        if let Some(changed) = tracker.consume(&common::json(&event)) {
            progress = Some(changed);
        }
    }
    let progress = progress.expect("a checklist");
    assert!(
        todos::total(&progress) == 3 && todos::completed(&progress) == 1 && todos::current_text(&progress).as_deref() == Some("Build"),
        "the turn's plan becomes the pane's checklist; another turn's plan is ignored"
    );
    f.notice("turn/completed", json!({"threadId": "thread-1", "turn": {"id": "other", "status": "completed"}}));
    assert!(!f.done(), "another turn cannot complete this run");
    f.notice("turn/completed", json!({"threadId": "thread-1", "turn": {"id": "turn-1", "status": "completed"}}));
    f.channel.flush();
    assert!(f.done() && f.channel.turn_completed() && !f.channel.failed(), "a matching completed turn settles cleanly");
    assert!(
        events().iter().any(|e| e.contains("turn.completed") && e.contains("\"input_tokens\":12") && e.contains("\"output_tokens\":4")),
        "the turn's usage rides on turn.completed"
    );
}

/// CodexApprovalVerification.cs:139 `ActualRunManagerProtocol`.
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn actual_run_manager_protocol() {
    use std::sync::{OnceLock, Weak};

    use mighty_core::model::Workspace;
    use mighty_run::model::RunEvent;
    use mighty_run::{RunManager, RunManagerOptions};

    common::korean();
    let directory = common::temp("codex-approval");
    let workspace =
        Workspace { id: Some("workspace".into()), path: Some(directory.to_string_lossy().into_owned()), ..Workspace::default() };
    let catalog = common::fake_catalog(&["--codex-approval-fixture"]);
    let resolved = workspace.clone();
    let resolve: mighty_run::manager::ResolveWorkspace = Arc::new(move |_| {
        let workspace = resolved.clone();
        Box::pin(async move { Ok(workspace) })
    });
    let events: Arc<Mutex<Vec<RunEvent>>> = Arc::default();
    let requests: Arc<Mutex<Vec<ToolPermissionRequest>>> = Arc::default();
    let cell: Arc<OnceLock<Weak<RunManager>>> = Arc::default();
    let list = events.clone();
    let mut options = RunManagerOptions::new("unused", resolve.clone(), Arc::new(move |event| list.lock().unwrap().push(event)));
    let (seen, answer) = (requests.clone(), cell.clone());
    options.permission_requested = Some(Arc::new(move |value: ToolPermissionRequest| {
        seen.lock().unwrap().push(value.clone());
        if value.state == "pending" {
            let manager = answer.get().and_then(Weak::upgrade).expect("the manager");
            manager.respond_to_tool_permission("codex-pane", &value.id, true).unwrap();
        }
    }));
    let manager = Arc::new(RunManager::new(catalog.clone(), options));
    cell.set(Arc::downgrade(&manager)).ok();
    manager.start(request(None)).await.unwrap();
    let ended = || {
        events
            .lock()
            .unwrap()
            .iter()
            .find(|e| e.kind == "status" && matches!(e.status.as_deref(), Some("completed" | "error" | "stopped")))
            .and_then(|e| e.status.clone())
    };
    common::until(|| ended().is_some(), 15000, "the approval run ends").await;
    manager.dispose().await;
    let all = events.lock().unwrap().clone();
    let texts: Vec<String> = all.iter().filter_map(|e| e.entry.as_ref().and_then(|l| l.text.clone())).collect();
    assert_eq!(ended().as_deref(), Some("completed"), "real child app-server exchange must complete: {}", texts.join(" / "));
    assert!(requests.lock().unwrap().iter().any(|r| r.state == "allowed"), "RunManager routes the UI decision to the active Codex channel");
    assert!(
        all.iter().any(|e| e.kind == "resume" && e.resume_id.as_deref() == Some("thread-1")),
        "thread resume identity reaches DesktopService"
    );
    assert!(texts.iter().any(|t| t == "fixture complete"), "agent message reaches the final transcript");
    let headless = RunManager::new(catalog, RunManagerOptions::new("unused", resolve, Arc::new(|_| {})));
    assert!(matches!(headless.start(request(None)).await, Err(RunError::State(_))), "Stale or unsupported consent was accepted.");
    headless.dispose().await;
    common::remove_when_free(&directory).await;
}
