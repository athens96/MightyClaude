//! The installed font families through DirectWrite (C# MainWindow.TextAppearance.cs `InstalledFamilies` over Win2D's system font set).
//! Owner: W6-A.
//!
//! Each family of the system font collection by its English name (else its first), once, left out when every face of it is a symbol
//! font, and fixed-pitch when every other face is (`IDWriteFont1::IsMonospacedFont`); vertical (`@`) and hidden (`.`) names left out;
//! sorted by name without regard to case.

use std::sync::Arc;

use mighty_platform::{FontCatalog, FontFamily, PlatformError, Result};
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FACTORY_TYPE_SHARED, DWriteCreateFactory, IDWriteFactory, IDWriteFont1, IDWriteFontCollection, IDWriteLocalizedStrings,
};
use windows::core::{BOOL, Interface, w};

/// The font catalog of Windows.
#[derive(Debug, Default)]
pub struct DirectWriteFonts;

pub fn font_catalog() -> Arc<dyn FontCatalog> {
    Arc::new(DirectWriteFonts)
}

fn failed(error: windows::core::Error) -> PlatformError {
    PlatformError::Refused(format!("DirectWrite: {error}"))
}

/// The `en-us` string of a name list, else its first.
fn english(names: &IDWriteLocalizedStrings) -> Result<String> {
    let (mut index, mut exists) = (0u32, BOOL(0));
    // SAFETY: a NUL-terminated locale name and two out-parameters that live across the call.
    unsafe { names.FindLocaleName(w!("en-us"), &mut index, &mut exists) }.map_err(failed)?;
    if !exists.as_bool() {
        index = 0;
    }
    // SAFETY: an index the list has (or 0, which a non-empty list has; an empty one fails here).
    let length = unsafe { names.GetStringLength(index) }.map_err(failed)? as usize;
    let mut buffer = vec![0u16; length + 1];
    // SAFETY: a buffer of the string's length plus its NUL.
    unsafe { names.GetString(index, &mut buffer) }.map_err(failed)?;
    Ok(String::from_utf16_lossy(&buffer[..length]))
}

impl FontCatalog for DirectWriteFonts {
    fn families(&self) -> Result<Vec<FontFamily>> {
        // SAFETY: plain DirectWrite calls on the objects they returned.
        unsafe {
            let factory: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED).map_err(failed)?;
            let mut collection: Option<IDWriteFontCollection> = None;
            factory.GetSystemFontCollection(&mut collection, false).map_err(failed)?;
            let collection = collection.ok_or_else(|| PlatformError::Refused("DirectWrite: no system font collection".into()))?;
            let mut families: Vec<FontFamily> = vec![];
            for i in 0..collection.GetFontFamilyCount() {
                let Ok(family) = collection.GetFontFamily(i) else { continue };
                let Ok(names) = family.GetFamilyNames() else { continue };
                let Ok(name) = english(&names) else { continue };
                if name.is_empty() || name.starts_with(['@', '.']) {
                    continue;
                }
                let (mut faces, mut fixed) = (0usize, true);
                for j in 0..family.GetFontCount() {
                    let Ok(font) = family.GetFont(j) else { continue };
                    if font.IsSymbolFont().as_bool() {
                        continue;
                    }
                    faces += 1;
                    fixed &= font.cast::<IDWriteFont1>().is_ok_and(|font| font.IsMonospacedFont().as_bool());
                }
                if faces == 0 {
                    continue;
                }
                match families.iter_mut().find(|f| f.name.eq_ignore_ascii_case(&name)) {
                    Some(known) => known.monospaced &= fixed,
                    None => families.push(FontFamily { name, monospaced: fixed }),
                }
            }
            families.sort_by_key(|f| f.name.to_lowercase());
            Ok(families)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every Windows has Consolas (fixed-pitch) and Segoe UI (not); no symbol font (Segoe MDL2 Assets, Wingdings) or hidden name is
    /// listed, and the list is sorted and has each name once.
    #[test]
    fn the_system_families_are_listed_once_without_symbol_fonts() {
        let families = DirectWriteFonts.families().unwrap();
        let find = |name: &str| families.iter().find(|f| f.name.eq_ignore_ascii_case(name));
        assert!(find("Consolas").is_some_and(|f| f.monospaced), "Consolas is fixed-pitch");
        assert!(find("Segoe UI").is_some_and(|f| !f.monospaced), "Segoe UI is proportional");
        assert!(find("Wingdings").is_none() && find("Segoe MDL2 Assets").is_none(), "symbol fonts are left out");
        assert!(families.iter().all(|f| !f.name.starts_with(['@', '.'])));
        let names: Vec<String> = families.iter().map(|f| f.name.to_lowercase()).collect();
        let mut sorted = names.clone();
        sorted.sort();
        sorted.dedup();
        assert_eq!(names, sorted, "sorted, once each");
    }
}
