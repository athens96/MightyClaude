//! C# `AttachmentSupport.cs`: files a request carries (`RunAttachment`), their checks, and their copies for one run
//! (`StagedAttachments`): at most 8 files, 5 MiB each, 8 MiB together.
//!
//! Deviation: C# narrows the staging folder's permissions to the user alone (a protected Windows ACL, `chmod 700` elsewhere); here
//! it keeps the temporary folder's own, which on both systems is already the user's (`%LOCALAPPDATA%\Temp`, the Mac's per-user
//! `$TMPDIR`). A port for private folders can take this over.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use mighty_core::wire;
use serde_json::{Value, json};

use crate::model::StartRunRequest;
use crate::permissions::ProviderInput;
use crate::providers;
use crate::strings;
use crate::text_json;
use crate::{Result, RunError};

pub const MAXIMUM_COUNT: usize = 8;
pub const MAXIMUM_FILE_BYTES: usize = 5 * 1024 * 1024;
pub const MAXIMUM_TOTAL_BYTES: usize = 8 * 1024 * 1024;
pub const MAXIMUM_REQUEST_BYTES: usize = 12 * 1024 * 1024;
const MEDIA_TYPES: [&str; 7] =
    ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain", "application/octet-stream"];
/// `PaneTitle.AttachmentMarker`: "첨부: ", stored log data in every language.
pub const ATTACHMENT_MARKER: &str = "\u{CCA8}\u{BD80}: ";

/// One file a request carries (C# `RunAttachment(Id, Name, MediaType, DataBase64)`): one definition in mighty-core, which the composer
/// (mighty-composer) reads and checks and the run engine stages.
pub use mighty_core::attachment::RunAttachment;

/// A picture (`RunAttachment.IsImage`).
fn is_image(attachment: &RunAttachment) -> bool {
    attachment.media_type.starts_with("image/")
}

fn is_control_char(c: char) -> bool {
    c.is_control()
}

/// `AttachmentSupport.Make`: a picked file as an attachment, its name cut to 180 UTF-16 units and its type sniffed.
pub fn make(name: &str, data: &[u8]) -> Result<RunAttachment> {
    if data.len() > MAXIMUM_FILE_BYTES {
        return Err(RunError::Invalid(strings::get("attachments.error.fileTooLarge")));
    }
    let replaced = name.replace('\\', "/");
    let base = replaced.split('/').next_back().unwrap_or("attachment");
    let mut basename: String = base.chars().filter(|c| !is_control_char(*c)).collect::<String>().trim().to_string();
    if basename.encode_utf16().count() > 180 {
        let mut cut = String::new();
        let mut units = 0;
        for c in basename.chars() {
            if units + c.len_utf16() > 180 {
                break;
            }
            units += c.len_utf16();
            cut.push(c);
        }
        basename = cut;
    }
    if matches!(basename.as_str(), "" | "." | "..") {
        basename = "attachment".into();
    }
    Ok(RunAttachment { id: wire::new_id(), name: basename, media_type: detect_media_type(data).into(), data_base64: STANDARD.encode(data) })
}

/// `AttachmentSupport.DetectMediaType`: the file's own signature, else strict UTF-8 text without control characters, else bytes.
pub fn detect_media_type(data: &[u8]) -> &'static str {
    if data.starts_with(&[137, 80, 78, 71, 13, 10, 26, 10]) {
        return "image/png";
    }
    if data.starts_with(&[255, 216, 255]) {
        return "image/jpeg";
    }
    if data.starts_with(b"GIF87a") || data.starts_with(b"GIF89a") {
        return "image/gif";
    }
    if data.len() >= 12 && &data[..4] == b"RIFF" && &data[8..12] == b"WEBP" {
        return "image/webp";
    }
    if data.starts_with(b"%PDF-") {
        return "application/pdf";
    }
    // A leading BOM decodes to U+FEFF, which is no control character (UTF8Encoding(false, true) keeps it).
    if let Ok(text) = std::str::from_utf8(data)
        && !text.chars().any(|c| is_control_char(c) && !matches!(c, '\t' | '\r' | '\n'))
    {
        return "text/plain";
    }
    "application/octet-stream"
}

/// `AttachmentSupport.Decode`: the attachment's bytes, after every check of its id, name, type and base64.
pub fn decode(attachment: &RunAttachment) -> Result<Vec<u8>> {
    let invalid = || RunError::Invalid(strings::get("attachments.error.invalid"));
    let name_length = attachment.name.encode_utf16().count();
    if !wire::identifier(&attachment.id)
        || !(1..=180).contains(&name_length)
        || matches!(attachment.name.as_str(), "." | "..")
        || attachment.name.chars().any(|c| c == '/' || c == '\\' || is_control_char(c))
        || !MEDIA_TYPES.contains(&attachment.media_type.as_str())
        || attachment.data_base64.len() > MAXIMUM_FILE_BYTES.div_ceil(3) * 4
    {
        return Err(invalid());
    }
    let bytes = STANDARD.decode(&attachment.data_base64).map_err(|_| invalid())?;
    if bytes.len() > MAXIMUM_FILE_BYTES || STANDARD.encode(&bytes) != attachment.data_base64 {
        return Err(invalid());
    }
    if detect_media_type(&bytes) != attachment.media_type {
        return Err(RunError::Invalid(strings::get("attachments.error.typeMismatch")));
    }
    Ok(bytes)
}

/// `AttachmentSupport.Validate`: None for none; at most 8, each decoding, ids distinct, 8 MiB together.
pub fn validate(attachments: Option<&[RunAttachment]>) -> Result<Option<Vec<RunAttachment>>> {
    let Some(attachments) = attachments.filter(|a| !a.is_empty()) else { return Ok(None) };
    if attachments.len() > MAXIMUM_COUNT {
        return Err(RunError::Invalid(strings::get("attachments.error.tooMany")));
    }
    let mut ids = HashSet::new();
    let mut total = 0usize;
    for attachment in attachments {
        total += decode(attachment)?.len();
        if !ids.insert(attachment.id.clone()) {
            return Err(RunError::Invalid(strings::get("attachments.error.tooMany")));
        }
        if total > MAXIMUM_TOTAL_BYTES {
            return Err(RunError::Invalid(strings::get("attachments.error.totalTooLarge")));
        }
    }
    Ok(Some(attachments.to_vec()))
}

/// `AttachmentSupport.DecodedLength`.
pub fn decoded_length(attachment: &RunAttachment) -> usize {
    let data = &attachment.data_base64;
    let padding = if data.ends_with("==") {
        2
    } else if data.ends_with('=') {
        1
    } else {
        0
    };
    (data.len() / 4 * 3).saturating_sub(padding)
}

/// `AttachmentSupport.Summary`: the transcript's "첨부: name (1,234 bytes), …" line, the size as `{:N0}` writes it in the current
/// culture (the user's regional format: 1.234 in German, 1 234 in French).
pub fn summary(attachments: Option<&[RunAttachment]>) -> String {
    match attachments.filter(|a| !a.is_empty()) {
        None => String::new(),
        Some(list) => format!(
            "{ATTACHMENT_MARKER}{}",
            list.iter()
                .map(|a| format!("{} ({} bytes)", wire::clean(&a.name, 180), strings::n0(decoded_length(a) as i64)))
                .collect::<Vec<_>>()
                .join(", ")
        ),
    }
}

/// `AttachmentSupport.Extension`: the copy's extension.
pub fn extension(attachment: &RunAttachment) -> String {
    let known = match attachment.media_type.as_str() {
        "image/png" => Some(".png"),
        "image/jpeg" => Some(".jpg"),
        "image/gif" => Some(".gif"),
        "image/webp" => Some(".webp"),
        "application/pdf" => Some(".pdf"),
        _ => None,
    };
    if let Some(known) = known {
        return known.into();
    }
    let suffix = match attachment.name.rfind('.') {
        Some(at) if at + 1 < attachment.name.len() => attachment.name[at..].to_lowercase(),
        _ => String::new(),
    };
    let length = suffix.encode_utf16().count();
    if (2..=13).contains(&length) && suffix[1..].chars().all(|c| c.is_ascii_alphanumeric()) {
        return suffix;
    }
    if attachment.media_type == "text/plain" { ".txt".into() } else { ".bin".into() }
}

/// C# `StagedAttachments`: a request's files copied for its run into a folder of their own, removed when the run ends.
#[derive(Debug)]
pub struct StagedAttachments {
    pub directory: PathBuf,
    pub files: Vec<(RunAttachment, PathBuf)>,
}

fn random_hex(bytes: usize) -> String {
    let mut buffer = vec![0u8; bytes];
    if getrandom::fill(&mut buffer).is_err() {
        return wire::new_id().replace('-', "");
    }
    buffer.iter().map(|b| format!("{b:02x}")).collect()
}

impl StagedAttachments {
    /// `StagedAttachments.CreateAsync`: each file written new (`CreateNew`) as `<index>-<guid><extension>` in a new temporary folder
    /// `mighty-attachments-…`; a failure removes the folder.
    pub fn create(attachments: &[RunAttachment]) -> Result<StagedAttachments> {
        let validated = validate(Some(attachments))?.ok_or_else(|| RunError::Invalid(strings::get("attachments.error.none")))?;
        let directory = std::env::temp_dir().join(format!("mighty-attachments-{}", random_hex(6)));
        std::fs::create_dir(&directory).map_err(|e| RunError::Io(e.to_string()))?;
        let staged = (|| -> Result<Vec<(RunAttachment, PathBuf)>> {
            let mut files = Vec::new();
            for (index, attachment) in validated.iter().enumerate() {
                let path = directory.join(format!("{index}-{}{}", random_hex(16), extension(attachment)));
                let bytes = decode(attachment)?;
                let mut file =
                    std::fs::OpenOptions::new().write(true).create_new(true).open(&path).map_err(|e| RunError::Io(e.to_string()))?;
                std::io::Write::write_all(&mut file, &bytes).map_err(|e| RunError::Io(e.to_string()))?;
                files.push((attachment.clone(), path));
            }
            Ok(files)
        })();
        match staged {
            Ok(files) => Ok(StagedAttachments { directory, files }),
            Err(error) => {
                let _ = std::fs::remove_dir_all(&directory);
                Err(error)
            }
        }
    }

    pub fn directory_text(&self) -> String {
        self.directory.to_string_lossy().into_owned()
    }

    /// `StagedAttachments.InputFor`: the prompt with the copies named (Antigravity and pi by their plain path), pi's startup with its
    /// pictures, or Claude's stream-json user message with the pictures and PDFs inline.
    pub fn input_for(&self, request: &StartRunRequest) -> String {
        let mut prompt = if request.input.trim().is_empty() { strings::get("attachments.prompt.default") } else { request.input.clone() };
        let provider = request.provider.as_str();
        let generic: Vec<&(RunAttachment, PathBuf)> =
            self.files
                .iter()
                .filter(|(a, _)| {
                    if provider == "pi" { !is_image(a) } else { provider != "claude" || !is_image(a) && a.media_type != "application/pdf" }
                })
                .collect();
        if !generic.is_empty() {
            let references: Vec<String> = generic
                .iter()
                .map(|(a, path)| {
                    let path = path.to_string_lossy();
                    let named = if matches!(provider, "gemini" | "pi") { format!("\"{path}\"") } else { mighty_core::json::escape(&path) };
                    format!("{}: {named}", mighty_core::json::escape(&a.name))
                })
                .collect();
            prompt.push_str("\n\nAttached files (copies provided for this run only):\n");
            prompt.push_str(&references.join("\n"));
        }
        if provider == "pi" {
            let pictures: Vec<(String, String)> =
                self.files.iter().filter(|(a, _)| is_image(a)).map(|(a, _)| (a.media_type.clone(), a.data_base64.clone())).collect();
            return ProviderInput::pi_startup(&prompt, &pictures);
        }
        if provider != "claude" {
            return prompt;
        }
        let mut content = vec![json!({"type": "text", "text": prompt})];
        for (attachment, _) in self.files.iter().filter(|(a, _)| is_image(a) || a.media_type == "application/pdf") {
            let kind = if attachment.media_type == "application/pdf" { "document" } else { "image" };
            content.push(
                json!({"type": kind, "source": {"type": "base64", "media_type": attachment.media_type, "data": attachment.data_base64}}),
            );
        }
        let frame = json!({"type": "user", "message": {"role": "user", "content": content}, "parent_tool_use_id": Value::Null});
        format!("{}\n", text_json::relaxed(&frame, false))
    }

    /// `StagedAttachments.ArgumentsFor`: the run's arguments with the copies' folder (or Codex's `--image` per picture).
    pub fn arguments_for(&self, request: &StartRunRequest, plugin_directory: &str) -> Result<Vec<String>> {
        let mut args = providers::arguments(request, plugin_directory, None)?;
        match request.provider.as_str() {
            "claude" => {
                args.extend(["--input-format".to_string(), "stream-json".to_string()]);
                if self.files.iter().any(|(a, _)| !is_image(a) && a.media_type != "application/pdf") {
                    args.extend(["--add-dir".to_string(), self.directory_text()]);
                }
            }
            "codex" => {
                for (_, path) in self.files.iter().filter(|(a, _)| is_image(a)) {
                    let at = args.len().saturating_sub(1);
                    args.splice(at..at, ["--image".to_string(), path.to_string_lossy().into_owned()]);
                }
            }
            "pi" => {}
            _ => args.extend(["--add-dir".to_string(), self.directory_text()]),
        }
        Ok(args)
    }

    /// `StagedAttachments.DisposeAsync`: the folder and the copies in it.
    pub fn dispose(&self) -> std::io::Result<()> {
        if self.directory.exists() { std::fs::remove_dir_all(&self.directory) } else { Ok(()) }
    }

    pub fn path(&self) -> &Path {
        &self.directory
    }
}
