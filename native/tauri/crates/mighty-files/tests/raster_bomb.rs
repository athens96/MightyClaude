//! Pictures that would make the app allocate gigabytes from a few bytes: a picture's header is read with what it may allocate bounded
//! (raster.rs `MAXIMUM_HEADER_BYTES`), so a 1×1 PNG of about a megabyte whose iCCP profile inflates to a gigabyte is measured without
//! the gigabyte; and a picture inside an SVG past `MAXIMUM_EMBEDDED_PIXELS` is left out of the drawing, which would decode it whole.
//! Their own test binary, so the allocator that records the largest allocation sees these checks alone (one at a time).

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};

use base64::Engine as _;
use mighty_files::raster;

/// The system's allocator, recording the largest block asked for.
struct Recording;

static LARGEST: AtomicUsize = AtomicUsize::new(0);
/// One check at a time records.
static ONE_AT_A_TIME: Mutex<()> = Mutex::new(());

// SAFETY: every call is the system allocator's; only a size is recorded besides.
unsafe impl GlobalAlloc for Recording {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        LARGEST.fetch_max(layout.size(), Ordering::Relaxed);
        unsafe { System.alloc(layout) }
    }
    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        LARGEST.fetch_max(layout.size(), Ordering::Relaxed);
        unsafe { System.alloc_zeroed(layout) }
    }
    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }
    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        LARGEST.fetch_max(new_size, Ordering::Relaxed);
        unsafe { System.realloc(ptr, layout, new_size) }
    }
}

#[global_allocator]
static ALLOCATOR: Recording = Recording;

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

fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    let mut body = kind.to_vec();
    body.extend_from_slice(data);
    out.extend_from_slice(&body);
    out.extend_from_slice(&crc32(&body).to_be_bytes());
}

/// Deflate's bits, least significant first; a Huffman code goes most significant bit first.
#[derive(Default)]
struct Bits {
    bytes: Vec<u8>,
    used: u32,
}

impl Bits {
    fn put(&mut self, value: u32, count: u32) {
        for bit in 0..count {
            if self.used % 8 == 0 {
                self.bytes.push(0);
            }
            if value >> bit & 1 == 1 {
                *self.bytes.last_mut().unwrap() |= 1 << (self.used % 8);
            }
            self.used += 1;
        }
    }
    fn code(&mut self, code: u32, length: u32) {
        for bit in (0..length).rev() {
            self.put(code >> bit & 1, 1);
        }
    }
}

/// A zlib stream of `length` (at least 1) zero bytes, about two bits per 258: one dynamic block whose literal/length code has the
/// zero byte (`10`), the end (`11`) and length 258 (`0`), and whose distance code has distances 1 (`0`) and 2 (`1`).
fn zeros(length: u64) -> Vec<u8> {
    let mut bits = Bits::default();
    bits.put(1, 1); // the last block
    bits.put(2, 2); // dynamic codes
    bits.put(29, 5); // 286 literal/length codes
    bits.put(1, 5); // 2 distance codes
    bits.put(14, 4); // 18 code length codes
    // The code length code, in its order (16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1): 18 takes 1 bit, 1 and 2 two.
    for length in [0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2] {
        bits.put(length, 3);
    }
    let (eighteen, one, two) = ((0, 1), (2, 2), (3, 2));
    bits.code(two.0, two.1); // the zero byte: 2 bits
    bits.code(eighteen.0, eighteen.1); // 138 unused …
    bits.put(127, 7);
    bits.code(eighteen.0, eighteen.1); // … and 117 more (1–255)
    bits.put(106, 7);
    bits.code(two.0, two.1); // the end: 2 bits
    bits.code(eighteen.0, eighteen.1); // 257–284 unused
    bits.put(17, 7);
    bits.code(one.0, one.1); // length 258: 1 bit
    bits.code(one.0, one.1); // distance 1: 1 bit
    bits.code(one.0, one.1); // distance 2: 1 bit
    bits.code(2, 2); // a zero byte
    for _ in 0..(length - 1) / 258 {
        bits.code(0, 1); // 258 bytes …
        bits.code(0, 1); // … one back
    }
    for _ in 0..(length - 1) % 258 {
        bits.code(2, 2); // a zero byte
    }
    bits.code(3, 2); // the end
    let adler = ((length % 65_521) << 16 | 1) as u32;
    let mut out = vec![0x78, 0x01];
    out.extend_from_slice(&bits.bytes);
    out.extend_from_slice(&adler.to_be_bytes());
    out
}

/// A 1×1 RGB PNG whose iCCP chunk is `profile` (zlib), with its one pixel.
fn png_with_profile(profile: &[u8]) -> Vec<u8> {
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];
    chunk(&mut out, b"IHDR", &[0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
    let mut iccp = b"bomb\0\0".to_vec();
    iccp.extend_from_slice(profile);
    chunk(&mut out, b"iCCP", &iccp);
    // The row: filter 0, then red; stored, in zlib.
    let row = [0u8, 0xCC, 0x33, 0x33];
    let mut pixels = vec![0x78, 0x01, 0x01, 4, 0, !4, 0xFF];
    pixels.extend_from_slice(&row);
    let (mut a, mut b) = (1u32, 0u32);
    for byte in row {
        a = (a + byte as u32) % 65_521;
        b = (b + a) % 65_521;
    }
    pixels.extend_from_slice(&(b << 16 | a).to_be_bytes());
    chunk(&mut out, b"IDAT", &pixels);
    chunk(&mut out, b"IEND", &[]);
    out
}

/// A black `width` × `height` grey PNG (every byte of its rows zero), compressed as [`zeros`] compresses.
fn black_png(width: u32, height: u32) -> Vec<u8> {
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];
    let mut header = width.to_be_bytes().to_vec();
    header.extend_from_slice(&height.to_be_bytes());
    header.extend_from_slice(&[8, 0, 0, 0, 0]);
    chunk(&mut out, b"IHDR", &header);
    chunk(&mut out, b"IDAT", &zeros((width as u64 + 1) * height as u64));
    chunk(&mut out, b"IEND", &[]);
    out
}

#[test]
fn a_profile_that_inflates_to_a_gigabyte_is_measured_within_the_bound() {
    let _one = ONE_AT_A_TIME.lock().unwrap_or_else(|p| p.into_inner());
    // About a megabyte that inflates to 1.03 GB.
    let bomb = png_with_profile(&zeros(4_000_000 * 258 + 1));
    assert!(bomb.len() < 1_100_000, "about a megabyte: {}", bomb.len());
    let honest = png_with_profile(&zeros(259));
    assert_eq!(raster::bitmap_size(&honest).map(|s| (s.width, s.height)), Some((1, 1)), "the fixture is a PNG the image crate reads");
    LARGEST.store(0, Ordering::Relaxed);
    let measured = raster::bitmap_size(&bomb);
    let largest = LARGEST.load(Ordering::Relaxed) as u64;
    assert_eq!(measured.map(|s| (s.width, s.height)), Some((1, 1)), "its header still says 1×1");
    // The inflater's last step may ask for a little past the bound; unbounded it asks for the whole gigabyte.
    assert!(largest <= 2 * raster::MAXIMUM_HEADER_BYTES, "the largest allocation measuring it: {largest} bytes");
}

/// An SVG's picture past 4,096 × 4,096 pixels is left out (resvg would decode its 100 MB whole); a small one is drawn.
#[test]
fn a_huge_picture_inside_an_svg_is_left_out() {
    let _one = ONE_AT_A_TIME.lock().unwrap_or_else(|p| p.into_inner());
    let svg = |png: &[u8]| {
        let data = base64::engine::general_purpose::STANDARD.encode(png);
        format!(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" fill=\"#c33\"/>\
             <image width=\"100\" height=\"100\" preserveAspectRatio=\"none\" href=\"data:image/png;base64,{data}\"/></svg>"
        )
    };
    let centre = |svg: &str| {
        let drawn = raster::svg_png(svg.as_bytes(), 100, false).unwrap();
        image::load_from_memory(&drawn.png).unwrap().to_rgba8().get_pixel(50, 50).0
    };
    assert_eq!(centre(&svg(&black_png(4, 4))), [0, 0, 0, 0xFF], "a small picture is drawn over the rectangle");
    let huge = svg(&black_png(5000, 5000));
    assert!(huge.len() < 64 * 1024, "a few kilobytes: {}", huge.len());
    LARGEST.store(0, Ordering::Relaxed);
    assert_eq!(centre(&huge), [0xCC, 0x33, 0x33, 0xFF], "25 megapixels: left out, the rectangle shows");
    let largest = LARGEST.load(Ordering::Relaxed);
    assert!(largest < 16 * 1024 * 1024, "the largest allocation drawing it: {largest} bytes");
}
