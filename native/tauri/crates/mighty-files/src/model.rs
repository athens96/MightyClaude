//! What the WinUI host builds for the page from the files pane's rules (MainWindow.WebShell.Panes.cs `WebFilesModel`, `WebFilePreview`,
//! `WebFileImage`; native/windows/web/src/panes/files/types.ts): the tree's rows and notes, and one file's preview — the text cut to
//! what the pane draws with its coloured spans as flat `[kind, start, length]` triples, the notes over it, and a picture as a data URL
//! (an SVG and a PDF's first page drawn to PNG, a bitmap as it is when the page can draw it whole, else fitted to a PNG).

use std::time::SystemTime;

use base64::Engine as _;
use mighty_core::locale::Locale;
use mighty_platform::PdfRaster;
use serde_json::{Map, Value, json};

use crate::classify::{self, PreviewKind, SourceLanguage};
use crate::preview::PreviewData;
use crate::raster;
use crate::source::{self, MAXIMUM_UNITS};
use crate::tree::{self, FilePaneTree, MAXIMUM_FILTER_RESULTS, MAXIMUM_SOURCE_UNITS, MAXIMUM_TREE_ROWS};
use crate::{listing, utf16};

/// C# `PaneView.MaximumColouredRuns`: past this many spans the rest of the text stays plain.
pub const MAXIMUM_COLOURED_RUNS: usize = 20_000;
/// C# `WebImageBytes`: the largest picture sent as it is; past it the picture is sent at the fit size.
pub const WEB_IMAGE_BYTES: usize = 16 * 1_048_576;

/// The user's number and date formats (C# `CultureInfo.CurrentCulture`), from the OS where it says.
pub trait Formats: Send + Sync {
    /// `value.ToString("N0")`.
    fn n0(&self, value: i64) -> String;
    /// The decimal separator `ToString("0.#")` writes.
    fn decimal(&self) -> String;
    /// `ToLocalTime().ToString("g")`: the short date and time.
    fn date_time(&self, at: SystemTime) -> String;
}

/// Formats where the OS says nothing: `1,234`, a point, `2026-11-14 04:10` in local time.
#[derive(Debug, Clone, Copy, Default)]
pub struct Plain;

/// `value.ToString("N0")` with a comma every three digits.
pub fn grouped(value: i64) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::new();
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            out.push(',');
        }
        out.push(digit);
    }
    if value < 0 { format!("-{out}") } else { out }
}

impl Formats for Plain {
    fn n0(&self, value: i64) -> String {
        grouped(value)
    }
    fn decimal(&self) -> String {
        ".".into()
    }
    fn date_time(&self, at: SystemTime) -> String {
        chrono::DateTime::<chrono::Local>::from(at).format("%Y-%m-%d %H:%M").to_string()
    }
}

/// `value.ToString(pattern)` for "0.#" (`places` 1) and "0.##" (2): rounded half away from zero, trailing zeros dropped.
fn decimal_places(value: f64, places: i32, separator: &str) -> String {
    let factor = 10f64.powi(places);
    let scaled = (value * factor).round() as i64;
    let (whole, fraction) = (scaled / factor as i64, scaled % factor as i64);
    if fraction == 0 {
        return whole.to_string();
    }
    let digits = format!("{fraction:0width$}", width = places as usize);
    format!("{whole}{separator}{}", digits.trim_end_matches('0'))
}

/// C# `PaneView.FormatBytes`.
pub fn format_bytes(bytes: u64, formats: &dyn Formats) -> String {
    let separator = formats.decimal();
    match bytes {
        0..1_000 => format!("{bytes} B"),
        1_000..1_000_000 => format!("{} KB", decimal_places(bytes as f64 / 1_000.0, 1, &separator)),
        1_000_000..1_000_000_000 => format!("{} MB", decimal_places(bytes as f64 / 1_000_000.0, 1, &separator)),
        _ => format!("{} GB", decimal_places(bytes as f64 / 1_000_000_000.0, 2, &separator)),
    }
}

/// C# `FileSymbolKind`: a row's symbol from its name.
fn symbol(name: &str) -> &'static str {
    match classify::kind_for_name(name) {
        Some(PreviewKind::Source(_)) => "source",
        Some(PreviewKind::Markdown) => "markdown",
        Some(PreviewKind::Image) => "image",
        _ => "unsupported",
    }
}

/// `{available: false}`: a remote workspace's files pane (its folder is on its host).
pub fn unavailable() -> Value {
    json!({"available": false})
}

/// C# `WebFilesModel`: the tree's rows and the notes after them, as PaneView.RenderTree draws them.
pub fn files_model(tree: &FilePaneTree, locale: &Locale, formats: &dyn Formats) -> Value {
    let (all, hit_cap) = tree.rows(locale);
    let (rows, drawn_capped) = tree::tree_rows(&all);
    let filtering = tree.is_filtering();
    let mut notes = Vec::new();
    if let Some(error) = tree.folder_errors.get("") {
        notes.push(error.clone());
    } else if !filtering && tree.children.get("").is_some_and(Vec::is_empty) {
        notes.push(locale.get("files.tree.empty"));
    } else if !filtering && tree.truncated.contains("") {
        notes.push(locale.get_with("files.tree.truncated", &[("count", &listing::MAXIMUM_ENTRIES_PER_FOLDER.to_string())]));
    }
    if filtering && rows.is_empty() {
        notes.push(locale.get("files.tree.noMatches"));
    }
    if !filtering && drawn_capped {
        notes.push(locale.get_with("files.tree.truncated", &[("count", &MAXIMUM_TREE_ROWS.to_string())]));
    }
    if filtering && hit_cap {
        notes.push(locale.get_with("files.tree.moreResults", &[("count", &formats.n0(MAXIMUM_FILTER_RESULTS as i64))]));
    }
    let rows: Vec<Value> = rows
        .iter()
        .map(|row| {
            let mut out = Map::new();
            out.insert("path".into(), json!(row.entry.relative_path));
            out.insert("name".into(), json!(row.entry.name));
            out.insert("depth".into(), json!(row.depth));
            out.insert("dir".into(), json!(row.entry.is_directory));
            out.insert("expanded".into(), json!(row.is_expanded));
            out.insert("noise".into(), json!(row.entry.is_noise()));
            out.insert("symlink".into(), json!(row.entry.is_symlink));
            if !row.entry.is_directory {
                out.insert("symbol".into(), json!(symbol(&row.entry.name)));
            }
            if let Some(caption) = &row.caption {
                out.insert("caption".into(), json!(caption));
            }
            let path = &row.entry.relative_path;
            out.insert("reports".into(), json!(tree.folder_errors.contains_key(path) || tree.truncated.contains(path)));
            Value::Object(out)
        })
        .collect();
    let mut out = Map::new();
    out.insert("available".into(), json!(true));
    out.insert("filter".into(), json!(tree.filter));
    out.insert("filtering".into(), json!(filtering));
    out.insert("notes".into(), json!(notes));
    if let Some(selected) = &tree.selected_path {
        out.insert("selected".into(), json!(selected));
    }
    if let Some(preview) = &tree.preview_path {
        out.insert("preview".into(), json!(preview));
    }
    out.insert("rows".into(), Value::Array(rows));
    Value::Object(out)
}

/// The preview of a file that could not be read at all.
pub fn failed_preview(path: &str, name: &str, failure: &str) -> Value {
    json!({"path": path, "name": name, "kind": "unsupported", "failure": failure, "banners": []})
}

/// The text the pane draws, with its spans, as `WebFilePreview` prepares it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PreparedSource {
    pub text: String,
    /// `[kind, start, length]` triples (0 keyword, 1 string, 2 comment, 3 number).
    pub tokens: Vec<usize>,
    pub lines: usize,
    pub wraps: bool,
    pub render_capped: bool,
    pub capped: bool,
    pub units: usize,
}

/// `WebFilePreview`'s text: cut to what the pane draws (256 KB of UTF-16 units, at a line break), CRLF read as LF, its line starts
/// and its colours; past the highlighter's cap or 20,000 spans the rest stays plain and says where colouring stopped.
pub fn prepare_source(full: &str, language: SourceLanguage) -> PreparedSource {
    let (mut text, render_capped) = tree::source_text(full);
    if text.contains("\r\n") {
        text = text.replace("\r\n", "\n");
    }
    let (starts, longest) = source::scan(&text);
    let mut tokens = source::tokens(&text, language, MAXIMUM_UNITS);
    let mut capped = language != SourceLanguage::Plain && utf16::units(&text).len() > MAXIMUM_UNITS;
    let mut units = MAXIMUM_UNITS;
    if tokens.len() > MAXIMUM_COLOURED_RUNS {
        tokens.truncate(MAXIMUM_COLOURED_RUNS);
        capped = true;
        let last = tokens[tokens.len() - 1];
        units = last.location + last.length;
    }
    let flat = tokens.iter().flat_map(|t| [t.kind as usize, t.location, t.length]).collect();
    PreparedSource { text, tokens: flat, lines: starts.len(), wraps: longest > source::WRAP_THRESHOLD, render_capped, capped, units }
}

/// C# `WebFilePreview`: one file's preview as PaneView.Prepare and DrawPreview make it. Runs off the page's thread (pictures are
/// decoded and drawn here).
pub fn file_preview(path: &str, data: &PreviewData, locale: &Locale, formats: &dyn Formats, pdf: &dyn PdfRaster) -> Value {
    if let Some(failure) = &data.failure {
        return failed_preview(path, &data.name, failure);
    }
    let kind = data.kind.word();
    let size = format_bytes(data.size, formats);
    let modified = data.modified.map(|at| formats.date_time(at));
    let mut out = Map::new();
    out.insert("path".into(), json!(path));
    out.insert("name".into(), json!(data.name));
    out.insert("kind".into(), json!(kind));
    if let Some(full) = &data.text {
        let language = match data.kind {
            PreviewKind::Source(language) => language,
            _ => SourceLanguage::Plain,
        };
        let source = prepare_source(full, language);
        let wrapped = locale.get_with("files.preview.wrapped", &[("count", &formats.n0(source::WRAP_THRESHOLD as i64))]);
        let mut banners = Vec::new();
        if data.kind == PreviewKind::Markdown && !data.markdown_renderable {
            banners.push(locale.get("files.markdown.tooLarge"));
        }
        if source.render_capped {
            banners.push(locale.get_with("files.preview.renderCapped", &[("count", &(MAXIMUM_SOURCE_UNITS / 1_024).to_string())]));
        } else if data.truncated {
            banners.push(locale.get("files.preview.truncated"));
        }
        if source.capped {
            banners.push(locale.get_with("files.preview.highlightCapped", &[("count", &formats.n0(source.units as i64))]));
        }
        if source.wraps {
            banners.push(wrapped.clone());
        }
        if let Some(encoding) = data.encoding {
            out.insert("encoding".into(), json!(classify::display_name(encoding)));
        }
        out.insert("text".into(), json!(source.text));
        out.insert("tokens".into(), json!(source.tokens));
        out.insert("lines".into(), json!(source.lines));
        out.insert("wraps".into(), json!(source.wraps));
        out.insert("banners".into(), json!(banners));
        out.insert("renderedBanners".into(), json!(if source.wraps { vec![wrapped] } else { vec![] }));
        out.insert("markdown".into(), json!(data.kind == PreviewKind::Markdown && data.markdown_renderable));
    } else if data.kind == PreviewKind::Image {
        let (image, reason, fit_only) = file_image(data, locale, pdf);
        if let Some(image) = image {
            out.insert("image".into(), image);
        }
        if let Some(reason) = reason {
            out.insert("reason".into(), json!(reason));
        }
        let banners = if fit_only {
            vec![locale.get_with("files.image.fitOnly", &[("count", &(classify::MAXIMUM_FULL_PIXELS / 1_000_000).to_string())])]
        } else {
            vec![]
        };
        out.insert("banners".into(), json!(banners));
    } else {
        if let Some(reason) = &data.reason {
            out.insert("reason".into(), json!(reason));
        }
        out.insert("banners".into(), json!([]));
    }
    out.insert("size".into(), json!(size));
    if let Some(modified) = modified {
        out.insert("modified".into(), json!(modified));
    }
    Value::Object(out)
}

fn data_url(media_type: &str, bytes: &[u8]) -> String {
    format!("data:{media_type};base64,{}", base64::engine::general_purpose::STANDARD.encode(bytes))
}

/// C# `WebImageTypes`: the pictures the page draws as they are, by extension.
fn web_image_type(extension: &str) -> Option<&'static str> {
    Some(match extension {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "ico" => "image/x-icon",
        _ => return None,
    })
}

/// C# `WebFileImage`: the picture as PaneView.ImageView decodes it: an SVG drawn to PNG, a PDF's first page, any other one decoded to
/// check its pixels. One the page can draw goes as it is; any other, or one too large to draw whole, as a PNG at the fit size. Answers
/// the image, the reason there is none, and whether it is shown fitted only.
pub fn file_image(data: &PreviewData, locale: &Locale, pdf: &dyn PdfRaster) -> (Option<Value>, Option<String>, bool) {
    let bytes = data.image_bytes.as_deref().unwrap_or_default();
    let extension = classify::file_extension(&data.name);
    let failed = || (None, Some(locale.get("files.preview.failed")), false);
    let pixels = |width: f64, height: f64| {
        locale.get_with("files.image.pixels", &[("width", &(width as i64).to_string()), ("height", &(height as i64).to_string())])
    };
    let image = |url: String, width: f64, height: f64, bitmap: bool, zoomable: bool| json!({"url": url, "width": width, "height": height, "bitmap": bitmap, "label": pixels(width, height), "zoomable": zoomable});
    if extension == "svg" {
        return match raster::svg_png(bytes, classify::MAXIMUM_FIT_PIXELS, false) {
            Ok(raster) => {
                (Some(image(data_url("image/png", &raster.png), raster.original_width, raster.original_height, false, true)), None, false)
            }
            Err(_) => failed(),
        };
    }
    if extension == "pdf" {
        let fit = classify::MAXIMUM_FIT_PIXELS as f64;
        let size = |width: f64, height: f64| {
            if !classify::is_drawable(width, height) {
                return None;
            }
            let scale = (fit / width.max(height)).min(2.0);
            Some(((width * scale).max(1.0) as u32, (height * scale).max(1.0) as u32))
        };
        return match pdf.first_page(bytes, &size) {
            Ok(Some(page)) => match page.png {
                Some(png) => (Some(image(data_url("image/png", &png), page.width, page.height, false, true)), None, false),
                None => (None, None, false),
            },
            Ok(None) | Err(_) => failed(),
        };
    }
    let Some(size) = raster::bitmap_size(bytes) else { return failed() };
    let (width, height) = (size.width as i64, size.height as i64);
    let pixels = classify::pixel_count(width, height);
    let Some(pixels) = pixels.filter(|p| *p <= classify::MAXIMUM_DECODE_PIXELS) else {
        let count = (classify::MAXIMUM_DECODE_PIXELS / 1_000_000).to_string();
        return (None, Some(locale.get_with("files.preview.tooManyPixels", &[("count", &count)])), false);
    };
    let full = pixels <= classify::MAXIMUM_FULL_PIXELS;
    let url = match web_image_type(&extension) {
        Some(media) if full && bytes.len() <= WEB_IMAGE_BYTES => data_url(media, bytes),
        _ => {
            // The fit size, at most MaximumFitPixels on the long side, as a PNG.
            let fit = (classify::MAXIMUM_FIT_PIXELS as f64 / width.max(height) as f64).min(1.0);
            let Some(png) = raster::fitted_png(bytes, fit) else { return failed() };
            data_url("image/png", &png)
        }
    };
    (Some(image(url, width as f64, height as f64, true, full)), None, !full)
}
