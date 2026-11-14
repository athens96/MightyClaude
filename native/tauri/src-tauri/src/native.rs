//! The native forms the page draws (C# MainWindow.WebShell.Settings.cs: `WebNativeSurface`, `PushWebNatives`, `PushNative`,
//! `PushWebText`, the five native methods, `ShowDialogAsync`, `HideDialog`), over mighty-native's surfaces.
//!
//! - `native{id, place, view}` is pushed when a surface opens, on the frame's 250 ms clock ([`tick`], from host.rs's shell clock) for
//!   every open surface, after an input on it (marked here, pushed by the flush that follows the call's result), after a nav and a
//!   refused button; each only when its view's JSON text differs from the one sent last. `native{id}` when it closes. Nothing is
//!   pushed before the page is ready, and while it is not, every surface's last text is forgotten so the first ready tick sends all.
//! - `text{scale, family?, transcriptFamily?, codeFamily?}` (the text setting) on the same tick when its JSON changed; `nativeState`
//!   (the page mounting its surfaces) forgets every text sent and pushes all of them, the text always.
//! - With `--native-dump <file>` every `native` and `text` push is also written there as sent, one per line (smoke and debugging).
//!
//! Locks: a surface's lock is taken before the state's (a builder reads the state), never after; [`post`] takes the state's alone.

use std::collections::BTreeSet;
use std::future::Future;
use std::io::Write;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, MutexGuard};

use mighty_core::json::JsonOut;
use mighty_core::text_appearance::TextAppearance;
use mighty_native::surface::{DialogResult, DialogSpec, DialogSurface, Next, SurfaceModel, Surfaces};
use mighty_native::tokens::Theme;
use mighty_native::ui::Env;
use mighty_native::view::Place;
use serde_json::{Value, json};
use tauri::Manager;

use crate::host::{Host, Registry, WINDOW};
use crate::methods::{number, text};

/// The host's surfaces and what was last sent of the text setting.
#[derive(Default)]
struct Natives {
    surfaces: Surfaces,
    text_sent: Mutex<Option<String>>,
    /// Surfaces an input changed, pushed by the next flush.
    dirty: Mutex<BTreeSet<String>>,
    /// `--native-dump`.
    dump: Mutex<Option<std::fs::File>>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn of(host: &Host) -> Arc<Natives> {
    if let Some(natives) = host.app.try_state::<Arc<Natives>>() {
        return natives.inner().clone();
    }
    host.app.manage(Arc::new(Natives::default()));
    host.app.state::<Arc<Natives>>().inner().clone()
}

/// `--native-dump <file>`: every native and text push written there too (the file made anew).
pub fn configure(host: &Host, dump: Option<PathBuf>) {
    let Some(path) = dump else { return };
    if let Some(folder) = path.parent() {
        let _ = std::fs::create_dir_all(folder);
    }
    match std::fs::File::create(&path) {
        Ok(file) => *lock(&of(host).dump) = Some(file),
        Err(error) => host.log.line(&format!("native dump {}: {error}", path.display())),
    }
}

/// What a read is drawn with now: the theme, the text setting, the language.
pub fn env(host: &Host) -> Env {
    let theme = Theme::of(host.view().snapshot.theme());
    Env { theme, text: crate::frame::text_setting(host), locale: host.locale }
}

/// A push to the page (dropped while it is not ready), written to the dump as sent.
fn post(host: &Host, kind: &str, json: String) {
    let view = host.view();
    if !view.ready {
        return;
    }
    if let Some(file) = lock(&of(host).dump).as_mut() {
        let _ = writeln!(file, "{json}");
    }
    host.push(&view, kind, json);
}

/// `PushNative`: the surface read again and pushed when its text changed.
pub fn render(host: &Host, id: &str) {
    let env = env(host);
    if let Some(push) = of(host).surfaces.render(id, &env) {
        post(host, "native", push);
    }
}

/// `native::touch`: a surface whose model changed outside an input (a read finished, a timer) is read again now instead of on the
/// next tick.
pub fn touch(host: &Host, id: &str) {
    render(host, id);
}

/// `OpenNativeSurface` then `PushNative`: a surface of that id already open is replaced.
pub fn open(host: &Host, id: &str, place: Place, model: Box<dyn SurfaceModel>) {
    of(host).surfaces.open(id, place, model);
    render(host, id);
}

/// `CloseNativeSurface`: its `native{id}` push when it was open.
pub fn close(host: &Host, id: &str) {
    if let Some(push) = of(host).surfaces.close(id) {
        post(host, "native", push);
    }
}

pub fn is_open(host: &Host, id: &str) -> bool {
    of(host).surfaces.contains(id)
}

/// `surface.Navigate(to)`: true when the surface changed what it shows (the caller pushes it).
pub fn navigate(host: &Host, id: &str, to: &str) -> bool {
    of(host).surfaces.navigate(id, to)
}

/// The text setting's push, as C#'s anonymous object writes it (nulls left out, the scale in .NET's form).
fn text_json(text: &TextAppearance) -> String {
    let mut out = JsonOut::new();
    out.begin_object();
    out.prop_str("kind", Some("text"));
    out.prop_float("scale", text.scale);
    out.prop_str("family", text.family.as_deref());
    out.prop_str("transcriptFamily", text.resolved_transcript_family());
    out.prop_str("codeFamily", text.code_family.as_deref());
    out.end_object();
    out.finish()
}

/// `PushWebText`: the text setting when its JSON changed since it was last sent.
pub fn push_text(host: &Host) {
    let json = text_json(&crate::frame::text_setting(host));
    {
        let natives = of(host);
        let mut sent = lock(&natives.text_sent);
        if sent.as_deref() == Some(json.as_str()) || !host.view().ready {
            return;
        }
        *sent = Some(json.clone());
    }
    post(host, "text", json);
}

/// `PushWebNatives`, on the frame's 250 ms clock: every open surface and the text setting, each when it changed; while the page is
/// not ready (or the app quits) every text sent is forgotten.
pub fn tick(host: &Host) {
    let natives = of(host);
    if host.closing() || !host.view().ready {
        natives.surfaces.forget_sent();
        *lock(&natives.text_sent) = None;
        return;
    }
    let env = env(host);
    for push in natives.surfaces.render_all(&env) {
        post(host, "native", push);
    }
    push_text(host);
}

/// The surfaces an input changed, pushed after the call's result (C#'s `DispatcherQueue.TryEnqueue(PushNative)`); host.rs's flush.
pub fn flush(host: &Host) {
    let ids: Vec<String> = std::mem::take(&mut *lock(&of(host).dirty)).into_iter().collect();
    for id in ids {
        render(host, &id);
    }
}

/// The main window's client area in DIPs, what `StyledDialog` fits a fixed sheet against (none known: 0).
fn window_room(host: &Host) -> (f64, f64) {
    let Some(page) = host.app.get_webview(WINDOW) else { return (0.0, 0.0) };
    let window = page.window();
    match (window.inner_size(), window.scale_factor()) {
        (Ok(size), Ok(scale)) if scale > 0.0 => (f64::from(size.width) / scale, f64::from(size.height) / scale),
        _ => (0.0, 0.0),
    }
}

/// A dialog the page shows (from [`start_dialog`]): its surface id, for [`hide_dialog`], and its answer.
pub struct DialogHandle {
    pub id: String,
    host: Arc<Host>,
    answer: tokio::sync::oneshot::Receiver<DialogResult>,
}

impl DialogHandle {
    /// The answer (None when the dialog went without one); the surface is closed then (C#'s `finally`).
    pub async fn wait(self) -> DialogResult {
        let result = self.answer.await.unwrap_or(DialogResult::None);
        close(&self.host, &self.id);
        result
    }
}

/// `ShowDialogAsync` without the wait: the dialog `dialog-N` opened, pushed, then its `opened` run.
pub fn start_dialog(host: &Arc<Host>, mut spec: DialogSpec) -> DialogHandle {
    let natives = of(host);
    let id = natives.surfaces.next_dialog();
    let (answer, answered) = tokio::sync::oneshot::channel();
    let opened = spec.opened.take();
    let spec = spec.fitted(window_room(host));
    let model = DialogSurface::new(spec, move |result| {
        let _ = answer.send(result);
    });
    natives.surfaces.open(&id, Place::Dialog, Box::new(model));
    render(host, &id);
    if let Some(opened) = opened {
        opened();
    }
    DialogHandle { id, host: host.clone(), answer: answered }
}

/// `native::show_dialog`: C# `ShowDialogAsync` — the dialog drawn by the page from the spec, answered with its button (None for its
/// close button, Esc and [`hide_dialog`]).
pub async fn show_dialog(host: &Arc<Host>, spec: DialogSpec) -> DialogResult {
    start_dialog(host, spec).wait().await
}

/// `native::hide_dialog`: C# `HideDialog` — the dialog closes as None unless its `can_close` refuses.
pub fn hide_dialog(host: &Host, id: &str) {
    if of(host).surfaces.hide(id) == Some(Next::Close) {
        close(host, id);
    }
}

/// C# `SafeButton`'s `Act` for a control's handler: on its own task, the error banner cleared first and what the work fails with
/// shown there.
pub fn act(host: &Arc<Host>, work: impl Future<Output = Result<(), String>> + Send + 'static) {
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        let _ = crate::frame::act(&host, work).await;
    });
}

/// What a surface's button, close or hide asked for.
fn follow(host: &Host, id: &str, next: Option<Next>) {
    match next {
        Some(Next::Close) => close(host, id),
        Some(Next::Push) => render(host, id),
        Some(Next::Stay) | None => {}
    }
}

/// The page's methods (MainWindow.WebShell.Settings.cs:108-137).
pub fn register(methods: &mut Registry) {
    methods.add("nativeAction", |host: Arc<Host>, args| async move {
        let natives = of(&host);
        let (Some(view), Some(id)) = (text(&args, "view").filter(|v| natives.surfaces.contains(v)), number(&args, "id")) else {
            return Ok(json!(false));
        };
        let value = args.get("value").cloned().unwrap_or(Value::Null);
        // `(int)element`: the id as an integer, a fraction cut off.
        let outcome = natives.surfaces.act(view, id as i64, text(&args, "op").unwrap_or(""), &value);
        let done = outcome.run(|url| {
            if let Some(url) = crate::methods::openable_url(url)
                && let Err(error) = crate::platform::open_url(&url)
            {
                host.log.line(&format!("native link: {error}"));
            }
        });
        // The change shows at once, after this call's result (the flush the dispatcher schedules), not on the next tick.
        lock(&natives.dirty).insert(view.to_string());
        Ok(json!(done))
    });
    methods.add("nativeNav", |host: Arc<Host>, args| async move {
        if let (Some(view), Some(to)) = (text(&args, "view"), text(&args, "id"))
            && navigate(&host, view, to)
        {
            render(&host, view);
        }
        Ok(Value::Null)
    });
    methods.add("nativeButton", |host: Arc<Host>, args| async move {
        if let Some(view) = text(&args, "view") {
            let next = of(&host).surfaces.button(view, text(&args, "button"));
            follow(&host, view, next);
        }
        Ok(Value::Null)
    });
    methods.add("nativeClose", |host: Arc<Host>, args| async move {
        if let Some(view) = text(&args, "view") {
            let next = of(&host).surfaces.button(view, None);
            follow(&host, view, next);
        }
        Ok(Value::Null)
    });
    methods.add("nativeState", |host: Arc<Host>, _| async move {
        let natives = of(&host);
        natives.surfaces.forget_sent();
        *lock(&natives.text_sent) = None;
        tick(&host);
        Ok(Value::Null)
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// C# `PushWebText`'s anonymous object: nulls left out, the scale as .NET writes a double.
    #[test]
    fn the_text_push_is_written_as_csharp_writes_it() {
        assert_eq!(text_json(&TextAppearance::default()), r#"{"kind":"text","scale":1}"#);
        let chosen = TextAppearance::new(1.2, Some("Pretendard"), None, Some("Fira Code"));
        assert_eq!(
            text_json(&chosen),
            r#"{"kind":"text","scale":1.2,"family":"Pretendard","transcriptFamily":"Pretendard","codeFamily":"Fira Code"}"#
        );
    }
}
