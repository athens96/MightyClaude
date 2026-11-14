//! The installed font families through DirectWrite's system font set (C# MainWindow.TextAppearance.cs `InstalledFamilies`). Owner: W6-A.

use std::sync::Arc;

use mighty_platform::FontCatalog;
use mighty_platform::unsupported::Unsupported;

/// The font catalog of Windows. Stub: W6-A1 fills it with DirectWrite's system font set (symbol fonts left out, en-us names, fixed
/// pitch); until then it answers `Unsupported`, and Settings › 글자 lists only the families it names itself.
pub fn font_catalog() -> Arc<dyn FontCatalog> {
    Arc::new(Unsupported)
}
