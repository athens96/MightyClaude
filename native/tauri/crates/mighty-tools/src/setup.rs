//! Setup and components (C# `SetupCatalog`, `SetupPlan`, `SetupScript`, `SetupScriptFiles`, `SetupProber`, `SetupRows`,
//! `SetupFirstRun`, `SetupRuntimeSync`, `ComponentSection`) and the screen's state as MainWindow.Setup.cs keeps it (`screen`).

pub mod catalog;
pub mod components;
pub mod files;
pub mod plan;
pub mod probe;
pub mod screen;
pub mod script;
pub mod windows;

pub use catalog::{
    CatalogError, SetupAfterInstall, SetupAfterInstallKind, SetupCatalog, SetupGroup, SetupItem, SetupPlatform, SetupPlatformEntry,
    SetupProbe, SetupRecipe,
};
pub use probe::{SetupFirstRun, SetupProbeResult, SetupProbeState, SetupProber, SetupRowState, SetupRows, SetupRuntimeSync};
