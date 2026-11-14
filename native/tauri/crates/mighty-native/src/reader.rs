//! The reader: a control tree into the page's NativeNode dictionaries in C#'s key order (C# `WebNativeReader.Node`, `Read`, `Words`,
//! `BlockText`, `MenuItems`; WebNative.cs:123-371), written as C#'s `Wire.Json` writes them (`mighty_core::json::JsonOut`).
//!
//! Its quirks are C#'s:
//! - what does not show is dropped: not visible, opacity 0, a TextBlock without words, a shape (but an Ellipse whose fill has a
//!   name), a SymbolIcon, an empty stack or grid, an inactive ring, a closed info bar, an Image without a picture;
//! - a control's enabled state is its own and every ancestor control's, worked out before it is read;
//! - `role == "loading"` is `{t:"loading"}` with nothing after it;
//! - a ScrollViewer, a ContentControl, a Viewbox and a bare Border answer their child's dictionary, and the wrapper's tail is then
//!   written into it: a key the child had keeps its place with the wrapper's value, a new one goes at the end;
//! - a dictionary's null is written as `null` (a glyph's `ink`, a closed expander's `c`, a grid without definitions' `cols`), ids,
//!   indexes and cells are integers, every other number a double in .NET's shortest form.
//!
//! What a TextBlock inherits is followed down the tree as C# reads it: its ink (WinUI's stock text colour, at the root and inside a
//! control alike: C# reads each form as it builds it, before the window's `DesignBrushes` or a template's `Foreground` reach it, so
//! its capture reads `on-status` by night), its size (a control's `TextSize`, else the stock 14 at the text setting's scale; at the
//! root a plain 14) and its family (a control's, else the interface font).

use indexmap::IndexMap;
use mighty_core::design_tokens::{Token, metrics};
use mighty_core::json::{JsonOut, format_double};

use crate::act::{Acts, Target};
use crate::ids::Ids;
use crate::tokens::Brush;
use crate::ui::{Content, El, Env, HAlign, Inline, Kind, MenuItem, Thickness, Track, VAlign, Wrapping};

/// A value of a node dictionary (C#'s `object?` in a `Dictionary<string, object?>`).
#[derive(Debug, Clone, PartialEq)]
pub enum J {
    Null,
    Bool(bool),
    Int(i64),
    Num(f64),
    Str(String),
    Arr(Vec<J>),
    Obj(Dict),
}

/// A node: its keys in the order C# added them; `insert` on a key it has keeps its place.
pub type Dict = IndexMap<&'static str, J>;

impl From<&str> for J {
    fn from(text: &str) -> J {
        J::Str(text.to_string())
    }
}

impl From<String> for J {
    fn from(text: String) -> J {
        J::Str(text)
    }
}

impl From<bool> for J {
    fn from(value: bool) -> J {
        J::Bool(value)
    }
}

impl From<f64> for J {
    fn from(value: f64) -> J {
        J::Num(value)
    }
}

impl From<Dict> for J {
    fn from(dict: Dict) -> J {
        J::Obj(dict)
    }
}

impl From<Option<&str>> for J {
    fn from(text: Option<&str>) -> J {
        text.map_or(J::Null, J::from)
    }
}

/// Writes a value as C#'s `Wire.Json` does.
pub fn write_into(out: &mut JsonOut, value: &J) {
    match value {
        J::Null => out.null(),
        J::Bool(b) => out.bool(*b),
        J::Int(i) => out.int(*i),
        J::Num(n) => out.float(*n),
        J::Str(s) => out.str(s),
        J::Arr(items) => {
            out.begin_array();
            for item in items {
                write_into(out, item);
            }
            out.end_array();
        }
        J::Obj(dict) => {
            out.begin_object();
            for (name, item) in dict {
                out.name(name);
                write_into(out, item);
            }
            out.end_object();
        }
    }
}

/// A value's JSON text.
pub fn write(value: &J) -> String {
    let mut out = JsonOut::new();
    write_into(&mut out, value);
    out.finish()
}

fn dict(pairs: Vec<(&'static str, J)>) -> Dict {
    pairs.into_iter().collect()
}

fn edges(t: &Thickness) -> J {
    J::Arr(vec![J::Num(t.left), J::Num(t.top), J::Num(t.right), J::Num(t.bottom)])
}

/// `Track(GridLength)`: `auto`, `minmax(0,{v}fr)` or `{v}px`, the number as .NET's invariant shortest text.
fn track(length: &Track) -> String {
    match length {
        Track::Auto => "auto".into(),
        Track::Star(v) => format!("minmax(0,{}fr)", format_double(*v)),
        Track::Pixel(v) => format!("{}px", format_double(*v)),
    }
}

/// `BlockText`: the TextBlock's `Text`, else its inlines' words.
fn block_text(text: &str, inlines: &[Inline]) -> String {
    if !text.is_empty() || inlines.is_empty() {
        return text.to_string();
    }
    fn inline(item: &Inline) -> String {
        match item {
            Inline::Run(text) => text.clone(),
            Inline::LineBreak => "\n".into(),
            Inline::Span(items) => items.iter().map(inline).collect(),
        }
    }
    inlines.iter().map(inline).collect()
}

/// `WebNativeReader.Words`: the words of a content, hidden parts included.
pub fn words(content: &Content) -> String {
    match content {
        Content::None => String::new(),
        Content::Text(text) => text.clone(),
        Content::El(el) => el_words(el),
    }
}

/// `Words` of an element: a TextBlock's words, a content control's content's, a panel's children's (joined by a space, empty ones
/// left out), a border's or viewbox's child's; nothing for any other element.
pub fn el_words(el: &El) -> String {
    match &el.kind {
        Kind::TextBlock(block) => block_text(&block.text, &block.inlines),
        Kind::Hyperlink { content, .. }
        | Kind::CheckBox { content, .. }
        | Kind::RadioButton { content, .. }
        | Kind::ToggleButton { content, .. }
        | Kind::ScrollViewer { content }
        | Kind::Expander { content, .. }
        | Kind::ContentControl { content } => words(content),
        Kind::Button(button) => words(&button.content),
        Kind::Grid(grid) => join_words(&grid.children),
        Kind::StackPanel(stack) => join_words(&stack.children),
        Kind::Panel { children } => join_words(children),
        Kind::Border(border) => border.child.as_deref().map(el_words).unwrap_or_default(),
        Kind::Viewbox { child } => child.as_deref().map(el_words).unwrap_or_default(),
        _ => String::new(),
    }
}

fn join_words(children: &[El]) -> String {
    children.iter().map(el_words).filter(|w| !w.is_empty()).collect::<Vec<_>>().join(" ")
}

/// What a TextBlock inherits at this point of the tree.
#[derive(Clone)]
struct Inherit {
    ink: Option<Brush>,
    /// The size as drawn (scaled).
    size: f64,
    family: Option<String>,
}

/// The stock size of a control's words (`ControlContentThemeFontSize`), at 100%.
const STOCK_SIZE: f64 = 14.0;
/// A FontIcon's own size when none is given.
const ICON_SIZE: f64 = 20.0;

/// One read of a surface (C# `Begin` then `Node`): it gives ids from the host's [`Ids`] and records what each id can do in [`Acts`].
pub struct Reader<'a> {
    env: &'a Env,
    surface: &'a str,
    ids: &'a mut Ids,
    acts: &'a mut Acts,
}

impl<'a> Reader<'a> {
    /// A read of `surface`; the caller clears `acts` first when this is a new read (C# `Begin`).
    pub fn new(env: &'a Env, surface: &'a str, ids: &'a mut Ids, acts: &'a mut Acts) -> Reader<'a> {
        Reader { env, surface, ids, acts }
    }

    /// What the form's root hands down: WinUI's stock text colour, not the theme's `ink` (C#'s forms sit in a hidden host where the
    /// window's `DesignBrushes` do not reach the stock TextBlock style; C#'s capture reads `on-status` by night).
    fn root(&self) -> Inherit {
        Inherit { ink: Some(self.env.theme.stock_text()), size: STOCK_SIZE, family: None }
    }

    /// The interface font a control draws in by default (`ContentControlThemeFontFamily`).
    fn control_family(&self) -> String {
        self.env.text.family.clone().unwrap_or_else(|| metrics::FONT_BODY.to_string())
    }

    /// What a control's content inherits when the control's style sets its own words' look (the stock 14; WinUI's stock text colour:
    /// C# reads every form as it builds it, before a template hands the control's `Foreground` down, so its capture reads a button's
    /// glyph and words in `on-status` by night whatever the button was painted with).
    fn control_inherit(&self, size: Option<f64>) -> Inherit {
        Inherit {
            ink: Some(self.env.theme.stock_text()),
            size: self.env.text.size(size.unwrap_or(STOCK_SIZE)),
            family: Some(self.control_family()),
        }
    }

    /// `Unscaled`: a size as the form wrote it, before the text setting scaled it.
    fn unscaled(&self, size: f64) -> f64 {
        (size / self.env.text.scale * 2.0).round() / 2.0
    }

    fn tone(&self, brush: Option<Brush>) -> Option<&'static str> {
        self.env.theme.tone(brush)
    }

    fn id(&mut self, path: &str, kind: &str, words: &str) -> i64 {
        self.ids.id(self.surface, &format!("{path}|{kind}|{words}"))
    }

    /// `Node(element)`: the element's node, or none when nothing of it shows. The root of a section, a footer or a chrome part.
    pub fn node(&mut self, el: &El) -> Option<J> {
        self.node_in(el, "")
    }

    /// `Node(element)` for one part of a view (a section, the footer, a chrome part): its controls' key paths start with `part`, so two
    /// sections' first buttons are different controls.
    pub fn node_in(&mut self, el: &El, part: &str) -> Option<J> {
        let root = self.root();
        self.node_at(el, true, part, &root).map(J::Obj)
    }

    /// `Node(content)` for a content that may be words.
    pub fn content(&mut self, content: &Content) -> Option<J> {
        let root = self.root();
        self.content_at(content, true, "", &root).map(J::Obj)
    }

    fn content_at(&mut self, content: &Content, enabled: bool, path: &str, inherit: &Inherit) -> Option<Dict> {
        match content {
            Content::None => None,
            Content::Text(text) if text.is_empty() => None,
            Content::Text(text) => Some(dict(vec![("t", "text".into()), ("text", text.clone().into())])),
            Content::El(el) => self.node_at(el, enabled, path, inherit),
        }
    }

    fn node_at(&mut self, el: &El, enabled: bool, path: &str, inherit: &Inherit) -> Option<Dict> {
        if !el.visible || el.opacity == 0.0 {
            return None;
        }
        let enabled = enabled && (!el.is_control() || el.enabled);
        if el.role == Some("loading") {
            return Some(dict(vec![("t", "loading".into())]));
        }
        let mut node = self.read(el, enabled, path, inherit)?;
        if let Some(role) = el.role {
            node.insert("role", role.into());
        }
        if let Some(aid) = el.aid.as_deref().filter(|a| !a.is_empty()) {
            node.insert("aid", aid.into());
        }
        if let Some(help) = el.tooltip.as_deref().filter(|h| !h.is_empty()) {
            node.insert("help", help.into());
        }
        if !el.margin.is_zero() {
            node.insert("margin", edges(&el.margin));
        }
        match el.ha {
            HAlign::Left => node.insert("ha", "start".into()),
            HAlign::Center => node.insert("ha", "center".into()),
            HAlign::Right => node.insert("ha", "end".into()),
            HAlign::Stretch => None,
        };
        match el.va {
            VAlign::Top => node.insert("va", "start".into()),
            VAlign::Center => node.insert("va", "center".into()),
            VAlign::Bottom => node.insert("va", "end".into()),
            VAlign::Stretch => None,
        };
        if let Some(width) = el.width
            && !matches!(el.kind, Kind::TextBlock(_))
        {
            node.insert("w", width.into());
        }
        if let Some(height) = el.height
            && (matches!(el.kind, Kind::Image { .. } | Kind::Border(_)) || el.is_panel())
        {
            node.insert("h", height.into());
        }
        if let Some(max) = el.max_width.filter(|m| *m > 0.0 && m.is_finite()) {
            node.insert("maxW", max.into());
        }
        if let Some(max) = el.max_height.filter(|m| *m > 0.0 && m.is_finite()) {
            node.insert("maxH", max.into());
        }
        if let Some(min) = el.min_width.filter(|m| *m > 0.0)
            && !el.is_control()
        {
            node.insert("minW", min.into());
        }
        if el.opacity < 0.75 {
            node.insert("dim", true.into());
        }
        if el.is_control() && !enabled {
            node.insert("disabled", true.into());
        }
        Some(node)
    }

    fn nodes(&mut self, children: &[El], enabled: bool, path: &str, inherit: &Inherit) -> Vec<J> {
        let mut list = vec![];
        for (index, child) in children.iter().enumerate() {
            let at = child_path(path, child, index);
            if let Some(node) = self.node_at(child, enabled, &at, inherit) {
                list.push(J::Obj(node));
            }
        }
        list
    }

    fn read(&mut self, el: &El, enabled: bool, path: &str, inherit: &Inherit) -> Option<Dict> {
        match &el.kind {
            Kind::TextBlock(block) => {
                let text = block_text(&block.text, &block.inlines);
                if text.is_empty() {
                    return None;
                }
                let size = block.size.map_or(inherit.size, |s| self.env.text.size(s));
                let mut node = dict(vec![("t", "text".into()), ("text", text.into()), ("size", self.unscaled(size).into())]);
                if block.weight >= 600 {
                    node.insert("weight", "bold".into());
                } else if block.weight >= 500 {
                    node.insert("weight", "medium".into());
                }
                let family = block.family.as_deref().or(inherit.family.as_deref());
                if family.is_some_and(|face| {
                    let face = face.to_lowercase();
                    face.contains("mono") || face.contains("consol") || face.contains("code")
                }) {
                    node.insert("mono", true.into());
                }
                if let Some(ink) = self.tone(block.foreground.or(inherit.ink)) {
                    node.insert("ink", ink.into());
                }
                if block.wrapping == Wrapping::NoWrap {
                    node.insert("nowrap", true.into());
                }
                if block.selectable {
                    node.insert("selectable", true.into());
                }
                Some(node)
            }
            Kind::RichText { paragraphs, size } => {
                let text = paragraphs.join("\n");
                if text.is_empty() {
                    return None;
                }
                let size = size.map_or(inherit.size, |s| self.env.text.size(s));
                Some(dict(vec![
                    ("t", "text".into()),
                    ("text", text.into()),
                    ("size", self.unscaled(size).into()),
                    ("selectable", true.into()),
                ]))
            }
            Kind::RichEdit { text, size } => {
                let text = text.trim_end_matches(['\r', '\n']).replace('\r', "\n");
                if text.is_empty() {
                    return None;
                }
                let size = self.env.text.size(size.unwrap_or(STOCK_SIZE));
                Some(dict(vec![
                    ("t", "text".into()),
                    ("text", text.into()),
                    ("size", self.unscaled(size).into()),
                    ("selectable", true.into()),
                ]))
            }
            Kind::FontIcon { glyph, size, foreground } => {
                let size = size.map_or(ICON_SIZE, |s| self.env.text.size(s));
                Some(dict(vec![
                    ("t", "glyph".into()),
                    ("glyph", glyph.clone().into()),
                    ("size", self.unscaled(size).into()),
                    ("ink", self.tone(foreground.or(inherit.ink)).into()),
                ]))
            }
            Kind::SymbolIcon | Kind::Shape => None,
            Kind::Image { src } => src.as_ref().map(|src| dict(vec![("t", "image".into()), ("src", src.clone().into())])),
            Kind::Ellipse { fill } => {
                let fill = self.tone(*fill)?;
                Some(dict(vec![("t", "dot".into()), ("fill", fill.into()), ("size", el.width.unwrap_or(8.0).into())]))
            }
            Kind::ProgressRing { active } => active.then(|| dict(vec![("t", "spinner".into())])),
            Kind::ProgressBar { value, max, indeterminate } => Some(dict(vec![
                ("t", "progress".into()),
                ("value", (*value).into()),
                ("max", (*max).into()),
                ("indeterminate", (*indeterminate).into()),
            ])),
            Kind::Hyperlink { content, uri } => {
                let label = Some(words(content)).filter(|w| !w.is_empty()).unwrap_or_else(|| el.name.clone());
                let id = self.id(path, "link", &label);
                self.acts.insert(id, Target::Link { uri: uri.clone() });
                Some(dict(vec![("t", "link".into()), ("id", J::Int(id)), ("label", label.into())]))
            }
            Kind::CheckBox { on, content } => {
                let label = words(content);
                let id = self.id(path, "check", &label);
                self.acts.insert(id, Target::Toggle { enabled, on: *on, handler: el.on.clone() });
                Some(dict(vec![("t", "check".into()), ("id", J::Int(id)), ("on", (*on).into()), ("label", label.into())]))
            }
            Kind::RadioButton { on, content } => {
                let label = words(content);
                let id = self.id(path, "radio", &label);
                self.acts.insert(id, Target::Radio { enabled, on: *on, handler: el.on.clone() });
                Some(dict(vec![("t", "radio".into()), ("id", J::Int(id)), ("on", (*on).into()), ("label", label.into())]))
            }
            Kind::ToggleSwitch { on, header } => {
                let name = Some(words(header)).filter(|w| !w.is_empty()).unwrap_or_else(|| el.name.clone());
                let id = self.id(path, "switch", &name);
                self.acts.insert(id, Target::Switch { enabled, on: *on, handler: el.on.clone() });
                Some(dict(vec![("t", "switch".into()), ("id", J::Int(id)), ("on", (*on).into()), ("name", name.into())]))
            }
            Kind::ToggleButton { on, .. } => {
                let id = self.id(path, "toggle", &el.name);
                self.acts.insert(id, Target::Toggle { enabled, on: *on, handler: el.on.clone() });
                Some(dict(vec![("t", "switch".into()), ("id", J::Int(id)), ("on", (*on).into()), ("name", el.name.clone().into())]))
            }
            Kind::ComboBox { index, items } => {
                let id = self.id(path, "select", &el.name);
                self.acts.insert(id, Target::Combo { enabled, index: *index, count: items.len(), handler: el.on.clone() });
                Some(dict(vec![
                    ("t", "select".into()),
                    ("id", J::Int(id)),
                    ("index", J::Int(i64::from(*index))),
                    ("name", el.name.clone().into()),
                    ("items", J::Arr(items.iter().map(|item| words(item).into()).collect())),
                ]))
            }
            Kind::TextBox { text, placeholder, multiline, readonly, header, family } => {
                let name = Some(el.name.clone()).filter(|n| !n.is_empty()).unwrap_or_else(|| words(header));
                let id = self.id(path, "field", &name);
                self.acts.insert(id, Target::Field { enabled, readonly: *readonly, text: text.clone(), handler: el.on.clone() });
                let face = family.clone().unwrap_or_else(|| self.control_family());
                Some(dict(vec![
                    ("t", "field".into()),
                    ("id", J::Int(id)),
                    ("text", text.clone().into()),
                    ("placeholder", placeholder.clone().into()),
                    ("multiline", (*multiline).into()),
                    ("readonly", (*readonly).into()),
                    ("name", name.into()),
                    ("mono", face.to_lowercase().contains("mono").into()),
                ]))
            }
            Kind::PasswordBox { text, placeholder } => {
                let id = self.id(path, "password", &el.name);
                self.acts.insert(id, Target::Password { enabled, text: text.clone(), handler: el.on.clone() });
                Some(dict(vec![
                    ("t", "field".into()),
                    ("id", J::Int(id)),
                    ("text", text.clone().into()),
                    ("password", true.into()),
                    ("placeholder", placeholder.clone().into()),
                    ("name", el.name.clone().into()),
                ]))
            }
            Kind::Slider { value, min, max, step } => {
                let id = self.id(path, "slider", &el.name);
                self.acts.insert(id, Target::Slider { enabled, value: *value, min: *min, max: *max, handler: el.on.clone() });
                Some(dict(vec![
                    ("t", "slider".into()),
                    ("id", J::Int(id)),
                    ("value", (*value).into()),
                    ("min", (*min).into()),
                    ("max", (*max).into()),
                    ("step", (*step).into()),
                    ("name", el.name.clone().into()),
                ]))
            }
            Kind::Button(button) => {
                let label = matches!(&button.content, Content::Text(_))
                    || matches!(&button.content, Content::El(inner) if matches!(inner.kind, Kind::TextBlock(_)));
                let id = self.id(
                    path,
                    if button.flyout.is_some() { "menu" } else { "button" },
                    &format!("{}|{}", el.name, words(&button.content)),
                );
                self.acts.insert(id, Target::Button { enabled, on: el.on.clone() });
                let mut node = dict(vec![("t", "button".into()), ("id", J::Int(id)), ("name", el.name.clone().into())]);
                if label {
                    node.insert("label", words(&button.content).into());
                } else {
                    let inside = self.control_inherit(button.size);
                    if let Some(inner) = self.content_at(&button.content, enabled, &format!("{path}/c"), &inside) {
                        node.insert("c", J::Arr(vec![J::Obj(inner)]));
                    }
                }
                if self.tone(button.background) == Some("accent") {
                    node.insert("accent", true.into());
                }
                if self.tone(button.foreground.or(Some(self.env.theme.brush(Token::Ink)))) == Some("err-text") {
                    node.insert("destructive", true.into());
                }
                if let Some(menu) = &button.flyout {
                    node.insert("t", "menu".into());
                    let items = self.menu_items(menu, &format!("{path}/m"));
                    node.insert("items", J::Arr(items));
                }
                Some(node)
            }
            Kind::ScrollViewer { content } | Kind::ContentControl { content } => {
                self.content_at(content, enabled, &format!("{path}/c"), inherit)
            }
            Kind::Expander { header, content, open } => {
                let is_open = open.get();
                let header_words = words(header);
                let id = self.id(path, "expander", &header_words);
                self.acts.insert(id, Target::Expander { open: open.clone() });
                let inside = self.control_inherit(None);
                let header = self.content_at(header, enabled, &format!("{path}/h"), &inside).map_or(J::Null, J::Obj);
                let children = if is_open {
                    J::Arr(self.content_at(content, enabled, &format!("{path}/c"), &inside).map(J::Obj).into_iter().collect())
                } else {
                    J::Null
                };
                Some(dict(vec![
                    ("t", "expander".into()),
                    ("id", J::Int(id)),
                    ("open", is_open.into()),
                    ("header", header),
                    ("c", children),
                ]))
            }
            Kind::InfoBar { open, title, message, severity } => open.then(|| {
                dict(vec![
                    ("t", "info".into()),
                    ("title", title.as_deref().into()),
                    ("message", message.as_deref().into()),
                    ("severity", (*severity).into()),
                ])
            }),
            Kind::ListView { index, items } => {
                let id = self.id(path, "list", &el.name);
                self.acts.insert(id, Target::List { index: *index, count: items.len(), handler: el.on.clone() });
                let inside = self.control_inherit(None);
                let mut rows = vec![];
                for (at, item) in items.iter().enumerate() {
                    let item_path = match item {
                        Content::El(row) => child_path(&format!("{path}/i"), row, at),
                        _ => format!("{path}/i/{at}"),
                    };
                    let node = self
                        .content_at(item, enabled, &item_path, &inside)
                        .unwrap_or_else(|| dict(vec![("t", "text".into()), ("text", words(item).into())]));
                    rows.push(J::Obj(node));
                }
                Some(dict(vec![("t", "list".into()), ("id", J::Int(id)), ("index", J::Int(i64::from(*index))), ("items", J::Arr(rows))]))
            }
            Kind::ItemsControl { items } => {
                let mut list = vec![];
                for (at, item) in items.iter().enumerate() {
                    let item_path = match item {
                        Content::El(row) => child_path(path, row, at),
                        _ => format!("{path}/{at}"),
                    };
                    if let Some(node) = self.content_at(item, enabled, &item_path, inherit) {
                        list.push(J::Obj(node));
                    }
                }
                Some(dict(vec![("t", "stack".into()), ("dir", "v".into()), ("c", J::Arr(list))]))
            }
            Kind::Viewbox { child } => child.as_deref().and_then(|child| self.node_at(child, enabled, &format!("{path}/c"), inherit)),
            Kind::Border(border) => {
                let fill = self.tone(border.background);
                let lined = !border.border_thickness.is_zero() && self.tone(border.border_brush).is_some();
                let Some(child) = border.child.as_deref() else {
                    let thin = |side: Option<f64>| side.is_some_and(|s| s <= 2.0);
                    return (fill.is_some() && (thin(el.height) || thin(el.width)))
                        .then(|| dict(vec![("t", "rule".into()), ("fill", fill.into()), ("vertical", thin(el.width).into())]));
                };
                let child = self.node_at(child, enabled, &format!("{path}/c"), inherit)?;
                if fill.is_none() && !lined && border.padding.is_zero() && border.corner_radius == 0.0 {
                    return Some(child);
                }
                let mut node = dict(vec![("t", "box".into()), ("c", J::Arr(vec![J::Obj(child)]))]);
                if let Some(fill) = fill {
                    node.insert("fill", fill.into());
                }
                if lined {
                    node.insert("line", edges(&border.border_thickness));
                    node.insert("lineInk", self.tone(border.border_brush).into());
                }
                if !border.padding.is_zero() {
                    node.insert("pad", edges(&border.padding));
                }
                if border.corner_radius > 0.0 {
                    node.insert("radius", border.corner_radius.into());
                }
                Some(node)
            }
            Kind::Grid(grid) if grid.segmented => {
                // The form's segmented picker (SettingsSegmented): its segments are the toggles in it, shown or not.
                let mut options = vec![];
                for (index, child) in grid.children.iter().enumerate() {
                    let Kind::ToggleButton { on, content } = &child.kind else { continue };
                    let label = words(content);
                    let at = child_path(path, child, index);
                    let id = self.id(&at, "segment", &label);
                    self.acts.insert(id, Target::Toggle { enabled: enabled && child.enabled, on: *on, handler: child.on.clone() });
                    options.push(J::Obj(dict(vec![("id", J::Int(id)), ("label", label.into()), ("on", (*on).into())])));
                }
                let disabled = el.opacity < 0.75 || options.is_empty();
                Some(dict(vec![
                    ("t", "segmented".into()),
                    ("name", el.name.clone().into()),
                    ("options", J::Arr(options)),
                    ("disabled", disabled.into()),
                ]))
            }
            Kind::Grid(grid) => {
                let mut cells = vec![];
                for (index, child) in grid.children.iter().enumerate() {
                    let at = child_path(path, child, index);
                    let Some(mut node) = self.node_at(child, enabled, &at, inherit) else { continue };
                    node.insert("row", J::Int(child.cell.row));
                    node.insert("col", J::Int(child.cell.col));
                    if child.cell.row_span > 1 {
                        node.insert("rowSpan", J::Int(child.cell.row_span));
                    }
                    if child.cell.col_span > 1 {
                        node.insert("colSpan", J::Int(child.cell.col_span));
                    }
                    cells.push(J::Obj(node));
                }
                if cells.is_empty() {
                    return None;
                }
                let tracks = |list: &[Track], rows: bool| {
                    if list.is_empty() {
                        J::Null
                    } else {
                        J::Arr(
                            list.iter()
                                .map(|t| if rows && matches!(t, Track::Star(_)) { "auto".into() } else { track(t).into() })
                                .collect(),
                        )
                    }
                };
                let mut node = dict(vec![
                    ("t", "grid".into()),
                    ("c", J::Arr(cells)),
                    ("cols", tracks(&grid.cols, false)),
                    ("rows", tracks(&grid.rows, true)),
                    ("gap", J::Arr(vec![J::Num(grid.row_spacing), J::Num(grid.col_spacing)])),
                ]);
                if !grid.padding.is_zero() {
                    node.insert("pad", edges(&grid.padding));
                }
                if let Some(fill) = self.tone(grid.background) {
                    node.insert("fill", fill.into());
                }
                Some(node)
            }
            Kind::StackPanel(stack) => {
                let children = self.nodes(&stack.children, enabled, path, inherit);
                if children.is_empty() {
                    return None;
                }
                let mut node = dict(vec![
                    ("t", "stack".into()),
                    ("dir", if stack.horizontal { "h" } else { "v" }.into()),
                    ("gap", stack.spacing.into()),
                    ("c", J::Arr(children)),
                ]);
                if !stack.padding.is_zero() {
                    node.insert("pad", edges(&stack.padding));
                }
                if let Some(fill) = self.tone(stack.background) {
                    node.insert("fill", fill.into());
                }
                Some(node)
            }
            Kind::Panel { children } => {
                let children = self.nodes(children, enabled, path, inherit);
                (!children.is_empty()).then(|| dict(vec![("t", "stack".into()), ("dir", "v".into()), ("c", J::Arr(children))]))
            }
        }
    }

    /// `MenuItems`: separators and rows with words; a sub-menu's own rows under it.
    fn menu_items(&mut self, items: &[MenuItem], path: &str) -> Vec<J> {
        let mut list = vec![];
        for (index, item) in items.iter().enumerate() {
            let at = format!("{path}/{index}");
            let node = match item {
                MenuItem::Separator => dict(vec![("sep", true.into())]),
                MenuItem::Sub { text, items, enabled } => {
                    let rows = self.menu_items(items, &at);
                    dict(vec![("label", text.clone().into()), ("items", J::Arr(rows)), ("disabled", (!enabled).into())])
                }
                MenuItem::Toggle { text, checked, enabled, on } => {
                    let id = self.id(&at, "toggleItem", text);
                    self.acts.insert(id, Target::ToggleMenuItem { enabled: *enabled, checked: *checked, on: on.clone() });
                    dict(vec![
                        ("id", J::Int(id)),
                        ("label", text.clone().into()),
                        ("checked", (*checked).into()),
                        ("disabled", (!enabled).into()),
                    ])
                }
                MenuItem::Item { text, enabled, on } => {
                    let id = self.id(&at, "item", text);
                    self.acts.insert(id, Target::MenuItem { enabled: *enabled, on: on.clone() });
                    dict(vec![("id", J::Int(id)), ("label", text.clone().into()), ("disabled", (!enabled).into())])
                }
            };
            let kept = node.contains_key("sep") || matches!(node.get("label"), Some(J::Str(label)) if !label.is_empty());
            if kept {
                list.push(J::Obj(node));
            }
        }
        list
    }
}

/// A child's key path: its key among its siblings, else its position.
fn child_path(parent: &str, child: &El, index: usize) -> String {
    match &child.key {
        Some(key) => format!("{parent}/#{key}"),
        None => format!("{parent}/{index}"),
    }
}

/// The node JSON of `el` read on its own (a throwaway id counter and act table): what a builder's test compares.
pub fn render(el: &El, env: &Env) -> String {
    let (mut ids, mut acts) = (Ids::default(), Acts::default());
    let node = Reader::new(env, "test", &mut ids, &mut acts).node(el);
    node.as_ref().map_or_else(|| "null".to_string(), write)
}
