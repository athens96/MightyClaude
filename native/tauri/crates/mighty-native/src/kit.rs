//! The settings kit: the C# helpers every settings box and dialog builds with, as [`Ui`] methods that make the same elements
//! (MainWindow.SettingsNavigation.cs:286-718 `Settings*`; MainWindow.Settings.cs:827-836 `SettingsMenuButton`; MainWindow.Panels.cs
//! `SheetFit`, `SheetTitle`, `PushButton`, `ToneSecondaryText`, `PaintSettingsButton`, `BuildSectionContainer`; MainWindow.PaneHeader.cs
//! `PaintPlainButton`; MainWindow.cs `Button`; MainWindow.BetaBadge.cs `BetaBadgeView`).
//!
//! Only what the reader reads is kept: a control's size, padding or lightweight-styling resources that never reach the page are left
//! out, but every visibility, alignment, margin, width, height, brush, text size, family and automation property is set as C# sets it.
//! A control's own enabled state must be set before [`Ui::settings_push`] paints a prominent button (its opacity follows it).

use std::sync::Arc;

use mighty_core::design_tokens::{Token, metrics};

use crate::tokens::Brush;
use crate::ui::{Border, Content, El, Grid, HAlign, Input, Kind, MenuItem, TextBlock, Thickness, Track, Ui, VAlign, Wrapping, handler};

/// `SettingsFormInset`, `SettingsGroupGap`.
pub const SETTINGS_FORM_INSET: f64 = metrics::SPACING_LG;
pub const SETTINGS_GROUP_GAP: f64 = metrics::SPACING_XL;
/// `SettingsHeadingGap`, `SettingsRowInset`, `SettingsRowContent`.
pub const SETTINGS_HEADING_GAP: f64 = metrics::SPACING_SM;
pub const SETTINGS_ROW_INSET: f64 = metrics::SPACING_MD;
pub const SETTINGS_ROW_CONTENT: f64 = 16.0;
/// The Mac's control heights: a push button, pop-up or segmented picker, their small size and the mini one.
pub const SETTINGS_CONTROL_HEIGHT: f64 = 20.0;
pub const SETTINGS_SMALL_HEIGHT: f64 = 16.0;
pub const SETTINGS_MINI_HEIGHT: f64 = 14.0;
pub const SETTINGS_FIELD_HEIGHT: f64 = 19.0;
pub const SETTINGS_SWITCH_WIDTH: f64 = 26.0;
pub const SETTINGS_SWITCH_HEIGHT: f64 = 15.0;
pub const SETTINGS_CONTROL_RADIUS: f64 = 5.0;
pub const SETTINGS_DISABLED_OPACITY: f64 = 0.5;
pub const SETTINGS_POPUP_CHIP_INSET: f64 = 19.0;
pub const SETTINGS_CHIP_TINT: f64 = 0.08;
/// `SettingsCardRadius`: the section box's corner.
pub const SETTINGS_CARD_RADIUS: f64 = metrics::RADIUS_SEGMENT;
/// `SettingsRowMargin`: a row's inset in its box, of which the box's own 1pt line is the first point.
pub const SETTINGS_ROW_MARGIN: Thickness =
    Thickness::new(SETTINGS_ROW_INSET - metrics::STROKE_LINE, 0.0, SETTINGS_ROW_INSET - metrics::STROKE_LINE, 0.0);
/// `SheetMargin`, `SheetMinimum`.
pub const SHEET_MARGIN: f64 = metrics::SPACING_XL;
pub const SHEET_MINIMUM: f64 = 240.0;
/// The chevron of a pull-down's chip (Segoe Fluent `ChevronDown`).
pub const CHEVRON_DOWN: &str = "\u{E70D}";

/// `SheetFit`: a fixed sheet's side in a window with `room`: the side, or what the window leaves it (at least 240).
pub fn sheet_fit(side: f64, room: f64) -> f64 {
    if room > 0.0 { side.min(SHEET_MINIMUM.max(room - 2.0 * SHEET_MARGIN)) } else { side }
}

/// `ProviderMarkView.Create`: a provider's mark is a Path, which the page never draws.
pub fn provider_mark() -> El {
    El::shape()
}

/// `SettingsText`'s optional arguments: 13 `ink`, regular, not mono, not selectable.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TextStyle {
    pub size: f64,
    pub ink: Token,
    pub medium: bool,
    pub mono: bool,
    pub selectable: bool,
}

impl Default for TextStyle {
    fn default() -> Self {
        TextStyle { size: 13.0, ink: Token::Ink, medium: false, mono: false, selectable: false }
    }
}

impl TextStyle {
    pub fn size(size: f64) -> TextStyle {
        TextStyle { size, ..TextStyle::default() }
    }

    pub fn ink(self, ink: Token) -> TextStyle {
        TextStyle { ink, ..self }
    }

    pub fn medium(self) -> TextStyle {
        TextStyle { medium: true, ..self }
    }

    pub fn mono(self) -> TextStyle {
        TextStyle { mono: true, ..self }
    }

    pub fn selectable(self) -> TextStyle {
        TextStyle { selectable: true, ..self }
    }
}

/// `SettingsControlSize`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ControlSize {
    #[default]
    Regular,
    Small,
    Mini,
}

/// A box's rows (C# `SettingsRow` and `RuleSettingsRows` over one StackPanel): each row a Border marked `settingsRow`, the box's
/// line over every row that shows but the first. Hide a row through the element [`SettingsRows::add`] answers.
pub struct SettingsRows {
    rows: Vec<El>,
    line: Brush,
}

impl SettingsRows {
    /// `SettingsRow(rows, content)`: the row's border, to hide it (its rule and padding go with it) or key it.
    pub fn add(&mut self, content: El) -> &mut El {
        let row = El::border(Border {
            child: Some(Box::new(content)),
            padding: Thickness::new(0.0, SETTINGS_ROW_INSET, 0.0, SETTINGS_ROW_INSET),
            border_brush: Some(self.line),
            ..Border::default()
        })
        .margin(SETTINGS_ROW_MARGIN)
        .mark("settingsRow");
        self.rows.push(row);
        self.rows.last_mut().expect("a row was just added")
    }

    pub fn is_empty(&self) -> bool {
        self.rows.is_empty()
    }

    /// The box's rows panel, ruled (`RuleSettingsRows`).
    pub fn finish(mut self) -> El {
        rule_settings_rows(&mut self.rows);
        El::vstack(self.rows)
    }
}

/// `RuleSettingsRows`: the first row that shows has no line and its padding starts 7 down (the box's own line is its first point);
/// every other row that shows has a 1pt line over it and 8 above.
pub fn rule_settings_rows(rows: &mut [El]) {
    let mut first = true;
    for row in rows.iter_mut() {
        if !row.visible {
            continue;
        }
        let Kind::Border(border) = &mut row.kind else { continue };
        let line = if first { 0.0 } else { metrics::STROKE_LINE };
        let over = if first { SETTINGS_ROW_INSET - metrics::STROKE_LINE } else { SETTINGS_ROW_INSET };
        first = false;
        border.border_thickness = Thickness::new(0.0, line, 0.0, 0.0);
        border.padding = Thickness::new(0.0, over, 0.0, SETTINGS_ROW_INSET);
    }
}

impl Ui<'_> {
    /// C# `MainWindow.Button(title, action)`: a Button whose content and automation name are `title`.
    pub fn button(&self, title: impl Into<String>, action: impl Fn() + Send + Sync + 'static) -> El {
        let title = title.into();
        El::button(title.clone()).named(title).on_click(action)
    }

    /// `PaintPlainButton`: the button's fill, its ink (the template's when none) and its disabled ink (the template's when none).
    pub fn paint_plain_button(&self, button: &mut El, normal: Brush, ink: Option<Brush>, disabled_ink: Option<Brush>) {
        if let Some(b) = button.button_mut() {
            b.background = Some(normal);
            if ink.is_some() {
                b.foreground = ink;
            }
            if disabled_ink.is_some() {
                b.disabled_ink = disabled_ink;
            }
            b.painted = true;
        }
    }

    /// `SettingsText`: words at the Mac's sizes, wrapping, centred, in an ink.
    pub fn settings_text(&self, text: impl Into<String>, style: TextStyle) -> El {
        let family = if style.mono { Some(self.code_family()) } else { self.sized_family() };
        El::new(Kind::TextBlock(TextBlock {
            text: text.into(),
            size: Some(style.size),
            weight: if style.medium { 500 } else { 400 },
            family,
            foreground: Some(self.brush(style.ink)),
            wrapping: Wrapping::Wrap,
            selectable: style.selectable,
            ..TextBlock::default()
        }))
        .va(VAlign::Center)
    }

    /// `SettingsTertiary`: words in the Mac's tertiary ink (10 by default).
    pub fn settings_tertiary(&self, text: impl Into<String>, size: f64, mono: bool, selectable: bool) -> El {
        let mut style = TextStyle::size(size);
        style.mono = mono;
        style.selectable = selectable;
        let mut words = self.settings_text(text, style);
        if let Some(block) = words.text_mut() {
            block.foreground = Some(self.env.theme.tertiary());
        }
        words
    }

    /// `SettingsTitled`: a switch's label over its 11pt `ink2` explanation, 2 apart.
    pub fn settings_titled(&self, title: impl Into<String>, help: impl Into<String>) -> El {
        El::stack(
            false,
            metrics::SPACING_XXS,
            vec![self.settings_text(title, TextStyle::default()), self.settings_text(help, TextStyle::size(11.0).ink(Token::Ink2))],
        )
        .va(VAlign::Center)
    }

    /// `SettingsLabeled`: the name at the left taking what is left, the control at the right (at the top beside two lines), marked `row`.
    pub fn settings_labeled(&self, label: El, control: El, top: bool) -> El {
        let mut control = control.ha(HAlign::Right).va(if top { VAlign::Top } else { VAlign::Center }).cell(0, 1);
        if top {
            control.margin = Thickness::new(0.0, 1.0, 0.0, 0.0);
        }
        El::grid(Grid {
            children: vec![label, control],
            cols: vec![Track::Star(1.0), Track::Auto],
            col_spacing: metrics::SPACING_MD,
            ..Grid::default()
        })
        .mark("row")
    }

    /// `SettingsWide`: the name over the control, which takes the row's whole width, marked `wideRow`.
    pub fn settings_wide(&self, label: El, control: El) -> El {
        El::stack(false, metrics::SPACING_SM, vec![label, control.ha(HAlign::Stretch)]).mark("wideRow")
    }

    /// A box's rows ([`SettingsRows`]).
    pub fn settings_rows(&self) -> SettingsRows {
        SettingsRows { rows: vec![], line: self.brush(Token::Line) }
    }

    /// `SettingsSwitch`: the Mac's small switch, a 26×15 ToggleButton named `name`.
    pub fn settings_switch(
        &self,
        name: impl Into<String>,
        on: bool,
        aid: Option<&str>,
        toggle: impl Fn(bool) + Send + Sync + 'static,
    ) -> El {
        let mut switch = El::new(Kind::ToggleButton { on, content: Content::None })
            .width(SETTINGS_SWITCH_WIDTH)
            .height(SETTINGS_SWITCH_HEIGHT)
            .va(VAlign::Center)
            .named(name)
            .on_toggle(toggle);
        if let Some(aid) = aid {
            switch.aid = Some(aid.to_string());
        }
        switch
    }

    /// `SettingsSegmented`: equal segments, the chosen one filled; each segment a ToggleButton named by its label, its automation
    /// id `automation_id-value`. Choosing an unchosen segment calls `pick` with its value; the chosen one cannot be switched off.
    #[allow(clippy::too_many_arguments)]
    pub fn settings_segmented(
        &self,
        name: impl Into<String>,
        automation_id: &str,
        options: &[(String, String)],
        selected: &str,
        pick: impl Fn(String) + Send + Sync + 'static,
        small: bool,
        enabled: bool,
    ) -> El {
        let pick: Arc<dyn Fn(String) + Send + Sync> = Arc::new(pick);
        let radius = SETTINGS_CONTROL_RADIUS;
        let font = if small { 11.0 } else { 13.0 };
        let count = options.len() as i64;
        let mut children = vec![
            El::border(Border {
                background: Some(self.env.theme.segment_on()),
                border_brush: Some(self.brush(Token::Line)),
                border_thickness: Thickness::all(metrics::STROKE_LINE),
                corner_radius: radius,
                ..Border::default()
            })
            .span(1, count.max(1)),
        ];
        for (i, (value, label)) in options.iter().enumerate() {
            let chosen = value == selected;
            let mut words = self.text_block(label.clone(), font).ha(HAlign::Center).va(VAlign::Center);
            if let Some(block) = words.text_mut() {
                block.foreground = Some(self.brush(if chosen { Token::OnAccent } else { Token::Ink }));
            }
            let chip = El::border(Border {
                child: Some(Box::new(words)),
                background: Some(if chosen { self.brush(Token::Accent) } else { Brush::TRANSPARENT }),
                corner_radius: if i == 0 { radius } else { 0.0 },
                ..Border::default()
            });
            let (choose, value_owned) = (pick.clone(), value.clone());
            let segment = El::new(Kind::ToggleButton { on: chosen, content: chip.into() })
                .enabled(enabled)
                .named(label.clone())
                .aid(format!("{automation_id}-{value}"))
                .cell(0, i as i64)
                .on(handler(move |input| {
                    if input == Input::Toggle(true) && !chosen {
                        choose(value_owned.clone());
                    }
                }));
            children.push(segment);
            if i > 0 {
                let previous_chosen = options[i - 1].0 == selected;
                children.push(
                    El::border(Border { background: Some(self.brush(Token::Line)), ..Border::default() })
                        .width(metrics::STROKE_LINE)
                        .ha(HAlign::Left)
                        .margin(Thickness::new(0.0, metrics::SPACING_XS, 0.0, metrics::SPACING_XS))
                        .visible(!(chosen || previous_chosen))
                        .cell(0, i as i64),
                );
            }
        }
        El::grid(Grid { children, cols: vec![Track::Star(1.0); options.len()], segmented: true, ..Grid::default() })
            .height(self.size(if small { SETTINGS_SMALL_HEIGHT } else { SETTINGS_CONTROL_HEIGHT }))
            .opacity(if enabled { 1.0 } else { SETTINGS_DISABLED_OPACITY })
            .named(name)
            .aid(automation_id)
    }

    /// `SettingsPopup`: a ComboBox the Mac's inline way, 20 high, centred.
    pub fn settings_popup(&self, picker: El, _bordered: bool) -> El {
        picker.height(self.size(SETTINGS_CONTROL_HEIGHT)).ha(HAlign::Center).va(VAlign::Center)
    }

    /// `SettingsPopupFrame`: the pop-up in a frame with the Mac's chip after its words; the frame takes the pop-up's place.
    pub fn settings_popup_frame(&self, picker: El) -> El {
        let chip = self.settings_chip_mark(El::shape()).ha(HAlign::Right).margin(Thickness::new(0.0, 0.0, SETTINGS_POPUP_CHIP_INSET, 0.0));
        let (ha, va, margin) = (picker.ha, picker.va, picker.margin);
        let picker = picker.ha(HAlign::Stretch).margin(Thickness::ZERO);
        El::grid(Grid { children: vec![chip, picker], ..Grid::default() }).ha(ha).va(va).margin(margin)
    }

    /// `SettingsField`: the Mac's rounded field, 19 high, its words in the code font.
    pub fn settings_field(&self, mut field: El, _size: f64) -> El {
        if let Kind::TextBox { family, .. } = &mut field.kind {
            *family = Some(self.code_family());
        }
        field.height(SETTINGS_FIELD_HEIGHT).va(VAlign::Center)
    }

    /// `SettingsPush`: the Mac's bordered push button (`err-text` words when destructive), or `accent` under `on-accent` when
    /// prominent, at half strength while disabled.
    pub fn settings_push(&self, mut button: El, size: ControlSize, prominent: bool, destructive: bool) -> El {
        let (height, font) = match size {
            ControlSize::Small => (SETTINGS_SMALL_HEIGHT, 11.0),
            ControlSize::Mini => (SETTINGS_MINI_HEIGHT, 9.0),
            ControlSize::Regular => (SETTINGS_CONTROL_HEIGHT, 13.0),
        };
        button.height = Some(self.size(height));
        button.va = VAlign::Center;
        if let Some(b) = button.button_mut() {
            b.size = Some(font);
        }
        if prominent {
            let on_accent = self.brush(Token::OnAccent);
            self.paint_plain_button(&mut button, self.brush(Token::Accent), Some(on_accent), Some(on_accent));
            button.opacity = if button.enabled { 1.0 } else { SETTINGS_DISABLED_OPACITY };
        } else {
            let ink = self.brush(if destructive { Token::ErrText } else { Token::Ink });
            self.paint_plain_button(&mut button, self.env.theme.segment_on(), Some(ink), Some(self.brush(Token::Ink3)));
        }
        button
    }

    /// `SettingsGlyphLabel`: a push button's content with a leading symbol, 4 apart.
    pub fn settings_glyph_label(&self, glyph: impl Into<String>, label: impl Into<String>, font: f64) -> El {
        El::stack(
            true,
            metrics::SPACING_XS,
            vec![El::font_icon(glyph, Some(font)).va(VAlign::Center), self.text_block(label, font).va(VAlign::Center)],
        )
        .va(VAlign::Center)
    }

    /// `SettingsIconButton`: a symbol-only button, 20 square, its name its tooltip.
    pub fn settings_icon_button(
        &self,
        glyph: impl Into<String>,
        name: impl Into<String>,
        action: impl Fn() + Send + Sync + 'static,
        size: f64,
    ) -> El {
        let name = name.into();
        let mut button = self.button(name.clone(), action);
        if let Some(b) = button.button_mut() {
            b.content = El::font_icon(glyph, Some(size)).into();
        }
        let mut button = button.width(SETTINGS_CONTROL_HEIGHT).height(SETTINGS_CONTROL_HEIGHT).va(VAlign::Center).tooltip(name);
        self.paint_plain_button(&mut button, Brush::TRANSPARENT, Some(self.brush(Token::Ink)), Some(self.brush(Token::Ink3)));
        button
    }

    /// `SettingsChip(glyph)`: the 16pt chip after a pull-down's words, its chevron 8pt.
    pub fn settings_chip(&self, glyph: impl Into<String>) -> El {
        self.settings_chip_mark(El::font_icon(glyph, Some(8.0)).ha(HAlign::Center).va(VAlign::Center))
    }

    /// `SettingsChip(mark)`: the chip around a mark of its own.
    pub fn settings_chip_mark(&self, mark: El) -> El {
        El::border(Border {
            child: Some(Box::new(mark)),
            background: Some(self.brush_at(Token::Ink, SETTINGS_CHIP_TINT)),
            corner_radius: 4.0,
            ..Border::default()
        })
        .width(16.0)
        .height(16.0)
        .va(VAlign::Center)
    }

    /// `SettingsCapsule`: a small capsule of words on a tint (medium below 10pt).
    pub fn settings_capsule(&self, text: impl Into<String>, ink: Brush, fill: Brush, size: f64, radius: f64) -> El {
        let mut words = self.text_block(text, size);
        if let Some(block) = words.text_mut() {
            block.foreground = Some(ink);
            if size < 10.0 {
                block.weight = 500;
            }
        }
        El::border(Border {
            child: Some(Box::new(words)),
            padding: Thickness::new(metrics::SPACING_XS, 1.0, metrics::SPACING_XS, 1.0),
            corner_radius: radius,
            background: Some(fill),
            ..Border::default()
        })
        .va(VAlign::Center)
    }

    /// `SettingsSymbol`: a symbol in an ink.
    pub fn settings_symbol(&self, glyph: impl Into<String>, size: f64, ink: Token) -> El {
        El::new(Kind::FontIcon { glyph: glyph.into(), size: Some(size), foreground: Some(self.brush(ink)) }).va(VAlign::Center)
    }

    /// `SettingsMenuButton`: the Mac's pull-down in a form, its words then the chip; a Button with a menu.
    pub fn settings_menu_button(&self, title: impl Into<String>, menu: Vec<MenuItem>) -> El {
        let title = title.into();
        let line = El::stack(
            true,
            metrics::SPACING_SM,
            vec![self.text_block(title.clone(), 13.0).va(VAlign::Center), self.settings_chip(CHEVRON_DOWN)],
        )
        .va(VAlign::Center);
        let mut button = El::new(Kind::Button(crate::ui::Button { content: line.into(), flyout: Some(menu), ..Default::default() }))
            .height(self.size(SETTINGS_CONTROL_HEIGHT));
        self.paint_plain_button(&mut button, Brush::TRANSPARENT, Some(self.brush(Token::Ink)), Some(self.brush(Token::Ink3)));
        button.named(title)
    }

    /// `BetaBadgeView.Create`: the 베타 capsule, 9pt medium `stop-text` on `stop-soft`.
    pub fn beta_badge(&self) -> El {
        let mut text = self.text_block(self.locale().get("badge.beta"), metrics::TYPE_BADGE);
        if let Some(block) = text.text_mut() {
            block.weight = 500;
            block.foreground = Some(self.brush(Token::StopText));
        }
        El::border(Border {
            child: Some(Box::new(text)),
            corner_radius: 7.0,
            padding: Thickness::new(metrics::SPACING_XS, 1.0, metrics::SPACING_XS, 1.0),
            background: Some(self.brush(Token::StopSoft)),
            ..Border::default()
        })
        .va(VAlign::Center)
        .named(self.locale().get("badge.betaAccessibility"))
    }

    /// `SheetTitle`: a sheet's title in its content, 13pt bold `ink`.
    pub fn sheet_title(&self, text: impl Into<String>) -> El {
        let mut title = self.text_block(text, metrics::TYPE_TITLE).va(VAlign::Center);
        if let Some(block) = title.text_mut() {
            block.weight = 700;
            block.foreground = Some(self.brush(Token::Ink));
            block.wrapping = Wrapping::Wrap;
        }
        title
    }

    /// `PushButton`: the popup standard's push button, 28 high; the bordered card look, or `accent` when prominent.
    pub fn push_button(&self, title: impl Into<String>, prominent: bool, action: impl Fn() + Send + Sync + 'static) -> El {
        let mut button = self.button(title, action).height(self.size(metrics::DIALOG_BUTTON_HEIGHT)).va(VAlign::Center);
        if let Some(b) = button.button_mut() {
            b.size = Some(metrics::TYPE_BODY);
        }
        if prominent {
            let on_accent = self.brush(Token::OnAccent);
            self.paint_plain_button(&mut button, self.brush(Token::Accent), Some(on_accent), None);
        } else {
            self.tone_secondary(&mut button, true);
        }
        button
    }

    /// `PaintSettingsButton`: a plain button no one painted takes the bordered card look.
    fn paint_settings_button(&self, button: &mut El) {
        if button.button_mut().is_some_and(|b| b.painted) {
            return;
        }
        self.paint_plain_button(button, self.brush(Token::Card), Some(self.brush(Token::Ink)), Some(self.brush(Token::Ink3)));
    }

    /// `ToneSecondaryText`: a TextBlock dimmed with an opacity and no ink of its own takes `ink2` (`ink3` under 0.6) at full
    /// opacity; with `paint_buttons`, plain buttons take the bordered card look. Walks panels, borders and content.
    pub fn tone_secondary(&self, el: &mut El, paint_buttons: bool) {
        if matches!(el.kind, Kind::Button(_)) {
            if paint_buttons {
                self.paint_settings_button(el);
            }
            if let Some(Content::El(inner)) = el.button_mut().map(|b| &mut b.content) {
                self.tone_secondary(inner, paint_buttons);
            }
            return;
        }
        if let Kind::TextBlock(block) = &mut el.kind {
            if el.opacity > 0.0 && el.opacity < 1.0 && block.foreground.is_none() {
                block.foreground = Some(self.brush(if el.opacity < 0.6 { Token::Ink3 } else { Token::Ink2 }));
                el.opacity = 1.0;
            }
            return;
        }
        match &mut el.kind {
            Kind::Grid(Grid { children, .. }) | Kind::StackPanel(crate::ui::StackPanel { children, .. }) | Kind::Panel { children } => {
                for child in children {
                    self.tone_secondary(child, paint_buttons);
                }
            }
            Kind::Border(Border { child: Some(child), .. }) => self.tone_secondary(child, paint_buttons),
            Kind::Hyperlink { content, .. }
            | Kind::CheckBox { content, .. }
            | Kind::RadioButton { content, .. }
            | Kind::ToggleButton { content, .. }
            | Kind::ScrollViewer { content }
            | Kind::Expander { content, .. }
            | Kind::ContentControl { content } => {
                if let Content::El(inner) = content {
                    self.tone_secondary(inner, paint_buttons);
                }
            }
            _ => {}
        }
    }

    /// What `BuildSectionContainer` does to a box's rows before the page reads them (the heading and the box are the page's own):
    /// [`Ui::tone_secondary`] with the plain buttons painted.
    pub fn section_body(&self, mut body: El) -> El {
        self.tone_secondary(&mut body, true);
        body
    }
}
