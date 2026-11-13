//! The smoke's files steps (C# MainWindow.WebShell.Panes.cs `RunWebPanesSmoke`'s files part, MainWindow.WebShell.cs
//! `RunWebReferenceSmoke` and `RunWebPictureSmoke`), and an HTML frame: the host makes a workspace of its own (a temporary folder) with
//! fixture files, a Claude pane whose 기록 view holds a reply naming `src/a.ts:3` and a Claude pane whose diagram holds a PNG and an
//! SVG picture; the probe then works the page as a person would.
//!
//! - Add pane: the workspace's add menu, its files entry (frame.rs's `addPaneEntry` through `OPEN_FILES`): the workspace's files pane
//!   opens and shows its tree.
//! - Files: the tree lists `smoke-folder`, a click opens it, a click on `hello.ts` previews it with line numbers and colour.
//! - HTML frame: a click on `page/index.html` draws it in the sandboxed frame from the `mightyhtml` protocol; the host must have served
//!   the page and the style sheet and script beside it.
//! - Reference bubble: the reply's `src/a.ts:3` clicked where it is drawn opens the bubble on the file's source at line 3, with line
//!   numbers; its close button closes it.
//! - Picture viewer: both thumbnails come as PNG data URLs (the SVG drawn to PNG), a click on the first opens the viewer as a dialog
//!   with focus inside and the picture as a data URL, and Esc closes it and gives focus back to the thumbnail. No viewer button is
//!   pressed (copy would touch the clipboard).

use std::path::PathBuf;
use std::sync::Arc;

use base64::Engine as _;
use mighty_core::{AppSnapshot, Wire};
use serde_json::{Value, json};
use tauri::Manager as _;

use crate::host::{Host, lock};

/// The workspace and panes the steps add (UUID-shaped, as the state's ids are).
const WORKSPACE: &str = "5a0e0b0b-0000-4000-8000-0000000f11e5";
const REFERENCE: &str = "5a0e0b0b-0000-4000-8000-0000000f1e00";
const PICTURES: &str = "5a0e0b0b-0000-4000-8000-0000000f1c70";

/// The C# smoke's 4 × 3 PNG and 40 × 30 SVG.
const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAEElEQVR4nGPQqDgBRww4OQBBxhDhzXmo9QAAAABJRU5ErkJggg==";
const SVG: &str =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"><rect width=\"40\" height=\"30\" fill=\"#c33\"/></svg>";

/// The probe; `__WORKSPACE__`, `__FILES__`, `__REFERENCE__` and `__PICTURES__` are filled in (UUID-shaped ids, no quotes).
const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const workspaceId = '__WORKSPACE__', filesId = '__FILES__', referenceId = '__REFERENCE__', picturesId = '__PICTURES__';
  const slot = (id) => document.querySelector('.pane-slot[data-pane-id="' + id + '"]');
  const shown = (id) => { const s = slot(id); return s && !s.classList.contains('hidden') ? s : null; };
  const row = (path) => slot(filesId)?.querySelector('[data-testid=files-tree] [data-path="' + path + '"]');
  const report = {};
  try {
    // The files pane from the workspace's add menu: its files entry opens the workspace's one files pane.
    document.querySelector('[data-testid=sidebar-workspace][data-workspace-id="' + workspaceId + '"] [data-testid=sidebar-add]')?.click();
    const entry = await until(() => document.querySelector('[data-testid="add-entry-files"]'), 10000);
    report.filesAddEntry = !!entry;
    entry?.click();
    // Files: the fixture folder in the tree, opened, its file previewed with line numbers and colour.
    report.filesTree = !!(await until(() => shown(filesId) && row('smoke-folder'), 15000));
    row('smoke-folder')?.click();
    report.filesFolderOpened = !!(await until(() => row('smoke-folder/hello.ts'), 10000));
    row('smoke-folder/hello.ts')?.click();
    const preview = (path) => slot(filesId)?.querySelector('[data-testid=files-preview][data-preview-path="' + path + '"]');
    report.filesPreview = !!(await until(() => { const p = preview('smoke-folder/hello.ts'); return !!p && (p.querySelector('[data-testid=files-source]')?.textContent ?? '').includes('smokeMarker') && !!p.querySelector('.syntax-keyword') && (p.querySelector('.files-lines')?.textContent ?? '').startsWith('1'); }, 10000));
    // An HTML file as a page: the sandboxed frame from the host's protocol.
    row('page')?.click();
    report.htmlFolderOpened = !!(await until(() => row('page/index.html'), 10000));
    row('page/index.html')?.click();
    const frame = await until(() => preview('page/index.html')?.querySelector('[data-testid=files-html]'), 10000);
    report.htmlFrame = frame?.getAttribute('src') ?? null;
    report.htmlSandbox = frame?.getAttribute('sandbox') ?? null;
    report.htmlLoaded = !!frame && !!(await until(() => new Promise((done) => { if (frame.dataset.smokeLoaded) return done(true); frame.addEventListener('load', () => { frame.dataset.smokeLoaded = '1'; done(true); }, { once: true }); setTimeout(() => done(false), 1500); }), 10000));
    // The reference bubble: the reply's src/a.ts:3 clicked where it is drawn.
    document.querySelector('[data-testid=sidebar-pane][data-session-id="' + referenceId + '"]')?.click();
    const room = () => shown(referenceId);
    report.referencePane = !!(await until(() => room(), 10000));
    for (let tries = 0; tries < 100; tries++) {
      const heads = [...(room()?.querySelectorAll('.timeline-card-head') ?? [])].filter((h) => !h.disabled);
      if (heads.length) { heads.forEach((h) => h.click()); break; }
      await new Promise((r) => setTimeout(r, 100));
    }
    const click = () => { const c = room()?.querySelector('.timeline-transcript'); if (!c) return false; const w = document.createTreeWalker(c, NodeFilter.SHOW_TEXT); let n;
      while ((n = w.nextNode())) { const i = n.textContent.indexOf('src/a.ts:3'); if (i < 0) continue; n.parentElement.scrollIntoView({ block: 'center' }); const r = document.createRange(); r.setStart(n, i + 2); r.setEnd(n, i + 3); const b = r.getBoundingClientRect();
        n.parentElement.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0, clientX: b.left + b.width / 2, clientY: b.top + b.height / 2 })); return true; } return false; };
    report.referenceClicked = !!(await until(() => click(), 10000));
    const bubble = () => room()?.querySelector('[data-testid=reference-bubble]');
    report.referenceOpened = !!(await until(() => { const b = bubble(); return !!b && b.dataset.referenceKind === 'source' && b.dataset.referenceLine === '3' && (b.querySelector('[data-testid=files-source]')?.textContent ?? '').includes('referenceMarker') && (b.querySelector('.files-lines')?.textContent ?? '').startsWith('1'); }, 15000));
    bubble()?.querySelector('[data-testid=reference-close]')?.click();
    report.referenceClosed = !!(await until(() => !bubble(), 10000));
    // The picture viewer: a PNG and an SVG in the diagram, the SVG drawn as PNG, the first opened and closed with Esc.
    document.querySelector('[data-testid=sidebar-pane][data-session-id="' + picturesId + '"]')?.click();
    const thumbs = () => [...(shown(picturesId)?.querySelectorAll('[data-testid=mighty-image-tile] img') ?? [])];
    report.pictureThumbnails = !!(await until(() => { const t = thumbs(); return t.length === 2 && t.every((i) => i.src.startsWith('data:image/png;base64,')); }, 15000));
    const tile = shown(picturesId)?.querySelector('[data-testid=mighty-image-tile]');
    if (tile) { tile.scrollIntoView({ block: 'center' }); tile.focus(); tile.click(); }
    const viewer = () => document.querySelector('[data-testid=picture-viewer]');
    report.pictureOpened = !!(await until(() => { const v = viewer(); const i = v?.querySelector('[data-testid=picture-viewer-image]'); return !!i && i.src.startsWith('data:image/') && !!v.querySelector('[role=dialog][aria-modal=true]') && v.contains(document.activeElement) && i.style.visibility !== 'hidden'; }, 15000));
    (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    report.pictureClosed = !!(await until(() => !viewer() && document.activeElement?.dataset.testid === 'mighty-image-tile', 10000));
  } catch (error) {
    report.error = String(error);
  }
  await window.__TAURI_INTERNALS__.invoke('smoke_probe', { probe: report });
})();"#;

/// The steps' workspace folder with its fixture files.
fn fixture_folder() -> std::io::Result<PathBuf> {
    let folder = std::env::temp_dir().join(format!("mighty-tauri-smoke-files-{}", std::process::id()));
    let files = [
        ("smoke-folder/hello.ts", "const smokeMarker = 42; // web files smoke\n"),
        ("src/a.ts", "// referenceMarker\nconst one = 1;\nconst three = 3;\nexport { one, three };\n"),
        (
            "page/index.html",
            "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"style.css\"><script src=\"app.js\"></script></head><body><h1>Mighty smoke page</h1></body></html>",
        ),
        ("page/style.css", "h1 { color: #1f7a52; }"),
        ("page/app.js", "document.title = 'smoke page';"),
    ];
    for (relative, text) in files {
        let path = folder.join(relative);
        std::fs::create_dir_all(path.parent().expect("a folder"))?;
        std::fs::write(path, text)?;
    }
    Ok(folder)
}

fn entry(kind: &str, text: &str, extra: Value) -> Value {
    let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let mut value = json!({"id": mighty_core::wire::new_id(), "kind": kind, "text": text, "timestamp": now});
    if let (Some(value), Some(extra)) = (value.as_object_mut(), extra.as_object()) {
        value.extend(extra.clone());
    }
    value
}

/// The workspace and its two Claude panes, in memory (as the other steps add theirs); answers the probe to run (it opens the files pane
/// from the add menu).
pub fn start(host: &Host) -> Result<String, String> {
    let folder = fixture_folder().map_err(|e| e.to_string())?;
    let cache = host.engine()?.images.clone();
    let png = base64::engine::general_purpose::STANDARD.decode(PNG).map_err(|e| e.to_string())?;
    let stored = |data: &[u8], media: &str, source: &str| -> Result<Value, String> {
        let image = cache.store(data, media, source, None).map_err(|e| format!("{e:?}"))?;
        serde_json::from_str(&image.to_json()).map_err(|e| e.to_string())
    };
    let pictures = vec![stored(&png, "image/png", "smoke-picture.png")?, stored(SVG.as_bytes(), "image/svg+xml", "smoke-picture.svg")?];
    let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let reply = "fixture answer 0\n\n## Result\n\n**Passed**: see `src/a.ts:3`.";
    let pane = |id: &str, title: &str, mode: Option<&str>, entries: Vec<Value>| {
        let mut pane = json!({
            "id": id, "workspaceId": WORKSPACE, "title": title, "kind": "claude", "status": "completed", "model": "default",
            "provider": "claude", "settings": {"effort": "default", "permissionMode": "manual", "maxTurns": null, "maxBudgetUsd": null},
            "logs": [], "draft": "", "createdAt": now, "agentViewMode": "mighty",
            "graphRuns": [{"id": mighty_core::wire::new_id(), "input": "fixture request 0", "status": "completed", "provider": "claude", "rootEntries": entries}],
        });
        if let Some(mode) = mode {
            pane["graphViewMode"] = json!(mode);
        }
        pane
    };
    let reference = pane(REFERENCE, "Smoke reference", Some("timeline"), vec![entry("assistant", reply, json!({"provider": "claude"}))]);
    let gallery = pane(PICTURES, "Smoke pictures", None, vec![entry("image", "Read · smoke-picture", json!({"images": pictures}))]);
    let mut view = host.view();
    let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
    let workspace = json!({"id": WORKSPACE, "name": "Smoke files", "path": folder, "createdAt": now});
    for (name, item) in [("workspaces", workspace), ("sessions", reference), ("sessions", gallery)] {
        state.get_mut(name).and_then(Value::as_array_mut).ok_or_else(|| format!("the state has no {name}"))?.push(item);
    }
    let mut expanded: Vec<Value> = state.get("expandedWorkspaceIds").and_then(Value::as_array).cloned().unwrap_or_default();
    expanded.push(json!(WORKSPACE));
    state["expandedWorkspaceIds"] = json!(expanded);
    state["activeWorkspaceId"] = json!(WORKSPACE);
    state["activeSessionId"] = json!(REFERENCE);
    view.replace(AppSnapshot::read(&state).map_err(|e| e.to_string())?);
    let files = mighty_files::pane::pane_id(WORKSPACE);
    Ok(PROBE
        .replace("__WORKSPACE__", WORKSPACE)
        .replace("__FILES__", &files)
        .replace("__REFERENCE__", REFERENCE)
        .replace("__PICTURES__", PICTURES))
}

/// The steps' checks, from the probe's report and what the frames' protocol served.
pub fn checks(host: &Host, report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |key: &str| report.get(key) == Some(&Value::Bool(true));
    let served = served(host);
    let frame = report.get("htmlFrame").and_then(Value::as_str).unwrap_or("");
    let origin = if cfg!(windows) { "http://mightyhtml.h" } else { "mightyhtml://h" };
    vec![
        ("filesNoError", report.get("error").is_none()),
        ("filesAddPane", yes("filesAddEntry") && yes("filesTree")),
        ("filesTree", yes("filesTree")),
        ("filesFolderOpened", yes("filesFolderOpened")),
        ("filesPreview", yes("filesPreview")),
        (
            "htmlFrame",
            yes("htmlFolderOpened")
                && frame.starts_with(origin)
                && frame.ends_with(".html.mighty.invalid/index.html")
                && report.get("htmlSandbox").and_then(Value::as_str) == Some("allow-scripts")
                && ["/index.html", "/style.css", "/app.js"].iter().all(|p| served.iter().any(|s| s == p)),
        ),
        ("referenceBubble", yes("referenceClicked") && yes("referenceOpened") && yes("referenceClosed")),
        ("pictureViewer", yes("pictureThumbnails") && yes("pictureOpened") && yes("pictureClosed")),
    ]
}

/// What the frames' protocol served, for the result.
pub fn served(host: &Host) -> Vec<String> {
    host.app.try_state::<Arc<crate::files::Files>>().map(|files| lock(&files.served).clone()).unwrap_or_default()
}
