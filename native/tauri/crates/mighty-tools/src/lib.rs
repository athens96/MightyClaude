//! Mighty Code's setup screen, Settings › 도구 and the status line (W6-B), with no Tauri dependency and no OS code: the setup
//! catalog, plan, probes and rows with the components (`setup`), the toolkit (`toolkit`), the status line (`status_line`) and their
//! forms (`view`, phase 2). Every OS question goes through a mighty-platform port (`ProcessHost`, `SetupFiles`, `PrefsStore`) or a
//! small trait of this crate the host fills ([`setup::screen::InstallWindow`], W6-A's terminal launcher once it lands).

pub mod process;
pub mod setup;
pub mod status_line;
pub mod toolkit;
pub mod view;
