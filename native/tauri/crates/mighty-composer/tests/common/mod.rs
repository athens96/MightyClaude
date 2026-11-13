//! What the ported C# checks share: the shared fixtures, the language the C# runner pins (ko), temporary folders and real PNGs.
#![allow(dead_code)]

use std::path::{Path, PathBuf};

use mighty_core::locale::Locale;

/// The C# runner pins the language to Korean (Verification.cs).
pub fn ko() -> Locale {
    Locale::new("ko")
}

pub fn en() -> Locale {
    Locale::new("en")
}

/// `native/contracts/fixtures/<name>`, parsed.
pub fn fixture(name: &str) -> serde_json::Value {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures").join(name);
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// A folder of its own under the temporary folder, removed when dropped (C# `Verification.Temp`).
pub struct Temp(pub PathBuf);

impl Temp {
    pub fn new(name: &str) -> Temp {
        let path = std::env::temp_dir().join(format!("mighty-composer-{name}-{}", mighty_core::wire::new_id()));
        std::fs::create_dir_all(&path).unwrap();
        Temp(path)
    }
    pub fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

pub fn write(path: &Path, text: &str) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(path, text).unwrap();
}

fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for b in bytes {
        crc ^= *b as u32;
        for _ in 0..8 {
            crc = if crc & 1 == 1 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
        }
    }
    !crc
}

fn adler32(bytes: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for x in bytes {
        a = (a + *x as u32) % 65521;
        b = (b + a) % 65521;
    }
    (b << 16) | a
}

/// zlib with stored (uncompressed) deflate blocks.
fn zlib_stored(raw: &[u8]) -> Vec<u8> {
    let mut out = vec![0x78, 0x01];
    let chunks: Vec<&[u8]> = if raw.is_empty() { vec![&[][..]] } else { raw.chunks(65_535).collect() };
    for (i, chunk) in chunks.iter().enumerate() {
        out.push(if i + 1 == chunks.len() { 1 } else { 0 });
        let len = chunk.len() as u16;
        out.extend_from_slice(&len.to_le_bytes());
        out.extend_from_slice(&(!len).to_le_bytes());
        out.extend_from_slice(chunk);
    }
    out.extend_from_slice(&adler32(raw).to_be_bytes());
    out
}

fn chunk(png: &mut Vec<u8>, kind: &str, body: &[u8]) {
    png.extend_from_slice(&(body.len() as u32).to_be_bytes());
    let mut typed = kind.as_bytes().to_vec();
    typed.extend_from_slice(body);
    png.extend_from_slice(&typed);
    png.extend_from_slice(&crc32(&typed).to_be_bytes());
}

/// C# `AgentImageVerification.Png`: a real PNG of `width` × `height` pixels in one colour.
pub fn png(width: u32, height: u32, red: u8) -> Vec<u8> {
    let mut header = Vec::new();
    header.extend_from_slice(&width.to_be_bytes());
    header.extend_from_slice(&height.to_be_bytes());
    header.extend_from_slice(&[8, 2, 0, 0, 0]);
    // Each row: filter type 0, then the pixels.
    let row: Vec<u8> = std::iter::once(0u8).chain((0..width).flat_map(|_| [red, 102, 51])).collect();
    let raw: Vec<u8> = (0..height).flat_map(|_| row.iter().copied()).collect();
    let mut png = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    chunk(&mut png, "IHDR", &header);
    chunk(&mut png, "IDAT", &zlib_stored(&raw));
    chunk(&mut png, "IEND", &[]);
    png
}

/// C# `Png()` with its defaults (8 × 6, red 255).
pub fn png_default() -> Vec<u8> {
    png(8, 6, 255)
}

/// A symbolic link, or false where this user may not make one (Windows without the privilege).
pub fn symlink_file(target: &Path, link: &Path) -> bool {
    #[cfg(windows)]
    {
        std::os::windows::fs::symlink_file(target, link).is_ok()
    }
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(target, link).is_ok()
    }
}
