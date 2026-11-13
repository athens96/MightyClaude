//! C# `ModBridge` and `HttpHost` (ModBridge.cs, HttpHost.cs): the loopback HTTP endpoint the mighty-bridge plugin's hooks post a
//! Claude run's metadata to (`POST /events`, a bearer token per run, 120 requests a second per run, 16 KiB bodies, 64 KiB for an
//! opted-in graph event), and the records the graph reads of them (`ModMetadata`, `ModGraphMetadata`).
//!
//! The server is hyper's HTTP/1.1 on `127.0.0.1:0` (C#: Kestrel with 64 connections, 8 KiB of headers, 5 s to send them).
//! Deviation: each connection closes after its response (Kestrel keeps one open for a second), which every client handles.

use std::collections::{HashMap, HashSet};
use std::convert::Infallible;
use std::net::{Ipv4Addr, SocketAddr};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};

use bytes::Bytes;
use http_body_util::{BodyExt, Empty};
use hyper::body::Incoming;
use hyper::server::conn::http1;
use hyper::service::service_fn;
use hyper::{Method, Request, Response, StatusCode};
use hyper_util::rt::{TokioIo, TokioTimer};
use mighty_core::model::SessionUsage;
use mighty_core::wire;
use serde_json::Value;
use tokio::net::TcpListener;
use tokio::sync::Semaphore;

use crate::activity::{MAXIMUM_OUTPUT_BYTES, MAXIMUM_SUMMARY_BYTES, clean};
use crate::meta;
use crate::{RunError, lock};

const METADATA_BODY_LIMIT: usize = 16_384;
const GRAPH_BODY_LIMIT: usize = 65_536;
const REQUESTS_PER_SECOND: u32 = 120;

fn metadata_keys() -> &'static HashSet<&'static str> {
    static KEYS: OnceLock<HashSet<&'static str>> = OnceLock::new();
    KEYS.get_or_init(|| {
        [
            "version",
            "runId",
            "claudeSessionId",
            "event",
            "turnId",
            "tool",
            "durationMs",
            "reason",
            "toolUseId",
            "summary",
            "output",
            "isError",
            "sequence",
            "agentId",
            "usage",
            "graph",
        ]
        .into_iter()
        .collect()
    })
}

fn graph_keys() -> &'static HashSet<&'static str> {
    static KEYS: OnceLock<HashSet<&'static str>> = OnceLock::new();
    KEYS.get_or_init(|| {
        ["version", "phase", "agentId", "parentAgentId", "parentToolUseId", "name", "agentType", "model", "input", "output"]
            .into_iter()
            .collect()
    })
}

/// The graph part of one Mods event (C# `ModGraphMetadata`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModGraphMetadata {
    pub version: i32,
    pub phase: String,
    pub agent_id: Option<String>,
    pub parent_agent_id: Option<String>,
    pub parent_tool_use_id: Option<String>,
    pub name: Option<String>,
    pub agent_type: Option<String>,
    pub model: Option<String>,
    pub input: Option<String>,
    pub output: Option<String>,
}

/// The fields of one Mods event the execution graph reads (C# `ModMetadata`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModMetadata {
    pub claude_session_id: String,
    pub event: String,
    pub tool: Option<String>,
    pub tool_use_id: Option<String>,
    pub agent_id: Option<String>,
    pub graph: Option<ModGraphMetadata>,
}

impl ModMetadata {
    /// `RunManager.FeedMod`: the event as the graph reads it, None when it names no session or event.
    pub fn read(root: &Value) -> Option<ModMetadata> {
        let session = meta::text(root, "claudeSessionId")?;
        let event = meta::text(root, "event")?;
        let graph =
            meta::prop(root, "graph").filter(|g| g.is_object() && meta::safe_integer(g, "version") == Some(1)).map(|g| ModGraphMetadata {
                version: 1,
                phase: meta::text(g, "phase").unwrap_or("running").into(),
                agent_id: meta::text(g, "agentId").map(str::to_string),
                parent_agent_id: meta::text(g, "parentAgentId").map(str::to_string),
                parent_tool_use_id: meta::text(g, "parentToolUseId").map(str::to_string),
                name: meta::text(g, "name").or_else(|| meta::text(g, "agentType")).map(str::to_string),
                agent_type: meta::text(g, "agentType").map(str::to_string),
                model: meta::text(g, "model").map(str::to_string),
                input: meta::text(g, "input").map(str::to_string),
                output: meta::text(g, "output").map(str::to_string),
            });
        Some(ModMetadata {
            claude_session_id: session.into(),
            event: event.into(),
            tool: meta::text(root, "tool").map(str::to_string),
            tool_use_id: meta::text(root, "toolUseId").map(str::to_string),
            agent_id: meta::text(root, "agentId").map(str::to_string),
            graph,
        })
    }
}

pub type Clock = Arc<dyn Fn() -> Instant + Send + Sync>;
pub type Receive = Box<dyn Fn(&Value) + Send + Sync>;

struct ConnectionState {
    window: Instant,
    count: u32,
    received: i32,
    released: bool,
}

/// One run's registration (C# `ModBridge.Connection`): its id, its secret token and the URL its hooks post to.
pub struct Connection {
    pub id: String,
    pub token: String,
    pub url: String,
    graph_enabled: bool,
    receive: Receive,
    state: Mutex<ConnectionState>,
    bridge: std::sync::Weak<Inner>,
}

impl Connection {
    /// Events delivered so far (saturating).
    pub fn received(&self) -> i32 {
        lock(&self.state).received
    }

    pub fn set_received(&self, value: i32) {
        lock(&self.state).received = value;
    }

    /// Requests counted in the current one-second window.
    pub fn requests(&self) -> u32 {
        lock(&self.state).count
    }

    /// Counts one request in the rolling one-second window; false once `limit` is reached.
    fn admit(&self, now: Instant, limit: u32) -> bool {
        let mut state = lock(&self.state);
        if now.duration_since(state.window) >= Duration::from_secs(1) {
            state.window = now;
            state.count = 0;
        }
        if state.count >= limit {
            return false;
        }
        state.count += 1;
        true
    }

    /// Hands a checked event to the run while it is still registered. `dispose` takes the same lock, so once it returns even an
    /// authenticated request still reading its body cannot reach the run.
    fn deliver(&self, value: &Value) -> bool {
        let mut state = lock(&self.state);
        if state.released || !self.bridge.upgrade().is_some_and(|bridge| bridge.is_current(self)) {
            return false;
        }
        state.received = state.received.saturating_add(1);
        (self.receive)(value);
        true
    }

    /// Revokes the token: no request of this run is delivered from now on.
    pub fn dispose(&self) {
        let mut state = lock(&self.state);
        if state.released {
            return;
        }
        state.released = true;
        if let Some(bridge) = self.bridge.upgrade() {
            lock(&bridge.connections).remove(&self.id);
        }
    }
}

impl Drop for Connection {
    fn drop(&mut self) {
        self.dispose();
    }
}

struct Inner {
    connections: Mutex<HashMap<String, Arc<Connection>>>,
    clock: Clock,
}

impl Inner {
    fn is_current(&self, connection: &Connection) -> bool {
        lock(&self.connections).get(&connection.id).is_some_and(|current| std::ptr::eq(current.as_ref(), connection))
    }
}

struct Host {
    address: SocketAddr,
    stop: Arc<tokio::sync::Notify>,
    stopped: Arc<AtomicBool>,
}

/// C# `ModBridge`: one loopback server for every run of the app, started with the first registration.
pub struct ModBridge {
    inner: Arc<Inner>,
    host: tokio::sync::Mutex<Option<Host>>,
    disposed: AtomicBool,
}

impl Default for ModBridge {
    fn default() -> Self {
        ModBridge::new()
    }
}

fn random_token() -> String {
    let mut bytes = [0u8; 32];
    if getrandom::fill(&mut bytes).is_err() {
        // Two random GUIDs, should the OS's generator fail.
        return format!("{}{}", wire::new_id(), wire::new_id()).replace('-', "")[..64].to_string();
    }
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// `CryptographicOperations.FixedTimeEquals`.
fn fixed_time_equals(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

impl ModBridge {
    pub fn new() -> ModBridge {
        ModBridge::with_clock(Arc::new(Instant::now))
    }

    /// A bridge whose rate window reads `clock` (the checks advance it by hand).
    pub fn with_clock(clock: Clock) -> ModBridge {
        ModBridge {
            inner: Arc::new(Inner { connections: Mutex::new(HashMap::new()), clock }),
            host: tokio::sync::Mutex::new(None),
            disposed: AtomicBool::new(false),
        }
    }

    /// `RegisterAsync`: a run's token and URL (the server starts with the first). `graph_enabled`: the run takes graph events.
    pub async fn register(&self, receive: Receive, graph_enabled: bool) -> crate::Result<Arc<Connection>> {
        let mut host = self.host.lock().await;
        if self.disposed.load(Ordering::SeqCst) {
            return Err(RunError::State("the Mods bridge is closed".into()));
        }
        if host.is_none() {
            *host = Some(start(self.inner.clone()).await.map_err(|e| RunError::Io(e.to_string()))?);
        }
        let address = host.as_ref().map(|h| h.address).expect("started");
        let connection = Arc::new(Connection {
            id: wire::new_id(),
            token: random_token(),
            url: format!("http://{address}/events"),
            graph_enabled,
            receive,
            state: Mutex::new(ConnectionState { window: (self.inner.clock)(), count: 0, received: 0, released: false }),
            bridge: Arc::downgrade(&self.inner),
        });
        lock(&self.inner.connections).insert(connection.id.clone(), connection.clone());
        Ok(connection)
    }

    /// `DisposeAsync`: every token revoked and the server stopped.
    pub async fn dispose(&self) {
        let mut host = self.host.lock().await;
        self.disposed.store(true, Ordering::SeqCst);
        let connections: Vec<Arc<Connection>> = lock(&self.inner.connections).values().cloned().collect();
        for connection in connections {
            connection.dispose();
        }
        if let Some(host) = host.take() {
            host.stopped.store(true, Ordering::SeqCst);
            host.stop.notify_waiters();
        }
    }
}

/// `HttpHost.StartAsync` on loopback: an accept loop, at most 64 connections at once, 8 KiB of headers sent within 5 s.
async fn start(inner: Arc<Inner>) -> std::io::Result<Host> {
    let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await?;
    let address = listener.local_addr()?;
    let stop = Arc::new(tokio::sync::Notify::new());
    let stopped = Arc::new(AtomicBool::new(false));
    let (stop_wait, stopped_flag) = (stop.clone(), stopped.clone());
    let slots = Arc::new(Semaphore::new(64));
    tokio::spawn(async move {
        loop {
            let accepted = tokio::select! {
                accepted = listener.accept() => accepted,
                _ = stop_wait.notified() => break,
            };
            if stopped_flag.load(Ordering::SeqCst) {
                break;
            }
            let Ok((stream, remote)) = accepted else { continue };
            let Ok(slot) = slots.clone().try_acquire_owned() else { continue };
            let inner = inner.clone();
            tokio::spawn(async move {
                let service = service_fn(move |request| {
                    let inner = inner.clone();
                    async move { Ok::<_, Infallible>(handle(&inner, remote, request).await) }
                });
                let _ = http1::Builder::new()
                    .timer(TokioTimer::new())
                    .header_read_timeout(Duration::from_secs(5))
                    .max_buf_size(8192)
                    .keep_alive(false)
                    .serve_connection(TokioIo::new(stream), service)
                    .await;
                drop(slot);
            });
        }
    });
    Ok(Host { address, stop, stopped })
}

fn status(code: StatusCode) -> Response<Empty<Bytes>> {
    let mut response = Response::new(Empty::new());
    *response.status_mut() = code;
    response
}

/// C# `ModBridge.HandleAsync`.
async fn handle(inner: &Inner, remote: SocketAddr, request: Request<Incoming>) -> Response<Empty<Bytes>> {
    let headers = request.headers();
    if !remote.ip().is_loopback()
        || request.method() != Method::POST
        || request.uri().path() != "/events"
        || request.uri().query().is_some()
        || headers.contains_key("origin")
        || headers.contains_key("sec-fetch-site")
    {
        return status(StatusCode::FORBIDDEN);
    }
    let authorization: String = headers.get_all("authorization").iter().filter_map(|v| v.to_str().ok()).collect::<Vec<_>>().join(",");
    let connection = if authorization.len() == 71 && authorization.starts_with("Bearer ") {
        let presented = &authorization.as_bytes()[7..];
        lock(&inner.connections).values().find(|c| fixed_time_equals(presented, c.token.as_bytes())).cloned()
    } else {
        None
    };
    let Some(connection) = connection else { return status(StatusCode::UNAUTHORIZED) };
    let graph_request = connection.graph_enabled && headers.get("x-mighty-graph").and_then(|v| v.to_str().ok()) == Some("1");
    if !connection.admit((inner.clock)(), REQUESTS_PER_SECOND) {
        return status(StatusCode::TOO_MANY_REQUESTS);
    }
    let maximum = if graph_request { GRAPH_BODY_LIMIT } else { METADATA_BODY_LIMIT };
    let (value, length) = match read_json(request, maximum).await {
        Ok(read) => read,
        Err(code) => return status(code),
    };
    if !inner.is_current(&connection) {
        return status(StatusCode::GONE);
    }
    // A graph header does not enlarge ordinary telemetry: the whole body counts, whitespace and chunked transfers too.
    if length > METADATA_BODY_LIMIT && value.is_object() && !matches!(meta::text(&value, "event"), Some("agent.spawn" | "agent.complete")) {
        return status(StatusCode::PAYLOAD_TOO_LARGE);
    }
    if !valid_metadata(&value, &connection.id, graph_request) {
        return status(StatusCode::BAD_REQUEST);
    }
    if !connection.deliver(&value) {
        return status(StatusCode::GONE);
    }
    status(StatusCode::NO_CONTENT)
}

/// `HttpHost.ReadJsonWithLengthAsync`: `application/json` within `maximum` bytes (read within 10 s), at most 32 levels deep.
async fn read_json(request: Request<Incoming>, maximum: usize) -> Result<(Value, usize), StatusCode> {
    let headers = request.headers();
    let content_type = headers.get("content-type").and_then(|v| v.to_str().ok()).unwrap_or("");
    if content_type.split(';').next().unwrap_or("").trim() != "application/json" {
        return Err(StatusCode::BAD_REQUEST);
    }
    if let Some(length) = headers.get("content-length").and_then(|v| v.to_str().ok()).and_then(|v| v.trim().parse::<u64>().ok())
        && length > maximum as u64
    {
        return Err(StatusCode::BAD_REQUEST);
    }
    let mut body = request.into_body();
    let mut data: Vec<u8> = Vec::new();
    let read = tokio::time::timeout(Duration::from_secs(10), async {
        while let Some(frame) = body.frame().await {
            let frame = frame.map_err(|_| StatusCode::BAD_REQUEST)?;
            if let Some(chunk) = frame.data_ref() {
                if data.len() + chunk.len() > maximum {
                    return Err(StatusCode::BAD_REQUEST);
                }
                data.extend_from_slice(chunk);
            }
        }
        Ok(())
    })
    .await;
    match read {
        Ok(Ok(())) => {}
        Ok(Err(code)) => return Err(code),
        Err(_) => return Err(StatusCode::BAD_REQUEST),
    }
    let value: Value = serde_json::from_slice(&data).map_err(|_| StatusCode::BAD_REQUEST)?;
    if mighty_core::json::depth(&value) > 32 {
        return Err(StatusCode::BAD_REQUEST);
    }
    Ok((value, data.len()))
}

fn version_one(value: &Value) -> bool {
    matches!(meta::prop(value, "version"), Some(v) if v.is_number() && v.as_f64() == Some(1.0))
}

/// `CleanText`: a string that is already clean (`ActivitySupport.Clean`).
fn clean_text(value: &Value, maximum: usize, single_line: bool) -> bool {
    value.as_str().is_some_and(|text| text == clean(Some(text), maximum, single_line))
}

/// `ModBridge.ValidMetadata`.
fn valid_metadata(value: &Value, run_id: &str, graph_request: bool) -> bool {
    let Some(object) = value.as_object() else { return false };
    if object.keys().any(|k| !metadata_keys().contains(k.as_str()))
        || !version_one(value)
        || meta::text(value, "runId") != Some(run_id)
        || !wire::is_identifier(meta::text(value, "claudeSessionId"))
    {
        return false;
    }
    let Some(kind) = meta::text(value, "event") else { return false };
    if !matches!(
        kind,
        "session.start"
            | "turn.start"
            | "turn.complete"
            | "tool.call"
            | "tool.waiting"
            | "tool.complete"
            | "session.usage"
            | "agent.spawn"
            | "agent.complete"
    ) {
        return false;
    }
    for key in ["turnId", "toolUseId", "agentId"] {
        if let Some(raw) = object.get(key)
            && !wire::is_identifier(raw.as_str())
        {
            return false;
        }
    }
    for (key, bytes, line) in [("tool", 160, true), ("summary", MAXIMUM_SUMMARY_BYTES, true), ("output", MAXIMUM_OUTPUT_BYTES, false)] {
        if let Some(raw) = object.get(key)
            && !clean_text(raw, bytes, line)
        {
            return false;
        }
    }
    if let Some(reason) = object.get("reason")
        && !matches!(reason.as_str(), Some("answer" | "aborted" | "refusal" | "error"))
    {
        return false;
    }
    if let Some(duration) = object.get("durationMs")
        && !duration.as_f64().is_some_and(|ms| duration.is_number() && ms.is_finite() && (0.0..=1_000_000_000_000.0).contains(&ms))
    {
        return false;
    }
    if let Some(sequence) = object.get("sequence")
        && !sequence
            .as_f64()
            .is_some_and(|n| sequence.is_number() && n.is_finite() && n.trunc() == n && (0.0..=9_007_199_254_740_991.0).contains(&n))
    {
        return false;
    }
    if let Some(flag) = object.get("isError")
        && !flag.is_boolean()
    {
        return false;
    }
    if kind.starts_with("tool.") && meta::text(value, "tool").is_none_or(str::is_empty) {
        return false;
    }
    if matches!(kind, "tool.waiting" | "tool.complete") && meta::text(value, "toolUseId").is_none() {
        return false;
    }
    match object.get("usage") {
        Some(usage) => {
            let clean = if usage.is_object() { SessionUsage::from_json(usage) } else { None };
            if kind != "session.usage"
                || clean.as_ref().map(|c| c.provider.as_str()) != Some("claude")
                || clean.as_ref().and_then(|c| c.provider_session_id.as_deref()) != meta::text(value, "claudeSessionId")
            {
                return false;
            }
        }
        None if kind == "session.usage" => return false,
        None => {}
    }
    match object.get("graph") {
        Some(graph) => graph_request && matches!(kind, "agent.spawn" | "agent.complete") && valid_graph(graph, kind),
        None => !matches!(kind, "agent.spawn" | "agent.complete"),
    }
}

/// `ModBridge.ValidGraph`.
fn valid_graph(graph: &Value, kind: &str) -> bool {
    let Some(object) = graph.as_object() else { return false };
    if object.keys().any(|k| !graph_keys().contains(k.as_str())) || !version_one(graph) {
        return false;
    }
    let phase = meta::text(graph, "phase");
    let phase_valid = if kind == "agent.spawn" {
        matches!(phase, Some("starting" | "running" | "error" | "stopped"))
    } else {
        matches!(phase, Some("completed" | "error" | "stopped"))
    };
    if !phase_valid {
        return false;
    }
    for key in ["agentId", "parentAgentId", "parentToolUseId"] {
        if let Some(raw) = object.get(key).filter(|v| !v.is_null())
            && (!wire::is_identifier(raw.as_str()) || !clean_text(raw, 128, true))
        {
            return false;
        }
    }
    let agent_id = meta::text(graph, "agentId");
    if agent_id.is_some() && agent_id == meta::text(graph, "parentAgentId") {
        return false;
    }
    if kind == "agent.spawn" && meta::text(graph, "parentToolUseId").is_none() {
        return false;
    }
    if (kind == "agent.complete" || phase == Some("running")) && agent_id.is_none() {
        return false;
    }
    for (key, bytes, line) in
        [("name", 160, true), ("agentType", 160, true), ("model", 200, true), ("input", 16_384, false), ("output", 32_768, false)]
    {
        if let Some(raw) = object.get(key).filter(|v| !v.is_null())
            && !clean_text(raw, bytes, line)
        {
            return false;
        }
    }
    meta::text(graph, "model").is_none_or(wire::model)
}
