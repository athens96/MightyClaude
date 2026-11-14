//! The installed font families port (C# MainWindow.TextAppearance.cs `InstalledFamilies`, the Mac's `NSFontManager`): what Settings ›
//! 글자 offers. Owner: W6-A.

use crate::Result;

/// One installed family as the text setting names it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FontFamily {
    /// Its English name (what the settings store keeps and RTF can name), else its first name.
    pub name: String,
    /// Every face of it is fixed-pitch.
    pub monospaced: bool,
}

/// Contract: `families` lists every installed family once (names compared without regard to case), symbol fonts and vertical (`@`)
/// or hidden (`.`) faces left out, sorted by name without regard to case; an OS that cannot list them fails.
pub trait FontCatalog: Send + Sync {
    fn families(&self) -> Result<Vec<FontFamily>>;
}
