//! An HTML preview that reads only bounded assets in its own folder (C# `LocalHtmlDocument.cs`): its own origin
//! `https://preview-<id>.invalid/`, GET only, no `..` or `.` segment, no backslash, no link leaving the folder, at most 8 MiB.

use std::path::{Path, PathBuf};

use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, utf8_percent_encode};

use crate::files;

/// C# `LocalHtmlDocument.MaximumAssetBytes`.
pub const MAXIMUM_ASSET_BYTES: usize = 8 * 1024 * 1024;
/// C# `LocalHtmlDocument.ContentPolicy`.
pub const CONTENT_POLICY: &str = "default-src 'none'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

/// What `Uri.EscapeDataString` leaves as it is: the unreserved characters.
const DATA_STRING: &AsciiSet = &NON_ALPHANUMERIC.remove(b'-').remove(b'.').remove(b'_').remove(b'~');

/// C# `LocalHtmlDocument.Resource`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Resource {
    pub bytes: Vec<u8>,
    pub media_type: &'static str,
}

/// C# `LocalHtmlDocument`.
#[derive(Debug, Clone)]
pub struct LocalHtmlDocument {
    host: String,
    directory: PathBuf,
    address: String,
}

impl LocalHtmlDocument {
    /// C# `new LocalHtmlDocument(root, relativePath)`: Err (the C# `ArgumentException`) unless it is a workspace HTML document.
    pub fn new(root: &Path, relative: &str) -> Result<LocalHtmlDocument, String> {
        let refused = || "A workspace HTML document is required.".to_string();
        let path = files::resolve(relative, root).ok_or_else(refused)?;
        let extension = path.extension().map(|e| e.to_string_lossy().to_ascii_lowercase()).unwrap_or_default();
        if !path.is_file() || !matches!(extension.as_str(), "html" | "htm") {
            return Err(refused());
        }
        let directory = path.parent().ok_or_else(refused)?.to_path_buf();
        let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let host = format!("preview-{}.invalid", mighty_core::wire::new_id().replace('-', ""));
        let address = format!("https://{host}/{}", utf8_percent_encode(&name, DATA_STRING));
        Ok(LocalHtmlDocument { host, directory, address })
    }

    pub fn host(&self) -> &str {
        &self.host
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub fn address(&self) -> &str {
        &self.address
    }

    /// The path of an address on this document's origin (https, this host, the default port, no user), or None.
    fn path_of<'a>(&self, address: &'a str) -> Option<&'a str> {
        let rest = address.strip_prefix("https://")?;
        let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
        let authority = &rest[..end];
        if authority.contains('@') {
            return None;
        }
        let host = match authority.rsplit_once(':') {
            Some((host, "443")) => host,
            Some(_) => return None,
            None => authority,
        };
        if !host.eq_ignore_ascii_case(&self.host) {
            return None;
        }
        let path = &rest[end..];
        Some(path.split(['?', '#']).next().unwrap_or(""))
    }

    /// C# `LocalHtmlDocument.Allows`: navigation never leaves the isolated origin.
    pub fn allows(&self, address: &str) -> bool {
        self.path_of(address).is_some()
    }

    /// C# `LocalHtmlDocument.Read`: a GET of an asset in the document's own folder, or None.
    pub fn read(&self, address: &str, method: &str) -> Option<Resource> {
        if method != "GET" {
            return None;
        }
        let raw = self.path_of(address)?.trim_start_matches('/');
        let path = percent_encoding::percent_decode_str(raw).decode_utf8().ok()?.into_owned();
        if path.contains('\\') || path.split('/').any(|segment| segment == ".." || segment == ".") {
            return None;
        }
        let (opened, bytes) = files::read_under(&path, &self.directory, MAXIMUM_ASSET_BYTES + 1).ok()?;
        if bytes.len() > MAXIMUM_ASSET_BYTES {
            return None;
        }
        let extension = opened.extension().map(|e| e.to_string_lossy().to_ascii_lowercase()).unwrap_or_default();
        let media_type = match extension.as_str() {
            "html" | "htm" => "text/html; charset=utf-8",
            "css" => "text/css; charset=utf-8",
            "js" | "mjs" => "text/javascript; charset=utf-8",
            "png" => "image/png",
            "jpg" | "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            "woff" => "font/woff",
            "woff2" => "font/woff2",
            "ttf" => "font/ttf",
            "ico" => "image/x-icon",
            _ => "text/plain; charset=utf-8",
        };
        Some(Resource { bytes, media_type })
    }
}
