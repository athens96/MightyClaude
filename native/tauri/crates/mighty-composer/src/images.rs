//! Pictures agents show (C# `AgentImages.cs`): the caps and media types, what the bytes really are (header sniffing, TIFF
//! included), base64 and the payload walk, the entry and failure texts, the content-addressed cache keyed by SHA-256, the path
//! rule for pictures named in agent text, Markdown pictures, and the transcript's RTF picture.

use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

use base64::Engine as _;
use base64::engine::{GeneralPurpose, GeneralPurposeConfig};
use mighty_core::locale::Locale;
use mighty_core::model::AgentImageRef;
use mighty_core::normalize;
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::files;
use crate::svg;

/// Larger decoded pictures are refused, never written.
pub const MAXIMUM_IMAGE_BYTES: usize = 20 * 1_048_576;
/// The cache on disk; least recently used pictures go first above it.
pub const MAXIMUM_CACHE_BYTES: u64 = 200 * 1_048_576;
pub const MAXIMUM_IMAGES_PER_RUN: usize = 64;
pub const MAXIMUM_IMAGES_PER_ENTRY: usize = 16;
pub const THUMBNAIL_PIXELS: u32 = 960;
pub const MAXIMUM_PIXELS: i64 = 64_000_000;
pub const MAXIMUM_SIDE: i64 = 8_000;
const MAXIMUM_SOURCE_BYTES: usize = 240;

/// C# `AgentImageError`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AgentImageError {
    Empty,
    TooLarge,
    UnsupportedType,
    Undecodable,
    ExternalSvg,
    TooManyPixels,
    InvalidEncoding,
}

impl std::fmt::Display for AgentImageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for AgentImageError {}

/// C# `AgentImageSupport.Extensions`: the formats kept, each with the extension it is stored under.
pub const EXTENSIONS: [(&str, &str); 9] = [
    ("image/png", "png"),
    ("image/jpeg", "jpg"),
    ("image/gif", "gif"),
    ("image/webp", "webp"),
    ("image/heic", "heic"),
    ("image/heif", "heif"),
    ("image/tiff", "tiff"),
    ("image/bmp", "bmp"),
    ("image/svg+xml", "svg"),
];

fn extension_of(media_type: &str) -> Option<&'static str> {
    EXTENSIONS.iter().find(|(t, _)| *t == media_type).map(|(_, e)| *e)
}

/// C# `AgentImageSupport.MediaType`: a recognised picture media type.
pub fn media_type(value: Option<&str>) -> Option<&'static str> {
    normalize::media_type(value)
}

/// C# `FilePreviewClassifier.FileExtension`: the lower-cased extension; a leading dot (".env") is not one.
pub fn file_extension(name: &str) -> String {
    match name.rfind('.') {
        Some(dot) if dot > 0 => crate::slash::lower_invariant(&name[dot + 1..]),
        _ => String::new(),
    }
}

/// C# `AgentImageSupport.MediaTypeForFileName`.
pub fn media_type_for_file_name(name: &str) -> Option<&'static str> {
    let base = Path::new(name).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    match file_extension(&base).as_str() {
        "jpeg" => Some("image/jpeg"),
        "tif" => Some("image/tiff"),
        ext => EXTENSIONS.iter().find(|(_, e)| *e == ext).map(|(t, _)| *t),
    }
}

/// C# `AgentImageSupport.Sha256`: lower-case hex.
pub fn sha256(data: &[u8]) -> String {
    Sha256::digest(data).iter().map(|b| format!("{b:02x}")).collect()
}

/// C# `FilePreviewClassifier.PixelCount`: width × height, None when either is not positive or the product overflows.
pub fn pixel_count(width: i64, height: i64) -> Option<i64> {
    (width > 0 && height > 0).then(|| width.checked_mul(height)).flatten()
}

/// C# `AgentImageSupport.Inspect` (AgentImages.cs:85): a bitmap within the pixel and side caps, or an svg that references nothing
/// outside itself. The declared type only chooses between the two; a bitmap is stored as what its header says it is.
pub fn inspect(data: &[u8], declared: &str) -> Result<(&'static str, i32, i32), AgentImageError> {
    inspect_with(data, declared, MAXIMUM_PIXELS, MAXIMUM_SIDE)
}

pub fn inspect_with(
    data: &[u8],
    declared: &str,
    maximum_pixels: i64,
    maximum_side: i64,
) -> Result<(&'static str, i32, i32), AgentImageError> {
    if data.is_empty() {
        return Err(AgentImageError::Empty);
    }
    if data.len() > MAXIMUM_IMAGE_BYTES {
        return Err(AgentImageError::TooLarge);
    }
    let kind = media_type(Some(declared)).ok_or(AgentImageError::UnsupportedType)?;
    if kind == "image/svg+xml" {
        if !svg::svg_document(data) {
            return Err(AgentImageError::Undecodable);
        }
        if svg::svg_loads_external_content(data) {
            return Err(AgentImageError::ExternalSvg);
        }
        return Ok(("image/svg+xml", 0, 0));
    }
    let (actual, width, height) = bitmap_header(data);
    let actual = actual.ok_or(AgentImageError::Undecodable)?;
    if actual.is_empty() {
        return Err(AgentImageError::UnsupportedType);
    }
    let pixels = pixel_count(width, height).ok_or(AgentImageError::Undecodable)?;
    if pixels > maximum_pixels || width.max(height) > maximum_side {
        return Err(AgentImageError::TooManyPixels);
    }
    Ok((actual, width as i32, height as i32))
}

fn u16_le(d: &[u8], at: usize) -> i64 {
    u16::from_le_bytes([d[at], d[at + 1]]) as i64
}
fn u16_be(d: &[u8], at: usize) -> i64 {
    u16::from_be_bytes([d[at], d[at + 1]]) as i64
}
fn u32_le(d: &[u8], at: usize) -> u32 {
    u32::from_le_bytes([d[at], d[at + 1], d[at + 2], d[at + 3]])
}
fn u32_be(d: &[u8], at: usize) -> u32 {
    u32::from_be_bytes([d[at], d[at + 1], d[at + 2], d[at + 3]])
}

/// C# `AgentImageSupport.BitmapHeader` (AgentImages.cs:108): the type and size a bitmap's header declares. None: not a picture
/// this reads; `Some("")`: a format Windows does not decode without an extra codec (heic).
pub fn bitmap_header(d: &[u8]) -> (Option<&'static str>, i64, i64) {
    const NONE: (Option<&str>, i64, i64) = (None, 0, 0);
    if d.len() >= 24 && d[..8] == [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] && &d[12..16] == b"IHDR" {
        return (Some("image/png"), u32_be(d, 16) as i64, u32_be(d, 20) as i64);
    }
    if d.len() >= 10 && (&d[..6] == b"GIF87a" || &d[..6] == b"GIF89a") {
        return (Some("image/gif"), u16_le(d, 6), u16_le(d, 8));
    }
    if d.len() >= 26 && d[0] == b'B' && d[1] == b'M' {
        let header = u32_le(d, 14);
        if header == 12 {
            return (Some("image/bmp"), u16_le(d, 18), u16_le(d, 20));
        }
        if header >= 40 {
            let w = i32::from_le_bytes([d[18], d[19], d[20], d[21]]) as i64;
            let h = i32::from_le_bytes([d[22], d[23], d[24], d[25]]) as i64;
            return (Some("image/bmp"), w.abs(), h.abs());
        }
        return NONE;
    }
    if d.len() >= 30 && &d[..4] == b"RIFF" && &d[8..12] == b"WEBP" {
        let chunk = &d[12..16];
        if chunk == b"VP8 " && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A {
            return (Some("image/webp"), u16_le(d, 26) & 0x3FFF, u16_le(d, 28) & 0x3FFF);
        }
        if chunk == b"VP8L" && d[20] == 0x2F {
            let bits = u32_le(d, 21) as i64;
            return (Some("image/webp"), (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }
        if chunk == b"VP8X" {
            let w = d[24] as i64 | (d[25] as i64) << 8 | (d[26] as i64) << 16;
            let h = d[27] as i64 | (d[28] as i64) << 8 | (d[29] as i64) << 16;
            return (Some("image/webp"), w + 1, h + 1);
        }
        return NONE;
    }
    if d.len() >= 4 && d[0] == 0xFF && d[1] == 0xD8 {
        let mut at = 2;
        while at + 4 <= d.len() {
            if d[at] != 0xFF {
                return NONE;
            }
            let marker = d[at + 1];
            if marker == 0xFF {
                at += 1;
                continue;
            }
            if marker == 0x01 || (0xD0..=0xD7).contains(&marker) {
                at += 2;
                continue;
            }
            let length = u16_be(d, at + 2) as usize;
            if length < 2 {
                return NONE;
            }
            if (0xC0..=0xCF).contains(&marker) && !matches!(marker, 0xC4 | 0xC8 | 0xCC) {
                return if at + 9 <= d.len() { (Some("image/jpeg"), u16_be(d, at + 7), u16_be(d, at + 5)) } else { NONE };
            }
            if matches!(marker, 0xD9 | 0xDA) {
                return NONE;
            }
            at += 2 + length;
        }
        return NONE;
    }
    if d.len() >= 4 && (d[..4] == [0x49, 0x49, 0x2A, 0x00] || d[..4] == [0x4D, 0x4D, 0x00, 0x2A]) {
        // TIFF 6.0: only the first image directory and its scalar size tags, every offset checked before it is read.
        if d.len() < 8 {
            return NONE;
        }
        let little = d[0] == 0x49;
        let u16_at = |at: usize| if little { u16_le(d, at) } else { u16_be(d, at) };
        let u32_at = |at: usize| if little { u32_le(d, at) } else { u32_be(d, at) };
        let offset = u32_at(4) as i64;
        if offset < 8 || offset > d.len() as i64 - 2 {
            return NONE;
        }
        let offset = offset as usize;
        let count = u16_at(offset);
        if count > 4096 || offset as i64 + 2 + count * 12 > d.len() as i64 {
            return NONE;
        }
        let (mut width, mut height) = (0i64, 0i64);
        for i in 0..count as usize {
            let entry = offset + 2 + i * 12;
            let tag = u16_at(entry);
            if tag != 256 && tag != 257 {
                continue;
            }
            let kind = u16_at(entry + 2);
            if u32_at(entry + 4) != 1 || !(kind == 3 || kind == 4) {
                return NONE;
            }
            let value = if kind == 3 { u16_at(entry + 8) } else { u32_at(entry + 8) as i64 };
            if tag == 256 {
                width = value;
            } else {
                height = value;
            }
        }
        return (Some("image/tiff"), width, height);
    }
    if d.len() >= 12 && &d[4..8] == b"ftyp" && [b"heic", b"heix", b"mif1", b"msf1"].iter().any(|brand| &d[8..12] == *brand) {
        return (Some(""), 0, 0);
    }
    NONE
}

/// C# `AgentImageSupport.Normalized(AgentImageRef)`: a saved or received record, or None when any field is out of shape.
pub fn normalized(value: &AgentImageRef) -> Option<AgentImageRef> {
    normalize::image(value)
}

/// C# `AgentImageSupport.EntryText`: the text an image entry carries.
pub fn entry_text(count: usize, source: &str, l: &Locale) -> String {
    let label = normalize::activity_clean(Some(source), MAXIMUM_SOURCE_BYTES, true);
    if count == 1 {
        l.get_with("images.entry.one", &[("source", &label)])
    } else {
        l.get_with("images.entry.many", &[("count", &count.to_string()), ("source", &label)])
    }
}

/// C# `AgentImageSupport.FailureText`: the system line a refused picture leaves.
pub fn failure_text(error: AgentImageError, l: &Locale) -> String {
    match error {
        AgentImageError::TooLarge => l.get_with("images.failed.tooLarge", &[("limit", &(MAXIMUM_IMAGE_BYTES / 1_048_576).to_string())]),
        AgentImageError::ExternalSvg => l.get("images.failed.externalSVG"),
        AgentImageError::TooManyPixels => l.get("images.failed.tooManyPixels"),
        AgentImageError::UnsupportedType => l.get("images.failed.unsupported"),
        _ => l.get("images.failed.undecodable"),
    }
}

fn text_of<'a>(value: &'a Value, name: &str) -> Option<&'a str> {
    value.as_object()?.get(name)?.as_str()
}

/// C# `AgentImageSupport.Payloads` (AgentImages.cs:248): base64 picture payloads in a tool result or message content, in order.
pub fn payloads(value: &Value) -> Vec<(String, String)> {
    payloads_at(value, 0)
}

fn payloads_at(value: &Value, depth: usize) -> Vec<(String, String)> {
    let mut result = Vec::new();
    if depth >= 4 {
        return result;
    }
    match value {
        Value::Object(map) => {
            if text_of(value, "type") == Some("image") {
                let source = map.get("source").unwrap_or(&Value::Null);
                if let (Some("base64"), Some(data), Some(kind)) =
                    (text_of(source, "type"), text_of(source, "data"), text_of(source, "media_type"))
                {
                    result.push((kind.to_string(), data.to_string()));
                } else if let Some(inline) = text_of(value, "data")
                    && let Some(kind) =
                        text_of(value, "mimeType").or_else(|| text_of(value, "mime_type")).or_else(|| text_of(value, "media_type"))
                {
                    result.push((kind.to_string(), inline.to_string()));
                }
                return result;
            }
            payloads_at(map.get("content").unwrap_or(&Value::Null), depth + 1)
        }
        Value::Array(items) => {
            for block in items.iter().take(64) {
                result.extend(payloads_at(block, depth + 1));
            }
            result
        }
        _ => result,
    }
}

/// C# `AgentImageSupport.DecodeBase64` (AgentImages.cs:269): strict base64 (white space allowed), decoded within the cap.
pub fn decode_base64(text: &str) -> Result<Vec<u8>, AgentImageError> {
    if text.encode_utf16().count() > (MAXIMUM_IMAGE_BYTES / 3 + 2) * 4 + 4_096 {
        return Err(AgentImageError::TooLarge);
    }
    let compact: String = text.chars().filter(|c| !c.is_whitespace()).collect();
    // Convert.TryFromBase64String: padding required, the unused bits of the last character ignored.
    let engine = GeneralPurpose::new(&base64::alphabet::STANDARD, GeneralPurposeConfig::new().with_decode_allow_trailing_bits(true));
    let bytes = engine.decode(compact.as_bytes()).map_err(|_| AgentImageError::InvalidEncoding)?;
    if bytes.is_empty() {
        return Err(AgentImageError::InvalidEncoding);
    }
    if bytes.len() > MAXIMUM_IMAGE_BYTES {
        return Err(AgentImageError::TooLarge);
    }
    Ok(bytes)
}

/// C# `AgentImagePrepared(Hash, MediaType, Width, Height, Bytes)`: a picture checked and written to the cache.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentImagePrepared {
    pub hash: String,
    pub media_type: String,
    pub width: i32,
    pub height: i32,
    pub bytes: i32,
}

impl AgentImagePrepared {
    /// C# `AgentImagePrepared.Ref`.
    pub fn reference(&self, source: &str, path: Option<&str>) -> AgentImageRef {
        let value = AgentImageRef {
            hash: Some(self.hash.clone()),
            media_type: Some(self.media_type.clone()),
            width: self.width,
            height: self.height,
            bytes: self.bytes,
            source: Some(source.into()),
            path: path.map(str::to_string),
        };
        normalized(&value).unwrap_or(value)
    }
}

/// Why the cache could not take a picture.
#[derive(Debug)]
pub enum CacheError {
    Image(AgentImageError),
    Io(std::io::Error),
}

impl std::fmt::Display for CacheError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CacheError::Image(e) => write!(f, "{e}"),
            CacheError::Io(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for CacheError {}

/// C# `AgentImageCache` (AgentImages.cs:285): files named `<sha256>.<ext>` in one folder; reading one marks it recently used, and
/// writing past the cap removes the least recently used others.
#[derive(Debug)]
pub struct AgentImageCache {
    directory: PathBuf,
    maximum_bytes: u64,
    sync: Mutex<()>,
}

fn touch(path: &Path) {
    if let Ok(file) = std::fs::File::options().write(true).open(path) {
        let _ = file.set_modified(SystemTime::now());
    }
}

impl AgentImageCache {
    pub fn new(directory: impl Into<PathBuf>, maximum_bytes: u64) -> Self {
        AgentImageCache { directory: directory.into(), maximum_bytes, sync: Mutex::new(()) }
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub fn maximum_bytes(&self) -> u64 {
        self.maximum_bytes
    }

    /// C# `FilePath`: None for a hash that is not 64 hex digits or an unknown type.
    pub fn file_path(&self, hash: &str, media: &str) -> Option<PathBuf> {
        if hash.len() != 64 || !hash.bytes().all(|b| b.is_ascii_hexdigit()) {
            return None;
        }
        let kind = media_type(Some(media))?;
        Some(self.directory.join(format!("{}.{}", hash.to_ascii_lowercase(), extension_of(kind)?)))
    }

    /// C# `AgentImageCache.Prepare`: checked, then written once under its hash; a file whose bytes no longer match is written again.
    pub fn prepare(&self, data: &[u8], media: &str) -> Result<AgentImagePrepared, CacheError> {
        let (kind, width, height) = inspect(data, media).map_err(CacheError::Image)?;
        let hash = sha256(data);
        let path = self.file_path(&hash, kind).ok_or(CacheError::Image(AgentImageError::UnsupportedType))?;
        let _guard = self.sync.lock().unwrap_or_else(|e| e.into_inner());
        std::fs::create_dir_all(&self.directory).map_err(CacheError::Io)?;
        if intact(&path, data.len(), &hash) {
            touch(&path);
        } else {
            let temporary = PathBuf::from(format!("{}.{}.tmp", path.display(), mighty_core::wire::new_id().replace('-', "")));
            std::fs::write(&temporary, data).map_err(CacheError::Io)?;
            std::fs::rename(&temporary, &path).map_err(CacheError::Io)?;
            self.evict(&path);
        }
        Ok(AgentImagePrepared { hash, media_type: kind.into(), width, height, bytes: data.len() as i32 })
    }

    /// C# `AgentImageCache.Prepare(string base64, string mediaType)`.
    pub fn prepare_base64(&self, base64: &str, media: &str) -> Result<AgentImagePrepared, CacheError> {
        self.prepare(&decode_base64(base64).map_err(CacheError::Image)?, media)
    }

    /// C# `AgentImageCache.Store`.
    pub fn store(&self, data: &[u8], media: &str, source: &str, path: Option<&str>) -> Result<AgentImageRef, CacheError> {
        Ok(self.prepare(data, media)?.reference(source, path))
    }

    /// C# `AgentImageCache.PathFor`: the cached file, marked as just used; None once it was evicted or removed.
    pub fn path_for(&self, value: &AgentImageRef) -> Option<PathBuf> {
        let path = self.file_path(value.hash.as_deref()?, value.media_type.as_deref()?)?;
        let _guard = self.sync.lock().unwrap_or_else(|e| e.into_inner());
        if !path.is_file() {
            return None;
        }
        touch(&path);
        Some(path)
    }

    /// C# `AgentImageCache.Data`: the cached bytes, checked against the hash they are named by.
    pub fn data(&self, value: &AgentImageRef) -> Option<Vec<u8>> {
        let path = self.path_for(value)?;
        if std::fs::metadata(&path).ok()?.len() > MAXIMUM_IMAGE_BYTES as u64 {
            return None;
        }
        let data = std::fs::read(&path).ok()?;
        (sha256(&data) == value.hash.as_deref()?.to_ascii_lowercase()).then_some(data)
    }

    /// C# `AgentImageCache.TotalBytes`.
    pub fn total_bytes(&self) -> u64 {
        let _guard = self.sync.lock().unwrap_or_else(|e| e.into_inner());
        self.entries().iter().map(|(_, len, _)| len).sum()
    }

    fn entries(&self) -> Vec<(PathBuf, u64, SystemTime)> {
        let Ok(read) = std::fs::read_dir(&self.directory) else { return vec![] };
        read.filter_map(Result::ok)
            .filter_map(|entry| {
                let name = entry.file_name().to_string_lossy().into_owned();
                let meta = entry.metadata().ok()?;
                (meta.is_file() && !name.starts_with('.') && !name.ends_with(".tmp"))
                    .then(|| (entry.path(), meta.len(), meta.modified().unwrap_or(SystemTime::UNIX_EPOCH)))
            })
            .collect()
    }

    fn evict(&self, kept: &Path) {
        let mut files = self.entries();
        files.sort_by_key(|(_, _, modified)| *modified);
        let mut total: u64 = files.iter().map(|(_, len, _)| len).sum();
        let kept = files::full_path(kept).unwrap_or_else(|| kept.to_path_buf());
        for (path, len, _) in files {
            if total <= self.maximum_bytes {
                break;
            }
            if path.to_string_lossy().eq_ignore_ascii_case(&kept.to_string_lossy()) {
                continue;
            }
            if std::fs::remove_file(&path).is_ok() {
                total -= len;
            }
        }
    }
}

fn intact(path: &Path, bytes: usize, hash: &str) -> bool {
    std::fs::metadata(path).is_ok_and(|m| m.is_file() && m.len() == bytes as u64)
        && std::fs::read(path).is_ok_and(|data| sha256(&data) == hash)
}

/// C# `AgentImageLocation` (AgentImages.cs:373): where a picture an agent named may be read from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AgentImageLocation {
    /// An existing picture file whose real path is inside `root`.
    File {
        path: PathBuf,
        root: PathBuf,
    },
    /// A `data:image/…;base64,` URI, still encoded.
    Inline {
        media_type: String,
        base64: String,
    },
    /// An http(s) address: shown as a link, never fetched.
    Remote {
        url: String,
        host: String,
    },
    Refused,
}

/// C# `AgentImagePaths.TemporaryRoots`: the temporary folders an agent's tools write screenshots to.
pub fn temporary_roots() -> Vec<PathBuf> {
    let mut roots = vec![std::env::temp_dir()];
    if std::path::MAIN_SEPARATOR == '/'
        && let Some(tmp) = files::real_path(Path::new("/tmp"))
        && !roots.iter().any(|r| files::real_path(r).as_deref() == Some(tmp.as_path()))
    {
        roots.push(PathBuf::from("/tmp"));
    }
    roots
}

fn is_absolute_path(path: &str) -> bool {
    path.starts_with('/') || files::fully_qualified(path)
}

/// The scheme of an absolute URI (`scheme:`), as `Uri.TryCreate(…, UriKind.Absolute)` reads one.
fn uri_scheme(value: &str) -> Option<&str> {
    let colon = value.find(':')?;
    let scheme = &value[..colon];
    let mut chars = scheme.chars();
    (chars.next().is_some_and(|c| c.is_ascii_alphabetic()) && chars.all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.')))
        .then_some(scheme)
}

/// `Uri.UnescapeDataString`: `%XX` sequences decoded as UTF-8 where they form valid UTF-8, the rest kept.
fn unescape_data_string(value: &str) -> String {
    percent_encoding::percent_decode_str(value).decode_utf8().map(|c| c.into_owned()).unwrap_or_else(|_| value.to_string())
}

fn inline(value: &str) -> AgentImageLocation {
    let Some(comma) = value.find(',') else { return AgentImageLocation::Refused };
    let head = crate::slash::lower_invariant(&value[5..comma]);
    let parts: Vec<&str> = head.split(';').map(crate::slash::dotnet_trim).collect();
    if parts.last() != Some(&"base64") {
        return AgentImageLocation::Refused;
    }
    match media_type(parts.first().copied()) {
        Some(kind) => AgentImageLocation::Inline { media_type: kind.into(), base64: value[comma + 1..].into() },
        None => AgentImageLocation::Refused,
    }
}

/// The host of an `http(s)://` address: after `//`, before the path, without user and port.
fn http_host(value: &str) -> Option<String> {
    let rest = value.split_once("://")?.1;
    let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
    let host = authority.rsplit('@').next().unwrap_or("");
    let host = if host.starts_with('[') {
        host.split(']').next().map(|h| format!("{h}]")).unwrap_or_default()
    } else {
        host.split(':').next().unwrap_or("").to_string()
    };
    Some(host)
}

/// `file:` URI → local path (`Uri.LocalPath`): `file:///C:/x` is `C:\x`, `file://server/share/x` the UNC path `\\server\share\x`.
fn file_uri_path(value: &str) -> Option<String> {
    let rest = value.get(5..)?;
    let rest = rest.strip_prefix("//").unwrap_or(rest);
    let (host, path) = match rest.find('/') {
        Some(at) => (&rest[..at], &rest[at..]),
        None => (rest, ""),
    };
    let path = unescape_data_string(path);
    if !host.is_empty() && !host.eq_ignore_ascii_case("localhost") {
        return Some(format!("\\\\{host}{}", path.replace('/', "\\")));
    }
    let trimmed = path.trim_start_matches('/');
    if trimmed.len() >= 2 && trimmed.as_bytes()[1] == b':' {
        return Some(trimmed.replace('/', "\\"));
    }
    Some(path)
}

/// C# `AgentImagePaths.Locate` (AgentImages.cs:403): a picture file whose real path lies inside the workspace (relative paths) or a
/// temporary folder (absolute ones), a data URI, a remote link, or refused.
pub fn locate(reference: &str, workspace_root: Option<&Path>, temporary: Option<&[PathBuf]>) -> AgentImageLocation {
    let value = crate::slash::dotnet_trim(reference);
    if value.is_empty() || value.contains('\0') {
        return AgentImageLocation::Refused;
    }
    if value.len() >= 5 && value[..5].eq_ignore_ascii_case("data:") {
        return inline(value);
    }
    let scheme = uri_scheme(value);
    if let Some(s) = scheme
        && (s.eq_ignore_ascii_case("http") || s.eq_ignore_ascii_case("https"))
        && value[s.len() + 1..].starts_with("//")
    {
        let host = http_host(value).unwrap_or_default();
        return if host.is_empty() { AgentImageLocation::Refused } else { AgentImageLocation::Remote { url: value.into(), host } };
    }
    let mut candidates = Vec::new();
    if scheme.is_some_and(|s| s.eq_ignore_ascii_case("file")) {
        match file_uri_path(value) {
            Some(path) => candidates.push(path),
            None => return AgentImageLocation::Refused,
        }
    } else if scheme.is_some_and(|s| s.len() > 1) && !is_absolute_path(value) {
        return AgentImageLocation::Refused;
    } else {
        candidates.push(value.to_string());
        let decoded = unescape_data_string(value);
        if decoded != value {
            candidates.push(decoded);
        }
    }
    let defaults;
    let temporary = match temporary {
        Some(t) => t,
        None => {
            defaults = temporary_roots();
            &defaults
        }
    };
    for path in candidates {
        if path.starts_with('~') || path.contains('\0') || media_type_for_file_name(&path).is_none() {
            continue;
        }
        let absolute = is_absolute_path(&path);
        let roots: Vec<&Path> =
            workspace_root.into_iter().chain(if absolute { temporary.iter().map(PathBuf::as_path).collect() } else { vec![] }).collect();
        for root in roots {
            let Some(real_root) = files::real_path(root) else { continue };
            // On paper first: a path outside the root as written (a share, another drive, `..` out of it) is refused before
            // anything on disk or on the network is asked about it.
            let joined = if absolute { PathBuf::from(&path) } else { real_root.join(path.replace('/', std::path::MAIN_SEPARATOR_STR)) };
            let Some(full) = files::full_path(&joined) else { continue };
            let written_root = files::full_path(root).unwrap_or_else(|| root.to_path_buf());
            if !files::contains(&full, &real_root) && !files::contains(&full, &written_root) {
                continue;
            }
            let Some(real) = files::real_path(&full) else { continue };
            if !files::contains(&real, &real_root) || !real.is_file() || media_type_for_file_name(&real.to_string_lossy()).is_none() {
                continue;
            }
            return AgentImageLocation::File { path: real, root: real_root };
        }
    }
    AgentImageLocation::Refused
}

/// Why a located picture could not be read.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReadError {
    Open(files::OpenError),
    Image(AgentImageError),
}

/// C# `AgentImagePaths.Read` (AgentImages.cs:459): a located file through the workspace safe-open, at most 20 MiB.
pub fn read_located(path: &Path, root: &Path) -> Result<Vec<u8>, ReadError> {
    let real_root = files::real_path(root).ok_or(ReadError::Open(files::OpenError::Missing))?;
    let relative = path.strip_prefix(&real_root).map_err(|_| ReadError::Open(files::OpenError::Missing))?;
    let relative = relative.to_string_lossy().replace(std::path::MAIN_SEPARATOR, "/");
    let (_, data) = files::read_under(&relative, &real_root, MAXIMUM_IMAGE_BYTES + 1).map_err(ReadError::Open)?;
    if data.len() > MAXIMUM_IMAGE_BYTES {
        return Err(ReadError::Image(AgentImageError::TooLarge));
    }
    Ok(data)
}

/// C# `AgentMarkdownImage(Alt, Source)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentMarkdownImage {
    pub alt: String,
    pub source: String,
}

/// C# `AgentMarkdownImages.MaximumImages`.
pub const MAXIMUM_MARKDOWN_IMAGES: usize = 32;

/// C# `AgentMarkdownImages.Extract` (AgentImages.cs:478): `![alt](target)` pictures outside fenced code, in order.
pub fn markdown_images(text: &str) -> Vec<AgentMarkdownImage> {
    static PATTERN: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let mut result = Vec::new();
    if !text.contains("![") {
        return result;
    }
    let mut fenced = false;
    let mut outside = String::new();
    for line in text.split('\n') {
        let trimmed = line.trim_start_matches(' ');
        if trimmed.starts_with("```") || trimmed.starts_with("~~~") {
            fenced = !fenced;
            outside.push('\n');
            continue;
        }
        if !fenced {
            outside.push_str(line);
        }
        outside.push('\n');
    }
    let pattern = PATTERN.get_or_init(|| {
        regex::Regex::new(r#"!\[([^\]\n]{0,500})\]\(\s*(?:<([^>\n]+)>|([^)\s]+))(?:\s+(?:"[^"\n]*"|'[^'\n]*'))?\s*\)"#)
            .expect("the pattern is valid")
    });
    for captures in pattern.captures_iter(&outside) {
        let source = captures.get(2).or_else(|| captures.get(3)).map_or("", |m| m.as_str());
        if source.is_empty() {
            continue;
        }
        result.push(AgentMarkdownImage { alt: captures.get(1).map_or("", |m| m.as_str()).into(), source: source.into() });
        if result.len() == MAXIMUM_MARKDOWN_IMAGES {
            break;
        }
    }
    result
}

/// C# `AgentImageRtf` (AgentImages.cs:505): the transcript's `\pict` group, drawn at most 480 × 640.
pub mod rtf {
    pub const MAXIMUM_WIDTH: i32 = 480;
    pub const MAXIMUM_HEIGHT: i32 = 640;

    /// The size a picture is drawn at, in device-independent pixels (`Math.Round` rounds half to even).
    pub fn display_size(width: i32, height: i32) -> (i32, i32) {
        display_size_within(width, height, MAXIMUM_WIDTH, MAXIMUM_HEIGHT)
    }

    pub fn display_size_within(width: i32, height: i32, maximum_width: i32, maximum_height: i32) -> (i32, i32) {
        if width <= 0 || height <= 0 {
            return (0, 0);
        }
        let scale = 1f64.min((maximum_width as f64 / width as f64).min(maximum_height as f64 / height as f64));
        (((width as f64 * scale).round_ties_even() as i32).max(1), ((height as f64 * scale).round_ties_even() as i32).max(1))
    }

    /// A `\pict` group for a PNG (`\pngblip`) or, for an opaque thumbnail, a JPEG (`\jpegblip`); 1 DIP = 15 twips.
    pub fn picture(image: &[u8], pixel_width: i32, pixel_height: i32, display: (i32, i32)) -> String {
        let mut body = String::with_capacity(image.len() * 2 + 128);
        let kind = if image.len() >= 2 && image[0] == 0xFF && image[1] == 0xD8 { r"{\pict\jpegblip\picw" } else { r"{\pict\pngblip\picw" };
        body.push_str(&format!(r"{kind}{pixel_width}\pich{pixel_height}\picwgoal{}\pichgoal{} ", display.0 * 15, display.1 * 15));
        for (i, b) in image.iter().enumerate() {
            if i > 0 && i % 64 == 0 {
                body.push('\n');
            }
            body.push_str(&format!("{b:02x}"));
        }
        body.push('}');
        body
    }
}
