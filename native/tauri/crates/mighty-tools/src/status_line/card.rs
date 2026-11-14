//! What a pane's status line card is fed (C# MainWindow.StatusLine.cs `RenderStatusLine`, `Row`, `Terminal`,
//! `StatusLineEligible`, `BuildStatusLineContext`; MainWindow.WebShell.Cards.cs:599-624 `WebSegment`): mighty-composer's
//! `StatusLineInput`, each run coloured as the terminal colours it.

use mighty_composer::cards::{StatusLineInput, StatusRun};
use mighty_core::model::{AppSnapshot, RunSession};

use crate::status_line::ansi::{AnsiColor, AnsiColorKind, AnsiSegment, palette_rgb};
use crate::status_line::config::StatusLineConfig;
use crate::status_line::payload::{StatusLineContext, transcript_path};
use crate::status_line::run::StatusLineResult;

/// The status line's terminal colours for the standard ANSI indexes 1–6 (and their bright twins 9–14), the same in both themes
/// (C# `DesignTokens.AnsiStandard`, held to `design-tokens.json`'s `windowsOnly.ansi.standard`); None for 0, 7, 8 and 15, which take
/// an ink.
pub fn ansi_standard(index: i32) -> Option<(u8, u8, u8)> {
    match index.rem_euclid(8) {
        1 => Some((0xDB, 0x4C, 0x4C)),
        2 => Some((0x4C, 0xA8, 0x66)),
        3 => Some((0xCC, 0x9E, 0x33)),
        4 => Some((0x5C, 0x8C, 0xE6)),
        5 => Some((0xB2, 0x73, 0xD9)),
        6 => Some((0x40, 0xA6, 0xB2)),
        _ => None,
    }
}

/// The opacity a dim (SGR 2) segment keeps its colour at: 150 of 255 (C# `DesignTokens.AnsiDimOpacity`).
pub const ANSI_DIM_OPACITY: f64 = 150.0 / 255.0;

/// C# `Terminal(AnsiColor)`: a segment's terminal colour, or None when it takes an ink (no colour, 0, 7, 8 and 15).
pub fn terminal_color(color: AnsiColor) -> Option<(u8, u8, u8)> {
    match color.kind {
        AnsiColorKind::Rgb => Some((color.a as u8, color.b as u8, color.c as u8)),
        AnsiColorKind::Palette => Some(palette_rgb(color.a)),
        AnsiColorKind::Standard => ansi_standard(color.a),
        AnsiColorKind::None => None,
    }
}

/// C# `WebSegment`: the terminal's RGB as `#rrggbb`, else the secondary ink for bright black, else the pane's ink.
pub fn run_of(segment: &AnsiSegment) -> StatusRun {
    let color = terminal_color(segment.foreground).map(|(r, g, b)| format!("#{r:02x}{g:02x}{b:02x}"));
    StatusRun {
        text: segment.text.clone(),
        bright_black: color.is_none() && segment.foreground == AnsiColor::BRIGHT_BLACK,
        color,
        dim: segment.dim,
        bold: segment.bold,
        italic: segment.italic,
        underline: segment.underline,
    }
}

/// What `RenderStatusLine` last drew, as the card reads it: the configuration in use and an untrusted one as (source label,
/// command), and the result's lines when there is a result for a configuration.
pub fn input(
    config: Option<&StatusLineConfig>,
    untrusted: Option<&StatusLineConfig>,
    result: Option<&StatusLineResult>,
) -> StatusLineInput {
    StatusLineInput {
        config: config.map(|c| (c.source_label(), c.command.clone())),
        untrusted: untrusted.map(|c| (c.source_label(), c.command.clone())),
        lines: result.map(|r| r.lines.iter().map(|line| line.iter().map(run_of).collect()).collect()),
        error_text: result.and_then(|r| r.error_text.clone()),
    }
}

/// C# `StatusLineEligible`: the status line is on, the pane is a Claude agent pane, and its workspace has a folder on this PC (a
/// remote pane's command and settings would be this PC's, run in its host's path).
pub fn eligible(snapshot: &AppSnapshot, pane: &RunSession, local_path: Option<&str>) -> bool {
    snapshot.status_line_enabled && pane.kind() == "claude" && pane.provider() == "claude" && local_path.is_some()
}

/// The toggle in the composer shows on a Claude agent pane.
pub fn toggle_visible(pane: &RunSession) -> bool {
    pane.kind() == "claude" && pane.provider() == "claude"
}

/// The numbers C# `BuildStatusLineContext` reads besides the pane: the model's id and name (`ModelLabel.StatusLine`), the
/// runtime's version, the run's elapsed milliseconds, and Claude's config folder.
pub struct ContextInputs<'a> {
    pub folder: Option<&'a str>,
    pub model_id: &'a str,
    pub model_name: &'a str,
    pub version: Option<&'a str>,
    pub elapsed_ms: Option<i64>,
    pub config_dir: &'a std::path::Path,
}

/// C# `BuildStatusLineContext`: the pane's session, folder, model, usage and settings as the command reads them.
pub fn context(pane: &RunSession, inputs: &ContextInputs) -> StatusLineContext {
    let usage = pane.session_usage.as_ref();
    let settings = pane.settings.as_ref();
    let session_id = pane.resume_id.clone().or_else(|| pane.id.clone()).unwrap_or_default();
    let effort = settings.map(|s| s.effort.clone()).filter(|effort| effort != "default");
    StatusLineContext {
        session_id: session_id.clone(),
        cwd: inputs.folder.map(str::to_string),
        project_dir: inputs.folder.map(str::to_string),
        model_id: Some(inputs.model_id.to_string()),
        model_name: Some(inputs.model_name.to_string()),
        version: Some(inputs.version.unwrap_or("").to_string()),
        cost_usd: usage.and_then(|u| u.cost_usd),
        duration_ms: inputs.elapsed_ms,
        api_duration_ms: Some(0),
        input_tokens: usage.and_then(|u| u.input_tokens),
        output_tokens: usage.and_then(|u| u.output_tokens),
        cache_read_tokens: usage.and_then(|u| u.cache_read_tokens),
        cache_write_tokens: usage.and_then(|u| u.cache_write_tokens),
        context_used_tokens: usage.and_then(|u| u.context_used_tokens),
        context_window_tokens: usage.and_then(|u| u.context_window_tokens),
        effort,
        fast_mode: settings.is_some_and(|s| s.fast_mode),
        rate_limits: usage.and_then(|u| u.rate_limits.clone()),
        output_style: None,
        thinking_enabled: None,
        transcript_path: Some(transcript_path(inputs.config_dir, inputs.folder, &session_id).to_string_lossy().into_owned()),
    }
}

/// C# `ModelLabel.StatusLine` (ModelLabel.cs:116): `model.id` and `model.display_name` for a status line. The model the CLI reported
/// counts only for the pane's own provider and only while it reported it for the selection the pane still has; else the catalogue's
/// resolution of the selection, else the selection itself; named by its family and version, else as the model menu names it.
pub fn model_label(pane: &RunSession, catalog: &mighty_run::model::ModelCatalog, locale: &mighty_core::locale::Locale) -> (String, String) {
    let usage = pane.session_usage.as_ref().filter(|usage| usage.provider == pane.provider());
    let current = usage
        .filter(|usage| usage.selected_model.as_deref().is_none_or(|selected| selected == pane.model()))
        .and_then(|usage| usage.model.clone());
    let id = current
        .or_else(|| catalog.models.iter().find(|m| m.value == pane.model()).and_then(|m| m.resolved_model.clone()))
        .unwrap_or_else(|| pane.model().to_string());
    let options: Vec<mighty_core::model_label::ModelOption> = catalog.models.iter().map(|m| m.label_option()).collect();
    let name = mighty_core::model_label::format(&id).unwrap_or_else(|| mighty_core::model_label::selection(pane, &options, locale));
    (id, name)
}
