//! Which steps of the Mighty diagram get a picture preview block, and what it shows (C# `MightyGraphImages`,
//! MightyCode.Core/MightyGraphImages.cs, a port of macOS MightyGraphImages). The pictures a step produced are its stored tool pictures and
//! the Markdown pictures its answers name that can be read (`AgentMarkdownImages.Extract`, AgentImages.cs:473). Where a Markdown source
//! may be read from is the image path rule's (`AgentImagePaths.Locate`, AgentImages.cs:403), which touches the file system: it comes in
//! through [`ImageLocator`].

use std::collections::{HashMap, HashSet};
use std::hash::{DefaultHasher, Hash, Hasher};
use std::sync::{LazyLock, Mutex};

use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{AgentImageRef, LogEntry};
use regex::Regex;
use sha2::{Digest, Sha256};

use crate::layout::Gallery;

/// `AgentImageLocation` (AgentImages.cs:373): where a picture an agent named may be read from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AgentImageLocation {
    /// An existing picture file whose real path is inside `root`.
    File { path: String, root: String },
    /// A `data:image/…;base64,` URI, still encoded.
    Inline { media_type: String, base64: String },
    /// An http(s) address, shown as a link and never fetched.
    Remote { url: String },
    /// Anything else: outside the allowed folders, not a picture, malformed.
    Refused,
}

/// The image path rule (`AgentImagePaths.Locate(reference, workspaceRoot)`): the host's implementation reads the file system.
pub trait ImageLocator {
    fn locate(&self, source: &str, root: Option<&str>) -> AgentImageLocation;
}

impl<F: Fn(&str, Option<&str>) -> AgentImageLocation> ImageLocator for F {
    fn locate(&self, source: &str, root: Option<&str>) -> AgentImageLocation {
        self(source, root)
    }
}

/// `AgentImageItem(Id, Stored, Located, Alt, Source)`: one picture a graph step shows in its preview block.
#[derive(Debug, Clone, PartialEq)]
pub struct AgentImageItem {
    pub id: String,
    pub stored: Option<AgentImageRef>,
    pub located: Option<AgentImageLocation>,
    pub alt: String,
    pub source: String,
}

/// `AgentMarkdownImage(Alt, Source)`: a `![alt](target)` image in agent Markdown.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentMarkdownImage {
    pub alt: String,
    pub source: String,
}

/// `AgentMarkdownImages.MaximumImages`.
pub const MAXIMUM_MARKDOWN_IMAGES: usize = 32;

static MARKDOWN_IMAGE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"!\[([^\]\n]{0,500})\]\(\s*(?:<([^>\n]+)>|([^)\s]+))(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)"#).expect("pattern")
});

/// `AgentMarkdownImages.Extract`: `![alt](target)` images outside fenced code blocks, at most 32.
pub fn markdown_images(text: &str) -> Vec<AgentMarkdownImage> {
    let mut result = vec![];
    if !text.contains("![") {
        return result;
    }
    let mut fenced = false;
    let mut outside = String::new();
    for line in text.split('\n') {
        let trimmed = line.trim_start_matches(' ');
        if trimmed.starts_with("```") || trimmed.starts_with("~~~") {
            fenced = !fenced;
            outside.push('\n');
            continue;
        }
        if !fenced {
            outside.push_str(line);
        }
        outside.push('\n');
    }
    for captures in MARKDOWN_IMAGE.captures_iter(&outside) {
        let source = captures.get(2).or(captures.get(3)).map_or("", |m| m.as_str());
        if source.is_empty() {
            continue;
        }
        result.push(AgentMarkdownImage { alt: captures.get(1).map_or("", |m| m.as_str()).into(), source: source.into() });
        if result.len() == MAXIMUM_MARKDOWN_IMAGES {
            break;
        }
    }
    result
}

/// `RequestStep`: the step names a preview block attaches to.
pub const REQUEST_STEP: &str = "request";

/// `AgentStep`.
pub fn agent_step(agent_id: &str) -> String {
    format!("agent:{agent_id}")
}

fn sha256_hex(data: &[u8]) -> String {
    Sha256::digest(data).iter().map(|b| format!("{b:02x}")).collect()
}

/// `Items(entries, locate)`: pictures a step produced, once each: stored tool pictures, then readable Markdown pictures.
pub fn items_of(entries: &[LogEntry], mut locate: impl FnMut(&str) -> AgentImageLocation) -> Vec<AgentImageItem> {
    let mut seen = HashSet::new();
    let mut result = vec![];
    for entry in entries {
        for image in entry.images.iter().flatten().flatten() {
            let id = format!("hash:{}", image.hash.as_deref().unwrap_or(""));
            if seen.insert(id.clone()) {
                result.push(AgentImageItem { id, stored: Some(image.clone()), located: None, alt: String::new(), source: String::new() });
            }
        }
        if entry.kind() != "assistant" {
            continue;
        }
        for image in markdown_images(entry.text()) {
            let location = locate(&image.source);
            let id = match &location {
                AgentImageLocation::File { path, .. } => Some(format!("file:{path}")),
                AgentImageLocation::Inline { base64, .. } => Some(format!("data:{}", sha256_hex(base64.as_bytes()))),
                _ => None,
            };
            if let Some(id) = id
                && seen.insert(id.clone())
            {
                result.push(AgentImageItem { id, stored: None, located: Some(location), alt: image.alt, source: image.source });
            }
        }
    }
    result
}

/// `Items(run, step, root)`: a step's pictures, worked out once per step and remembered until its entries or status change.
pub fn items(run: &MightyGraphRun, step: &str, root: Option<&str>, locator: &dyn ImageLocator) -> Vec<AgentImageItem> {
    if step == REQUEST_STEP {
        return remembered(run.id(), step, run.root_entries(), run.status(), root, false, locator);
    }
    if step.starts_with("agent:")
        && let Some(agent) = run.agents().iter().find(|a| agent_step(a.id()) == step)
    {
        return remembered(run.id(), step, agent.entries(), agent.status(), root, false, locator);
    }
    vec![]
}

/// `Galleries`: every step with at least one picture, for the layout. The first `settled` runs never change (requests read back from the
/// session record).
pub fn galleries(runs: &[MightyGraphRun], root: Option<&str>, settled: usize, locator: &dyn ImageLocator) -> Vec<Gallery> {
    let mut result = vec![];
    for (index, run) in runs.iter().enumerate() {
        let main = remembered(run.id(), REQUEST_STEP, run.root_entries(), run.status(), root, index < settled, locator).len();
        if main > 0 {
            result.push(Gallery { run_id: run.id().into(), step: REQUEST_STEP.into(), count: main });
        }
        for agent in run.agents() {
            let step = agent_step(agent.id());
            let count = remembered(run.id(), &step, agent.entries(), agent.status(), root, index < settled, locator).len();
            if count > 0 {
                result.push(Gallery { run_id: run.id().into(), step, count });
            }
        }
    }
    result
}

const MAXIMUM_REMEMBERED: usize = 1_024;

struct Memo {
    signature: u64,
    status: String,
    items: Vec<AgentImageItem>,
    located: HashMap<String, AgentImageLocation>,
}

/// Each step's answer, and where each Markdown source it read was found while the step kept its status (C#'s static `Memo`).
static MEMO: LazyLock<Mutex<HashMap<String, Memo>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

fn remembered(
    run_id: &str,
    step: &str,
    entries: &[LogEntry],
    status: &str,
    root: Option<&str>,
    settled: bool,
    locator: &dyn ImageLocator,
) -> Vec<AgentImageItem> {
    let mut hasher = DefaultHasher::new();
    status.hash(&mut hasher);
    entries.len().hash(&mut hasher);
    // Only entries that can carry pictures; their text by length, not content.
    if !settled {
        for entry in entries.iter().filter(|e| e.kind() == "assistant" || e.images.is_some()) {
            entry.id().hash(&mut hasher);
            entry.kind().hash(&mut hasher);
            entry.text().encode_utf16().count().hash(&mut hasher);
            for image in entry.images.iter().flatten().flatten() {
                image.hash.hash(&mut hasher);
            }
        }
    }
    let signature = hasher.finish();
    let key = format!("{run_id}\u{1F}{step}\u{1F}{}", root.unwrap_or(""));
    let mut located = {
        let memo = MEMO.lock().unwrap_or_else(|e| e.into_inner());
        match memo.get(&key) {
            Some(hit) if hit.signature == signature => return hit.items.clone(),
            // A step that settled looks every file up again, so one that appeared meanwhile is picked up.
            Some(hit) if hit.status == status => hit.located.clone(),
            _ => HashMap::new(),
        }
    };
    let found = items_of(entries, |source| {
        if let Some(place) = located.get(source) {
            return place.clone();
        }
        let place = locator.locate(source, root);
        located.insert(source.to_string(), place.clone());
        place
    });
    let mut memo = MEMO.lock().unwrap_or_else(|e| e.into_inner());
    if memo.len() >= MAXIMUM_REMEMBERED {
        memo.clear();
    }
    memo.insert(key, Memo { signature, status: status.into(), items: found.clone(), located });
    found
}
