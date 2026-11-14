//! The colours a form paints with and the names the JSON gives them (C# `DesignBrushes`, MainWindow.WebShell.Settings.cs
//! `NewNativeReader` and WebNative.cs `Tone`).
//!
//! A brush is a colour with its alpha (C# `DesignBrushes.ToColor`: a token at an opacity below 1 carries it in the alpha). The reader
//! names a brush by looking its exact ARGB up in the theme's palette, built from every token at full opacity, Accent first and then in
//! `DesignToken` order, the first token of a colour winning. So a translucent brush (Transparent, `Subtle`, `Tertiary`, any token at an
//! opacity below 1) reads as no name, and two tokens of one colour read as the first: by day `card` for white, `ink` for `#0E1320`.

use std::collections::HashMap;
use std::sync::OnceLock;

use mighty_core::design_tokens::{self, DesignColor, DesignPalette, Token, metrics};

/// The app's theme (C# `snapshot.Theme`: only "light" is light).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Theme {
    Light,
    #[default]
    Dark,
}

/// `DesignBrushes.TertiaryOpacity`: the Mac's tertiary label colour, `ink` at this strength.
pub const TERTIARY_OPACITY: f64 = 0.27;

/// A solid brush: its colour as ARGB (C# `SolidColorBrush` at opacity 1, the alpha carrying any token opacity).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Brush(pub u32);

impl Brush {
    /// C# `DesignBrushes.Transparent` (`Colors.Transparent`).
    pub const TRANSPARENT: Brush = Brush(0x00FF_FFFF);

    /// C# `DesignBrushes.ToColor(color, opacity)`: the opacity, clamped to 0…1, as the alpha (rounded to the nearest of 255).
    pub fn of(color: DesignColor, opacity: f64) -> Brush {
        let alpha = (opacity.clamp(0.0, 1.0) * 255.0).round() as u32;
        Brush(alpha << 24 | u32::from(color.r) << 16 | u32::from(color.g) << 8 | u32::from(color.b))
    }

    pub fn alpha(self) -> u8 {
        (self.0 >> 24) as u8
    }
}

impl Theme {
    /// The theme of the app's setting.
    pub fn of(setting: &str) -> Theme {
        if setting == "light" { Theme::Light } else { Theme::Dark }
    }

    pub fn palette(self) -> &'static DesignPalette {
        match self {
            Theme::Light => &design_tokens::LIGHT,
            Theme::Dark => &design_tokens::DARK,
        }
    }

    /// C# `brushes.Brush(token)`.
    pub fn brush(self, token: Token) -> Brush {
        Brush::of(self.palette()[token], 1.0)
    }

    /// C# `brushes.Brush(token, opacity)`.
    pub fn brush_at(self, token: Token, opacity: f64) -> Brush {
        Brush::of(self.palette()[token], opacity)
    }

    /// WinUI's own `TextFillColorPrimaryBrush` for the theme (white by night, black at 0xE4 by day): what a TextBlock with no ink of
    /// its own inherits in C#'s hidden form host, outside the window's `DesignBrushes` (the capture reads it as `on-status` by night
    /// and as no name by day).
    pub fn stock_text(self) -> Brush {
        match self {
            Theme::Dark => Brush(0xFFFF_FFFF),
            Theme::Light => Brush(0xE400_0000),
        }
    }

    /// C# `brushes.Tertiary`: `ink` at [`TERTIARY_OPACITY`].
    pub fn tertiary(self) -> Brush {
        self.brush_at(Token::Ink, TERTIARY_OPACITY)
    }

    /// C# `brushes.Subtle`: black by day, white by night, at 0.035.
    pub fn subtle(self) -> Brush {
        Brush::of(self.palette().subtle(), metrics::SUBTLE_OPACITY)
    }

    /// C# `brushes.SegmentOn`: the segmented track's chosen side (`card` by day, `idle` by night).
    pub fn segment_on(self) -> Brush {
        Brush::of(self.palette().segment_on(), 1.0)
    }

    /// C# `brushes.SegmentTrack`.
    pub fn segment_track(self) -> Brush {
        Brush::of(self.palette().segment_track(), 1.0)
    }

    /// The reader's name for a brush (WebNative.cs `Tone`): a brush with some alpha whose colour is a token's at full opacity.
    pub fn tone(self, brush: Option<Brush>) -> Option<&'static str> {
        let brush = brush?;
        if brush.alpha() == 0 {
            return None;
        }
        names(self).get(&brush.0).map(String::as_str)
    }
}

/// The palette the reader looks names up in (MainWindow.WebShell.Settings.cs `NewNativeReader`): every token at full opacity, Accent
/// first and then in enum order, the first name of a colour kept.
fn names(theme: Theme) -> &'static HashMap<u32, String> {
    static LIGHT: OnceLock<HashMap<u32, String>> = OnceLock::new();
    static DARK: OnceLock<HashMap<u32, String>> = OnceLock::new();
    let cell = match theme {
        Theme::Light => &LIGHT,
        Theme::Dark => &DARK,
    };
    cell.get_or_init(|| {
        let mut table = HashMap::new();
        let order = std::iter::once(Token::Accent).chain(Token::ALL.into_iter().filter(|t| *t != Token::Accent));
        for token in order {
            table.entry(theme.brush(token).0).or_insert_with(|| token.kebab());
        }
        table
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The plan's §1.6 table: the colours two tokens share, and the name each theme writes for them.
    #[test]
    fn shared_colours_read_as_the_first_token_in_lookup_order() {
        let light = Theme::Light;
        assert_eq!(light.tone(Some(light.brush(Token::OnAccent))), Some("card"), "#FFFFFF by day");
        assert_eq!(light.tone(Some(light.brush(Token::OnStatus))), Some("card"));
        assert_eq!(light.tone(Some(light.brush(Token::Idle))), Some("ink"), "#0E1320 by day");
        assert_eq!(light.tone(Some(light.brush(Token::CodeSurface))), Some("ink"));
        assert_eq!(light.tone(Some(light.brush(Token::RunSoft))), Some("accent-soft"));
        assert_eq!(light.tone(Some(light.brush(Token::StopText))), Some("sidebar-ink2"));
        assert_eq!(light.tone(Some(light.brush(Token::QuestionText))), Some("wait-text"));
        let dark = Theme::Dark;
        assert_eq!(dark.tone(Some(dark.brush(Token::SidebarAccent))), Some("accent"), "accent is looked up first");
        assert_eq!(dark.tone(Some(dark.brush(Token::OnAccent))), Some("page"));
        assert_eq!(dark.tone(Some(dark.brush(Token::Track))), Some("card-raised"));
        assert_eq!(dark.tone(Some(dark.brush(Token::StopText))), Some("ink2"));
        assert_eq!(dark.tone(Some(dark.brush(Token::RunSoft))), Some("accent-soft"));
        assert_eq!(dark.tone(Some(dark.brush(Token::QuestionText))), Some("wait-text"));
        assert_eq!(dark.tone(Some(dark.brush(Token::OnStatus))), Some("on-status"), "white by night is onStatus alone");
    }

    /// A brush with opacity below 1 (and Transparent, Subtle, Tertiary) reads as null; a derived opaque brush as the token it equals.
    #[test]
    fn translucent_brushes_read_as_null_and_derived_ones_as_their_token() {
        for theme in [Theme::Light, Theme::Dark] {
            assert_eq!(theme.tone(Some(Brush::TRANSPARENT)), None);
            assert_eq!(theme.tone(Some(theme.subtle())), None);
            assert_eq!(theme.tone(Some(theme.tertiary())), None);
            assert_eq!(theme.tone(Some(theme.brush_at(Token::Accent, 0.9))), None);
            assert_eq!(theme.tone(Some(theme.brush_at(Token::Ink2, 0.3))), None);
            assert_eq!(theme.tone(None), None);
        }
        assert_eq!(Theme::Dark.tone(Some(Theme::Dark.segment_on())), Some("idle"));
        assert_eq!(Theme::Light.tone(Some(Theme::Light.segment_on())), Some("card"));
        // The tertiary ink carries its opacity in the alpha: 0.27 × 255 = 68.85 → 69.
        assert_eq!(Theme::Dark.tertiary().alpha(), 69);
    }

    #[test]
    fn only_light_is_light() {
        assert_eq!(Theme::of("light"), Theme::Light);
        assert_eq!(Theme::of("dark"), Theme::Dark);
        assert_eq!(Theme::of("system"), Theme::Dark);
    }
}
