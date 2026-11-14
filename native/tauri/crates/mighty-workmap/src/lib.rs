//! The work map of docs/work-map.md (C# `WorkMap`, `WorkMapSummary`, `WorkMapSummaryStore`, `WorkMapService`, MightyCode.Core/WorkMap.cs
//! and WorkMapSummary.cs; core catalog subsystem 34): the git history of a workspace split by version tag, read by platform, release
//! state and the user's own requests, with topic summaries made by one headless Claude run per version. The rules are held to
//! native/contracts/fixtures/work-map.json, as MightyCore's WorkMap.swift holds them. Everything here only reads the repository; git
//! and Claude start through the platform's `ProcessHost`.
//!
//! Also the rest of C# `WorkDashboard` (`dashboard`): the counts the shell model does not already take from mighty-core.

pub mod dashboard;
pub mod git;
pub mod originals;
pub mod service;
pub mod summary;

use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::LazyLock;

use indexmap::IndexMap;
use mighty_core::model::RunSession;
use mighty_core::time::{self, DateTimeOffset};
use mighty_core::wire;
use regex::Regex;
use serde::Serialize;
use serde_json::Value;

pub use git::Git;
pub use service::{WorkMapService, WorkMapStart};
pub use summary::{SummaryError, WorkMapSummaryStore};

/// `WorkMapCommit(Sha, Date, Lanes, Scope, Subject, Files, FilesCount)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkMapCommit {
    pub sha: String,
    pub date: String,
    pub lanes: Vec<String>,
    pub scope: Option<String>,
    pub subject: String,
    pub files: Vec<String>,
    pub files_count: i32,
}

/// `WorkMapRequest(Id, PaneId, Text, Date, Files, Commits)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkMapRequest {
    pub id: String,
    pub pane_id: String,
    pub text: String,
    pub date: Option<String>,
    pub files: Vec<String>,
    pub commits: Vec<String>,
}

/// `WorkMapTopic(Title, Summary, Lanes, Commits, Requests)`.
#[derive(Debug, Clone, PartialEq, Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkMapTopic {
    pub title: String,
    pub summary: String,
    pub lanes: Vec<String>,
    pub commits: Vec<String>,
    pub requests: Vec<String>,
}

/// `WorkMapVersion`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkMapVersion {
    pub key: String,
    pub version: Option<String>,
    pub tag: Option<String>,
    pub date: Option<String>,
    pub status: String,
    pub release_step: Option<String>,
    pub range_end: String,
    pub total: i32,
    pub truncated: bool,
    pub counts: IndexMap<String, i32>,
    pub commits: Vec<WorkMapCommit>,
    pub topics: Option<Vec<WorkMapTopic>>,
    pub topics_stale: bool,
    pub summary_error: Option<String>,
    pub requests: Vec<WorkMapRequest>,
}

/// `WorkMapModel(Available, Reason, Head, Lanes, Summarizing, Versions)`: the model as the page and the phone read it — camelCase, every
/// field present (a missing one reads as null on both: C# `WorkMap.Json` writes nulls, as serde does).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkMapModel {
    pub available: bool,
    pub reason: Option<String>,
    pub head: Option<String>,
    pub lanes: Vec<String>,
    pub summarizing: Option<String>,
    pub versions: Vec<WorkMapVersion>,
}

impl WorkMapModel {
    pub fn unavailable(reason: &str) -> WorkMapModel {
        WorkMapModel { available: false, reason: Some(reason.into()), head: None, lanes: lanes(), summarizing: None, versions: vec![] }
    }

    /// The JSON the bridge and the push carry (`JsonSerializer.SerializeToElement(model, WorkMap.Json)`).
    pub fn to_value(&self) -> Value {
        serde_json::to_value(self).unwrap_or(Value::Null)
    }
}

/// `WorkMapRequestInput(PaneId, RunId, Text, Date, Files)`: one run of an open pane the user typed a request in, with what it edited.
#[derive(Debug, Clone, PartialEq)]
pub struct WorkMapRequestInput {
    pub pane_id: String,
    pub run_id: String,
    pub text: String,
    pub date: Option<String>,
    pub files: Vec<String>,
}

/// `WorkMapStoredSummary(RangeEnd, Topics, Created)`: a saved topic summary of one version.
#[derive(Debug, Clone, PartialEq)]
pub struct WorkMapStoredSummary {
    pub range_end: String,
    pub topics: Vec<WorkMapTopic>,
    pub created: String,
}

/// `WorkMapPaneKind`: the work map's pane, a RunSession of its own kind that runs nothing and is never saved, one per workspace.
pub mod pane {
    pub const KIND: &str = "workmap";
    pub fn pane_id(workspace_id: &str) -> String {
        format!("{KIND}:{workspace_id}")
    }
    pub fn is_work_map(kind: Option<&str>) -> bool {
        kind == Some(KIND)
    }
}

pub const MAXIMUM_VERSIONS: usize = 12;
pub const MAXIMUM_COMMITS_PER_VERSION: usize = 400;
pub const MAXIMUM_FILES_PER_COMMIT: usize = 50;
pub const MAXIMUM_REQUESTS: usize = 200;
pub const MAXIMUM_COMMITS_PER_REQUEST: usize = 20;
pub const REQUEST_TEXT_LENGTH: usize = 200;
pub const GIT_TIMEOUT_SECONDS: f64 = 10.0;
pub const LANES: [&str; 5] = ["mac", "windows", "phone", "ci", "etc"];
pub const RELEASE_STEPS: [&str; 10] = ["check", "version", "push", "ci", "download", "manifest", "page", "upload", "github", "verify"];

pub fn lanes() -> Vec<String> {
    LANES.iter().map(|l| l.to_string()).collect()
}

static TAG_PATTERN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^v\d+\.\d+\.\d+$").expect("regex"));
static VERSION_PATTERN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^\d+\.\d+\.\d+$").expect("regex"));
static PAREN_SCOPE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?s)^\(([^)]*)\)\s*(.*)$").expect("regex"));
static CONVENTIONAL_SCOPE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?s)^\w[\w-]*\(([^)]*)\)!?:\s*(.*)$").expect("regex"));

// ── rules ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `ParseSubject`: the platforms a commit title's scope names, in [`LANES`] order, and the scope and title it leaves.
pub fn parse_subject(subject: &str) -> (Vec<String>, Option<String>, String) {
    let (mut scope, mut text) = (None, subject.to_string());
    if let Some(paren) = PAREN_SCOPE.captures(subject) {
        scope = Some(paren[1].to_string());
        text = paren[2].to_string();
    } else if let Some(conventional) = CONVENTIONAL_SCOPE.captures(subject) {
        scope = Some(conventional[1].to_string());
        text = conventional[2].to_string();
    }
    let mut found = HashSet::new();
    for range in scope.as_deref().unwrap_or("").split(',') {
        let word = range.split_whitespace().next().map(str::to_lowercase);
        let lane = match word.as_deref() {
            Some("mac" | "macos") => Some("mac"),
            Some("windows" | "web" | "winui") => Some("windows"),
            Some("phone" | "mobile" | "android") => Some("phone"),
            Some("ci" | "release") => Some("ci"),
            _ => None,
        };
        if let Some(lane) = lane {
            found.insert(lane);
        }
    }
    let lanes = if found.is_empty() {
        vec!["etc".to_string()]
    } else {
        LANES.iter().filter(|l| found.contains(*l)).map(|l| l.to_string()).collect()
    };
    (lanes, scope, text)
}

fn numbers(text: &str) -> (i32, i32, i32) {
    let parts: Vec<i32> =
        text.split('.').map(|p| if !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()) { p.parse().unwrap_or(0) } else { 0 }).collect();
    (parts.first().copied().unwrap_or(0), parts.get(1).copied().unwrap_or(0), parts.get(2).copied().unwrap_or(0))
}

fn tag_version(name: &str) -> (i32, i32, i32) {
    numbers(name.trim_start_matches('v'))
}

/// `Tags`: the release tags among `names` (vX.Y.Z only), newest first by version number.
pub fn tags<'a>(names: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let mut kept: Vec<String> = names.into_iter().filter(|n| TAG_PATTERN.is_match(n)).map(str::to_string).collect();
    kept.sort_by_key(|t| std::cmp::Reverse(tag_version(t)));
    kept
}

/// `VersionName`: the name of the version being worked on — the VERSION file when it holds x.y.z above the newest tag, else None.
pub fn version_name(file: Option<&str>, newest_tag: Option<&str>) -> Option<String> {
    let text = file?.trim();
    if !VERSION_PATTERN.is_match(text) {
        return None;
    }
    newest_tag.is_none_or(|tag| numbers(text) > tag_version(tag)).then(|| text.to_string())
}

/// `ReleaseStatus`: "releasing" and the step still to do when `release/<version>/state.json` says the push is done and the verify is
/// not; otherwise "unreleased". Anything unreadable is not a release.
pub fn release_status(state_json: Option<&str>) -> (String, Option<String>) {
    let unreleased = ("unreleased".to_string(), None);
    let Some(text) = state_json else { return unreleased };
    let Ok(Value::Object(root)) = serde_json::from_str::<Value>(text) else { return unreleased };
    let Some(Value::Object(done)) = root.get("done") else { return unreleased };
    let finished = |step: &str| done.get(step) == Some(&Value::Bool(true));
    if !finished("push") || finished("verify") {
        return unreleased;
    }
    ("releasing".into(), RELEASE_STEPS.iter().find(|s| !finished(s)).map(|s| s.to_string()))
}

/// `DotSize`: the overview dot's diameter, `6 + 14·√(n / largest)`, 0 for no commits.
pub fn dot_size(count: i32, largest: i32) -> i32 {
    if count <= 0 || largest <= 0 {
        return 0;
    }
    (6.0 + 14.0 * (count as f64 / largest as f64).sqrt()).round() as i32
}

/// `Short`: a sha's first 8 characters.
pub fn short(sha: &str) -> String {
    sha.chars().take(8).collect()
}

/// `ParseLog`: the commits of `git log --format=%x1e%H%x1f%aI%x1f%s --name-only`, newest first as git lists them.
pub fn parse_log(output: &str) -> Vec<WorkMapCommit> {
    let mut commits = vec![];
    for chunk in output.split('\u{1e}').filter(|c| !c.is_empty()) {
        let cleaned = chunk.replace('\r', "");
        let lines: Vec<&str> = cleaned.split('\n').collect();
        let fields: Vec<&str> = lines[0].splitn(3, '\u{1f}').collect();
        if fields.len() < 3 || fields[0].encode_utf16().count() < 8 {
            continue;
        }
        let files: Vec<String> = lines[1..].iter().filter(|l| !l.is_empty()).map(|l| l.to_string()).collect();
        let (lanes, scope, text) = parse_subject(fields[2]);
        commits.push(WorkMapCommit {
            sha: short(fields[0]),
            date: fields[1].to_string(),
            lanes,
            scope,
            subject: text,
            files_count: files.len() as i32,
            files: files.into_iter().take(MAXIMUM_FILES_PER_COMMIT).collect(),
        });
    }
    commits
}

fn when(text: Option<&str>) -> Option<DateTimeOffset> {
    time::parse(text)
}

fn ticks(value: Option<DateTimeOffset>) -> i64 {
    value.map_or(i64::MIN, |v| v.utc_ticks())
}

/// Whether two paths are the same file as this OS compares them (`OrdinalIgnoreCase` on Windows).
fn same_file(a: &str, b: &str) -> bool {
    if std::path::MAIN_SEPARATOR == '\\' { mighty_core::normalize::ordinal_ignore_case(a, b).is_eq() } else { a == b }
}

/// `Attach`: puts each request in the version of the oldest commit that followed it, as docs/work-map.md says.
pub fn attach(versions: Vec<WorkMapVersion>, inputs: &[WorkMapRequestInput], newest_tag_date: Option<&str>) -> Vec<WorkMapVersion> {
    let mut placed: HashMap<String, Vec<WorkMapRequest>> = HashMap::new();
    let mut ordered: Vec<(&WorkMapRequestInput, Option<DateTimeOffset>)> = inputs.iter().map(|i| (i, when(i.date.as_deref()))).collect();
    ordered.sort_by_key(|(_, at)| std::cmp::Reverse(ticks(*at)));
    ordered.truncate(MAXIMUM_REQUESTS);
    // Every commit, oldest first, with the version it lies in.
    let mut commits: Vec<(&str, &WorkMapCommit, Option<DateTimeOffset>)> =
        versions.iter().flat_map(|v| v.commits.iter().map(move |c| (v.key.as_str(), c, when(Some(&c.date))))).collect();
    commits.sort_by_key(|(_, _, at)| ticks(*at));
    let has_next = versions.iter().any(|v| v.key == "next");
    for (input, at) in ordered {
        let mut files: Vec<String> = vec![];
        for file in input.files.iter().map(|f| f.replace('\\', "/")) {
            if !files.iter().any(|known| same_file(known, &file)) {
                files.push(file);
            }
        }
        let followed: Vec<&(&str, &WorkMapCommit, Option<DateTimeOffset>)> = if files.is_empty() {
            vec![]
        } else {
            commits
                .iter()
                .filter(|(_, commit, time)| {
                    (at.is_none() || matches!((time, at), (Some(t), Some(a)) if t.utc_ticks() > a.utc_ticks()))
                        && commit.files.iter().any(|f| files.iter().any(|g| same_file(g, f)))
                })
                .take(MAXIMUM_COMMITS_PER_REQUEST)
                .collect()
        };
        let key = match followed.first() {
            Some((key, _, _)) => Some(key.to_string()),
            None => match (&input.date, at) {
                (Some(date), Some(_)) if has_next => {
                    let day: String = date.chars().take(10).collect();
                    newest_tag_date.is_none_or(|tag| mighty_core::normalize::ordinal(&day, tag).is_ge()).then(|| "next".to_string())
                }
                _ => None,
            },
        };
        let Some(key) = key else { continue };
        placed.entry(key).or_default().push(WorkMapRequest {
            id: format!("{}/{}", input.pane_id, input.run_id),
            pane_id: input.pane_id.clone(),
            text: input.text.clone(),
            date: input.date.clone(),
            files: files.clone(),
            commits: followed.iter().map(|(_, c, _)| c.sha.clone()).collect(),
        });
    }
    versions
        .into_iter()
        .map(|v| match placed.remove(&v.key) {
            Some(list) => WorkMapVersion { requests: list, ..v },
            None => v,
        })
        .collect()
}

/// `Requests`: the requests of the workspace's panes — every run of every pane with a graph that the user started (no notice run, no
/// empty input), with the files it edited (`files(pane, run, root)`, the file originals' record of the run) under the workspace's folder.
pub fn requests<'a>(
    sessions: impl IntoIterator<Item = &'a RunSession>,
    workspace_id: &str,
    root: &str,
    files: &dyn Fn(&str, &str, &str) -> Vec<String>,
) -> Vec<WorkMapRequestInput> {
    let mut inputs = vec![];
    for session in sessions.into_iter().filter(|s| s.workspace_id() == workspace_id && s.kind() == "claude") {
        for run in session.graph_runs() {
            if run.is_notice() || run.input().trim().is_empty() || !wire::identifier(run.id()) {
                continue;
            }
            let text = run.input().trim();
            let units: Vec<u16> = text.encode_utf16().take(REQUEST_TEXT_LENGTH).collect();
            inputs.push(WorkMapRequestInput {
                pane_id: session.id().to_string(),
                run_id: run.id().to_string(),
                text: String::from_utf16_lossy(&units),
                date: run.root_entries().first().and_then(|e| e.timestamp.clone()),
                files: files(session.id(), run.source_run_id.as_deref().unwrap_or(run.id()), root),
            });
        }
    }
    inputs
}

// ── git ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

#[allow(clippy::too_many_arguments)]
fn version(
    key: &str,
    name: Option<String>,
    tag: Option<String>,
    date: Option<String>,
    (status, step): (String, Option<String>),
    range_end: &str,
    commits: Vec<WorkMapCommit>,
    total: i32,
) -> WorkMapVersion {
    let mut counts = IndexMap::new();
    for lane in LANES {
        let n = commits.iter().filter(|c| c.lanes.iter().any(|l| l == lane)).count() as i32;
        if n > 0 {
            counts.insert(lane.to_string(), n);
        }
    }
    WorkMapVersion {
        key: key.into(),
        version: name,
        tag,
        date,
        status,
        release_step: step,
        range_end: range_end.into(),
        total,
        truncated: total > commits.len() as i32,
        counts,
        commits,
        topics: None,
        topics_stale: false,
        summary_error: None,
        requests: vec![],
    }
}

/// The largest VERSION file the map reads.
pub const VERSION_BYTES: usize = 1_024;
/// The largest release state the map reads, and how much of the release notes a summary reads (it keeps far fewer characters).
pub const RELEASE_FILE_BYTES: usize = 65_536;

/// `File.ReadAllTextAsync`'s text: UTF-8, its byte order mark dropped.
fn text_of(bytes: &[u8]) -> String {
    let text = String::from_utf8_lossy(bytes).into_owned();
    text.strip_prefix('\u{feff}').map(str::to_string).unwrap_or(text)
}

/// A file of the repository at `relative` (`/`-separated) under `root`, through the workspace reader (mighty-composer `read_under`: no
/// link, nothing outside the folder, a regular file only); None when it is not such a file or holds more than `limit` bytes.
pub fn read_text(root: &Path, relative: &str, limit: usize) -> Option<String> {
    let (_, bytes) = mighty_composer::files::read_under(relative, root, limit + 1).ok()?;
    (bytes.len() <= limit).then(|| text_of(&bytes))
}

/// The first `limit` bytes of such a file (the release notes, which a summary cuts anyway).
pub fn read_start(root: &Path, relative: &str, limit: usize) -> Option<String> {
    mighty_composer::files::read_under(relative, root, limit).ok().map(|(_, bytes)| text_of(&bytes))
}

async fn read_commits(git: &Git, root: &Path, range: &str) -> Option<(Vec<WorkMapCommit>, i32)> {
    let limit = MAXIMUM_COMMITS_PER_VERSION.to_string();
    let log = git
        .run(
            &[
                "-c",
                "core.quotepath=false",
                "log",
                "--no-merges",
                "--no-show-signature",
                "--format=%x1e%H%x1f%aI%x1f%s",
                "--name-only",
                "-n",
                &limit,
                range,
                "--",
            ],
            root,
            GIT_TIMEOUT_SECONDS,
        )
        .await
        .filter(|r| r.exit_code == 0)?;
    let commits = parse_log(&log.stdout);
    let mut total = commits.len() as i32;
    if commits.len() >= MAXIMUM_COMMITS_PER_VERSION {
        let count =
            git.run(&["rev-list", "--count", "--no-merges", range, "--"], root, GIT_TIMEOUT_SECONDS).await.filter(|r| r.exit_code == 0)?;
        total = git::line(&count.stdout).parse().ok().filter(|_| git::line(&count.stdout).bytes().all(|b| b.is_ascii_digit()))?;
    }
    Some((commits, total))
}

/// `WorkMap.BuildAsync`: reads the git history of `root` into the model; the topic summaries and requests come from the caller.
pub async fn build(
    git: &Git,
    root: &Path,
    requests: &[WorkMapRequestInput],
    stored: &IndexMap<String, WorkMapStoredSummary>,
    summarizing: Option<&str>,
    errors: &HashMap<String, String>,
) -> WorkMapModel {
    let Some(inside) = git.run(&["rev-parse", "--is-inside-work-tree"], root, GIT_TIMEOUT_SECONDS).await else {
        return WorkMapModel::unavailable("gitFailed");
    };
    if inside.exit_code != 0 {
        return WorkMapModel::unavailable(if inside.stderr.to_lowercase().contains("not a git repository") {
            "notGit"
        } else {
            "gitFailed"
        });
    }
    let Some(head) = git.run(&["rev-parse", "HEAD"], root, GIT_TIMEOUT_SECONDS).await else {
        return WorkMapModel::unavailable("gitFailed");
    };
    // A repository with no commit yet has nothing to show, and nothing has failed.
    if head.exit_code != 0 {
        return WorkMapModel {
            available: true,
            reason: None,
            head: None,
            lanes: lanes(),
            summarizing: summarizing.map(str::to_string),
            versions: vec![],
        };
    }
    let head_sha = git::line(&head.stdout).to_string();
    let Some(refs) = git
        .run(
            &["for-each-ref", "refs/tags", "--format=%(refname:short)%1f%(creatordate:iso-strict)%1f%(*objectname)%1f%(objectname)"],
            root,
            GIT_TIMEOUT_SECONDS,
        )
        .await
        .filter(|r| r.exit_code == 0)
    else {
        return WorkMapModel::unavailable("gitFailed");
    };
    // In git's order (by name), as C#'s dictionary keeps it: tags of the same version number stay in that order.
    let mut tag_info: IndexMap<String, (String, String)> = IndexMap::new();
    for line in refs.stdout.split('\n').filter(|l| !l.is_empty()) {
        let fields: Vec<&str> = line.trim_end_matches('\r').split('\u{1f}').collect();
        if fields.len() < 4 || !TAG_PATTERN.is_match(fields[0]) {
            continue;
        }
        let date: String =
            if fields[1].encode_utf16().count() >= 10 { fields[1].chars().take(10).collect() } else { fields[1].to_string() };
        let sha = if fields[2].is_empty() { fields[3] } else { fields[2] };
        tag_info.insert(fields[0].to_string(), (date, sha.to_string()));
    }
    let tags = tags(tag_info.keys().map(String::as_str));
    let visible: Vec<String> = tags.iter().take(MAXIMUM_VERSIONS).cloned().collect();
    // Each version's range is read; a failing one fails the map.
    let mut results = vec![];
    for (i, tag) in visible.iter().enumerate() {
        let range = match tags.get(i + 1) {
            Some(below) => format!("{below}..{tag}"),
            None => tag.clone(),
        };
        let Some(read) = read_commits(git, root, &range).await else { return WorkMapModel::unavailable("gitFailed") };
        results.push(read);
    }
    let newest = tags.first().cloned();
    let next_range = match &newest {
        Some(tag) => format!("{tag}..HEAD"),
        None => "HEAD".to_string(),
    };
    let Some((next_commits, next_total)) = read_commits(git, root, &next_range).await else {
        return WorkMapModel::unavailable("gitFailed");
    };

    let mut versions = vec![];
    if !next_commits.is_empty() {
        let name = version_name(read_text(root, "VERSION", VERSION_BYTES).as_deref(), newest.as_deref());
        let status = match &name {
            Some(name) => release_status(read_text(root, &format!("release/{name}/state.json"), RELEASE_FILE_BYTES).as_deref()),
            None => ("unreleased".to_string(), None),
        };
        let date = Some(&next_commits[0].date).filter(|d| d.encode_utf16().count() >= 10).map(|d| d.chars().take(10).collect());
        versions.push(version("next", name, None, date, status, &head_sha, next_commits, next_total));
    }
    for (tag, (commits, total)) in visible.iter().zip(results) {
        let (date, sha) = tag_info[tag].clone();
        let name = tag.trim_start_matches('v').to_string();
        versions.push(version(tag, Some(name), Some(tag.clone()), Some(date), ("released".into(), None), &sha, commits, total));
    }
    let newest_date = newest.as_ref().map(|tag| tag_info[tag].0.clone());
    let versions = attach(versions, requests, newest_date.as_deref())
        .into_iter()
        .map(|version| {
            let saved = stored.get(&version.key);
            WorkMapVersion {
                topics: saved.map(|s| s.topics.clone()),
                topics_stale: saved.is_some_and(|s| s.range_end != version.range_end),
                summary_error: errors.get(&version.key).cloned(),
                ..version
            }
        })
        .collect();
    WorkMapModel {
        available: true,
        reason: None,
        head: Some(head_sha.chars().take(8).collect()),
        lanes: lanes(),
        summarizing: summarizing.map(str::to_string),
        versions,
    }
}
