//! The virtual WinUI: the control kinds the C# settings builders use, with only the properties C# `WebNativeReader` reads
//! (WebNative.cs), and the context a builder runs in ([`Ui`]).
//!
//! Builders are immediate-mode: every read of a surface builds its tree again from the surface's models, with no I/O, and the reader
//! turns it into the page's nodes. An input (a click, a switch, a pick, typed words) reaches the handler the builder gave the control.
//! What the C# control kept by itself between reads (a field's typed words, an expander's disclosure) lives in the surface's
//! [`Locals`], under a key the builder names.
//!
//! The defaults are WinUI's: a TextBlock does not wrap and is 14 at the root, a FontIcon is 20, alignments stretch, sizes are NaN
//! (none), every control is enabled, the automation name is "".

use std::any::Any;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use mighty_core::design_tokens::Token;
use mighty_core::locale::Locale;
use mighty_core::text_appearance::TextAppearance;

use crate::tokens::{Brush, Theme};

/// What a control's handler is told (WebNative.cs `Act`'s ops, as the control's own event would carry them).
#[derive(Debug, Clone, PartialEq)]
pub enum Input {
    /// A button's Click, a menu row's Click.
    Invoke,
    /// A toggle's new state: a check box, a radio button (always true), a switch, a segment, a toggle menu row.
    Toggle(bool),
    /// A pop-up's or a list's new row.
    Select(i32),
    /// A field's new words.
    Text(String),
    /// A slider's new value, held to its range.
    Value(f64),
}

/// A control's handler, run on the thread that took the page's input, with no surface lock held.
pub type Handler = Arc<dyn Fn(Input) + Send + Sync>;

/// A handler from a closure.
pub fn handler(f: impl Fn(Input) + Send + Sync + 'static) -> Handler {
    Arc::new(f)
}

/// WinUI's `Thickness`: left, top, right, bottom.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Thickness {
    pub left: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
}

impl Thickness {
    pub const ZERO: Thickness = Thickness { left: 0.0, top: 0.0, right: 0.0, bottom: 0.0 };

    pub const fn new(left: f64, top: f64, right: f64, bottom: f64) -> Thickness {
        Thickness { left, top, right, bottom }
    }

    pub const fn all(value: f64) -> Thickness {
        Thickness { left: value, top: value, right: value, bottom: value }
    }

    pub fn is_zero(&self) -> bool {
        self.left == 0.0 && self.top == 0.0 && self.right == 0.0 && self.bottom == 0.0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum HAlign {
    Left,
    Center,
    Right,
    #[default]
    Stretch,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum VAlign {
    Top,
    Center,
    Bottom,
    #[default]
    Stretch,
}

/// A grid's column or row (`GridLength`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Track {
    Auto,
    Star(f64),
    Pixel(f64),
}

/// `TextWrapping`: a TextBlock does not wrap unless told.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Wrapping {
    #[default]
    NoWrap,
    Wrap,
    WrapWholeWords,
}

/// A grid cell (`Grid.Row`, `Grid.Column` and their spans).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Cell {
    pub row: i64,
    pub col: i64,
    pub row_span: i64,
    pub col_span: i64,
}

impl Default for Cell {
    fn default() -> Self {
        Cell { row: 0, col: 0, row_span: 1, col_span: 1 }
    }
}

/// A control's `Content` or `Header` (an object in C#): nothing, words, or an element.
#[derive(Default)]
pub enum Content {
    #[default]
    None,
    Text(String),
    El(Box<El>),
}

impl From<&str> for Content {
    fn from(text: &str) -> Content {
        Content::Text(text.to_string())
    }
}

impl From<String> for Content {
    fn from(text: String) -> Content {
        Content::Text(text)
    }
}

impl From<El> for Content {
    fn from(el: El) -> Content {
        Content::El(Box::new(el))
    }
}

/// A TextBlock's inlines (`Run`, `LineBreak`, `Span`), read when its `Text` is empty.
#[derive(Debug, Clone, PartialEq)]
pub enum Inline {
    Run(String),
    LineBreak,
    Span(Vec<Inline>),
}

/// A TextBlock.
#[derive(Default)]
pub struct TextBlock {
    pub text: String,
    pub inlines: Vec<Inline>,
    /// `TextSize` (the size at 100%), or none: the size it inherits.
    pub size: Option<f64>,
    /// `FontWeight.Weight` (400 normal, 500 medium, 600 semibold, 700 bold).
    pub weight: u16,
    /// Its own family, or none: the one it inherits.
    pub family: Option<String>,
    /// Its own `Foreground`, or none: the one it inherits.
    pub foreground: Option<Brush>,
    pub wrapping: Wrapping,
    pub selectable: bool,
}

/// A menu flyout's row (`MenuFlyoutItem`, `ToggleMenuFlyoutItem`, `MenuFlyoutSubItem`, `MenuFlyoutSeparator`).
pub enum MenuItem {
    Separator,
    Sub { text: String, items: Vec<MenuItem>, enabled: bool },
    Toggle { text: String, checked: bool, enabled: bool, on: Option<Handler> },
    Item { text: String, enabled: bool, on: Option<Handler> },
}

impl MenuItem {
    pub fn item(text: impl Into<String>, on: impl Fn() + Send + Sync + 'static) -> MenuItem {
        MenuItem::Item { text: text.into(), enabled: true, on: Some(handler(move |_| on())) }
    }

    pub fn toggle(text: impl Into<String>, checked: bool, on: impl Fn(bool) + Send + Sync + 'static) -> MenuItem {
        let on = handler(move |input| {
            if let Input::Toggle(value) = input {
                on(value)
            }
        });
        MenuItem::Toggle { text: text.into(), checked, enabled: true, on: Some(on) }
    }
}

/// What kind of control an element is, with the properties the reader reads.
pub enum Kind {
    TextBlock(TextBlock),
    /// A RichTextBlock: its paragraphs' words, one per line; always selectable.
    RichText {
        paragraphs: Vec<String>,
        size: Option<f64>,
    },
    /// A RichEditBox (a control): its document's words.
    RichEdit {
        text: String,
        size: Option<f64>,
    },
    FontIcon {
        glyph: String,
        size: Option<f64>,
        foreground: Option<Brush>,
    },
    SymbolIcon,
    /// An Image: its picture as a data, http or https URL; none drops it.
    Image {
        src: Option<String>,
    },
    Ellipse {
        fill: Option<Brush>,
    },
    /// Any other shape (a Path, a Rectangle, a provider mark): never drawn by the page.
    Shape,
    ProgressRing {
        active: bool,
    },
    ProgressBar {
        value: f64,
        max: f64,
        indeterminate: bool,
    },
    Hyperlink {
        content: Content,
        uri: Option<String>,
    },
    CheckBox {
        on: bool,
        content: Content,
    },
    RadioButton {
        on: bool,
        content: Content,
    },
    ToggleSwitch {
        on: bool,
        header: Content,
    },
    /// A ToggleButton (the form's switch, a segment): only its automation name and state are read.
    ToggleButton {
        on: bool,
        content: Content,
    },
    ComboBox {
        index: i32,
        items: Vec<Content>,
    },
    TextBox {
        text: String,
        placeholder: String,
        multiline: bool,
        readonly: bool,
        header: Content,
        family: Option<String>,
    },
    PasswordBox {
        text: String,
        placeholder: String,
    },
    Slider {
        value: f64,
        min: f64,
        max: f64,
        step: f64,
    },
    Button(Button),
    ScrollViewer {
        content: Content,
    },
    /// An Expander: its disclosure kept in the surface's locals.
    Expander {
        header: Content,
        content: Content,
        open: Local<bool>,
    },
    InfoBar {
        open: bool,
        title: Option<String>,
        message: Option<String>,
        severity: &'static str,
    },
    ListView {
        index: i32,
        items: Vec<Content>,
    },
    ItemsControl {
        items: Vec<Content>,
    },
    ContentControl {
        content: Content,
    },
    Viewbox {
        child: Option<Box<El>>,
    },
    Border(Border),
    Grid(Grid),
    StackPanel(StackPanel),
    /// Any other panel (a Canvas): its children as a vertical stack.
    Panel {
        children: Vec<El>,
    },
}

/// A Button (and its painted look, C# `PaintPlainButton`).
#[derive(Default)]
pub struct Button {
    pub content: Content,
    /// `Background`, or none: the style's (read as no name).
    pub background: Option<Brush>,
    /// `Foreground`, or none: the style's, the theme's `ink`.
    pub foreground: Option<Brush>,
    /// The words' ink while it is disabled (`ButtonForegroundDisabled`), or none: the style's (read as no name).
    pub disabled_ink: Option<Brush>,
    /// Painted already (`PaintPlainButton` ran): `ToneSecondaryText` leaves it as it is.
    pub painted: bool,
    /// `TextSize`, or none: the stock 14.
    pub size: Option<f64>,
    /// A `MenuFlyout`: the page draws it as a menu.
    pub flyout: Option<Vec<MenuItem>>,
}

/// A Border.
#[derive(Default)]
pub struct Border {
    pub child: Option<Box<El>>,
    pub background: Option<Brush>,
    pub border_brush: Option<Brush>,
    pub border_thickness: Thickness,
    pub padding: Thickness,
    /// `CornerRadius.TopLeft` (the only corner read).
    pub corner_radius: f64,
}

/// A Grid; `segmented` marks the form's segmented picker (C#: a Grid whose Tag is an `Action<string>`).
#[derive(Default)]
pub struct Grid {
    pub children: Vec<El>,
    pub cols: Vec<Track>,
    pub rows: Vec<Track>,
    pub row_spacing: f64,
    pub col_spacing: f64,
    pub padding: Thickness,
    pub background: Option<Brush>,
    pub segmented: bool,
}

/// A StackPanel.
#[derive(Default)]
pub struct StackPanel {
    pub children: Vec<El>,
    pub horizontal: bool,
    pub spacing: f64,
    pub padding: Thickness,
    pub background: Option<Brush>,
}

/// One element: its kind and the FrameworkElement properties the reader reads.
pub struct El {
    pub kind: Kind,
    /// `Visibility == Visible`.
    pub visible: bool,
    pub opacity: f64,
    pub margin: Thickness,
    pub ha: HAlign,
    pub va: VAlign,
    /// `Width`, `Height` (NaN in C#: none).
    pub width: Option<f64>,
    pub height: Option<f64>,
    /// `MaxWidth`, `MaxHeight` (infinite in C#: none).
    pub max_width: Option<f64>,
    pub max_height: Option<f64>,
    /// `MinWidth` (0 in C#: none).
    pub min_width: Option<f64>,
    /// A control's own `IsEnabled`.
    pub enabled: bool,
    /// The builder's mark (C# `WebNativeReader.Mark`): `row`, `wideRow`, `settingsRow`, `loading`.
    pub role: Option<&'static str>,
    /// `AutomationProperties.AutomationId`.
    pub aid: Option<String>,
    /// `AutomationProperties.Name` ("" when unset, as C# reads it).
    pub name: String,
    /// A tooltip that is words.
    pub tooltip: Option<String>,
    pub cell: Cell,
    /// The key this element's id is kept under among its siblings, in place of its position (a list's row by its item's id).
    pub key: Option<String>,
    /// What the page's input does: a click, a toggle, a pick, typed words, a slider's value.
    pub on: Option<Handler>,
}

impl El {
    /// An element with WinUI's defaults: a Button's and a CheckBox's style aligns it left and centred, a ProgressRing's centres it
    /// (C#'s capture reads `ha:"start"`/`"center"`, `va:"center"` off every one not aligned otherwise), every other element stretches.
    pub fn new(kind: Kind) -> El {
        let (ha, va) = match kind {
            Kind::Button(_) | Kind::CheckBox { .. } => (HAlign::Left, VAlign::Center),
            Kind::ProgressRing { .. } => (HAlign::Center, VAlign::Center),
            _ => (HAlign::Stretch, VAlign::Stretch),
        };
        El {
            kind,
            visible: true,
            opacity: 1.0,
            margin: Thickness::ZERO,
            ha,
            va,
            width: None,
            height: None,
            max_width: None,
            max_height: None,
            min_width: None,
            enabled: true,
            role: None,
            aid: None,
            name: String::new(),
            tooltip: None,
            cell: Cell::default(),
            key: None,
            on: None,
        }
    }

    /// A TextBlock of `text` with WinUI's defaults (no size, family or ink of its own; no wrapping).
    pub fn text_block(text: impl Into<String>) -> El {
        El::new(Kind::TextBlock(TextBlock { text: text.into(), weight: 400, ..TextBlock::default() }))
    }

    pub fn stack(horizontal: bool, spacing: f64, children: Vec<El>) -> El {
        El::new(Kind::StackPanel(StackPanel { children, horizontal, spacing, ..StackPanel::default() }))
    }

    /// A vertical StackPanel of no spacing (a settings box's rows).
    pub fn vstack(children: Vec<El>) -> El {
        El::stack(false, 0.0, children)
    }

    pub fn grid(grid: Grid) -> El {
        El::new(Kind::Grid(grid))
    }

    pub fn border(border: Border) -> El {
        El::new(Kind::Border(border))
    }

    /// A Button whose content is `content` (no handler yet).
    pub fn button(content: impl Into<Content>) -> El {
        El::new(Kind::Button(Button { content: content.into(), ..Button::default() }))
    }

    pub fn font_icon(glyph: impl Into<String>, size: Option<f64>) -> El {
        El::new(Kind::FontIcon { glyph: glyph.into(), size, foreground: None })
    }

    /// A shape the page never draws (a Path, a provider mark: C# `ProviderMarkView.Create`).
    pub fn shape() -> El {
        El::new(Kind::Shape)
    }

    pub fn text_box(text: impl Into<String>) -> El {
        El::new(Kind::TextBox {
            text: text.into(),
            placeholder: String::new(),
            multiline: false,
            readonly: false,
            header: Content::None,
            family: None,
        })
    }

    pub fn combo_box(index: i32, items: Vec<Content>) -> El {
        El::new(Kind::ComboBox { index, items })
    }

    pub fn check_box(on: bool, content: impl Into<Content>) -> El {
        El::new(Kind::CheckBox { on, content: content.into() })
    }

    /// A TextBox's or PasswordBox's `PlaceholderText`.
    pub fn placeholder(mut self, text: impl Into<String>) -> El {
        if let Kind::TextBox { placeholder, .. } | Kind::PasswordBox { placeholder, .. } = &mut self.kind {
            *placeholder = text.into();
        }
        self
    }

    /// A TextBox's `Header` (its name when it has no automation name) or a ToggleSwitch's.
    pub fn header(mut self, content: impl Into<Content>) -> El {
        if let Kind::TextBox { header, .. } | Kind::ToggleSwitch { header, .. } = &mut self.kind {
            *header = content.into();
        }
        self
    }

    /// Marks the element for the page's standard patterns (C# `WebNativeReader.Mark`).
    pub fn mark(mut self, role: &'static str) -> El {
        self.role = Some(role);
        self
    }

    pub fn aid(mut self, aid: impl Into<String>) -> El {
        self.aid = Some(aid.into());
        self
    }

    pub fn named(mut self, name: impl Into<String>) -> El {
        self.name = name.into();
        self
    }

    pub fn margin(mut self, margin: Thickness) -> El {
        self.margin = margin;
        self
    }

    pub fn ha(mut self, ha: HAlign) -> El {
        self.ha = ha;
        self
    }

    pub fn va(mut self, va: VAlign) -> El {
        self.va = va;
        self
    }

    pub fn width(mut self, width: f64) -> El {
        self.width = Some(width);
        self
    }

    pub fn height(mut self, height: f64) -> El {
        self.height = Some(height);
        self
    }

    pub fn max_height(mut self, height: f64) -> El {
        self.max_height = Some(height);
        self
    }

    pub fn opacity(mut self, opacity: f64) -> El {
        self.opacity = opacity;
        self
    }

    pub fn visible(mut self, visible: bool) -> El {
        self.visible = visible;
        self
    }

    pub fn enabled(mut self, enabled: bool) -> El {
        self.enabled = enabled;
        self
    }

    pub fn tooltip(mut self, tooltip: impl Into<String>) -> El {
        self.tooltip = Some(tooltip.into());
        self
    }

    /// Its cell in a grid (`Grid.SetRow`, `Grid.SetColumn`).
    pub fn cell(mut self, row: i64, col: i64) -> El {
        self.cell.row = row;
        self.cell.col = col;
        self
    }

    pub fn span(mut self, rows: i64, cols: i64) -> El {
        self.cell.row_span = rows;
        self.cell.col_span = cols;
        self
    }

    /// Keeps the element's id (and its subtree's) under `key` among its siblings instead of its position.
    pub fn keyed(mut self, key: impl Into<String>) -> El {
        self.key = Some(key.into());
        self
    }

    /// The handler of the page's input on this control.
    pub fn on(mut self, on: Handler) -> El {
        self.on = Some(on);
        self
    }

    /// A Button's or menu button's Click.
    pub fn on_click(self, click: impl Fn() + Send + Sync + 'static) -> El {
        self.on(handler(move |input| {
            if input == Input::Invoke {
                click()
            }
        }))
    }

    /// A toggle's Checked/Unchecked, a switch's Toggled.
    pub fn on_toggle(self, toggle: impl Fn(bool) + Send + Sync + 'static) -> El {
        self.on(handler(move |input| {
            if let Input::Toggle(on) = input {
                toggle(on)
            }
        }))
    }

    /// A pop-up's or list's SelectionChanged.
    pub fn on_select(self, select: impl Fn(i32) + Send + Sync + 'static) -> El {
        self.on(handler(move |input| {
            if let Input::Select(index) = input {
                select(index)
            }
        }))
    }

    /// A field's TextChanged.
    pub fn on_text(self, text: impl Fn(String) + Send + Sync + 'static) -> El {
        self.on(handler(move |input| {
            if let Input::Text(words) = input {
                text(words)
            }
        }))
    }

    /// Whether WinUI makes it a `Control` (its `IsEnabled` counts, it is marked disabled, its `MinWidth` is not read).
    pub fn is_control(&self) -> bool {
        !matches!(
            self.kind,
            Kind::TextBlock(_)
                | Kind::RichText { .. }
                | Kind::FontIcon { .. }
                | Kind::SymbolIcon
                | Kind::Image { .. }
                | Kind::Ellipse { .. }
                | Kind::Shape
                | Kind::Viewbox { .. }
                | Kind::Border(_)
                | Kind::Grid(_)
                | Kind::StackPanel(_)
                | Kind::Panel { .. }
        )
    }

    /// Whether WinUI makes it a `Panel` (its `Height` is read).
    pub fn is_panel(&self) -> bool {
        matches!(self.kind, Kind::Grid(_) | Kind::StackPanel(_) | Kind::Panel { .. })
    }

    /// The TextBlock inside, to change its look.
    pub fn text_mut(&mut self) -> Option<&mut TextBlock> {
        match &mut self.kind {
            Kind::TextBlock(text) => Some(text),
            _ => None,
        }
    }

    /// The Button inside, to change its look.
    pub fn button_mut(&mut self) -> Option<&mut Button> {
        match &mut self.kind {
            Kind::Button(button) => Some(button),
            _ => None,
        }
    }

    /// The panel's children (a StackPanel, Grid or other panel), to add rows.
    pub fn children_mut(&mut self) -> Option<&mut Vec<El>> {
        match &mut self.kind {
            Kind::StackPanel(stack) => Some(&mut stack.children),
            Kind::Grid(grid) => Some(&mut grid.children),
            Kind::Panel { children } => Some(children),
            _ => None,
        }
    }
}

/// A value of a surface kept between reads (C#: the state a control held by itself), shared with the handlers that change it.
pub struct Local<T>(Arc<Mutex<T>>);

impl<T> Clone for Local<T> {
    fn clone(&self) -> Self {
        Local(self.0.clone())
    }
}

impl<T: Clone> Local<T> {
    pub fn new(value: T) -> Local<T> {
        Local(Arc::new(Mutex::new(value)))
    }

    pub fn get(&self) -> T {
        self.0.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).clone()
    }

    pub fn set(&self, value: T) {
        *self.0.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = value;
    }

    pub fn update(&self, change: impl FnOnce(&mut T)) {
        change(&mut self.0.lock().unwrap_or_else(|poisoned| poisoned.into_inner()));
    }
}

/// A surface's kept values by key ([`Ui::local`]).
#[derive(Default)]
pub struct Locals(HashMap<String, Arc<dyn Any + Send + Sync>>);

impl Locals {
    /// Forgets every kept value (C#: the controls built again, a settings category drawn anew).
    pub fn clear(&mut self) {
        self.0.clear();
    }
}

/// What a read is drawn with: the theme, the text setting and the language.
#[derive(Debug, Clone)]
pub struct Env {
    pub theme: Theme,
    pub text: TextAppearance,
    pub locale: Locale,
}

impl Default for Env {
    fn default() -> Self {
        Env { theme: Theme::Dark, text: TextAppearance::default(), locale: Locale::new("ko") }
    }
}

/// A builder's context: the read's [`Env`] and the surface's kept values.
pub struct Ui<'a> {
    pub env: &'a Env,
    locals: &'a mut Locals,
}

impl<'a> Ui<'a> {
    pub fn new(env: &'a Env, locals: &'a mut Locals) -> Ui<'a> {
        Ui { env, locals }
    }

    pub fn theme(&self) -> Theme {
        self.env.theme
    }

    pub fn locale(&self) -> Locale {
        self.env.locale
    }

    /// C# `brushes.Brush(token)`.
    pub fn brush(&self, token: Token) -> Brush {
        self.env.theme.brush(token)
    }

    /// C# `brushes.Brush(token, opacity)`.
    pub fn brush_at(&self, token: Token, opacity: f64) -> Brush {
        self.env.theme.brush_at(token, opacity)
    }

    /// C# `AppText.Size(size)`: a size at 100% at the text setting's scale (an element's `TextHeight`, `TextWidth`).
    pub fn size(&self, size: f64) -> f64 {
        self.env.text.size(size)
    }

    /// The family a sized element takes (C# `AppText` with no area: the interface font while one is chosen, else its own).
    pub fn sized_family(&self) -> Option<String> {
        self.env.text.family.clone()
    }

    /// C# `TextFont = TextArea.Code`: the code font, else `DesignMetrics.Font.Mono`.
    pub fn code_family(&self) -> String {
        self.env.text.code_family.clone().unwrap_or_else(|| mighty_core::design_tokens::metrics::FONT_MONO.to_string())
    }

    /// A TextBlock with `TextSize = size` (its family the interface font while one is chosen).
    pub fn text_block(&self, text: impl Into<String>, size: f64) -> El {
        let mut el = El::text_block(text);
        if let Some(block) = el.text_mut() {
            block.size = Some(size);
            block.family = self.sized_family();
        }
        el
    }

    /// The surface's value under `key`, made by `init` the first time (or when a value of another type was kept there).
    pub fn local<T: Clone + Send + 'static>(&mut self, key: &str, init: impl FnOnce() -> T) -> Local<T> {
        if let Some(kept) = self.locals.0.get(key).and_then(|any| any.clone().downcast::<Mutex<T>>().ok()) {
            return Local(kept);
        }
        let local = Local::new(init());
        self.locals.0.insert(key.to_string(), local.0.clone());
        local
    }

    /// An Expander whose disclosure is kept under `key` (initially `open`): the page's toggle flips it.
    pub fn expander(&mut self, key: &str, open: bool, header: impl Into<Content>, content: impl Into<Content>) -> El {
        let open = self.local(key, || open);
        El::new(Kind::Expander { header: header.into(), content: content.into(), open })
    }
}
