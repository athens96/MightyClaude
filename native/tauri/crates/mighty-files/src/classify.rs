//! How a file is previewed (C# `FilePreviewClassifier`, `TextEncodings`; WorkspaceFiles.cs:334-573): by name first, then by sniffing
//! its first bytes. A text kind still needs text bytes, so a binary `.plist` or a mislabelled file is unsupported. The text decoding
//! (byte order mark, then UTF-8, then CP949) and the SVG rules are mighty-composer's, shared with the pictures.

use std::io::{Read, Seek, SeekFrom};

pub use mighty_composer::svg::{is_drawable, svg_loads_external_content, svg_size};
pub use mighty_composer::text::{DecodedText, TextEncodingKind, byte_order_mark, decode_text, from_byte_order_mark};

/// C# `FilePreviewClassifier.MaximumTextBytes`.
pub const MAXIMUM_TEXT_BYTES: usize = 1_048_576;
/// C# `MaximumImageBytes`: 50 MB.
pub const MAXIMUM_IMAGE_BYTES: u64 = 50 * 1_048_576;
/// C# `MaximumFitPixels`: the longest side a picture is fitted to.
pub const MAXIMUM_FIT_PIXELS: u32 = 4_096;
/// C# `MaximumFullPixels`: past this a picture is shown fitted only.
pub const MAXIMUM_FULL_PIXELS: i64 = 100_000_000;
/// C# `MaximumDecodePixels`: past this a picture is not decoded at all.
pub const MAXIMUM_DECODE_PIXELS: i64 = 250_000_000;
/// C# `SniffBytes`.
pub const SNIFF_BYTES: usize = 8_192;
/// C# `MaximumMarkdownRenderBytes`: Markdown above this many bytes is shown as source.
pub const MAXIMUM_MARKDOWN_RENDER_BYTES: u64 = 131_072;

/// C# `SourceLanguage`: the languages the preview highlights; `Plain` is text without highlighting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SourceLanguage {
    Swift,
    C,
    JavaScript,
    Python,
    Kotlin,
    Java,
    Go,
    Rust,
    Shell,
    Json,
    Yaml,
    Toml,
    Xml,
    Html,
    Css,
    Sql,
    Gradle,
    Dockerfile,
    Makefile,
    Plain,
}

/// C# `FilePreviewKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PreviewKind {
    Markdown,
    Source(SourceLanguage),
    Image,
    Unsupported,
}

impl PreviewKind {
    /// The page's word for it (`source`, `markdown`, `image`, `unsupported`).
    pub fn word(self) -> &'static str {
        match self {
            PreviewKind::Source(_) => "source",
            PreviewKind::Markdown => "markdown",
            PreviewKind::Image => "image",
            PreviewKind::Unsupported => "unsupported",
        }
    }
}

/// C# `TextEncodings.DisplayName`: shown in the preview header, the same names as macOS.
pub fn display_name(kind: TextEncodingKind) -> &'static str {
    match kind {
        TextEncodingKind::Utf8 => "UTF-8",
        TextEncodingKind::Utf8Bom => "UTF-8 BOM",
        TextEncodingKind::Utf16Le => "UTF-16 LE",
        TextEncodingKind::Utf16Be => "UTF-16 BE",
        TextEncodingKind::Utf32Le => "UTF-32 LE",
        TextEncodingKind::Utf32Be => "UTF-32 BE",
        TextEncodingKind::Cp949 => "CP949 (EUC-KR)",
    }
}

/// C# `FileExtension`: the lower-cased extension; a leading dot (".env") is not one.
pub fn file_extension(name: &str) -> String {
    match name.rfind('.') {
        Some(dot) if dot > 0 => name[dot + 1..].to_lowercase(),
        _ => String::new(),
    }
}

fn source_language(extension: &str) -> Option<SourceLanguage> {
    use SourceLanguage::*;
    Some(match extension {
        "swift" => Swift,
        "c" | "h" | "cc" | "cpp" | "cxx" | "hpp" | "hh" | "m" | "mm" => C,
        "js" | "jsx" | "mjs" | "cjs" | "ts" | "tsx" | "mts" | "cts" => JavaScript,
        "py" | "pyi" => Python,
        "kt" | "kts" => Kotlin,
        "java" => Java,
        "go" => Go,
        "rs" => Rust,
        "sh" | "bash" | "zsh" | "fish" | "env" => Shell,
        "json" | "jsonc" => Json,
        "yaml" | "yml" => Yaml,
        "toml" => Toml,
        "xml" | "plist" | "xib" | "storyboard" | "entitlements" => Xml,
        "html" | "htm" => Html,
        "css" | "scss" | "less" => Css,
        "sql" => Sql,
        "gradle" => Gradle,
        "txt" | "log" | "csv" | "tsv" | "ini" | "cfg" | "conf" | "properties" => Plain,
        _ => return None,
    })
}

/// C# `KindForName`: the kind from the file name alone, or None when only the bytes can tell.
pub fn kind_for_name(name: &str) -> Option<PreviewKind> {
    let extension = file_extension(name);
    if matches!(extension.as_str(), "md" | "markdown" | "mdx") {
        return Some(PreviewKind::Markdown);
    }
    if matches!(
        extension.as_str(),
        "png" | "jpg" | "jpeg" | "gif" | "webp" | "heic" | "heif" | "tif" | "tiff" | "bmp" | "ico" | "svg" | "pdf"
    ) {
        return Some(PreviewKind::Image);
    }
    let lower = name.to_lowercase();
    if lower == "dockerfile" || lower.starts_with("dockerfile.") || extension == "dockerfile" {
        return Some(PreviewKind::Source(SourceLanguage::Dockerfile));
    }
    if matches!(lower.as_str(), "makefile" | "gnumakefile") || extension == "mk" {
        return Some(PreviewKind::Source(SourceLanguage::Makefile));
    }
    if lower == ".env" || lower.starts_with(".env.") {
        return Some(PreviewKind::Source(SourceLanguage::Shell));
    }
    source_language(&extension).map(PreviewKind::Source)
}

/// C# `LooksLikeText`: text in one of the encodings; without a UTF-16/32 byte order mark a NUL byte means binary.
pub fn looks_like_text(data: &[u8]) -> bool {
    decode_text(data, true).is_some()
}

/// C# `Classify`: the preview kind for `name` given its first bytes (up to [`SNIFF_BYTES`]).
pub fn classify(name: &str, head: &[u8]) -> PreviewKind {
    match kind_for_name(name) {
        Some(PreviewKind::Image) => PreviewKind::Image,
        Some(kind @ (PreviewKind::Markdown | PreviewKind::Source(_))) => {
            if looks_like_text(head) {
                kind
            } else {
                PreviewKind::Unsupported
            }
        }
        _ if looks_like_text(head) => PreviewKind::Source(SourceLanguage::Plain),
        _ => PreviewKind::Unsupported,
    }
}

/// C# `TextRead`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TextRead {
    pub text: String,
    pub truncated: bool,
    pub encoding: TextEncodingKind,
}

/// C# `DecodeLossily`: whatever is there, bad sequences replaced (a cut unit at the end dropped).
fn decode_lossily(data: &[u8], kind: TextEncodingKind) -> String {
    let utf16 = |little: bool| {
        let units: Vec<u16> = data
            .as_chunks::<2>()
            .0
            .iter()
            .map(|p| if little { u16::from_le_bytes([p[0], p[1]]) } else { u16::from_be_bytes([p[0], p[1]]) })
            .collect();
        String::from_utf16_lossy(&units)
    };
    let utf32 = |little: bool| {
        data.as_chunks::<4>()
            .0
            .iter()
            .map(|p| {
                let value =
                    if little { u32::from_le_bytes([p[0], p[1], p[2], p[3]]) } else { u32::from_be_bytes([p[0], p[1], p[2], p[3]]) };
                char::from_u32(value).unwrap_or('\u{FFFD}')
            })
            .collect::<String>()
    };
    match kind {
        TextEncodingKind::Utf16Le => utf16(true),
        TextEncodingKind::Utf16Be => utf16(false),
        TextEncodingKind::Utf32Le => utf32(true),
        TextEncodingKind::Utf32Be => utf32(false),
        _ => String::from_utf8_lossy(data).into_owned(),
    }
}

/// C# `ReadText`: the first `maximum_bytes` of a text file read from its start, in the encoding [`decode_text`] finds; bytes that no
/// longer decode become replacement characters.
pub fn read_text<R: Read + Seek>(stream: &mut R, maximum_bytes: usize) -> std::io::Result<TextRead> {
    let data = read_prefix(stream, maximum_bytes + 1)?;
    let truncated = data.len() > maximum_bytes;
    let body = &data[..data.len().min(maximum_bytes)];
    if let Some(decoded) = decode_text(body, true) {
        return Ok(TextRead { text: decoded.text, truncated, encoding: decoded.encoding });
    }
    let fallback = from_byte_order_mark(body).unwrap_or(TextEncodingKind::Utf8);
    Ok(TextRead { text: decode_lossily(&body[byte_order_mark(fallback).len()..], fallback), truncated, encoding: fallback })
}

/// C# `ReadHead`: the first [`SNIFF_BYTES`] of a file, read from its start.
pub fn read_head<R: Read + Seek>(stream: &mut R) -> std::io::Result<Vec<u8>> {
    read_prefix(stream, SNIFF_BYTES)
}

/// C# `ReadPrefix`: at most `count` bytes from the start.
pub fn read_prefix<R: Read + Seek>(stream: &mut R, count: usize) -> std::io::Result<Vec<u8>> {
    stream.seek(SeekFrom::Start(0))?;
    let mut data = Vec::new();
    stream.take(count as u64).read_to_end(&mut data)?;
    Ok(data)
}

/// C# `PixelCount`: width × height, or None when either is not positive or the product overflows.
pub fn pixel_count(width: i64, height: i64) -> Option<i64> {
    (width > 0 && height > 0).then(|| width.checked_mul(height)).flatten()
}
