//! The views the host pushes and their `native` messages, written as C#'s anonymous objects with `Wire.Json` (MainWindow.WebShell.
//! Settings.cs:186-193 settings, :233-239 setup, :268-285 a dialog, :325 the companion; :73 and :91 the pushes): a member that is null
//! is left out (`WhenWritingNull`), the keys in C#'s order, the nodes read by the [`Reader`] as they are written.

use mighty_core::json::JsonOut;

use crate::reader::{Reader, write_into};
use crate::ui::{Content, El, Kind};

/// Where the page draws a view (`NativePlace`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Place {
    Surface,
    Dialog,
    Popover,
}

impl Place {
    pub fn name(self) -> &'static str {
        match self {
            Place::Surface => "surface",
            Place::Dialog => "dialog",
            Place::Popover => "popover",
        }
    }
}

/// One section: its heading (none for a dialog's or the setup screen's), its 베타 capsule, its body.
pub struct Section {
    pub title: Option<String>,
    pub beta: bool,
    pub node: Option<El>,
}

/// A settings category in the side list (`nav`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NavItem {
    pub id: String,
    pub title: String,
    /// The Segoe Fluent symbol (`SettingsCategoryGlyph`).
    pub glyph: String,
}

/// A dialog's button (`{id, label, disabled, accent}`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ButtonView {
    pub id: &'static str,
    pub label: String,
    pub disabled: bool,
    pub accent: bool,
}

/// What the page draws around a dialog's form (C# `DialogChrome` as `ShowDialogAsync` writes it).
#[derive(Default)]
pub struct ChromeView {
    /// `panel`, `alert` or `sheet`.
    pub kind: &'static str,
    pub panel: Option<String>,
    pub icon: Option<String>,
    pub subtitle: Option<String>,
    pub danger: bool,
    pub busy: bool,
    pub min_width: Option<f64>,
    pub min_height: Option<f64>,
    pub tools: Option<El>,
    pub status: Option<El>,
    pub actions: Option<El>,
}

/// WinUI's `ContentDialogMaxWidth`: a dialog's width when it set none.
pub const CONTENT_DIALOG_MAX_WIDTH: f64 = 548.0;
/// WinUI's `ContentDialogMaxHeight`: a dialog's height when it set none.
pub const CONTENT_DIALOG_MAX_HEIGHT: f64 = 756.0;

/// A view (`NativeView`).
#[allow(clippy::large_enum_variant, reason = "built once per read and written at once; never kept or moved around")]
pub enum View {
    /// Settings: its categories, the one chosen and that category's boxes (`kind:"settings"`, 860 × 640).
    Settings { title: String, close: String, nav: Vec<NavItem>, selected: String, sections: Vec<Section>, width: f64, height: f64 },
    /// The setup screen, the popup standard's panel `setup`.
    Setup { title: String, subtitle: String, close: String, body: Option<El>, footer: Option<El>, width: f64, height: f64 },
    /// A dialog of the window's flows: its content as words (an alert's sentence) or a form.
    Dialog { title: String, chrome: ChromeView, content: Content, buttons: Vec<ButtonView>, width: Option<f64>, height: Option<f64> },
    /// The companion's status popover (W8).
    Companion { title: String, body: Option<El> },
}

/// Whether a dialog's content is a sentence asked (an alert): words, or a TextBlock.
pub fn is_alert(content: &Content) -> bool {
    match content {
        Content::Text(_) => true,
        Content::El(el) => matches!(el.kind, Kind::TextBlock(_)),
        Content::None => false,
    }
}

/// `node = surface.Reader.Node(element)`, left out when null; its controls' key paths start with the part's own name (`name`, or the
/// section's `s<index>`), so two parts' controls never share an id.
fn node(out: &mut JsonOut, reader: &mut Reader, name: &str, el: Option<&El>) {
    node_as(out, reader, name, name, el);
}

fn node_as(out: &mut JsonOut, reader: &mut Reader, name: &str, part: &str, el: Option<&El>) {
    if let Some(node) = el.and_then(|el| reader.node_in(el, part)) {
        out.name(name);
        write_into(out, &node);
    }
}

/// `[{title?, beta, node?}]`.
fn sections(out: &mut JsonOut, reader: &mut Reader, sections: &[Section]) {
    out.name("sections");
    out.begin_array();
    for (index, section) in sections.iter().enumerate() {
        out.begin_object();
        out.prop_str("title", section.title.as_deref());
        out.name("beta");
        out.bool(section.beta);
        node_as(out, reader, "node", &format!("s{index}"), section.node.as_ref());
        out.end_object();
    }
    out.end_array();
}

impl View {
    /// The view's JSON, its nodes read by `reader` (which gives their ids and fills the act table).
    pub fn write(&self, reader: &mut Reader) -> String {
        let mut out = JsonOut::new();
        out.begin_object();
        match self {
            View::Settings { title, close, nav, selected, sections: list, width, height } => {
                out.prop_str("kind", Some("settings"));
                out.prop_str("title", Some(title));
                out.prop_str("subtitle", Some("Windows · WinUI"));
                out.prop_str("close", Some(close));
                out.name("nav");
                out.begin_array();
                for item in nav {
                    out.begin_object();
                    out.prop_str("id", Some(&item.id));
                    out.prop_str("title", Some(&item.title));
                    out.prop_str("glyph", Some(&item.glyph));
                    out.end_object();
                }
                out.end_array();
                out.prop_str("selected", Some(selected));
                sections(&mut out, reader, list);
                out.prop_float("width", *width);
                out.prop_float("height", *height);
            }
            View::Setup { title, subtitle, close, body, footer, width, height } => {
                out.prop_str("kind", Some("setup"));
                out.prop_str("title", Some(title));
                out.prop_str("subtitle", Some(subtitle));
                out.prop_str("close", Some(close));
                out.name("chrome");
                out.begin_object();
                out.prop_str("kind", Some("panel"));
                out.prop_str("panel", Some("setup"));
                out.prop_str("icon", Some("wrench"));
                out.end_object();
                out.name("sections");
                out.begin_array();
                out.begin_object();
                out.prop_bool("beta", false);
                node(&mut out, reader, "node", body.as_ref());
                out.end_object();
                out.end_array();
                node(&mut out, reader, "footer", footer.as_ref());
                out.prop_float("width", *width);
                out.prop_float("height", *height);
            }
            View::Dialog { title, chrome, content, buttons, width, height } => {
                out.prop_str("kind", Some("dialog"));
                out.prop_str("title", Some(title));
                out.name("chrome");
                out.begin_object();
                out.prop_str("kind", Some(chrome.kind));
                out.prop_str("panel", chrome.panel.as_deref());
                out.prop_str("icon", chrome.icon.as_deref());
                out.prop_str("subtitle", chrome.subtitle.as_deref());
                out.prop_bool("danger", chrome.danger);
                out.prop_bool("busy", chrome.busy);
                out.prop_opt_float("minWidth", chrome.min_width);
                out.prop_opt_float("minHeight", chrome.min_height);
                node(&mut out, reader, "tools", chrome.tools.as_ref());
                node(&mut out, reader, "status", chrome.status.as_ref());
                node(&mut out, reader, "actions", chrome.actions.as_ref());
                out.end_object();
                out.name("sections");
                out.begin_array();
                out.begin_object();
                out.prop_bool("beta", false);
                match content {
                    // A sentence: `{t:"text", text, selectable:true}`, not read off a control.
                    Content::Text(words) => {
                        out.name("node");
                        out.begin_object();
                        out.prop_str("t", Some("text"));
                        out.prop_str("text", Some(words));
                        out.prop_bool("selectable", true);
                        out.end_object();
                    }
                    Content::El(el) => node(&mut out, reader, "node", Some(el)),
                    Content::None => {}
                }
                out.end_object();
                out.end_array();
                out.name("buttons");
                out.begin_array();
                for button in buttons {
                    out.begin_object();
                    out.prop_str("id", Some(button.id));
                    out.prop_str("label", Some(&button.label));
                    out.prop_bool("disabled", button.disabled);
                    out.prop_bool("accent", button.accent);
                    out.end_object();
                }
                out.end_array();
                // C# reads `ContentDialogMaxWidth`/`MaxHeight` off the dialog's resources, which answer WinUI's own when the dialog
                // set none (C#'s capture: 548 and 756 for a plain ContentDialog).
                out.prop_float("width", width.unwrap_or(CONTENT_DIALOG_MAX_WIDTH));
                out.prop_float("height", height.unwrap_or(CONTENT_DIALOG_MAX_HEIGHT));
            }
            View::Companion { title, body } => {
                out.prop_str("kind", Some("companion"));
                out.prop_str("title", Some(title));
                out.name("sections");
                out.begin_array();
                out.begin_object();
                out.prop_bool("beta", false);
                node(&mut out, reader, "node", body.as_ref());
                out.end_object();
                out.end_array();
            }
        }
        out.end_object();
        out.finish()
    }
}

/// `{"kind":"native","id":…,"place":…,"view":{…}}` around a view's JSON.
pub fn open_push(id: &str, place: Place, view: &str) -> String {
    let mut out = JsonOut::new();
    out.begin_object();
    out.prop_str("kind", Some("native"));
    out.prop_str("id", Some(id));
    out.prop_str("place", Some(place.name()));
    out.name("view");
    out.raw(view);
    out.end_object();
    out.finish()
}

/// `{"kind":"native","id":…}`: the view closed (its null `view` left out).
pub fn close_push(id: &str) -> String {
    let mut out = JsonOut::new();
    out.begin_object();
    out.prop_str("kind", Some("native"));
    out.prop_str("id", Some(id));
    out.end_object();
    out.finish()
}
