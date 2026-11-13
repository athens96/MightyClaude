//! The topic summary of docs/work-map.md (C# `WorkMapSummary`, `WorkMapSummaryStore`, WorkMapSummary.cs): one headless Claude run per
//! version — the model list's own launch, a temporary working folder, 120 seconds — its answer read back by the rules of the fixture's
//! `summaryCases`, and the summaries saved per workspace.

use std::collections::{BTreeMap, HashMap, HashSet};

use indexmap::IndexMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::Duration;

use mighty_core::json::JsonOut;
use mighty_core::locale::Locale;
use mighty_platform::ProcessHost;
use mighty_run::model::CliCommand;
use regex::Regex;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};

use crate::{LANES, WorkMapCommit, WorkMapRequest, WorkMapStoredSummary, WorkMapTopic};

pub const MAXIMUM_TOPICS: usize = 8;
pub const TITLE_LENGTH: usize = 40;
pub const SUMMARY_LENGTH: usize = 160;
pub const NOTES_LENGTH: usize = 8000;
pub const TIMEOUT: Duration = Duration::from_secs(120);

/// `WorkMapSummaryException`: a summary that did not come out; its key is the locale key of the words the version's card shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SummaryError {
    Cli,
    Timeout,
    Output,
}

impl SummaryError {
    pub fn key(self) -> &'static str {
        match self {
            SummaryError::Cli => "workMap.error.cli",
            SummaryError::Timeout => "workMap.error.timeout",
            SummaryError::Output => "workMap.error.output",
        }
    }
}

/// The fixture's `prompt` (the checks compare them).
pub const PROMPT_TEMPLATE: &str = "You group one software version's commits into topics for a project overview.\nAnswer in {language}. Reply with JSON only, no prose: {\"topics\":[{\"title\":\"…\",\"summary\":\"…\",\"commits\":[\"sha8\"],\"requests\":[\"id\"]}]}\nRules: 3 to 6 topics; a title of at most 6 words names a user-visible feature or fix; a summary of one sentence says what changed for the user; every commit sha8 belongs to exactly one topic; put a request id under the topic it asked for.\nVersion: {version}\nCommits (sha8 [lanes] scope: subject):\n{commits}\nRequests the user sent (id: text):\n{requests}\nRelease notes:\n{notes}";

/// `LanguageName`: the app language's English name.
pub fn language_name(language: &str) -> &'static str {
    match language {
        "ko" => "Korean",
        "ja" => "Japanese",
        "zh" => "Chinese",
        _ => "English",
    }
}

static PLACEHOLDER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\{(language|version|commits|requests|notes)\}").expect("regex"));

/// `Prompt`: the template with each placeholder filled in one pass, so nothing a commit title says is read as a placeholder.
pub fn prompt(language: &str, version: &str, commits: &[WorkMapCommit], requests: &[WorkMapRequest], notes: Option<&str>) -> String {
    let commit_lines = commits
        .iter()
        .map(|c| {
            let scope = c.scope.as_deref().filter(|s| !s.is_empty()).map(|s| format!("{s}: ")).unwrap_or_default();
            format!("{} [{}] {scope}{}", c.sha, c.lanes.join(","), c.subject)
        })
        .collect::<Vec<_>>()
        .join("\n");
    let request_lines = requests.iter().map(|r| format!("{}: {}", r.id, r.text.replace('\n', " "))).collect::<Vec<_>>().join("\n");
    let notes = match notes {
        Some(n) if !n.trim().is_empty() => cut(n, NOTES_LENGTH),
        _ => "(none)".to_string(),
    };
    let values: HashMap<&str, String> = HashMap::from([
        ("language", language.to_string()),
        ("version", version.to_string()),
        ("commits", if commit_lines.is_empty() { "(none)".into() } else { commit_lines }),
        ("requests", if request_lines.is_empty() { "(none)".into() } else { request_lines }),
        ("notes", notes),
    ]);
    PLACEHOLDER.replace_all(PROMPT_TEMPLATE, |c: &regex::Captures| values[&c[1]].clone()).into_owned()
}

/// `Cut`: the text cut to `length` Unicode scalar values.
pub fn cut(text: &str, length: usize) -> String {
    text.chars().take(length).collect()
}

/// `FirstObject`: the first `{` to its matching `}`, strings and escapes respected; None when it never closes.
pub fn first_object(answer: &str) -> Option<&str> {
    let start = answer.find('{')?;
    let (mut depth, mut in_string, mut escaped) = (0i32, false, false);
    for (i, c) in answer[start..].char_indices() {
        if in_string {
            if escaped {
                escaped = false;
            } else if c == '\\' {
                escaped = true;
            } else if c == '"' {
                in_string = false;
            }
            continue;
        }
        if c == '"' {
            in_string = true;
        } else if c == '{' {
            depth += 1;
        } else if c == '}' {
            depth -= 1;
            if depth == 0 {
                return Some(&answer[start..start + i + 1]);
            }
        }
    }
    None
}

fn row_text(row: &Value, name: &str) -> String {
    row.get(name).and_then(Value::as_str).map(|s| s.trim().to_string()).unwrap_or_default()
}

fn row_strings(row: &Value, name: &str) -> Vec<String> {
    row.get(name).and_then(Value::as_array).map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default()
}

/// `ParseAnswer`: the topics of an answer over `commits` (sha, lanes) and `request_ids`; `Output` when none is readable.
pub fn parse_answer(
    answer: &str,
    commits: &[(String, Vec<String>)],
    request_ids: &HashSet<String>,
    other_title: &str,
) -> Result<Vec<WorkMapTopic>, SummaryError> {
    let json = first_object(answer).ok_or(SummaryError::Output)?;
    let known: HashMap<&str, &Vec<String>> = commits.iter().map(|(sha, lanes)| (sha.as_str(), lanes)).collect();
    let lane_union = |shas: &[String]| -> Vec<String> {
        let set: HashSet<&str> = shas.iter().filter_map(|s| known.get(s.as_str())).flat_map(|l| l.iter().map(String::as_str)).collect();
        LANES.iter().filter(|l| set.contains(*l)).map(|l| l.to_string()).collect()
    };
    let root: Value = serde_json::from_str(json).map_err(|_| SummaryError::Output)?;
    let rows = root.as_object().and_then(|o| o.get("topics")).and_then(Value::as_array).ok_or(SummaryError::Output)?;
    let mut taken: HashSet<String> = HashSet::new();
    let mut topics = vec![];
    for row in rows {
        if topics.len() == MAXIMUM_TOPICS {
            break;
        }
        if !row.is_object() {
            continue;
        }
        let title = row_text(row, "title");
        if title.is_empty() {
            continue;
        }
        let shas: Vec<String> =
            row_strings(row, "commits").into_iter().filter(|s| known.contains_key(s.as_str()) && taken.insert(s.clone())).collect();
        if shas.is_empty() {
            continue;
        }
        let mut requests: Vec<String> = vec![];
        for id in row_strings(row, "requests").into_iter().filter(|r| request_ids.contains(r)) {
            if !requests.contains(&id) {
                requests.push(id);
            }
        }
        topics.push(WorkMapTopic {
            title: cut(&title, TITLE_LENGTH),
            summary: cut(&row_text(row, "summary"), SUMMARY_LENGTH),
            lanes: lane_union(&shas),
            commits: shas,
            requests,
        });
    }
    if topics.is_empty() {
        return Err(SummaryError::Output);
    }
    let rest: Vec<String> = commits.iter().map(|(sha, _)| sha.clone()).filter(|s| !taken.contains(s)).collect();
    if !rest.is_empty() {
        topics.push(WorkMapTopic {
            title: other_title.into(),
            summary: String::new(),
            lanes: lane_union(&rest),
            commits: rest,
            requests: vec![],
        });
    }
    Ok(topics)
}

/// `ResultText`: the text of the `result` event in Claude's stream-json output; None while there is none.
pub fn result_text(line: &str) -> Result<Option<String>, SummaryError> {
    let Ok(root) = serde_json::from_str::<Value>(line) else { return Ok(None) };
    if !root.is_object() || mighty_core::json::text(&root, "type") != Some("result") {
        return Ok(None);
    }
    if root.get("is_error") == Some(&Value::Bool(true)) {
        return Err(SummaryError::Output);
    }
    mighty_core::json::text(&root, "result").map(|t| Some(t.to_string())).ok_or(SummaryError::Output)
}

/// `RunAsync`: asks Claude once and returns what it answered. The folder it runs in is a temporary one, not the workspace. `environment`
/// is `ProviderCatalog.ClaudeHeadlessEnvironment`.
pub async fn run(
    processes: &dyn ProcessHost,
    command: &CliCommand,
    environment: &BTreeMap<String, String>,
    prompt: &str,
    timeout: Duration,
) -> Result<String, SummaryError> {
    let folder = std::env::temp_dir().join(format!("mighty-work-map-{}", mighty_core::wire::new_id()));
    let outcome = async {
        std::fs::create_dir_all(&folder).map_err(|_| SummaryError::Cli)?;
        let mut args = command.prefix.clone();
        args.extend(mighty_run::providers::CLAUDE_HEADLESS_ARGUMENTS.iter().map(|a| a.to_string()));
        let request = processes.cli_request(Path::new(&command.binary), &args, &folder, environment).map_err(|_| SummaryError::Cli)?;
        let mut child = processes.spawn(request).await.map_err(|_| SummaryError::Cli)?;
        let control = child.control();
        // The error stream is drained so the CLI never blocks on it.
        if let Some(mut stderr) = child.stderr.take() {
            tokio::spawn(async move {
                let mut buffer = [0u8; 4096];
                while let Ok(read) = stderr.read(&mut buffer).await {
                    if read == 0 {
                        break;
                    }
                }
            });
        }
        let work = async {
            let mut stdin = child.stdin.take().ok_or(SummaryError::Cli)?;
            let message = json!({"type": "user", "message": {"role": "user", "content": prompt}});
            stdin.write_all(format!("{message}\n").as_bytes()).await.map_err(|_| SummaryError::Cli)?;
            stdin.flush().await.map_err(|_| SummaryError::Cli)?;
            let stdout = child.stdout.take().ok_or(SummaryError::Cli)?;
            let mut lines = BufReader::new(stdout).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                if let Some(text) = result_text(&line)? {
                    return Ok(text);
                }
            }
            drop(stdin);
            Err(SummaryError::Output)
        };
        let answer = match tokio::time::timeout(timeout, work).await {
            Ok(answer) => answer,
            Err(_) => Err(SummaryError::Timeout),
        };
        control.kill();
        let _ = control.close().await;
        answer
    }
    .await;
    if folder.exists() {
        let _ = std::fs::remove_dir_all(&folder);
    }
    outcome
}

/// `WorkMapSummaryStore`: the saved summaries of one workspace, `<data>/work-map/<sha256 of its real path>.json`, written through a
/// temporary name.
pub struct WorkMapSummaryStore {
    directory: PathBuf,
    sync: Mutex<()>,
}

impl WorkMapSummaryStore {
    pub fn new(directory: PathBuf) -> WorkMapSummaryStore {
        WorkMapSummaryStore { directory, sync: Mutex::new(()) }
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub fn path_for(&self, root: &str) -> PathBuf {
        let real =
            mighty_core::workspace_files::real_path(Path::new(root)).map(|p| p.to_string_lossy().into_owned()).unwrap_or(root.into());
        let hash: String = Sha256::digest(real.as_bytes()).iter().map(|b| format!("{b:02x}")).collect();
        self.directory.join(format!("{hash}.json"))
    }

    /// The saved versions in the file's order (C#'s dictionary keeps it, and a write adds after them).
    fn read_unlocked(&self, root: &str) -> IndexMap<String, WorkMapStoredSummary> {
        let Ok(bytes) = std::fs::read(self.path_for(root)) else { return IndexMap::new() };
        let Ok(saved) = serde_json::from_slice::<Value>(&bytes) else { return IndexMap::new() };
        let versions = property(&saved, "versions").and_then(Value::as_object);
        let mut out = IndexMap::new();
        for (key, value) in versions.into_iter().flatten() {
            let (Some(range_end), Some(topics)) = (property(value, "rangeEnd").and_then(Value::as_str), property(value, "topics")) else {
                continue;
            };
            let Ok(topics) = serde_json::from_value::<Vec<StoredTopic>>(topics.clone()) else { return IndexMap::new() };
            out.insert(
                key.clone(),
                WorkMapStoredSummary {
                    range_end: range_end.to_string(),
                    topics: topics.into_iter().map(StoredTopic::into_topic).collect(),
                    created: property(value, "created").and_then(Value::as_str).unwrap_or("").to_string(),
                },
            );
        }
        out
    }

    pub fn read(&self, root: &str) -> IndexMap<String, WorkMapStoredSummary> {
        let _guard = self.sync.lock().unwrap_or_else(|p| p.into_inner());
        self.read_unlocked(root)
    }

    pub fn write(&self, root: &str, key: &str, range_end: &str, topics: &[WorkMapTopic]) -> std::io::Result<()> {
        let _guard = self.sync.lock().unwrap_or_else(|p| p.into_inner());
        let mut versions = self.read_unlocked(root);
        let summary = WorkMapStoredSummary { range_end: range_end.into(), topics: topics.to_vec(), created: mighty_core::time::now_o() };
        versions.insert(key.to_string(), summary);
        // `JsonSerializer.SerializeToUtf8Bytes(new Saved(versions), Wire.Json)`: camelCase, escaped as C# escapes.
        let mut out = JsonOut::new();
        out.begin_object();
        out.name("versions");
        out.begin_object();
        for (key, saved) in &versions {
            out.name(key);
            out.begin_object();
            out.prop_str("rangeEnd", Some(&saved.range_end));
            out.name("topics");
            out.value(&serde_json::to_value(&saved.topics).unwrap_or(Value::Null));
            out.prop_str("created", Some(&saved.created));
            out.end_object();
        }
        out.end_object();
        out.end_object();
        let path = self.path_for(root);
        let temporary = PathBuf::from(format!("{}.{}.tmp", path.display(), mighty_core::wire::new_id().replace('-', "")));
        std::fs::create_dir_all(&self.directory)?;
        let written = std::fs::write(&temporary, out.finish()).and_then(|_| std::fs::rename(&temporary, &path));
        if temporary.exists() {
            let _ = std::fs::remove_file(&temporary);
        }
        written
    }
}

/// `Wire.Json`'s case-insensitive property read.
fn property<'a>(value: &'a Value, name: &str) -> Option<&'a Value> {
    value.as_object()?.iter().filter(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v).next_back()
}

#[derive(serde::Deserialize)]
struct StoredTopic {
    #[serde(default, alias = "Title")]
    title: Option<String>,
    #[serde(default, alias = "Summary")]
    summary: Option<String>,
    #[serde(default, alias = "Lanes")]
    lanes: Option<Vec<String>>,
    #[serde(default, alias = "Commits")]
    commits: Option<Vec<String>>,
    #[serde(default, alias = "Requests")]
    requests: Option<Vec<String>>,
}

impl StoredTopic {
    fn into_topic(self) -> WorkMapTopic {
        WorkMapTopic {
            title: self.title.unwrap_or_default(),
            summary: self.summary.unwrap_or_default(),
            lanes: self.lanes.unwrap_or_default(),
            commits: self.commits.unwrap_or_default(),
            requests: self.requests.unwrap_or_default(),
        }
    }
}

/// What a summary run is: the CLI found, the prompt, the time limit.
pub type Runner = Arc<
    dyn Fn(CliCommand, String, Duration) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<String, SummaryError>> + Send>>
        + Send
        + Sync,
>;

/// The words a failure shows (`Locale.Get(ex.Key)`).
pub fn error_words(error: SummaryError, locale: &Locale) -> String {
    locale.get(error.key())
}
