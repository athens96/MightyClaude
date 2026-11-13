//! One file read for the preview, off the page's thread (C# `FilePreviewLoader.Load`, `FilePreviewData`; WorkspaceFiles.cs:986-1075):
//! classified from its first bytes; text read up to 1 MB in the encoding found; Markdown over 128 KB marked as source only; pictures
//! and SVG kept as bytes under 50 MB, an SVG refused when it can load outside content.

use std::path::Path;
use std::time::SystemTime;

use mighty_core::locale::Locale;
use mighty_platform::FileHandles;

use crate::classify::{self, PreviewKind, TextEncodingKind};
use crate::{OpenError, open_file};

/// C# `FilePreviewData`.
#[derive(Debug, Clone, PartialEq)]
pub struct PreviewData {
    pub name: String,
    pub relative_path: String,
    pub size: u64,
    pub modified: Option<SystemTime>,
    pub kind: PreviewKind,
    pub text: Option<String>,
    pub encoding: Option<TextEncodingKind>,
    pub truncated: bool,
    pub markdown_renderable: bool,
    pub image_bytes: Option<Vec<u8>>,
    pub reason: Option<String>,
    /// The preview could not be read at all: the "missing" or "failed" message.
    pub failure: Option<String>,
}

impl PreviewData {
    fn new(name: &str, relative_path: &str, size: u64, modified: Option<SystemTime>, kind: PreviewKind) -> PreviewData {
        PreviewData {
            name: name.into(),
            relative_path: relative_path.into(),
            size,
            modified,
            kind,
            text: None,
            encoding: None,
            truncated: false,
            markdown_renderable: false,
            image_bytes: None,
            reason: None,
            failure: None,
        }
    }
}

/// C# `FilePreviewLoader.Load`.
pub fn load(root: &Path, relative: &str, handles: &dyn FileHandles, locale: &Locale) -> PreviewData {
    let name = relative.rsplit('/').next().unwrap_or("").to_string();
    let mut opened = match open_file(relative, root, handles) {
        Ok(opened) => opened,
        Err(error) => {
            let mut data = PreviewData::new(&name, relative, 0, None, PreviewKind::Unsupported);
            data.failure = match error {
                OpenError::Missing => Some(locale.get("files.preview.missing")),
                OpenError::NotRegularFile => None,
                OpenError::Unreadable => Some(locale.get("files.preview.failed")),
            };
            return data;
        }
    };
    let (size, modified) = (opened.size, opened.modified);
    match read(&mut opened.file, &name, relative, size, modified, locale) {
        Ok(data) => data,
        Err(_) => PreviewData {
            failure: Some(locale.get("files.preview.failed")),
            ..PreviewData::new(&name, relative, size, modified, PreviewKind::Unsupported)
        },
    }
}

/// The opened file classified and read.
fn read(
    file: &mut std::fs::File,
    name: &str,
    relative: &str,
    size: u64,
    modified: Option<SystemTime>,
    locale: &Locale,
) -> std::io::Result<PreviewData> {
    let kind = classify::classify(name, &classify::read_head(file)?);
    Ok(match kind {
        PreviewKind::Image => {
            if size > classify::MAXIMUM_IMAGE_BYTES {
                let mut data = PreviewData::new(name, relative, size, modified, PreviewKind::Unsupported);
                data.reason = Some(locale.get("files.preview.tooLargeImage"));
                return Ok(data);
            }
            let bytes = classify::read_prefix(file, classify::MAXIMUM_IMAGE_BYTES as usize)?;
            if classify::file_extension(name) == "svg" && classify::svg_loads_external_content(&bytes) {
                let mut data = PreviewData::new(name, relative, size, modified, PreviewKind::Unsupported);
                data.reason = Some(locale.get("files.preview.svgExternal"));
                return Ok(data);
            }
            PreviewData { image_bytes: Some(bytes), ..PreviewData::new(name, relative, size, modified, kind) }
        }
        PreviewKind::Source(_) | PreviewKind::Markdown => {
            let text = classify::read_text(file, classify::MAXIMUM_TEXT_BYTES)?;
            PreviewData {
                text: Some(text.text),
                encoding: Some(text.encoding),
                truncated: text.truncated,
                markdown_renderable: kind == PreviewKind::Markdown && size <= classify::MAXIMUM_MARKDOWN_RENDER_BYTES,
                ..PreviewData::new(name, relative, size, modified, kind)
            }
        }
        PreviewKind::Unsupported => PreviewData::new(name, relative, size, modified, PreviewKind::Unsupported),
    })
}
