//! The files sent with a request (C# `AttachmentSupport`, AttachmentSupport.cs:19-78): made from bytes, typed by sniffing, checked
//! before a send (8 files, 5 MiB each, 8 MiB in all, canonical base64), the transcript's summary and the staged copy's extension.
//! Staging the copies on disk (C# `StagedAttachments`) is the run engine's (W2); it calls [`validate`], [`decode`] and
//! [`extension`] from here. Errors are the C# `ArgumentException` messages, in the language given.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use mighty_core::attachment::RunAttachment;
use mighty_core::locale::Locale;
use mighty_core::wire;

use crate::text::utf16_len;

pub const MAXIMUM_COUNT: usize = 8;
pub const MAXIMUM_FILE_BYTES: usize = 5 * 1024 * 1024;
pub const MAXIMUM_TOTAL_BYTES: usize = 8 * 1024 * 1024;
pub const MAXIMUM_REQUEST_BYTES: usize = 12 * 1024 * 1024;
const MEDIA_TYPES: [&str; 7] =
    ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain", "application/octet-stream"];
/// C# `PaneTitle.AttachmentMarker`: "첨부: ".
pub const ATTACHMENT_MARKER: &str = "\u{CCA8}\u{BD80}: ";

/// C# `char.IsControl` (Cc).
pub(crate) fn is_control(c: char) -> bool {
    matches!(c as u32, 0x00..=0x1f | 0x7f..=0x9f)
}

/// C# `AttachmentSupport.Make` (AttachmentSupport.cs:28): the file name's last part without control characters, at most 180
/// UTF-16 units (never half a surrogate pair), "attachment" when nothing is left; the type from the bytes.
pub fn make(name: &str, data: &[u8], l: &Locale) -> Result<RunAttachment, String> {
    if data.len() > MAXIMUM_FILE_BYTES {
        return Err(l.get("attachments.error.fileTooLarge"));
    }
    let replaced = name.replace('\\', "/");
    let last = replaced.split('/').next_back().unwrap_or("attachment");
    let kept: String = last.chars().filter(|c| !is_control(*c)).collect();
    let mut basename = crate::slash::dotnet_trim(&kept).to_string();
    if utf16_len(&basename) > 180 {
        // A pair cut in two loses its high half too: C# drops the lone surrogate.
        let mut units = 0;
        basename = basename
            .chars()
            .take_while(|c| {
                units += c.len_utf16();
                units <= 180
            })
            .collect();
    }
    if matches!(basename.as_str(), "" | "." | "..") {
        basename = "attachment".into();
    }
    Ok(RunAttachment { id: wire::new_id(), name: basename, media_type: detect_media_type(data).into(), data_base64: STANDARD.encode(data) })
}

/// C# `AttachmentSupport.DetectMediaType` (AttachmentSupport.cs:37).
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
    if let Ok(text) = std::str::from_utf8(data)
        && !text.chars().any(|c| is_control(c) && !matches!(c, '\t' | '\r' | '\n'))
    {
        return "text/plain";
    }
    "application/octet-stream"
}

/// C# `AttachmentSupport.Decode` (AttachmentSupport.cs:47): the bytes of a well-formed attachment whose type its bytes confirm.
pub fn decode(attachment: &RunAttachment, l: &Locale) -> Result<Vec<u8>, String> {
    let invalid = || l.get("attachments.error.invalid");
    let name = &attachment.name;
    let name_ok = (1..=180).contains(&utf16_len(name))
        && name != "."
        && name != ".."
        && !name.chars().any(|c| c == '/' || c == '\\' || is_control(c));
    if !wire::identifier(&attachment.id)
        || !name_ok
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
        return Err(l.get("attachments.error.typeMismatch"));
    }
    Ok(bytes)
}

/// C# `AttachmentSupport.Validate` (AttachmentSupport.cs:56): None for no attachments.
pub fn validate(attachments: &[RunAttachment], l: &Locale) -> Result<Option<Vec<RunAttachment>>, String> {
    if attachments.is_empty() {
        return Ok(None);
    }
    if attachments.len() > MAXIMUM_COUNT {
        return Err(l.get("attachments.error.tooMany"));
    }
    let mut ids = std::collections::HashSet::new();
    let mut total = 0usize;
    for attachment in attachments {
        total += decode(attachment, l)?.len();
        if !ids.insert(attachment.id.as_str()) {
            return Err(l.get("attachments.error.tooMany"));
        }
        if total > MAXIMUM_TOTAL_BYTES {
            return Err(l.get("attachments.error.totalTooLarge"));
        }
    }
    Ok(Some(attachments.to_vec()))
}

/// C# `AttachmentSupport.DecodedLength`.
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

/// `{value:N0}` as the app's cultures write it: digits grouped by three with commas.
pub(crate) fn grouped(value: u64) -> String {
    let digits = value.to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    out
}

/// C# `AttachmentSupport.Summary` (AttachmentSupport.cs:69): the line a request's attachments leave in the transcript.
pub fn summary(attachments: &[RunAttachment]) -> String {
    if attachments.is_empty() {
        return String::new();
    }
    let parts: Vec<String> =
        attachments.iter().map(|a| format!("{} ({} bytes)", wire::clean(&a.name, 180), grouped(decoded_length(a) as u64))).collect();
    format!("{ATTACHMENT_MARKER}{}", parts.join(", "))
}

/// C# `AttachmentSupport.Extension` (AttachmentSupport.cs:71): the staged copy's extension.
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
        Some(dot) if dot + 1 < attachment.name.len() => crate::slash::lower_invariant(&attachment.name[dot..]),
        _ => String::new(),
    };
    let units = utf16_len(&suffix);
    if (2..=13).contains(&units) && suffix[1..].chars().all(|c| c.is_ascii_alphanumeric()) {
        return suffix;
    }
    if attachment.media_type == "text/plain" { ".txt".into() } else { ".bin".into() }
}
