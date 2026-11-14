//! The design tokens' palettes and the metrics the native forms use (C# `DesignColor`, `DesignToken`, `DesignPalette`,
//! `DesignTokens`, MightyCode.Core/DesignTokens.cs; `DesignMetrics`, DesignMetrics.cs), value for value
//! native/contracts/fixtures/design-tokens.json, which the Mac and the C# app are held to as well. Owner: W6-A.

use std::ops::Index;

/// One opaque sRGB colour of the palette (C# `DesignColor`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct DesignColor {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl DesignColor {
    /// `new DesignColor(0xRRGGBB)`.
    pub const fn hex(value: u32) -> DesignColor {
        DesignColor { r: ((value >> 16) & 0xFF) as u8, g: ((value >> 8) & 0xFF) as u8, b: (value & 0xFF) as u8 }
    }

    /// `Hex`: "#RRGGBB", upper case.
    pub fn hex_text(&self) -> String {
        format!("#{:02X}{:02X}{:02X}", self.r, self.g, self.b)
    }

    /// `RelativeLuminance` (WCAG 2.x).
    pub fn relative_luminance(&self) -> f64 {
        fn channel(value: u8) -> f64 {
            let c = f64::from(value) / 255.0;
            if c <= 0.03928 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
        }
        0.2126 * channel(self.r) + 0.7152 * channel(self.g) + 0.0722 * channel(self.b)
    }

    /// `Contrast`: WCAG 2.x contrast ratio, 1…21, whichever colour is lighter.
    pub fn contrast(&self, other: DesignColor) -> f64 {
        let (a, b) = (self.relative_luminance(), other.relative_luminance());
        (a.max(b) + 0.05) / (a.min(b) + 0.05)
    }
}

/// Every field of the palette, in C#'s declaration order (`DesignToken`): the order the native reader's colour names are looked up in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Token {
    Page,
    Card,
    CardRaised,
    Sidebar,
    Line,
    Track,
    Ink,
    Ink2,
    Ink3,
    Accent,
    AccentSoft,
    OnAccent,
    SidebarInk2,
    SidebarAccent,
    Run,
    RunSoft,
    Wait,
    WaitSoft,
    OnWait,
    WaitText,
    Done,
    DoneSoft,
    DoneText,
    Err,
    ErrSoft,
    ErrText,
    Stop,
    StopSoft,
    StopText,
    Idle,
    OnStatus,
    Agent,
    AgentText,
    Task,
    TaskText,
    OnTask,
    SteerText,
    CompactText,
    QuestionText,
    CodeSurface,
    CodeText,
}

impl Token {
    /// `Enum.GetValues<DesignToken>()`.
    pub const ALL: [Token; 41] = [
        Token::Page,
        Token::Card,
        Token::CardRaised,
        Token::Sidebar,
        Token::Line,
        Token::Track,
        Token::Ink,
        Token::Ink2,
        Token::Ink3,
        Token::Accent,
        Token::AccentSoft,
        Token::OnAccent,
        Token::SidebarInk2,
        Token::SidebarAccent,
        Token::Run,
        Token::RunSoft,
        Token::Wait,
        Token::WaitSoft,
        Token::OnWait,
        Token::WaitText,
        Token::Done,
        Token::DoneSoft,
        Token::DoneText,
        Token::Err,
        Token::ErrSoft,
        Token::ErrText,
        Token::Stop,
        Token::StopSoft,
        Token::StopText,
        Token::Idle,
        Token::OnStatus,
        Token::Agent,
        Token::AgentText,
        Token::Task,
        Token::TaskText,
        Token::OnTask,
        Token::SteerText,
        Token::CompactText,
        Token::QuestionText,
        Token::CodeSurface,
        Token::CodeText,
    ];

    /// The C# member's name (`token.ToString()`).
    pub fn name(self) -> &'static str {
        const NAMES: [&str; 41] = [
            "Page",
            "Card",
            "CardRaised",
            "Sidebar",
            "Line",
            "Track",
            "Ink",
            "Ink2",
            "Ink3",
            "Accent",
            "AccentSoft",
            "OnAccent",
            "SidebarInk2",
            "SidebarAccent",
            "Run",
            "RunSoft",
            "Wait",
            "WaitSoft",
            "OnWait",
            "WaitText",
            "Done",
            "DoneSoft",
            "DoneText",
            "Err",
            "ErrSoft",
            "ErrText",
            "Stop",
            "StopSoft",
            "StopText",
            "Idle",
            "OnStatus",
            "Agent",
            "AgentText",
            "Task",
            "TaskText",
            "OnTask",
            "SteerText",
            "CompactText",
            "QuestionText",
            "CodeSurface",
            "CodeText",
        ];
        NAMES[self as usize]
    }

    /// The fixture's key: the name with its first letter in lower case (`cardRaised`).
    pub fn camel(self) -> String {
        let name = self.name();
        name[..1].to_ascii_lowercase() + &name[1..]
    }

    /// The page's CSS variable name (MainWindow.WebShell.Settings.cs `Kebab`): each upper-case letter after the first becomes `-`
    /// and its lower case (`card-raised`, `sidebar-ink2`, `err-text`).
    pub fn kebab(self) -> String {
        self.name()
            .chars()
            .enumerate()
            .map(|(i, ch)| {
                if ch.is_ascii_uppercase() {
                    format!("{}{}", if i == 0 { "" } else { "-" }, ch.to_ascii_lowercase())
                } else {
                    ch.to_string()
                }
            })
            .collect()
    }
}

/// One appearance's palette (C# `DesignPalette`): a colour per token.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DesignPalette {
    colors: [DesignColor; 41],
}

impl Index<Token> for DesignPalette {
    type Output = DesignColor;

    fn index(&self, token: Token) -> &DesignColor {
        &self.colors[token as usize]
    }
}

impl DesignPalette {
    /// `IsDark`: its words are lighter than its cards.
    pub fn is_dark(&self) -> bool {
        self[Token::Ink].relative_luminance() > self[Token::Card].relative_luminance()
    }

    /// `SegmentTrack`: the rail grey by day, the page navy by night.
    pub fn segment_track(&self) -> DesignColor {
        if self.is_dark() { self[Token::Page] } else { self[Token::Track] }
    }

    /// `SegmentOn`: a card chip by day, the idle slate by night.
    pub fn segment_on(&self) -> DesignColor {
        if self.is_dark() { self[Token::Idle] } else { self[Token::Card] }
    }

    /// `DesignTokens.Subtle`: the base of the neutral wash, black by day and white by night (drawn at [`metrics::SUBTLE_OPACITY`]).
    pub fn subtle(&self) -> DesignColor {
        if self.is_dark() { DesignColor::hex(0xFFFFFF) } else { DesignColor::hex(0x000000) }
    }
}

const fn palette(values: [u32; 41]) -> DesignPalette {
    let mut colors = [DesignColor::hex(0); 41];
    let mut i = 0;
    while i < 41 {
        colors[i] = DesignColor::hex(values[i]);
        i += 1;
    }
    DesignPalette { colors }
}

/// `DesignTokens.Light`, in [`Token::ALL`]'s order.
pub static LIGHT: DesignPalette = palette([
    0xECEEF3, 0xFFFFFF, 0xF5F7FB, 0xE2E6ED, 0xDEE2EA, 0xDDE1E9, // page … track
    0x0E1320, 0x5A6377, 0x616A7C, 0x2459E6, 0xE6EDFF, 0xFFFFFF, // ink … onAccent
    0x4F5869, 0x1F4FD1, // sidebarInk2, sidebarAccent
    0x2A5FEE, 0xE6EDFF, 0xFFA81F, 0xFFF3DE, 0x2B1B00, 0x8A5300, // run … waitText
    0x08804A, 0xE2F6EA, 0x06703F, 0xD42F22, 0xFDE6E4, 0xB42318, // done … errText
    0x667085, 0xEEF0F4, 0x4F5869, 0x0E1320, 0xFFFFFF, // stop … onStatus
    0x8A5CF6, 0x6D3FD9, 0x0EA5B7, 0x0A7480, 0x0E1320, // agent … onTask
    0xA33D8F, 0x4B5BB8, 0x8A5300, 0x0E1320, 0xD8DEEA, // steerText … codeText
]);

/// `DesignTokens.Dark`: the ink navy page, slate cards.
pub static DARK: DesignPalette = palette([
    0x0B0F19, 0x151B29, 0x1D2435, 0x0F1420, 0x283043, 0x1D2435, // page … track
    0xEEF1F7, 0xA9B1C2, 0x8E97AA, 0x7FA3FF, 0x1A2750, 0x0B0F19, // ink … onAccent
    0xA9B1C2, 0x7FA3FF, // sidebarInk2, sidebarAccent
    0x2A5FEE, 0x1A2750, 0xFFA81F, 0x3A2A0D, 0x2B1B00, 0xFFC45C, // run … waitText
    0x08804A, 0x0F2E22, 0x5BD49A, 0xD42F22, 0x3A1A18, 0xFF8A80, // done … errText
    0x667085, 0x222939, 0xA9B1C2, 0x2A3347, 0xFFFFFF, // stop … onStatus
    0x8A5CF6, 0xB9A0FF, 0x0EA5B7, 0x5ED3E0, 0x0E1320, // agent … onTask
    0xE58FD0, 0x9AA6F5, 0xFFC45C, 0x05070D, 0xD8DEEA, // steerText … codeText
]);

/// `DesignTokens.Palette(theme)`: the app's theme setting; only "light" is light.
pub fn palette_for(theme: &str) -> &'static DesignPalette {
    if theme == "light" { &LIGHT } else { &DARK }
}

/// The metrics the native forms read (C# `DesignMetrics`, the fixture's `metrics`).
pub mod metrics {
    pub const SPACING_XXS: f64 = 2.0;
    pub const SPACING_XS: f64 = 4.0;
    pub const SPACING_SM: f64 = 6.0;
    pub const SPACING_MD: f64 = 8.0;
    pub const SPACING_LG: f64 = 12.0;
    pub const SPACING_XL: f64 = 16.0;
    pub const STROKE_LINE: f64 = 1.0;
    pub const RADIUS_FILE_ROW: f64 = 5.0;
    pub const RADIUS_SEGMENT: f64 = 6.0;
    pub const TYPE_TITLE: f64 = 13.0;
    pub const TYPE_BODY: f64 = 13.0;
    pub const TYPE_PILL: f64 = 11.0;
    pub const TYPE_BADGE: f64 = 9.0;
    pub const FONT_MONO: &str = "Cascadia Mono, Consolas";
    pub const FONT_BODY: &str = "Segoe UI Variable Text, Segoe UI";
    pub const FONT_HEADING: &str = "Segoe UI Variable Display, Segoe UI";
    pub const SETTINGS_WIDTH: f64 = 860.0;
    pub const SETTINGS_HEIGHT: f64 = 640.0;
    pub const DIALOG_RADIUS: f64 = 12.0;
    pub const DIALOG_BUTTON_HEIGHT: f64 = 28.0;
    /// `DesignMetrics.Opacity.Subtle` (the fixture's `windowsOnly.subtle`).
    pub const SUBTLE_OPACITY: f64 = 0.035;
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> serde_json::Value {
        serde_json::from_str(include_str!("../../../../contracts/fixtures/design-tokens.json")).unwrap()
    }

    /// Verification.cs:1225 "design tokens: every palette field equals native/contracts/fixtures/design-tokens.json in both themes".
    #[test]
    fn every_fixture_hex_equals_the_palette_field() {
        let fixture = fixture();
        for (theme, palette) in [("light", &LIGHT), ("dark", &DARK)] {
            let table = fixture[theme].as_object().unwrap();
            assert_eq!(table.len(), 41, "{theme}: the fixture names 41 tokens");
            let names: Vec<String> = Token::ALL.iter().map(|t| t.camel()).collect();
            assert_eq!(
                table.keys().cloned().collect::<Vec<_>>(),
                names,
                "{theme}: the fixture and the palette name the same tokens in order"
            );
            for token in Token::ALL {
                assert_eq!(palette[token].hex_text(), table[&token.camel()].as_str().unwrap(), "{theme}.{}", token.camel());
            }
        }
        let metrics = &fixture["metrics"];
        assert_eq!(metrics["settings"]["width"], 860.0);
        assert_eq!(metrics["settings"]["height"], 640.0);
        assert_eq!(metrics["spacing"]["md"], metrics::SPACING_MD);
        assert_eq!(metrics["radius"]["segment"], metrics::RADIUS_SEGMENT);
        assert_eq!(metrics["type"]["badge"], metrics::TYPE_BADGE);
        assert_eq!(metrics["font"]["mono"], metrics::FONT_MONO);
        assert_eq!(metrics["font"]["body"], metrics::FONT_BODY);
        assert_eq!(fixture["windowsOnly"]["subtle"]["dark"]["opacity"], metrics::SUBTLE_OPACITY);
    }

    /// Verification.cs:1270 "design tokens: only the light theme setting picks the light palette".
    #[test]
    fn the_theme_setting_picks_the_palette() {
        assert_eq!(palette_for("light"), &LIGHT);
        assert_eq!(palette_for("dark"), &DARK);
        assert_eq!(palette_for(""), &DARK);
        assert_eq!(palette_for("system"), &DARK);
        assert!(!LIGHT.is_dark() && DARK.is_dark());
        assert!((DesignColor::hex(0xFFFFFF).contrast(DesignColor::hex(0x000000)) - 21.0).abs() < 1e-9);
        assert!((DesignColor::hex(0x777777).contrast(DesignColor::hex(0x777777)) - 1.0).abs() < 1e-9);
        assert_eq!(DesignColor::hex(0x0E1320).hex_text(), "#0E1320");
    }

    /// The derived colours the native reader names (`SegmentOn` reads `idle` by night and `card` by day).
    #[test]
    fn the_derived_colours_match_the_fixture() {
        let fixture = fixture();
        for (theme, palette) in [("light", &LIGHT), ("dark", &DARK)] {
            let derived = &fixture["derived"][theme];
            assert_eq!(derived["isDark"], palette.is_dark());
            assert_eq!(derived["segmentOn"].as_str().unwrap(), palette.segment_on().hex_text());
            assert_eq!(derived["segmentTrack"].as_str().unwrap(), palette.segment_track().hex_text());
        }
        assert_eq!(Token::CardRaised.kebab(), "card-raised");
        assert_eq!(Token::SidebarInk2.kebab(), "sidebar-ink2");
        assert_eq!(Token::ErrText.kebab(), "err-text");
        assert_eq!(Token::Ink2.kebab(), "ink2");
    }
}
