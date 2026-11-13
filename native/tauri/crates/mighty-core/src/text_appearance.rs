//! The text setting (C# `TextAppearance`, MightyCode.Core/TextAppearance.cs; the Mac's TextAppearance): one size scale for every word
//! the app draws and three optional font families, read from the user's settings store (Windows `HKCU\Software\MightyCode`, the
//! Mac's defaults) under the Mac's names. Ctrl+= / Ctrl+- / Ctrl+0 step it (`textSize`); the page follows the `text` push.

/// `Scales`: the sizes the shortcuts and Settings step through; 1 is the size the app was drawn at.
pub const SCALES: [f64; 8] = [0.85, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5];
pub const SCALE_KEY: &str = "textScale";
pub const FAMILY_KEY: &str = "textFontFamily";
pub const TRANSCRIPT_FAMILY_KEY: &str = "textTranscriptFontFamily";
pub const CODE_FAMILY_KEY: &str = "textCodeFontFamily";
/// `TerminalBaseSize`: a terminal's size at the standard setting (points).
pub const TERMINAL_BASE_SIZE: f64 = 12.0;

/// One stored value as the settings store returns it.
#[derive(Debug, Clone, PartialEq)]
pub enum Stored {
    Text(String),
    Number(i64),
}

#[derive(Debug, Clone, PartialEq)]
pub struct TextAppearance {
    pub scale: f64,
    pub family: Option<String>,
    pub transcript_family: Option<String>,
    pub code_family: Option<String>,
}

impl Default for TextAppearance {
    /// `Standard`.
    fn default() -> TextAppearance {
        TextAppearance::new(1.0, None, None, None)
    }
}

/// `NormalizedFamily`: trimmed; empty, or holding a control character or a line or paragraph separator, is none.
pub fn normalized_family(family: Option<&str>) -> Option<String> {
    let trimmed = family?.trim();
    (!trimmed.is_empty() && !trimmed.chars().any(|c| c.is_control() || matches!(c, '\u{2028}' | '\u{2029}'))).then(|| trimmed.to_string())
}

/// `Normalized`: the nearest step (the first of two as near); anything unreadable is 1.
pub fn normalized(value: f64) -> f64 {
    if !value.is_finite() || value <= 0.0 {
        return 1.0;
    }
    SCALES.into_iter().fold(SCALES[0], |best, step| if (step - value).abs() < (best - value).abs() { step } else { best })
}

impl TextAppearance {
    pub fn new(scale: f64, family: Option<&str>, transcript_family: Option<&str>, code_family: Option<&str>) -> TextAppearance {
        TextAppearance {
            scale: normalized(scale),
            family: normalized_family(family),
            transcript_family: normalized_family(transcript_family),
            code_family: normalized_family(code_family),
        }
    }

    fn index(&self) -> usize {
        SCALES.iter().position(|step| *step == self.scale).unwrap_or(2)
    }

    /// `Stepped`: `steps` sizes larger (smaller when negative), held to the ends.
    pub fn stepped(&self, steps: i32) -> TextAppearance {
        let at = (self.index() as i64 + i64::from(steps)).clamp(0, SCALES.len() as i64 - 1) as usize;
        self.with_scale(SCALES[at])
    }

    pub fn with_scale(&self, scale: f64) -> TextAppearance {
        TextAppearance { scale: normalized(scale), ..self.clone() }
    }

    /// `Size`: a size the app draws at 100%, drawn at this scale to the nearest half point (as it is at 100%).
    pub fn size(&self, size: f64) -> f64 {
        if self.scale == 1.0 { size } else { (size * self.scale * 2.0).round() / 2.0 }
    }

    /// `TerminalSize`: the terminal's size, 12 pt at 100%.
    pub fn terminal_size(&self) -> f64 {
        self.size(TERMINAL_BASE_SIZE)
    }

    /// `ResolvedTranscriptFamily`: the conversation's own family, else the interface's.
    pub fn resolved_transcript_family(&self) -> Option<&str> {
        self.transcript_family.as_deref().or(self.family.as_deref())
    }

    pub fn is_standard(&self) -> bool {
        *self == TextAppearance::default()
    }

    /// `FromValues`: the saved setting; a missing or broken value reads as the standard one, and a scale above 10 (a string or a
    /// number) is a percent.
    pub fn from_values(
        scale: Option<Stored>,
        family: Option<&str>,
        transcript_family: Option<&str>,
        code_family: Option<&str>,
    ) -> TextAppearance {
        let value = match scale {
            Some(Stored::Text(text)) => match text.trim().parse::<f64>() {
                Ok(number) if number > 10.0 => number / 100.0,
                Ok(number) => number,
                Err(_) => 1.0,
            },
            Some(Stored::Number(percent)) => percent as f64 / 100.0,
            None => 1.0,
        };
        TextAppearance::new(value, family, transcript_family, code_family)
    }

    /// The page's `text` push (`PushWebText`): `{kind:'text', scale, family, transcriptFamily, codeFamily}`, nulls left out.
    pub fn push(&self) -> serde_json::Value {
        let mut push = serde_json::json!({"kind": "text", "scale": self.scale});
        for (name, value) in [
            ("family", self.family.as_deref()),
            ("transcriptFamily", self.resolved_transcript_family()),
            ("codeFamily", self.code_family.as_deref()),
        ] {
            if let Some(value) = value {
                push[name] = value.into();
            }
        }
        push
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// C# `TextAppearanceVerification`: steps, their ends, the standard size and the saved values.
    #[test]
    fn the_size_steps_through_the_scales_and_holds_at_the_ends() {
        let standard = TextAppearance::default();
        assert!(standard.is_standard());
        assert_eq!(standard.stepped(1).scale, 1.1);
        assert_eq!(standard.stepped(-1).scale, 0.9);
        assert_eq!(standard.stepped(-10).scale, 0.85);
        assert_eq!(standard.stepped(10).scale, 1.5);
        assert_eq!(standard.stepped(10).stepped(1).scale, 1.5, "held at the largest");
        assert_eq!(TextAppearance::new(1.3, Some(" Fira Code "), None, None).with_scale(1.0).family.as_deref(), Some("Fira Code"));
        assert_eq!(normalized(1.04), 1.0);
        assert_eq!(normalized(f64::NAN), 1.0);
        assert_eq!(normalized(-3.0), 1.0);
        assert_eq!(normalized(9.0), 1.5);
    }

    /// C# `TextAppearanceVerification`: sizes at a scale, to the nearest half point, and the terminal's.
    #[test]
    fn sizes_follow_the_scale_to_the_half_point() {
        let standard = TextAppearance::default();
        for size in [9.0, 10.0, 10.5, 10.8, 11.0, 11.3, 12.5, 13.0, 34.0] {
            assert_eq!(standard.size(size), size, "at scale 1 a {size}pt base stays");
        }
        assert_eq!(standard.terminal_size(), 12.0);
        assert_eq!(TextAppearance::new(1.1, None, None, None).size(13.0), 14.5, "13 at 110% is 14.3, drawn at 14.5");
        assert_eq!(TextAppearance::new(0.85, None, None, None).size(12.5), 10.5, "12.5 at 85% is 10.625, drawn at 10.5");
        let large = TextAppearance::new(1.5, None, None, None);
        assert_eq!((large.size(11.0), large.terminal_size()), (16.5, 18.0), "150% of 11 and of the terminal's 12");
        assert_eq!(TextAppearance::new(0.85, None, None, None).size(10.0), 8.5);
    }

    #[test]
    fn saved_values_read_as_the_registry_holds_them() {
        assert_eq!(TextAppearance::from_values(Some(Stored::Text("1.1".into())), None, None, None).scale, 1.1);
        assert_eq!(TextAppearance::from_values(Some(Stored::Text("120".into())), None, None, None).scale, 1.2, "a percent by hand");
        assert_eq!(TextAppearance::from_values(Some(Stored::Number(130)), None, None, None).scale, 1.3);
        assert_eq!(TextAppearance::from_values(Some(Stored::Text("big".into())), None, None, None).scale, 1.0);
        let fonts = TextAppearance::from_values(None, Some("Segoe"), None, Some("bad\nname"));
        assert_eq!((fonts.family.as_deref(), fonts.code_family.as_deref()), (Some("Segoe"), None));
        assert_eq!(fonts.push(), serde_json::json!({"kind": "text", "scale": 1.0, "family": "Segoe", "transcriptFamily": "Segoe"}));
    }
}
