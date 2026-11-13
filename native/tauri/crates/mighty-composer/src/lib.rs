//! Mighty Code's composer core for the Rust host: no Tauri, no OS-specific code.
//!
//! The C# `MightyCode.Core` classes the composer reads, each in its own module: the slash commands (`slash`, C#
//! `SlashCommands.cs`/`SlashCommandStrings.cs`), the next-action breadcrumb (`next_actions`), the "send to" targets (`ask_targets`),
//! the queue, 바로 실행 and the second send (`queue`), natural submit (`pending`), pictures (`images`, `svg`), HTML previews (`html`),
//! attachments (`attachments`), with the text and path rules they share (`text`, `files`). Then what the WinUI host builds from
//! them for the page, as pure functions over typed inputs: the composer's state (`composer`, MainWindow.WebShell.Composer.cs), its
//! menus and run settings (`menus`), and the cards above it (`cards`, MainWindow.WebShell.Cards.cs) with the card presenters
//! (`presentation`: ToolPermissionPresentation, UserQuestionnaire, PlanCardSupport).

pub mod ask_targets;
pub mod attachments;
pub mod files;
pub mod html;
pub mod images;
pub mod next_actions;
pub mod pending;
pub mod queue;
pub mod slash;
pub mod svg;
pub mod text;
