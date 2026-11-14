//! The session context a status line command reads on stdin (C# `StatusLineContext`, `StatusLineSupport.TranscriptPath`,
//! `BuildPayload`, StatusLine.cs:184-329), with the CLI's field names and shape, written as `JsonObject.ToJsonString()` writes it.

use std::path::{Path, PathBuf};

use mighty_core::json::JsonOut;
use mighty_core::model::SessionRateLimit;

/// C# `StatusLineContext`.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StatusLineContext {
    pub session_id: String,
    pub cwd: Option<String>,
    pub project_dir: Option<String>,
    pub model_id: Option<String>,
    pub model_name: Option<String>,
    pub version: Option<String>,
    pub cost_usd: Option<f64>,
    pub duration_ms: Option<i64>,
    pub api_duration_ms: Option<i64>,
    pub input_tokens: Option<i64>,
    pub output_tokens: Option<i64>,
    pub cache_read_tokens: Option<i64>,
    pub cache_write_tokens: Option<i64>,
    pub context_used_tokens: Option<i64>,
    pub context_window_tokens: Option<i64>,
    pub effort: Option<String>,
    pub fast_mode: bool,
    pub rate_limits: Option<Vec<SessionRateLimit>>,
    pub output_style: Option<String>,
    pub thinking_enabled: Option<bool>,
    pub transcript_path: Option<String>,
}

/// The transcript path by the Mac's slug rule: every character that is not a letter or digit becomes `-` (each UTF-16 unit, as
/// C# reads the string), the first 200 kept: `<config>/projects/<slug>/<session>.jsonl`.
pub fn transcript_path(config_dir: &Path, cwd: Option<&str>, session_id: &str) -> PathBuf {
    let mut slug: Vec<char> = Vec::new();
    for c in cwd.unwrap_or("").chars() {
        let mut units = [0u16; 2];
        if c.len_utf16() == 1 && is_letter_or_digit(c.encode_utf16(&mut units)[0]) {
            slug.push(c);
        } else {
            slug.extend(std::iter::repeat_n('-', c.len_utf16()));
        }
    }
    slug.truncate(200);
    let slug: String = slug.into_iter().collect();
    let projects = config_dir.join("projects");
    let folder = if slug.is_empty() { projects } else { projects.join(slug) };
    folder.join(format!("{session_id}.jsonl"))
}

/// The BMP code units .NET's `char.IsLetterOrDigit` accepts (Unicode categories L* and Nd, as .NET 10 reads them), as inclusive
/// ranges generated from .NET itself (`char.IsLetterOrDigit((char)c)` for every BMP code unit), so the slug matches C#'s for every
/// code unit (Rust's `is_alphanumeric` also takes Nl, No and the marks and symbols Unicode calls alphabetic).
#[rustfmt::skip]
const LETTER_OR_DIGIT: &[(u16, u16)] = &[
    (0x0030, 0x0039), (0x0041, 0x005A), (0x0061, 0x007A), (0x00AA, 0x00AA), (0x00B5, 0x00B5), (0x00BA, 0x00BA), (0x00C0, 0x00D6),
    (0x00D8, 0x00F6), (0x00F8, 0x02C1), (0x02C6, 0x02D1), (0x02E0, 0x02E4), (0x02EC, 0x02EC), (0x02EE, 0x02EE), (0x0370, 0x0374),
    (0x0376, 0x0377), (0x037A, 0x037D), (0x037F, 0x037F), (0x0386, 0x0386), (0x0388, 0x038A), (0x038C, 0x038C), (0x038E, 0x03A1),
    (0x03A3, 0x03F5), (0x03F7, 0x0481), (0x048A, 0x052F), (0x0531, 0x0556), (0x0559, 0x0559), (0x0560, 0x0588), (0x05D0, 0x05EA),
    (0x05EF, 0x05F2), (0x0620, 0x064A), (0x0660, 0x0669), (0x066E, 0x066F), (0x0671, 0x06D3), (0x06D5, 0x06D5), (0x06E5, 0x06E6),
    (0x06EE, 0x06FC), (0x06FF, 0x06FF), (0x0710, 0x0710), (0x0712, 0x072F), (0x074D, 0x07A5), (0x07B1, 0x07B1), (0x07C0, 0x07EA),
    (0x07F4, 0x07F5), (0x07FA, 0x07FA), (0x0800, 0x0815), (0x081A, 0x081A), (0x0824, 0x0824), (0x0828, 0x0828), (0x0840, 0x0858),
    (0x0860, 0x086A), (0x0870, 0x0887), (0x0889, 0x088E), (0x08A0, 0x08C9), (0x0904, 0x0939), (0x093D, 0x093D), (0x0950, 0x0950),
    (0x0958, 0x0961), (0x0966, 0x096F), (0x0971, 0x0980), (0x0985, 0x098C), (0x098F, 0x0990), (0x0993, 0x09A8), (0x09AA, 0x09B0),
    (0x09B2, 0x09B2), (0x09B6, 0x09B9), (0x09BD, 0x09BD), (0x09CE, 0x09CE), (0x09DC, 0x09DD), (0x09DF, 0x09E1), (0x09E6, 0x09F1),
    (0x09FC, 0x09FC), (0x0A05, 0x0A0A), (0x0A0F, 0x0A10), (0x0A13, 0x0A28), (0x0A2A, 0x0A30), (0x0A32, 0x0A33), (0x0A35, 0x0A36),
    (0x0A38, 0x0A39), (0x0A59, 0x0A5C), (0x0A5E, 0x0A5E), (0x0A66, 0x0A6F), (0x0A72, 0x0A74), (0x0A85, 0x0A8D), (0x0A8F, 0x0A91),
    (0x0A93, 0x0AA8), (0x0AAA, 0x0AB0), (0x0AB2, 0x0AB3), (0x0AB5, 0x0AB9), (0x0ABD, 0x0ABD), (0x0AD0, 0x0AD0), (0x0AE0, 0x0AE1),
    (0x0AE6, 0x0AEF), (0x0AF9, 0x0AF9), (0x0B05, 0x0B0C), (0x0B0F, 0x0B10), (0x0B13, 0x0B28), (0x0B2A, 0x0B30), (0x0B32, 0x0B33),
    (0x0B35, 0x0B39), (0x0B3D, 0x0B3D), (0x0B5C, 0x0B5D), (0x0B5F, 0x0B61), (0x0B66, 0x0B6F), (0x0B71, 0x0B71), (0x0B83, 0x0B83),
    (0x0B85, 0x0B8A), (0x0B8E, 0x0B90), (0x0B92, 0x0B95), (0x0B99, 0x0B9A), (0x0B9C, 0x0B9C), (0x0B9E, 0x0B9F), (0x0BA3, 0x0BA4),
    (0x0BA8, 0x0BAA), (0x0BAE, 0x0BB9), (0x0BD0, 0x0BD0), (0x0BE6, 0x0BEF), (0x0C05, 0x0C0C), (0x0C0E, 0x0C10), (0x0C12, 0x0C28),
    (0x0C2A, 0x0C39), (0x0C3D, 0x0C3D), (0x0C58, 0x0C5A), (0x0C5D, 0x0C5D), (0x0C60, 0x0C61), (0x0C66, 0x0C6F), (0x0C80, 0x0C80),
    (0x0C85, 0x0C8C), (0x0C8E, 0x0C90), (0x0C92, 0x0CA8), (0x0CAA, 0x0CB3), (0x0CB5, 0x0CB9), (0x0CBD, 0x0CBD), (0x0CDD, 0x0CDE),
    (0x0CE0, 0x0CE1), (0x0CE6, 0x0CEF), (0x0CF1, 0x0CF2), (0x0D04, 0x0D0C), (0x0D0E, 0x0D10), (0x0D12, 0x0D3A), (0x0D3D, 0x0D3D),
    (0x0D4E, 0x0D4E), (0x0D54, 0x0D56), (0x0D5F, 0x0D61), (0x0D66, 0x0D6F), (0x0D7A, 0x0D7F), (0x0D85, 0x0D96), (0x0D9A, 0x0DB1),
    (0x0DB3, 0x0DBB), (0x0DBD, 0x0DBD), (0x0DC0, 0x0DC6), (0x0DE6, 0x0DEF), (0x0E01, 0x0E30), (0x0E32, 0x0E33), (0x0E40, 0x0E46),
    (0x0E50, 0x0E59), (0x0E81, 0x0E82), (0x0E84, 0x0E84), (0x0E86, 0x0E8A), (0x0E8C, 0x0EA3), (0x0EA5, 0x0EA5), (0x0EA7, 0x0EB0),
    (0x0EB2, 0x0EB3), (0x0EBD, 0x0EBD), (0x0EC0, 0x0EC4), (0x0EC6, 0x0EC6), (0x0ED0, 0x0ED9), (0x0EDC, 0x0EDF), (0x0F00, 0x0F00),
    (0x0F20, 0x0F29), (0x0F40, 0x0F47), (0x0F49, 0x0F6C), (0x0F88, 0x0F8C), (0x1000, 0x102A), (0x103F, 0x1049), (0x1050, 0x1055),
    (0x105A, 0x105D), (0x1061, 0x1061), (0x1065, 0x1066), (0x106E, 0x1070), (0x1075, 0x1081), (0x108E, 0x108E), (0x1090, 0x1099),
    (0x10A0, 0x10C5), (0x10C7, 0x10C7), (0x10CD, 0x10CD), (0x10D0, 0x10FA), (0x10FC, 0x1248), (0x124A, 0x124D), (0x1250, 0x1256),
    (0x1258, 0x1258), (0x125A, 0x125D), (0x1260, 0x1288), (0x128A, 0x128D), (0x1290, 0x12B0), (0x12B2, 0x12B5), (0x12B8, 0x12BE),
    (0x12C0, 0x12C0), (0x12C2, 0x12C5), (0x12C8, 0x12D6), (0x12D8, 0x1310), (0x1312, 0x1315), (0x1318, 0x135A), (0x1380, 0x138F),
    (0x13A0, 0x13F5), (0x13F8, 0x13FD), (0x1401, 0x166C), (0x166F, 0x167F), (0x1681, 0x169A), (0x16A0, 0x16EA), (0x16F1, 0x16F8),
    (0x1700, 0x1711), (0x171F, 0x1731), (0x1740, 0x1751), (0x1760, 0x176C), (0x176E, 0x1770), (0x1780, 0x17B3), (0x17D7, 0x17D7),
    (0x17DC, 0x17DC), (0x17E0, 0x17E9), (0x1810, 0x1819), (0x1820, 0x1878), (0x1880, 0x1884), (0x1887, 0x18A8), (0x18AA, 0x18AA),
    (0x18B0, 0x18F5), (0x1900, 0x191E), (0x1946, 0x196D), (0x1970, 0x1974), (0x1980, 0x19AB), (0x19B0, 0x19C9), (0x19D0, 0x19D9),
    (0x1A00, 0x1A16), (0x1A20, 0x1A54), (0x1A80, 0x1A89), (0x1A90, 0x1A99), (0x1AA7, 0x1AA7), (0x1B05, 0x1B33), (0x1B45, 0x1B4C),
    (0x1B50, 0x1B59), (0x1B83, 0x1BA0), (0x1BAE, 0x1BE5), (0x1C00, 0x1C23), (0x1C40, 0x1C49), (0x1C4D, 0x1C7D), (0x1C80, 0x1C8A),
    (0x1C90, 0x1CBA), (0x1CBD, 0x1CBF), (0x1CE9, 0x1CEC), (0x1CEE, 0x1CF3), (0x1CF5, 0x1CF6), (0x1CFA, 0x1CFA), (0x1D00, 0x1DBF),
    (0x1E00, 0x1F15), (0x1F18, 0x1F1D), (0x1F20, 0x1F45), (0x1F48, 0x1F4D), (0x1F50, 0x1F57), (0x1F59, 0x1F59), (0x1F5B, 0x1F5B),
    (0x1F5D, 0x1F5D), (0x1F5F, 0x1F7D), (0x1F80, 0x1FB4), (0x1FB6, 0x1FBC), (0x1FBE, 0x1FBE), (0x1FC2, 0x1FC4), (0x1FC6, 0x1FCC),
    (0x1FD0, 0x1FD3), (0x1FD6, 0x1FDB), (0x1FE0, 0x1FEC), (0x1FF2, 0x1FF4), (0x1FF6, 0x1FFC), (0x2071, 0x2071), (0x207F, 0x207F),
    (0x2090, 0x209C), (0x2102, 0x2102), (0x2107, 0x2107), (0x210A, 0x2113), (0x2115, 0x2115), (0x2119, 0x211D), (0x2124, 0x2124),
    (0x2126, 0x2126), (0x2128, 0x2128), (0x212A, 0x212D), (0x212F, 0x2139), (0x213C, 0x213F), (0x2145, 0x2149), (0x214E, 0x214E),
    (0x2183, 0x2184), (0x2C00, 0x2CE4), (0x2CEB, 0x2CEE), (0x2CF2, 0x2CF3), (0x2D00, 0x2D25), (0x2D27, 0x2D27), (0x2D2D, 0x2D2D),
    (0x2D30, 0x2D67), (0x2D6F, 0x2D6F), (0x2D80, 0x2D96), (0x2DA0, 0x2DA6), (0x2DA8, 0x2DAE), (0x2DB0, 0x2DB6), (0x2DB8, 0x2DBE),
    (0x2DC0, 0x2DC6), (0x2DC8, 0x2DCE), (0x2DD0, 0x2DD6), (0x2DD8, 0x2DDE), (0x2E2F, 0x2E2F), (0x3005, 0x3006), (0x3031, 0x3035),
    (0x303B, 0x303C), (0x3041, 0x3096), (0x309D, 0x309F), (0x30A1, 0x30FA), (0x30FC, 0x30FF), (0x3105, 0x312F), (0x3131, 0x318E),
    (0x31A0, 0x31BF), (0x31F0, 0x31FF), (0x3400, 0x4DBF), (0x4E00, 0xA48C), (0xA4D0, 0xA4FD), (0xA500, 0xA60C), (0xA610, 0xA62B),
    (0xA640, 0xA66E), (0xA67F, 0xA69D), (0xA6A0, 0xA6E5), (0xA717, 0xA71F), (0xA722, 0xA788), (0xA78B, 0xA7CD), (0xA7D0, 0xA7D1),
    (0xA7D3, 0xA7D3), (0xA7D5, 0xA7DC), (0xA7F2, 0xA801), (0xA803, 0xA805), (0xA807, 0xA80A), (0xA80C, 0xA822), (0xA840, 0xA873),
    (0xA882, 0xA8B3), (0xA8D0, 0xA8D9), (0xA8F2, 0xA8F7), (0xA8FB, 0xA8FB), (0xA8FD, 0xA8FE), (0xA900, 0xA925), (0xA930, 0xA946),
    (0xA960, 0xA97C), (0xA984, 0xA9B2), (0xA9CF, 0xA9D9), (0xA9E0, 0xA9E4), (0xA9E6, 0xA9FE), (0xAA00, 0xAA28), (0xAA40, 0xAA42),
    (0xAA44, 0xAA4B), (0xAA50, 0xAA59), (0xAA60, 0xAA76), (0xAA7A, 0xAA7A), (0xAA7E, 0xAAAF), (0xAAB1, 0xAAB1), (0xAAB5, 0xAAB6),
    (0xAAB9, 0xAABD), (0xAAC0, 0xAAC0), (0xAAC2, 0xAAC2), (0xAADB, 0xAADD), (0xAAE0, 0xAAEA), (0xAAF2, 0xAAF4), (0xAB01, 0xAB06),
    (0xAB09, 0xAB0E), (0xAB11, 0xAB16), (0xAB20, 0xAB26), (0xAB28, 0xAB2E), (0xAB30, 0xAB5A), (0xAB5C, 0xAB69), (0xAB70, 0xABE2),
    (0xABF0, 0xABF9), (0xAC00, 0xD7A3), (0xD7B0, 0xD7C6), (0xD7CB, 0xD7FB), (0xF900, 0xFA6D), (0xFA70, 0xFAD9), (0xFB00, 0xFB06),
    (0xFB13, 0xFB17), (0xFB1D, 0xFB1D), (0xFB1F, 0xFB28), (0xFB2A, 0xFB36), (0xFB38, 0xFB3C), (0xFB3E, 0xFB3E), (0xFB40, 0xFB41),
    (0xFB43, 0xFB44), (0xFB46, 0xFBB1), (0xFBD3, 0xFD3D), (0xFD50, 0xFD8F), (0xFD92, 0xFDC7), (0xFDF0, 0xFDFB), (0xFE70, 0xFE74),
    (0xFE76, 0xFEFC), (0xFF10, 0xFF19), (0xFF21, 0xFF3A), (0xFF41, 0xFF5A), (0xFF66, 0xFFBE), (0xFFC2, 0xFFC7), (0xFFCA, 0xFFCF),
    (0xFFD2, 0xFFD7), (0xFFDA, 0xFFDC),
];

/// C# `char.IsLetterOrDigit(c)` for one UTF-16 code unit.
fn is_letter_or_digit(unit: u16) -> bool {
    LETTER_OR_DIGIT
        .binary_search_by(|&(low, high)| {
            if high < unit {
                std::cmp::Ordering::Less
            } else if low > unit {
                std::cmp::Ordering::Greater
            } else {
                std::cmp::Ordering::Equal
            }
        })
        .is_ok()
}

/// Claude Code's five levels, and Codex's `ultra` for a Codex pane set to it.
fn valid_effort(effort: &str) -> bool {
    mighty_core::wire::CODEX_EFFORTS.contains(&effort)
}

/// C# `BuildPayload`: the JSON the command reads, in the CLI's shape. `now` decides which rate-limit resets are still ahead.
pub fn build_payload(ctx: &StatusLineContext, now: chrono::DateTime<chrono::Utc>) -> String {
    let mut out = JsonOut::new();
    let opt_str = |out: &mut JsonOut, value: Option<&str>| match value {
        Some(text) => out.str(text),
        None => out.null(),
    };
    out.begin_object();
    out.name("hook_event_name");
    out.str("Status");
    out.name("session_id");
    out.str(&ctx.session_id);
    out.name("transcript_path");
    opt_str(&mut out, ctx.transcript_path.as_deref());
    out.name("cwd");
    opt_str(&mut out, ctx.cwd.as_deref());
    out.name("model");
    out.begin_object();
    out.name("id");
    out.str(ctx.model_id.as_deref().unwrap_or(""));
    out.name("display_name");
    out.str(ctx.model_name.as_deref().unwrap_or(""));
    out.end_object();
    out.name("workspace");
    out.begin_object();
    out.name("current_dir");
    out.str(ctx.cwd.as_deref().unwrap_or(""));
    out.name("project_dir");
    out.str(ctx.project_dir.as_deref().unwrap_or(""));
    out.end_object();
    out.name("version");
    opt_str(&mut out, ctx.version.as_deref());
    out.name("cost");
    out.begin_object();
    out.name("total_cost_usd");
    out.float(ctx.cost_usd.unwrap_or(0.0));
    out.name("total_duration_ms");
    out.int(ctx.duration_ms.unwrap_or(0));
    out.name("total_api_duration_ms");
    out.int(ctx.api_duration_ms.unwrap_or(0));
    out.name("total_lines_added");
    out.int(0);
    out.name("total_lines_removed");
    out.int(0);
    out.end_object();
    out.name("fast_mode");
    out.bool(ctx.fast_mode);
    if let Some(style) = &ctx.output_style {
        out.name("output_style");
        out.begin_object();
        out.name("name");
        out.str(style);
        out.end_object();
    }
    if let Some(enabled) = ctx.thinking_enabled {
        out.name("thinking");
        out.begin_object();
        out.name("enabled");
        out.bool(enabled);
        out.end_object();
    }
    let size = ctx.context_window_tokens.unwrap_or(200_000);
    let usage = ctx.context_used_tokens.map(|used| {
        let percent = (used as f64 / size.max(1) as f64 * 100.0).clamp(0.0, 100.0);
        (used, percent)
    });
    out.name("exceeds_200k_tokens");
    out.bool(usage.is_some_and(|(used, _)| used > 200_000));
    out.name("context_window");
    out.begin_object();
    out.name("total_input_tokens");
    out.int(ctx.input_tokens.unwrap_or(0));
    out.name("total_output_tokens");
    out.int(ctx.output_tokens.unwrap_or(0));
    out.name("context_window_size");
    out.int(size);
    match usage {
        Some((used, percent)) => {
            out.name("current_usage");
            out.begin_object();
            out.name("input_tokens");
            out.int(used);
            out.name("output_tokens");
            out.int(0);
            out.name("cache_creation_input_tokens");
            out.int(0);
            out.name("cache_read_input_tokens");
            out.int(0);
            out.end_object();
            // .NET's Math.Round rounds a half to even.
            out.name("used_percentage");
            out.float((percent * 10.0).round_ties_even() / 10.0);
            out.name("remaining_percentage");
            out.float(((100.0 - percent) * 10.0).round_ties_even() / 10.0);
        }
        None => {
            for name in ["current_usage", "used_percentage", "remaining_percentage"] {
                out.name(name);
                out.null();
            }
        }
    }
    out.end_object();
    if let Some(effort) = ctx.effort.as_deref().filter(|effort| valid_effort(effort)) {
        out.name("effort");
        out.begin_object();
        out.name("level");
        out.str(effort);
        out.end_object();
    }
    if let Some(limits) = ctx.rate_limits.as_ref().filter(|limits| !limits.is_empty()) {
        let mut kept: Vec<(&str, f64, Option<i64>)> = Vec::new();
        for limit in limits {
            let key = match limit.kind.as_str() {
                "five_hour" | "session" | "5h" | "primary" => "five_hour",
                "seven_day" | "weekly" | "7d" | "secondary" => "seven_day",
                _ => continue,
            };
            if kept.iter().any(|(known, _, _)| *known == key) {
                continue;
            }
            let Some(percent) = limit.percent_used.filter(|p| p.is_finite()) else { continue };
            let mut resets_at = None;
            if let Some(text) = &limit.resets_at {
                let Some(at) = mighty_core::time::parse(Some(text)).map(|t| t.to_chrono()) else { continue };
                if at <= now {
                    continue;
                }
                resets_at = Some(at.timestamp());
            }
            kept.push((key, percent.clamp(0.0, 100.0), resets_at));
        }
        if !kept.is_empty() {
            out.name("rate_limits");
            out.begin_object();
            for (key, percent, resets_at) in kept {
                out.name(key);
                out.begin_object();
                out.name("used_percentage");
                out.float(percent);
                if let Some(at) = resets_at {
                    out.name("resets_at");
                    out.int(at);
                }
                out.end_object();
            }
            out.end_object();
        }
    }
    out.end_object();
    out.finish()
}
