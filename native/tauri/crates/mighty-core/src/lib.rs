//! Mighty Code's core for the Rust host: the saved state's models and their loading (MightyCode.Core `Models.cs`,
//! `StateStore.cs`), the shared locale catalogues (`Locale.cs`), the pane layout rules (`PaneLayout.cs`) and the frame model the
//! page draws (MightyCode.WinUI `MainWindow.WebShell.Shell.cs`). No Tauri and no OS-specific code, so it builds and tests anywhere.
//!
//! The skeleton only reads: nothing here writes the state file.

pub mod demo;
pub mod layout;
pub mod locale;
pub mod model;
pub mod normalize;
pub mod shell;
pub mod store;
pub mod wire;

pub use model::AppSnapshot;
