//! C# ModBridgeParityVerification, ActivityUsageVerification.Mods and Verification.cs "Mod bridge authenticates metadata and rejects
//! browser/secret/stale events": the loopback bridge spoken to over raw HTTP/1.1, one connection a request, as the hooks post.

mod common;

use std::sync::atomic::{AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_run::mods::{Connection, ModBridge, Receive};
use serde_json::{Map, Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;

const NO_CONTENT: u16 = 204;
const BAD_REQUEST: u16 = 400;
const UNAUTHORIZED: u16 = 401;
const FORBIDDEN: u16 = 403;
const GONE: u16 = 410;
const TOO_LARGE: u16 = 413;
const TOO_MANY: u16 = 429;

struct Post<'a> {
    connection: &'a Connection,
    body: Vec<u8>,
    graph_header: bool,
    token: Option<String>,
    browser_header: Option<&'a str>,
    chunked: bool,
}

impl<'a> Post<'a> {
    fn new(connection: &'a Connection, body: &Value) -> Post<'a> {
        Post::raw(connection, body.to_string())
    }
    fn raw(connection: &'a Connection, body: String) -> Post<'a> {
        Post { connection, body: body.into_bytes(), graph_header: true, token: None, browser_header: None, chunked: false }
    }
    fn without_graph_header(mut self) -> Self {
        self.graph_header = false;
        self
    }
    fn token(mut self, token: &str) -> Self {
        self.token = Some(token.into());
        self
    }
    fn browser(mut self, header: &'a str) -> Self {
        self.browser_header = Some(header);
        self
    }
    fn chunked(mut self, chunked: bool) -> Self {
        self.chunked = chunked;
        self
    }

    /// The status code; the request is written while the answer is read, so a refusal before the body is still heard.
    async fn send(self) -> u16 {
        let address = self.connection.url.trim_start_matches("http://").split('/').next().unwrap().to_string();
        let stream = TcpStream::connect(&address).await.unwrap();
        let (mut reader, mut writer) = stream.into_split();
        let mut head = format!(
            "POST /events HTTP/1.1\r\nHost: {address}\r\nConnection: close\r\nContent-Type: application/json\r\nAuthorization: Bearer {}\r\n",
            self.token.unwrap_or_else(|| self.connection.token.clone())
        );
        if self.graph_header {
            head.push_str("X-Mighty-Graph: 1\r\n");
        }
        if let Some(header) = self.browser_header {
            head.push_str(&format!("{header}: https://example.invalid\r\n"));
        }
        if self.chunked {
            head.push_str("Transfer-Encoding: chunked\r\n\r\n");
        } else {
            head.push_str(&format!("Content-Length: {}\r\n\r\n", self.body.len()));
        }
        let (body, chunked) = (self.body, self.chunked);
        let write = tokio::spawn(async move {
            let result: std::io::Result<()> = async {
                writer.write_all(head.as_bytes()).await?;
                if chunked {
                    // No computed length: the host must count the bytes it is given, two chunks here.
                    let split = body.len().min(1024);
                    for part in [&body[..split], &body[split..]] {
                        if !part.is_empty() {
                            writer.write_all(format!("{:x}\r\n", part.len()).as_bytes()).await?;
                            writer.write_all(part).await?;
                            writer.write_all(b"\r\n").await?;
                        }
                    }
                    writer.write_all(b"0\r\n\r\n").await?;
                } else {
                    writer.write_all(&body).await?;
                }
                writer.flush().await
            }
            .await;
            (writer, result)
        });
        let status = status_line(&mut reader).await;
        let _ = write.await;
        status
    }
}

async fn status_line(reader: &mut (impl AsyncReadExt + Unpin)) -> u16 {
    let mut line = Vec::new();
    let mut byte = [0u8; 1];
    while let Ok(1) = reader.read(&mut byte).await {
        if byte[0] == b'\n' {
            break;
        }
        line.push(byte[0]);
    }
    let text = String::from_utf8_lossy(&line);
    text.split(' ').nth(1).and_then(|code| code.parse().ok()).unwrap_or_else(|| panic!("no status line: {text:?}"))
}

fn envelope(connection: &Connection, kind: &str, graph: Option<Value>) -> Value {
    let mut value = json!({"version": 1, "runId": connection.id, "claudeSessionId": "graph-session", "event": kind, "sequence": 1});
    if let Some(graph) = graph {
        value["graph"] = graph;
    }
    value
}

fn starting() -> Value {
    json!({"version": 1, "phase": "starting", "parentToolUseId": "spawn-tool", "parentAgentId": "parent-agent", "name": "검토", "agentType": "Explore"})
}

fn with(value: &Value, changes: &[(&str, Value)]) -> Value {
    let mut value = value.clone();
    for (key, change) in changes {
        value[*key] = change.clone();
    }
    value
}

fn counting() -> (Arc<AtomicI32>, Receive) {
    let count = Arc::new(AtomicI32::new(0));
    let seen = count.clone();
    (
        count,
        Box::new(move |_| {
            seen.fetch_add(1, Ordering::SeqCst);
        }),
    )
}

#[tokio::test(flavor = "multi_thread")]
async fn graph_lifecycle_carries_bounded_content() {
    let seen: Arc<Mutex<Vec<Value>>> = Arc::default();
    let sink = seen.clone();
    let bridge = ModBridge::new();
    let connection = bridge.register(Box::new(move |value| sink.lock().unwrap().push(value.clone())), true).await.unwrap();
    let start = with(&starting(), &[("input", json!("i".repeat(16_384)))]);
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "agent.spawn", Some(start))).send().await,
        NO_CONTENT,
        "Graph input at the macOS 16 KiB boundary must reach the run."
    );
    let running =
        with(&starting(), &[("phase", json!("running")), ("agentId", json!("child-agent")), ("model", json!("claude-sonnet-4-6"))]);
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "agent.spawn", Some(running.clone()))).send().await,
        NO_CONTENT,
        "Spawned agent identities must reach the run."
    );
    let finished = with(&running, &[("phase", json!("completed")), ("output", json!("r".repeat(32_768)))]);
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "agent.complete", Some(finished))).send().await,
        NO_CONTENT,
        "Completed agent output at 32 KiB must reach the run."
    );
    let seen = seen.lock().unwrap().clone();
    assert!(seen.len() == 3 && connection.received() == 3, "Every valid lifecycle event must be delivered exactly once.");
    assert!(
        seen[0]["graph"]["input"].as_str().map(str::len) == Some(16_384) && seen[0]["graph"]["name"] == "검토",
        "Input and Unicode labels must remain intact after HTTP parsing."
    );
    let end = &seen[2]["graph"];
    assert!(
        seen[2]["event"] == "agent.complete"
            && end["phase"] == "completed"
            && end["agentId"] == "child-agent"
            && end["parentAgentId"] == "parent-agent"
            && end["parentToolUseId"] == "spawn-tool"
            && end["output"].as_str().map(str::len) == Some(32_768)
            && end["model"] == "claude-sonnet-4-6",
        "The callback must retain graph ownership, model and completed output."
    );
    bridge.dispose().await;
}

#[tokio::test(flavor = "multi_thread")]
async fn graph_opt_in_authentication_and_run_isolation() {
    let bridge = ModBridge::new();
    let (rich_seen, rich_sink) = counting();
    let (legacy_seen, legacy_sink) = counting();
    let rich = bridge.register(rich_sink, true).await.unwrap();
    let legacy = bridge.register(legacy_sink, false).await.unwrap();
    let graph = envelope(&rich, "agent.spawn", Some(starting()));
    assert_eq!(
        Post::new(&rich, &graph).token(&"0".repeat(64)).send().await,
        UNAUTHORIZED,
        "Graph requests require the authenticated run's bearer token."
    );
    assert_eq!(Post::new(&legacy, &graph).send().await, BAD_REQUEST, "A different run's valid token cannot publish this run's graph.");
    assert_eq!(
        Post::new(&legacy, &envelope(&legacy, "agent.spawn", Some(starting()))).send().await,
        BAD_REQUEST,
        "A run must explicitly opt into graph observations."
    );
    assert_eq!(
        Post::new(&rich, &graph).without_graph_header().send().await,
        BAD_REQUEST,
        "Even small graph requests require the graph header."
    );
    for header in ["Origin", "Sec-Fetch-Site"] {
        assert_eq!(
            Post::new(&rich, &graph).browser(header).send().await,
            FORBIDDEN,
            "Browser-origin graph requests must be rejected before delivery."
        );
    }
    assert_eq!(Post::new(&rich, &graph).send().await, NO_CONTENT, "Valid graph metadata must remain usable after rejected requests.");
    let original = envelope(&legacy, "session.start", None);
    assert_eq!(
        Post::new(&legacy, &original).without_graph_header().send().await,
        NO_CONTENT,
        "Legacy metadata remains supported without graph opt-in."
    );
    rich.dispose();
    assert_eq!(Post::new(&rich, &graph).send().await, UNAUTHORIZED, "Disposed run tokens must be revoked.");
    assert!(
        rich_seen.load(Ordering::SeqCst) == 1 && legacy_seen.load(Ordering::SeqCst) == 1,
        "Rejected requests must never cross run callbacks."
    );
    bridge.dispose().await;
}

#[tokio::test(flavor = "multi_thread")]
async fn malformed_graph_is_rejected_before_delivery() {
    let bridge = ModBridge::new();
    let (seen, sink) = counting();
    let connection = bridge.register(sink, true).await.unwrap();
    let valid = with(&starting(), &[("phase", json!("running")), ("agentId", json!("child-agent"))]);
    let changes: Vec<(&str, Value)> = vec![
        ("version", json!(true)),
        ("version", json!(2)),
        ("version", json!(1.5)),
        ("phase", json!("completed")),
        ("phase", Value::Null),
        ("agentId", json!("child-agent\n")),
        ("agentId", json!(123)),
        ("agentId", Value::Null),
        ("agentId", json!("a".repeat(129))),
        ("parentAgentId", json!("child-agent")),
        ("parentToolUseId", Value::Null),
        ("parentToolUseId", json!("path/to/tool")),
        ("model", json!("not a model")),
        ("input", json!("i".repeat(16_385))),
        ("input", json!("한".repeat(5462))),
        ("output", json!("o".repeat(32_769))),
        ("credentials", json!("never accepted")),
        ("name", json!("\u{1b}[31mforged")),
        ("name", json!("two\nlines")),
        ("name", json!("n".repeat(161))),
        ("agentType", json!("t".repeat(161))),
        ("model", json!("m".repeat(201))),
    ];
    for (key, value) in changes {
        let graph = with(&valid, &[(key, value)]);
        assert_eq!(
            Post::new(&connection, &envelope(&connection, "agent.spawn", Some(graph))).send().await,
            BAD_REQUEST,
            "Malformed graph field was accepted: {key}"
        );
    }
    let mut no_parent = valid.clone();
    no_parent.as_object_mut().unwrap().remove("parentToolUseId");
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "agent.spawn", Some(no_parent))).send().await,
        BAD_REQUEST,
        "Spawn must identify its parent tool."
    );
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "agent.complete", Some(json!({"version": 1, "phase": "completed"})))).send().await,
        BAD_REQUEST,
        "Completion must identify the actual agent."
    );
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "agent.spawn", None)).send().await,
        BAD_REQUEST,
        "Agent lifecycle events require graph metadata."
    );
    assert_eq!(
        Post::new(&connection, &envelope(&connection, "session.start", Some(valid.clone()))).send().await,
        BAD_REQUEST,
        "Ordinary events cannot smuggle graph metadata."
    );
    assert!(seen.load(Ordering::SeqCst) == 0 && connection.received() == 0, "Invalid graph content must not reach any consumer.");
    // The Mac's validModGraph also requires each ID to be clean at 128 bytes on one line: IDs at that boundary still pass.
    let longest = with(
        &valid,
        &[
            ("agentId", json!(format!("a{}", "b".repeat(127)))),
            ("parentAgentId", json!(format!("p{}", "q".repeat(127)))),
            ("parentToolUseId", json!(format!("t{}", "u".repeat(127)))),
        ],
    );
    assert!(
        Post::new(&connection, &envelope(&connection, "agent.spawn", Some(longest))).send().await == NO_CONTENT
            && seen.load(Ordering::SeqCst) == 1,
        "Clean 128-byte graph identities are accepted, as on the Mac."
    );
    bridge.dispose().await;
}

fn is_too_large(status: u16) -> bool {
    status == BAD_REQUEST || status == TOO_LARGE
}

#[tokio::test(flavor = "multi_thread")]
async fn body_allowance_is_limited_to_authenticated_graph_requests() {
    let bridge = ModBridge::new();
    let (seen, sink) = counting();
    let seen_legacy = seen.clone();
    let connection = bridge.register(sink, true).await.unwrap();
    let legacy = bridge
        .register(
            Box::new(move |_| {
                seen_legacy.fetch_add(1, Ordering::SeqCst);
            }),
            false,
        )
        .await
        .unwrap();
    let valid = envelope(&connection, "agent.spawn", Some(starting())).to_string();
    let graph_body = format!("{valid}{}", " ".repeat(65_536 - valid.len()));
    assert_eq!(
        Post::raw(&connection, graph_body.clone()).send().await,
        NO_CONTENT,
        "An authenticated graph body can use the 64 KiB limit."
    );
    assert_eq!(
        Post::raw(&connection, graph_body.clone()).chunked(true).send().await,
        NO_CONTENT,
        "Chunked graph bodies use the same authenticated limit."
    );
    for chunked in [false, true] {
        assert!(
            is_too_large(Post::raw(&connection, format!("{graph_body} ")).chunked(chunked).send().await),
            "Graph body beyond 64 KiB must be rejected."
        );
        assert!(
            is_too_large(Post::raw(&connection, graph_body.clone()).without_graph_header().chunked(chunked).send().await),
            "The graph header is required for the larger limit."
        );
        assert!(
            is_too_large(Post::raw(&legacy, graph_body.clone()).chunked(chunked).send().await),
            "A graph-disabled run must retain the 16 KiB limit."
        );
    }
    assert_eq!(
        Post::raw(&connection, graph_body.clone()).token(&"0".repeat(64)).send().await,
        UNAUTHORIZED,
        "Unauthenticated requests do not obtain the larger allowance."
    );
    let ordinary = envelope(&connection, "session.start", None).to_string();
    let small = format!("{ordinary}{}", " ".repeat(16_384 - ordinary.len()));
    assert_eq!(Post::raw(&connection, small.clone()).send().await, NO_CONTENT, "Ordinary telemetry still accepts exactly 16 KiB.");
    assert_eq!(
        Post::raw(&connection, format!("{small} ")).send().await,
        TOO_LARGE,
        "Graph header cannot enlarge ordinary telemetry's limit."
    );
    assert_eq!(
        Post::raw(&connection, format!("{small} ")).chunked(true).send().await,
        TOO_LARGE,
        "Chunked trailing whitespace also counts toward the ordinary limit."
    );
    assert_eq!(seen.load(Ordering::SeqCst), 3, "Only the two graph uploads and the bounded ordinary event may enter consumers.");
    bridge.dispose().await;
}

#[tokio::test(flavor = "multi_thread")]
async fn rate_limit_recovers_and_long_runs_keep_receiving() {
    let base = Instant::now();
    let offset = Arc::new(AtomicU64::new(0));
    let clock_offset = offset.clone();
    let bridge = ModBridge::with_clock(Arc::new(move || base + Duration::from_secs(clock_offset.load(Ordering::SeqCst))));
    let (received, sink) = counting();
    let connection = bridge.register(sink, false).await.unwrap();
    let other = bridge.register(Box::new(|_| {}), false).await.unwrap();
    let body = envelope(&connection, "session.start", None);
    // C#'s handler holds at most 8 connections to the server.
    let slots = Arc::new(tokio::sync::Semaphore::new(8));
    for window in 0..35 {
        if window > 0 {
            offset.fetch_add(1, Ordering::SeqCst);
        }
        let mut posts = Vec::new();
        for _ in 0..120 {
            let (connection, body, slots) = (connection.clone(), body.clone(), slots.clone());
            posts.push(tokio::spawn(async move {
                let _slot = slots.acquire_owned().await.unwrap();
                Post::new(&connection, &body).without_graph_header().send().await
            }));
        }
        for post in posts {
            assert_eq!(
                post.await.unwrap(),
                NO_CONTENT,
                "Valid events below the per-second limit must remain accepted after 4096 total events."
            );
        }
        assert_eq!(
            Post::new(&connection, &body).without_graph_header().send().await,
            TOO_MANY,
            "The 121st request in each window must be rate limited."
        );
    }
    let other_body = envelope(&other, "session.start", None);
    assert_eq!(
        Post::new(&other, &other_body).without_graph_header().send().await,
        NO_CONTENT,
        "One run's rate limit must not throttle another run."
    );
    assert!(
        connection.received() == 4200 && received.load(Ordering::SeqCst) == 4200,
        "Concurrent callbacks and received counts must stay exact for long runs."
    );
    offset.fetch_add(1, Ordering::SeqCst);
    connection.set_received(i32::MAX);
    assert!(
        Post::new(&connection, &body).without_graph_header().send().await == NO_CONTENT && connection.received() == i32::MAX,
        "The diagnostic counter saturates without terminating the observation stream."
    );
    bridge.dispose().await;
}

#[tokio::test(flavor = "multi_thread")]
async fn revoked_run_cannot_receive_an_already_authenticated_upload() {
    let bridge = ModBridge::new();
    let (received, sink) = counting();
    let connection = bridge.register(sink, true).await.unwrap();
    let bytes = envelope(&connection, "agent.spawn", Some(starting())).to_string().into_bytes();
    let address = connection.url.trim_start_matches("http://").split('/').next().unwrap().to_string();
    let mut stream = TcpStream::connect(&address).await.unwrap();
    let headers = format!(
        "POST /events HTTP/1.1\r\nHost: {address}\r\nAuthorization: Bearer {}\r\nContent-Type: application/json\r\nX-Mighty-Graph: 1\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        connection.token,
        bytes.len()
    );
    stream.write_all(headers.as_bytes()).await.unwrap();
    stream.write_all(&bytes[..1]).await.unwrap();
    stream.flush().await.unwrap();
    common::until(|| connection.requests() == 1, 5000, "the request is admitted").await;
    connection.dispose();
    stream.write_all(&bytes[1..]).await.unwrap();
    stream.flush().await.unwrap();
    let status = tokio::time::timeout(Duration::from_secs(15), status_line(&mut stream)).await.unwrap();
    assert!(
        status == GONE && received.load(Ordering::SeqCst) == 0 && connection.received() == 0,
        "Revoking a run while its authenticated body is pending must prevent callback delivery."
    );
    bridge.dispose().await;
}

/// ActivityUsageVerification.Mods.
#[tokio::test(flavor = "multi_thread")]
async fn mods() {
    let seen: Arc<Mutex<Vec<Value>>> = Arc::default();
    let sink = seen.clone();
    let bridge = ModBridge::new();
    let connection = bridge.register(Box::new(move |value| sink.lock().unwrap().push(value.clone())), false).await.unwrap();
    // `new SessionUsage { … }` written by Wire.Json: its defaults are written too.
    let usage = json!({
        "provider": "claude", "source": "claude.mods", "tokenScope": "run", "providerSessionId": "session",
        "contextUsedTokens": 100, "contextWindowTokens": 200000, "rateLimits": [{"kind": "five_hour", "percentUsed": 25}],
        "updatedAt": "2026-09-16T00:00:00Z",
    });
    let event = |fields: Value| {
        let mut value = json!({"version": 1, "runId": connection.id});
        let object: &mut Map<String, Value> = value.as_object_mut().unwrap();
        object.extend(fields.as_object().unwrap().clone());
        value
    };
    assert_eq!(
        Post::new(&connection, &event(json!({"claudeSessionId": "session", "event": "session.usage", "sequence": 1, "usage": usage})))
            .send()
            .await,
        NO_CONTENT,
        "Authenticated Mods usage must be accepted"
    );
    assert_eq!(
        Post::new(
            &connection,
            &event(json!({"claudeSessionId": "session", "event": "tool.complete", "tool": "Read", "toolUseId": "tool", "summary": "README.md", "output": "x".repeat(5000), "isError": false, "sequence": 2}))
        )
        .send()
        .await,
        NO_CONTENT,
        "Bounded structured output over old 4KiB limit must be accepted"
    );
    assert_eq!(
        Post::new(&connection, &event(json!({"claudeSessionId": "other", "event": "session.usage", "usage": usage}))).send().await,
        BAD_REQUEST,
        "Usage identity must match authenticated envelope"
    );
    assert_eq!(
        Post::new(&connection, &event(json!({"claudeSessionId": "session", "event": "tool.complete", "tool": "Read", "sequence": 3})))
            .send()
            .await,
        BAD_REQUEST,
        "Tool completion without identity must be rejected"
    );
    assert_eq!(
        Post::new(&connection, &event(json!({"claudeSessionId": "session", "event": "session.usage", "usage": usage})))
            .browser("Origin")
            .send()
            .await,
        FORBIDDEN,
        "Browser-origin usage must be rejected"
    );
    assert_eq!(seen.lock().unwrap().len(), 2, "Rejected metadata must never enter parser");
    bridge.dispose().await;
}

/// Verification.cs "Mod bridge authenticates metadata and rejects browser/secret/stale events".
#[tokio::test(flavor = "multi_thread")]
async fn mod_bridge_authenticates_metadata_and_rejects_browser_secret_stale_events() {
    let bridge = ModBridge::new();
    let (received, sink) = counting();
    let connection = bridge.register(sink, false).await.unwrap();
    let body = json!({"version": 1, "runId": connection.id, "claudeSessionId": "claude-session", "event": "session.start"});
    assert_eq!(Post::new(&connection, &body).without_graph_header().send().await, NO_CONTENT);
    assert_eq!(received.load(Ordering::SeqCst), 1);
    assert_eq!(Post::new(&connection, &body).without_graph_header().token(&"0".repeat(64)).send().await, UNAUTHORIZED);
    assert_eq!(Post::new(&connection, &body).without_graph_header().browser("Origin").send().await, FORBIDDEN);
    let prompt = with(&body, &[("prompt", json!("must never enter metadata"))]);
    assert_eq!(Post::new(&connection, &prompt).without_graph_header().send().await, BAD_REQUEST);
    connection.dispose();
    assert_eq!(Post::new(&connection, &body).without_graph_header().send().await, UNAUTHORIZED);
    assert_eq!(received.load(Ordering::SeqCst), 1);
    bridge.dispose().await;
}

/// A bridge closed before its accept loop first waits (registered and closed in one breath, on one thread) still stops its server:
/// the stop is a value the loop reads when it starts, not a notification only a waiting loop hears.
#[tokio::test(flavor = "current_thread")]
async fn a_bridge_closed_at_once_stops_its_server() {
    let bridge = ModBridge::new();
    let (_received, sink) = counting();
    let connection = bridge.register(sink, false).await.unwrap();
    let address = connection.url.trim_start_matches("http://").split('/').next().unwrap().to_string();
    bridge.dispose().await;
    // The accept loop runs now, for the first time, and must end with its listener.
    tokio::time::sleep(Duration::from_millis(200)).await;
    assert!(TcpStream::connect(&address).await.is_err(), "the server still listens after the bridge closed");
}
