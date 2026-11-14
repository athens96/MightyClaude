//! The status line (C# `StatusLineSupport`, `AnsiText`, `AnsiPalette`, `StatusLineRefresher`, `StatusLineStrings`, `StatusLineTrust`,
//! StatusLine.cs, StatusLineRefresher.cs, StatusLineStrings.cs, StatusLineTrust.cs) and what the pane's card is fed from it
//! (MainWindow.StatusLine.cs's `Row` and `Terminal`, MainWindow.WebShell.Cards.cs:599-624 `WebSegment`).

pub mod ansi;
pub mod card;
pub mod config;
pub mod payload;
pub mod refresher;
pub mod run;
pub mod strings;
pub mod trust;

pub use ansi::{AnsiColor, AnsiColorKind, AnsiSegment};
pub use config::{StatusLineConfig, StatusLineDiscovery};
pub use payload::StatusLineContext;
pub use refresher::{StatusLineClock, StatusLineRefresher};
pub use run::StatusLineResult;
