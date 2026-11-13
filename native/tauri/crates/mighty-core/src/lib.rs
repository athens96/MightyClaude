//! Mighty Code's core for the Rust host: the saved state's models, read and written exactly as C# `Wire.Json` does (`json.rs`,
//! `model.rs`, `graph.rs`, MightyCode.Core `Models.cs` and the records it holds), its normalize (`normalize.rs`, `StateStore.Normalize`),
//! its loading and saving (`store.rs`, `StateStore`), .NET's times (`time.rs`), the shared locale catalogues (`locale.rs`,
//! `Locale.cs`), the pane layout rules (`layout.rs`, `PaneLayout.cs`) and the frame model the page draws (`shell.rs`,
//! MightyCode.WinUI `MainWindow.WebShell.Shell.cs`). No Tauri and no OS-specific code, so it builds and tests anywhere.

pub mod demo;
pub mod graph;
pub mod json;
pub mod layout;
pub mod locale;
pub mod model;
pub mod model_label;
pub mod normalize;
pub mod shell;
pub mod store;
pub mod time;
pub mod wire;

pub use json::Wire;
pub use model::AppSnapshot;
