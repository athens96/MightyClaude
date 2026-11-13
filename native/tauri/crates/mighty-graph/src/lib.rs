//! Mighty Code's execution graph, the Rust side of MightyCode.Core's graph subsystems (docs/tauri-conversion.md W3): the tracker that
//! turns a run's stream into graph blocks and graph runs (`tracker`, C# `ExecutionGraphTracker`), the rules every Mighty view reads the
//! runs by (`support`, `notice`, C# `MightyGraphSupport`, `BackgroundNoticeRuns`), the views' own models — the diagram's layout, camera
//! and blocks, the role view, the timeline and the structure — and the host's `mighty` push (`view`, C#
//! MightyCode.WinUI/MainWindow.WebShell.Mighty.cs). Pure logic: no OS and no Tauri; a file a rule reads goes through a small trait.
//!
//! Every display string is the locale catalogue's (`mighty_core::locale`), so the functions that word something take a `&Locale`.

pub mod activity;
pub mod agent_definitions;
pub mod block_prefs;
pub mod camera;
pub mod dot_grid;
pub mod flow;
pub mod follow;
pub mod handback;
pub mod images;
pub mod labels;
pub mod layout;
pub mod node;
pub mod notice;
pub mod result_files;
pub mod structure;
pub mod support;
pub mod timeline;
pub mod tracker;
pub mod usage_format;
pub mod view;
pub mod view_model;

pub use node::ExecutionGraphNode;
pub use tracker::ExecutionGraphTracker;
