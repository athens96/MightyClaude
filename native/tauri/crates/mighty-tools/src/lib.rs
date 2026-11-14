//! Mighty Code's setup screen, Settings › 도구 and the status line (W6-B), with no Tauri dependency and no OS code: the setup
//! catalog, plan, probes and rows with the components (`setup`), the toolkit (`toolkit`), the status line (`status_line`) and their
//! forms (`view`: the setup screen, the components and toolkit boxes and their dialogs over mighty-native's kit). Every OS question
//! goes through a mighty-platform port (`ProcessHost`, `SetupFiles`, `PrefsStore`, `TerminalLauncher`).

pub mod process;
pub mod setup;
pub mod status_line;
pub mod toolkit;
pub mod view;
