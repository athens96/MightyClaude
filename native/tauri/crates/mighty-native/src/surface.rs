//! The open surfaces, in the order they opened, each with its model, its kept values, its act table and the text last sent (C#
//! `WebNativeSurface`, `OpenNativeSurface`, `CloseNativeSurface`, `PushNative`; MainWindow.WebShell.Settings.cs), and the dialog of
//! a flow (`ShowDialogAsync`, `HideDialog`: [`DialogSpec`] → [`DialogResult`]).
//!
//! Locks: the list is held only to find a surface; a surface's own lock is held while its model builds and is read and while its
//! button, nav or hide runs, and never while a control's handler runs ([`Surfaces::act`] answers what to run; the caller runs it).
//! A model may take the host's other locks while it builds (the state), but nothing may take a surface's lock while holding those.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};

use serde_json::Value;

use crate::act::{Acts, Outcome};
use crate::ids::Ids;
use crate::kit::sheet_fit;
use crate::reader::Reader;
use crate::ui::{Content, El, Env, Locals, Ui};
use crate::view::{ButtonView, ChromeView, Place, View, close_push, is_alert, open_push};

/// What a surface does after a button, a close or a hide.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Next {
    /// Nothing more (a disabled button).
    Stay,
    /// Read again and pushed if it changed (a refused button: C# `PushNative`).
    Push,
    /// It closes.
    Close,
}

/// A surface's model: what it shows, what its buttons and categories do. Its `view` builds the tree again from the model, with
/// no I/O: it runs on every read, four times a second.
pub trait SurfaceModel: Send {
    fn view(&mut self, ui: &mut Ui) -> View;
    /// `nativeButton{button}` (`primary`, `secondary`, `close`) or `nativeClose` (none).
    fn button(&mut self, which: Option<&str>) -> Next {
        let _ = which;
        Next::Close
    }
    /// `nativeNav{id}`: true when the surface changed what it shows (its kept values are then forgotten, as C# built new controls).
    fn navigate(&mut self, to: &str) -> bool {
        let _ = to;
        false
    }
    /// C# `HideDialog`: the flow closes it.
    fn hide(&mut self) -> Next {
        Next::Close
    }
}

struct Surface {
    id: String,
    place: Place,
    model: Box<dyn SurfaceModel>,
    locals: Locals,
    acts: Acts,
    sent: Option<String>,
    /// Closed (taken out of the list): a read in flight posts nothing more for it.
    closed: bool,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// The open surfaces of one host.
#[derive(Default)]
pub struct Surfaces {
    /// The open surfaces by id, oldest first (the id beside each, so finding one never waits on a surface that is being read).
    open: Mutex<Vec<(String, Arc<Mutex<Surface>>)>>,
    ids: Mutex<Ids>,
    dialogs: AtomicU64,
}

impl Surfaces {
    pub fn new() -> Surfaces {
        Surfaces::default()
    }

    fn find(&self, id: &str) -> Option<Arc<Mutex<Surface>>> {
        lock(&self.open).iter().find(|(open, _)| open == id).map(|(_, surface)| surface.clone())
    }

    /// `OpenNativeSurface`: a surface of that id already open is closed first (without a push) and the new one takes the end of
    /// the order.
    pub fn open(&self, id: &str, place: Place, model: Box<dyn SurfaceModel>) {
        let surface =
            Surface { id: id.to_string(), place, model, locals: Locals::default(), acts: Acts::default(), sent: None, closed: false };
        let replaced = self.take(id);
        if let Some(old) = &replaced {
            lock(old).closed = true;
        }
        lock(&self.open).push((id.to_string(), Arc::new(Mutex::new(surface))));
        lock(&self.ids).forget(id);
        // A replaced model is let go of outside the list's lock (dropping it may take the host's locks).
        drop(replaced);
    }

    /// The surface `id` taken out of the list.
    fn take(&self, id: &str) -> Option<Arc<Mutex<Surface>>> {
        let mut open = lock(&self.open);
        let at = open.iter().position(|(open, _)| open == id)?;
        Some(open.remove(at).1)
    }

    /// `CloseNativeSurface`: when it was open, it is marked closed and its close push handed to `post` under its own lock, so a read
    /// of it in flight either posted before this push or posts nothing (a closed dialog never comes back). Its model is let go of here,
    /// outside every lock. False when it was not open.
    pub fn close(&self, id: &str, post: impl FnOnce(String)) -> bool {
        let Some(removed) = self.take(id) else { return false };
        {
            let mut surface = lock(&removed);
            surface.closed = true;
            post(close_push(id));
        }
        lock(&self.ids).forget(id);
        drop(removed);
        true
    }

    pub fn contains(&self, id: &str) -> bool {
        self.find(id).is_some()
    }

    /// The open surfaces' ids, oldest first.
    pub fn ids(&self) -> Vec<String> {
        lock(&self.open).iter().map(|(id, _)| id.clone()).collect()
    }

    /// `"dialog-" + ++webNativeNext`.
    pub fn next_dialog(&self) -> String {
        format!("dialog-{}", self.dialogs.fetch_add(1, Ordering::SeqCst) + 1)
    }

    /// `PushNative`: the surface read again (C# `Begin`, then its model) and, when the text differs from the last one sent, its push
    /// handed to `post` while the surface's lock is still held (surface, then state: the host's lock order), so pushes of one surface
    /// reach the page in the order they were read. `post` answers whether the push went out; only then is it the text sent (a page that
    /// is not ready gets it later). True when a push went out.
    pub fn render_to(&self, id: &str, env: &Env, post: impl FnOnce(String) -> bool) -> bool {
        let Some(surface) = self.find(id) else { return false };
        let mut surface = lock(&surface);
        let Surface { id, place, model, locals, acts, sent, closed } = &mut *surface;
        if *closed {
            return false;
        }
        let view = model.view(&mut Ui::new(env, locals));
        acts.clear();
        let json = {
            let mut ids = lock(&self.ids);
            let json = view.write(&mut Reader::new(env, id, &mut ids, acts));
            ids.prune(id);
            json
        };
        if sent.as_deref() == Some(json.as_str()) {
            return false;
        }
        if !post(open_push(id, *place, &json)) {
            return false;
        }
        *sent = Some(json);
        true
    }

    /// [`Surfaces::render_to`] for a caller that takes the push itself (a test, the smoke's own surface): the push, taken as sent.
    pub fn render(&self, id: &str, env: &Env) -> Option<String> {
        let mut pushed = None;
        self.render_to(id, env, |push| {
            pushed = Some(push);
            true
        });
        pushed
    }

    /// Every open surface read again, oldest first, each push posted right after its read.
    pub fn render_all_to(&self, env: &Env, mut post: impl FnMut(String) -> bool) {
        for id in self.ids() {
            self.render_to(&id, env, &mut post);
        }
    }

    /// Every surface's last text forgotten (the page is not ready, or asked for them again: `nativeState`).
    pub fn forget_sent(&self) {
        let open: Vec<Arc<Mutex<Surface>>> = lock(&self.open).iter().map(|(_, surface)| surface.clone()).collect();
        for surface in open {
            lock(&surface).sent = None;
        }
    }

    /// `nativeAction`: what the input on control `id` of surface `view` does, as its last read left it.
    pub fn act(&self, view: &str, id: i64, op: &str, value: &Value) -> Outcome {
        match self.find(view) {
            Some(surface) => {
                let mut surface = lock(&surface);
                if surface.closed { Outcome::Refused } else { surface.acts.resolve(id, op, value) }
            }
            None => Outcome::Refused,
        }
    }

    /// `nativeButton` / `nativeClose`: none when no such surface is open.
    pub fn button(&self, view: &str, which: Option<&str>) -> Option<Next> {
        let surface = self.find(view)?;
        let next = lock(&surface).model.button(which);
        Some(next)
    }

    /// `nativeNav`: true when the surface changed what it shows.
    pub fn navigate(&self, view: &str, to: &str) -> bool {
        let Some(surface) = self.find(view) else { return false };
        let mut surface = lock(&surface);
        let changed = surface.model.navigate(to);
        if changed {
            surface.locals.clear();
        }
        changed
    }

    /// `HideDialog`: none when no such surface is open.
    pub fn hide(&self, view: &str) -> Option<Next> {
        let surface = self.find(view)?;
        let next = lock(&surface).model.hide();
        Some(next)
    }
}

/// A dialog's answer (`ContentDialogResult`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DialogResult {
    None,
    Primary,
    Secondary,
}

/// `ContentDialog.DefaultButton`: the button drawn as the accent one.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DefaultButton {
    #[default]
    None,
    Primary,
    Secondary,
    Close,
}

/// A dialog's primary or secondary button: its words and whether it is enabled now (read on every read).
pub struct DialogButton {
    pub label: String,
    pub enabled: Box<dyn Fn() -> bool + Send>,
}

impl DialogButton {
    pub fn new(label: impl Into<String>) -> DialogButton {
        DialogButton { label: label.into(), enabled: Box::new(|| true) }
    }

    pub fn enabled_when(mut self, enabled: impl Fn() -> bool + Send + 'static) -> DialogButton {
        self.enabled = Box::new(enabled);
        self
    }
}

type Part = Box<dyn FnMut(&mut Ui) -> El + Send>;

/// What the page draws around a dialog's form (C# `DialogChrome`): a `panel` makes it the standard's kind C.
#[derive(Default)]
pub struct DialogChrome {
    pub panel: Option<String>,
    /// A Lucide icon name for the header.
    pub icon: Option<String>,
    pub subtitle: Option<Box<dyn Fn() -> Option<String> + Send>>,
    /// The primary button is a dangerous action.
    pub danger: bool,
    pub min_width: Option<f64>,
    pub min_height: Option<f64>,
    /// The header's tools, the footer's status and the form's own buttons at the footer's right, built on every read.
    pub tools: Option<Part>,
    pub status: Option<Part>,
    pub actions: Option<Part>,
    /// Dims the ✕ and refuses Esc; when none, busy while `can_close` refuses.
    pub busy: Option<Box<dyn Fn() -> bool + Send>>,
}

/// A dialog of a flow (C# `ShowDialogAsync(dialog, opened, canClose, accept)` with the ContentDialog's own properties).
pub struct DialogSpec {
    /// `Title` (its words when it is a control).
    pub title: String,
    /// `Content`, built on every read: words make an alert, an element a sheet (a TextBlock an alert too).
    pub content: Box<dyn FnMut(&mut Ui) -> Content + Send>,
    pub chrome: Option<DialogChrome>,
    /// `CloseButtonText`, `SecondaryButtonText` with `IsSecondaryButtonEnabled`, `PrimaryButtonText` with `IsPrimaryButtonEnabled`.
    pub close: Option<String>,
    pub secondary: Option<DialogButton>,
    pub primary: Option<DialogButton>,
    pub default_button: DefaultButton,
    /// Refuses Esc and the buttons, as a Closing handler would.
    pub can_close: Option<Box<dyn Fn() -> bool + Send>>,
    /// Refuses a button, as a PrimaryButtonClick handler setting Cancel would.
    pub accept: Option<Box<dyn FnMut(DialogResult) -> bool + Send>>,
    /// Runs where the dialog's Opened would: once it was first pushed.
    pub opened: Option<Box<dyn FnOnce() + Send>>,
    /// `StyledDialog`: its words toned and its plain buttons painted, and its fixed size (each side fitted to the window by
    /// [`DialogSpec::fitted`]).
    pub styled: bool,
    pub width: Option<f64>,
    pub height: Option<f64>,
}

impl DialogSpec {
    /// A dialog titled `title` whose content `content` builds.
    pub fn new(title: impl Into<String>, content: impl FnMut(&mut Ui) -> Content + Send + 'static) -> DialogSpec {
        DialogSpec {
            title: title.into(),
            content: Box::new(content),
            chrome: None,
            close: None,
            secondary: None,
            primary: None,
            default_button: DefaultButton::None,
            can_close: None,
            accept: None,
            opened: None,
            styled: false,
            width: None,
            height: None,
        }
    }

    /// `StyledDialog(dialog, width, height, chrome)`.
    pub fn styled(mut self, width: Option<f64>, height: Option<f64>) -> DialogSpec {
        self.styled = true;
        self.width = width;
        self.height = height;
        self
    }

    /// `SheetFit` of each fixed side against the window's room (its client area in DIPs; 0 when unknown leaves the side).
    pub fn fitted(mut self, room: (f64, f64)) -> DialogSpec {
        self.width = self.width.map(|w| sheet_fit(w, room.0));
        self.height = self.height.map(|h| sheet_fit(h, room.1));
        self
    }
}

/// The surface of a dialog: its model is its spec; its answer goes to `answer` once.
pub struct DialogSurface {
    spec: DialogSpec,
    answer: Option<Box<dyn FnOnce(DialogResult) + Send>>,
}

impl DialogSurface {
    pub fn new(spec: DialogSpec, answer: impl FnOnce(DialogResult) + Send + 'static) -> DialogSurface {
        DialogSurface { spec, answer: Some(Box::new(answer)) }
    }

    fn enabled(button: &Option<DialogButton>) -> bool {
        button.as_ref().is_none_or(|b| (b.enabled)())
    }

    fn can_close(&self) -> Option<bool> {
        self.spec.can_close.as_ref().map(|f| f())
    }

    fn answer(&mut self, result: DialogResult) -> Next {
        if let Some(answer) = self.answer.take() {
            answer(result);
        }
        Next::Close
    }
}

impl SurfaceModel for DialogSurface {
    fn view(&mut self, ui: &mut Ui) -> View {
        let mut content = (self.spec.content)(ui);
        if self.spec.styled
            && let Content::El(el) = &mut content
        {
            ui.tone_secondary(el, true);
        }
        let busy_fallback = self.can_close() == Some(false);
        let alert = is_alert(&content);
        let spec = &mut self.spec;
        let chrome = match &mut spec.chrome {
            Some(chrome) => ChromeView {
                kind: if chrome.panel.is_some() {
                    "panel"
                } else if alert {
                    "alert"
                } else {
                    "sheet"
                },
                panel: chrome.panel.clone(),
                icon: chrome.icon.clone(),
                subtitle: chrome.subtitle.as_ref().and_then(|f| f()),
                danger: chrome.danger,
                busy: chrome.busy.as_ref().map_or(busy_fallback, |f| f()),
                min_width: chrome.min_width,
                min_height: chrome.min_height,
                tools: chrome.tools.as_mut().map(|f| f(ui)),
                status: chrome.status.as_mut().map(|f| f(ui)),
                actions: chrome.actions.as_mut().map(|f| f(ui)),
            },
            None => ChromeView { kind: if alert { "alert" } else { "sheet" }, busy: busy_fallback, ..ChromeView::default() },
        };
        let mut buttons = vec![];
        if let Some(label) = spec.close.as_ref().filter(|l| !l.is_empty()) {
            buttons.push(ButtonView {
                id: "close",
                label: label.clone(),
                disabled: false,
                accent: spec.default_button == DefaultButton::Close,
            });
        }
        if let Some(button) = spec.secondary.as_ref().filter(|b| !b.label.is_empty()) {
            buttons.push(ButtonView {
                id: "secondary",
                label: button.label.clone(),
                disabled: !(button.enabled)(),
                accent: spec.default_button == DefaultButton::Secondary,
            });
        }
        if let Some(button) = spec.primary.as_ref().filter(|b| !b.label.is_empty()) {
            buttons.push(ButtonView {
                id: "primary",
                label: button.label.clone(),
                disabled: !(button.enabled)(),
                accent: spec.default_button == DefaultButton::Primary,
            });
        }
        View::Dialog { title: spec.title.clone(), chrome, content, buttons, width: spec.width, height: spec.height }
    }

    /// C# `surface.Button`: a disabled primary or secondary is ignored; `canClose` or `accept` refusing pushes it again; else it
    /// answers Primary, Secondary, or None for the close button and Esc.
    fn button(&mut self, which: Option<&str>) -> Next {
        let result = match which {
            Some("primary") => DialogResult::Primary,
            Some("secondary") => DialogResult::Secondary,
            _ => DialogResult::None,
        };
        if result == DialogResult::Primary && !Self::enabled(&self.spec.primary)
            || result == DialogResult::Secondary && !Self::enabled(&self.spec.secondary)
        {
            return Next::Stay;
        }
        if self.can_close() == Some(false) || self.spec.accept.as_mut().is_some_and(|accept| !accept(result)) {
            return Next::Push;
        }
        self.answer(result)
    }

    /// C# `HideDialog`: closes as None unless `canClose` refuses.
    fn hide(&mut self) -> Next {
        if self.can_close() == Some(false) { Next::Stay } else { self.answer(DialogResult::None) }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::AtomicBool;

    use mighty_core::locale::Locale;
    use serde_json::json;

    use super::*;
    use crate::tokens::Theme;
    use crate::ui::El;

    struct Plain(&'static str);

    impl SurfaceModel for Plain {
        fn view(&mut self, ui: &mut Ui) -> View {
            View::Companion { title: self.0.into(), body: Some(ui.button("Go", || {})) }
        }
    }

    fn env() -> Env {
        Env { theme: Theme::Dark, text: Default::default(), locale: Locale::new("en") }
    }

    /// The push a read posts, if any.
    fn rendered(surfaces: &Surfaces, id: &str) -> Option<String> {
        surfaces.render(id, &env())
    }

    /// The close push, if the surface was open.
    fn closed(surfaces: &Surfaces, id: &str) -> Option<String> {
        let mut pushed = None;
        surfaces.close(id, |push| pushed = Some(push));
        pushed
    }

    /// Review M1: a close that comes while a read of the surface is posting waits for that post, so the page gets the view and then
    /// its close, never the close first; a read after the close posts nothing.
    #[test]
    fn a_close_never_overtakes_a_read_in_flight() {
        let surfaces = Arc::new(Surfaces::new());
        surfaces.open("dialog-1", Place::Dialog, Box::new(Plain("Ask")));
        let order = Arc::new(Mutex::new(Vec::<String>::new()));
        let (posting, posted) = std::sync::mpsc::channel::<()>();
        let (resume, waiting) = std::sync::mpsc::channel::<()>();
        let reader = {
            let (surfaces, order) = (surfaces.clone(), order.clone());
            std::thread::spawn(move || {
                surfaces.render_to("dialog-1", &env(), |push| {
                    posting.send(()).unwrap();
                    waiting.recv().unwrap();
                    order.lock().unwrap().push(if push.contains("\"view\"") { "view".into() } else { push });
                    true
                });
            })
        };
        posted.recv().unwrap();
        let closer = {
            let (surfaces, order) = (surfaces.clone(), order.clone());
            std::thread::spawn(move || {
                surfaces.close("dialog-1", |_| order.lock().unwrap().push("close".into()));
            })
        };
        std::thread::sleep(std::time::Duration::from_millis(100));
        resume.send(()).unwrap();
        reader.join().unwrap();
        closer.join().unwrap();
        assert_eq!(*order.lock().unwrap(), vec!["view".to_string(), "close".to_string()]);
        assert_eq!(rendered(&surfaces, "dialog-1"), None, "a closed surface is read no more");
    }

    /// Review L3: a push the page did not take (not ready) is not the text sent: the next read posts it again.
    #[test]
    fn a_push_not_taken_is_sent_again() {
        let surfaces = Surfaces::new();
        surfaces.open("companion", Place::Popover, Box::new(Plain("Status")));
        assert!(!surfaces.render_to("companion", &env(), |_| false));
        assert!(rendered(&surfaces, "companion").is_some());
    }

    /// Review L1: two sections' first buttons are two controls with two ids.
    #[test]
    fn sections_do_not_share_ids() {
        struct Two;
        impl SurfaceModel for Two {
            fn view(&mut self, ui: &mut Ui) -> View {
                let section = |ui: &mut Ui| crate::view::Section { title: None, beta: false, node: Some(ui.button("Go", || {})) };
                let sections = vec![section(ui), section(ui)];
                View::Settings {
                    title: "t".into(),
                    close: "c".into(),
                    nav: vec![],
                    selected: "general".into(),
                    sections,
                    width: 1.0,
                    height: 1.0,
                }
            }
        }
        let surfaces = Surfaces::new();
        surfaces.open("settings", Place::Surface, Box::new(Two));
        let pushed = rendered(&surfaces, "settings").unwrap();
        assert!(pushed.contains(r#""id":1,"#) && pushed.contains(r#""id":2,"#), "{pushed}");
    }

    #[test]
    fn a_surface_is_pushed_only_when_its_text_changed() {
        let surfaces = Surfaces::new();
        surfaces.open("companion", Place::Popover, Box::new(Plain("Status")));
        let first = rendered(&surfaces, "companion").unwrap();
        assert_eq!(
            first,
            r#"{"kind":"native","id":"companion","place":"popover","view":{"kind":"companion","title":"Status","sections":[{"beta":false,"node":{"t":"button","id":1,"name":"Go","label":"Go","ha":"start","va":"center"}}]}}"#
        );
        assert_eq!(rendered(&surfaces, "companion"), None, "unchanged: nothing pushed");
        surfaces.forget_sent();
        assert_eq!(rendered(&surfaces, "companion").as_deref(), Some(first.as_str()), "forgotten: pushed again");
        assert!(surfaces.act("companion", 1, "invoke", &Value::Null).taken());
        assert!(!surfaces.act("companion", 2, "invoke", &Value::Null).taken());
        assert_eq!(closed(&surfaces, "companion").as_deref(), Some(r#"{"kind":"native","id":"companion"}"#));
        assert_eq!(closed(&surfaces, "companion"), None);
        assert!(!surfaces.act("companion", 1, "invoke", &Value::Null).taken(), "a closed surface takes nothing");
    }

    #[test]
    fn surfaces_keep_their_open_order_and_reopen_at_the_end() {
        let surfaces = Surfaces::new();
        surfaces.open("settings", Place::Surface, Box::new(Plain("a")));
        surfaces.open("dialog-1", Place::Dialog, Box::new(Plain("b")));
        surfaces.open("settings", Place::Surface, Box::new(Plain("c")));
        assert_eq!(surfaces.ids(), vec!["dialog-1", "settings"]);
        assert_eq!((surfaces.next_dialog(), surfaces.next_dialog()), ("dialog-1".to_string(), "dialog-2".to_string()));
    }

    /// `ShowDialogAsync`: the view's keys, a refused button pushes again, a disabled one is ignored, the answer closes it.
    #[test]
    fn a_dialog_answers_once_its_button_is_accepted() {
        let answered = Arc::new(Mutex::new(None));
        let into = answered.clone();
        let valid = Arc::new(AtomicBool::new(false));
        let check = valid.clone();
        let mut spec = DialogSpec::new("Model id", |ui: &mut Ui| {
            Content::El(Box::new(El::vstack(vec![El::text_box("x").header("Id"), ui.settings_text("", Default::default())])))
        });
        spec.primary = Some(DialogButton::new("Select"));
        spec.close = Some("Cancel".into());
        spec.default_button = DefaultButton::Primary;
        spec.accept = Some(Box::new(move |result| result != DialogResult::Primary || check.load(Ordering::SeqCst)));
        let surfaces = Surfaces::new();
        surfaces.open("dialog-1", Place::Dialog, Box::new(DialogSurface::new(spec, move |r| *into.lock().unwrap() = Some(r))));
        let pushed = rendered(&surfaces, "dialog-1").unwrap();
        let view: Value = serde_json::from_str(&pushed).unwrap();
        assert_eq!(view["view"]["chrome"], json!({"kind": "sheet", "danger": false, "busy": false}));
        assert_eq!(
            view["view"]["buttons"],
            json!([{"id": "close", "label": "Cancel", "disabled": false, "accent": false}, {"id": "primary", "label": "Select", "disabled": false, "accent": true}])
        );
        // C# reads WinUI's own ContentDialogMaxWidth/MaxHeight off a dialog that set none (its capture: 548 and 756).
        assert_eq!((view["view"]["width"].clone(), view["view"]["height"].clone()), (json!(548), json!(756)), "an unstyled dialog's size");
        assert_eq!(surfaces.button("dialog-1", Some("primary")), Some(Next::Push), "accept refused");
        assert_eq!(*answered.lock().unwrap(), None);
        valid.store(true, Ordering::SeqCst);
        assert_eq!(surfaces.button("dialog-1", Some("primary")), Some(Next::Close));
        assert_eq!(*answered.lock().unwrap(), Some(DialogResult::Primary));
    }

    #[test]
    fn a_dialog_that_cannot_close_is_busy_and_stays() {
        let closable = Arc::new(AtomicBool::new(false));
        let check = closable.clone();
        let mut spec = DialogSpec::new("Plugins", |_: &mut Ui| Content::Text("Working".into()));
        spec.can_close = Some(Box::new(move || check.load(Ordering::SeqCst)));
        spec.secondary = Some(DialogButton::new("Later").enabled_when(|| false));
        let surfaces = Surfaces::new();
        surfaces.open("dialog-2", Place::Dialog, Box::new(DialogSurface::new(spec, |_| {})));
        let view: Value = serde_json::from_str(&rendered(&surfaces, "dialog-2").unwrap()).unwrap();
        assert_eq!(view["view"]["chrome"], json!({"kind": "alert", "danger": false, "busy": true}));
        assert_eq!(view["view"]["sections"], json!([{"beta": false, "node": {"t": "text", "text": "Working", "selectable": true}}]));
        assert_eq!(surfaces.button("dialog-2", Some("secondary")), Some(Next::Stay), "a disabled button is ignored");
        assert_eq!(surfaces.button("dialog-2", None), Some(Next::Push), "canClose refuses");
        assert_eq!(surfaces.hide("dialog-2"), Some(Next::Stay));
        closable.store(true, Ordering::SeqCst);
        assert_eq!(surfaces.hide("dialog-2"), Some(Next::Close));
    }

    #[test]
    fn a_styled_dialog_fits_the_window() {
        let spec = DialogSpec::new("t", |_: &mut Ui| Content::None).styled(Some(560.0), Some(520.0)).fitted((1440.0, 500.0));
        assert_eq!((spec.width, spec.height), (Some(560.0), Some(468.0)));
    }
}
