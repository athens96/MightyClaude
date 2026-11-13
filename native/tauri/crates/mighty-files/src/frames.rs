//! An HTML file shown as a page in the files pane or a reference bubble (C# `htmlFrame`, `WebHtmlAddress`; MainWindow.WebShell.cs:84-262,
//! WebShell.cs:36-42): its folder is served under a host name of its own, `h<random>.html.mighty.invalid`, for the page's frame
//! (sandboxed to scripts only), so the css, scripts and pictures beside it load and nothing else. C# maps the folder as a WebView2
//! virtual host; here the host's custom protocol asks [`FrameHosts::serve`] for each request. Past eight folders the oldest mapping
//! goes, so a mapping lives while its folder is among the recently previewed ones.

use std::path::{Path, PathBuf};

use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, utf8_percent_encode};

/// C# `WebShell.HtmlHostSuffix`.
pub const HOST_SUFFIX: &str = ".html.mighty.invalid";
/// C# `MaximumHtmlHosts`.
pub const MAXIMUM_HOSTS: usize = 8;
/// The largest file a frame is served (C#'s virtual host has no cap; a page's assets are far below it).
pub const MAXIMUM_ASSET_BYTES: usize = 64 * 1024 * 1024;

/// What `Uri.EscapeDataString` leaves as it is: the unreserved characters.
const DATA_STRING: &AsciiSet = &NON_ALPHANUMERIC.remove(b'-').remove(b'.').remove(b'_').remove(b'~');

/// C# `Uri.EscapeDataString`.
pub fn escape_data_string(value: &str) -> String {
    utf8_percent_encode(value, DATA_STRING).to_string()
}

fn same_folder(a: &Path, b: &Path) -> bool {
    mighty_core::normalize::ordinal_ignore_case(&a.to_string_lossy(), &b.to_string_lossy()).is_eq()
}

/// One served file: its bytes and its media type.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Served {
    pub bytes: Vec<u8>,
    pub media_type: &'static str,
}

/// The folders served and their hosts, oldest first (C# `htmlHosts`, an ordered dictionary with folders compared ignoring case).
#[derive(Debug, Default)]
pub struct FrameHosts {
    hosts: Vec<(PathBuf, String)>,
}

impl FrameHosts {
    /// C# `WebHtmlAddress`: the host serving `file`'s folder (a new one, dropping the oldest past eight), and the file's address under
    /// `origin` (the custom protocol's start for a host: `http://mightyhtml.<host>` on Windows), the name escaped.
    pub fn address(&mut self, file: &Path, origin: impl Fn(&str) -> String) -> Option<String> {
        let folder = file.parent()?.to_path_buf();
        let name = file.file_name()?.to_string_lossy().into_owned();
        let host = match self.hosts.iter().find(|(f, _)| same_folder(f, &folder)) {
            Some((_, host)) => host.clone(),
            None => {
                if self.hosts.len() >= MAXIMUM_HOSTS {
                    self.hosts.remove(0);
                }
                let host = format!("h{}{HOST_SUFFIX}", uuid::Uuid::new_v4().simple());
                self.hosts.push((folder, host.clone()));
                host
            }
        };
        Some(format!("{}/{}", origin(&host), escape_data_string(&name)))
    }

    /// The folder a host serves, if it still does.
    pub fn folder(&self, host: &str) -> Option<PathBuf> {
        self.hosts.iter().find(|(_, h)| h.eq_ignore_ascii_case(host)).map(|(f, _)| f.clone())
    }

    pub fn len(&self) -> usize {
        self.hosts.len()
    }

    pub fn is_empty(&self) -> bool {
        self.hosts.is_empty()
    }
}

/// A request's file under the folder a host serves: GET only; the path decoded, with no `..` or `.` segment, no backslash and no
/// empty name; read through the files pane's open (inside the folder, no link out of it, the handle checked), at most
/// [`MAXIMUM_ASSET_BYTES`]. None for anything else (the protocol answers 404).
pub fn serve(folder: &Path, method: &str, raw_path: &str) -> Option<Served> {
    if method != "GET" {
        return None;
    }
    let raw = raw_path.split(['?', '#']).next().unwrap_or("").trim_start_matches('/');
    let path = percent_encoding::percent_decode_str(raw).decode_utf8().ok()?.into_owned();
    if path.is_empty() || path.contains('\\') || path.split('/').any(|segment| matches!(segment, "" | "." | "..")) {
        return None;
    }
    let (opened, bytes) = mighty_composer::files::read_under(&path, folder, MAXIMUM_ASSET_BYTES + 1).ok()?;
    if bytes.len() > MAXIMUM_ASSET_BYTES {
        return None;
    }
    Some(Served { bytes, media_type: media_type(&opened) })
}

/// The media type a file is served as, by its extension (WebView2's virtual host reads the system's; these are the ones a page uses).
pub fn media_type(path: &Path) -> &'static str {
    let extension = path.extension().map(|e| e.to_string_lossy().to_ascii_lowercase()).unwrap_or_default();
    match extension.as_str() {
        "html" | "htm" => "text/html; charset=utf-8",
        "css" => "text/css; charset=utf-8",
        "js" | "mjs" | "cjs" => "text/javascript; charset=utf-8",
        "json" | "map" => "application/json; charset=utf-8",
        "txt" | "md" | "csv" => "text/plain; charset=utf-8",
        "xml" => "application/xml",
        "svg" => "image/svg+xml",
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "avif" => "image/avif",
        "bmp" => "image/bmp",
        "ico" => "image/x-icon",
        "woff" => "font/woff",
        "woff2" => "font/woff2",
        "ttf" => "font/ttf",
        "otf" => "font/otf",
        "wasm" => "application/wasm",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" => "audio/ogg",
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        "pdf" => "application/pdf",
        _ => "application/octet-stream",
    }
}
