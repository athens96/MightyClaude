//! Pictures drawn for the page: an SVG to PNG (C# `NativeSvgRaster`, Win2D's `CanvasSvgDocument` over `SafeSvgDocument`'s canonical
//! XML; here resvg), and a bitmap decoded for its size and, when the page cannot draw it whole, fitted to a PNG (C# `BitmapDecoder`
//! with `BitmapTransform`, Fant, respecting EXIF orientation; here the image crate, Lanczos).

use std::io::Cursor;
use std::sync::{Arc, OnceLock};

use image::{DynamicImage, ImageDecoder, ImageFormat, ImageReader};
use mighty_composer::images::{AgentImageError, MAXIMUM_IMAGE_BYTES};
use mighty_composer::svg::SafeSvgDocument;
use resvg::{tiny_skia, usvg};

/// C# `NativeSvgRaster.Result`.
#[derive(Debug, Clone, PartialEq)]
pub struct SvgRaster {
    pub png: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub original_width: f64,
    pub original_height: f64,
}

/// The system's fonts, read once (Win2D draws an SVG's text with them).
fn fonts() -> Arc<usvg::fontdb::Database> {
    static FONTS: OnceLock<Arc<usvg::fontdb::Database>> = OnceLock::new();
    FONTS
        .get_or_init(|| {
            let mut database = usvg::fontdb::Database::new();
            database.load_system_fonts();
            Arc::new(database)
        })
        .clone()
}

/// C# `NativeSvgRaster.Render`: the safe document (`SafeSvgDocument.Parse`: no script, no outside content, bounded) drawn no larger
/// than its own size within `maximum_edge` (1-4,096), or scaled to it with `enlarge`, on transparent black; refused past 20 MB of PNG.
pub fn svg_png(bytes: &[u8], maximum_edge: u32, enlarge: bool) -> Result<SvgRaster, AgentImageError> {
    let edge = maximum_edge.clamp(1, 4096) as f64;
    let source = SafeSvgDocument::parse(bytes)?;
    let longest = source.width.max(source.height);
    let scale = if enlarge { edge / longest } else { (edge / longest).min(1.0) };
    let width = (source.width * scale).round().max(1.0) as u32;
    let height = (source.height * scale).round().max(1.0) as u32;
    let mut options = usvg::Options { fontdb: fonts(), ..usvg::Options::default() };
    // Only what the document holds is drawn: no file is read for an <image> (SafeSvgDocument let only data: pictures through).
    options.image_href_resolver.resolve_string = Box::new(|_, _| None);
    let tree = usvg::Tree::from_str(&source.xml, &options).map_err(|_| AgentImageError::Undecodable)?;
    let mut pixmap = tiny_skia::Pixmap::new(width, height).ok_or(AgentImageError::TooManyPixels)?;
    // The document laid out at its own size, then scaled to the pixels (C#: the drawing session's transform over DrawSvg).
    let size = tree.size();
    let transform = tiny_skia::Transform::from_scale(width as f32 / size.width(), height as f32 / size.height());
    resvg::render(&tree, transform, &mut pixmap.as_mut());
    let png = pixmap.encode_png().map_err(|_| AgentImageError::Undecodable)?;
    if png.len() > MAXIMUM_IMAGE_BYTES {
        return Err(AgentImageError::TooLarge);
    }
    Ok(SvgRaster { png, width, height, original_width: source.width, original_height: source.height })
}

/// A bitmap's size as it shows (EXIF orientation applied, C# `OrientedPixelWidth`/`Height`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BitmapSize {
    pub width: u32,
    pub height: u32,
}

/// A decoder over the bytes; `measuring` reads the header only, so no limit on what a decode would allocate applies (WIC reads a
/// picture's size without decoding it); a decode keeps the image crate's limits.
fn decoder(bytes: &[u8], measuring: bool) -> image::ImageResult<impl ImageDecoder + '_> {
    let mut reader = ImageReader::new(Cursor::new(bytes)).with_guessed_format()?;
    if measuring {
        reader.no_limits();
    }
    reader.into_decoder()
}

fn rotates(orientation: image::metadata::Orientation) -> bool {
    use image::metadata::Orientation::*;
    matches!(orientation, Rotate90 | Rotate270 | Rotate90FlipH | Rotate270FlipH)
}

/// The bitmap's oriented size, read from its header without decoding it; None when the bytes are no picture this reads.
pub fn bitmap_size(bytes: &[u8]) -> Option<BitmapSize> {
    let mut decoder = decoder(bytes, true).ok()?;
    let (width, height) = decoder.dimensions();
    let orientation = decoder.orientation().unwrap_or(image::metadata::Orientation::NoTransforms);
    Some(if rotates(orientation) { BitmapSize { width: height, height: width } } else { BitmapSize { width, height } })
}

/// The bitmap decoded, turned upright.
pub fn decode(bytes: &[u8]) -> Option<DynamicImage> {
    let mut decoder = decoder(bytes, false).ok()?;
    let orientation = decoder.orientation().unwrap_or(image::metadata::Orientation::NoTransforms);
    let mut picture = DynamicImage::from_decoder(decoder).ok()?;
    picture.apply_orientation(orientation);
    Some(picture)
}

/// C#'s fit: the picture upright, at most `fit` × its size (each side at least 1), as a PNG.
pub fn fitted_png(bytes: &[u8], fit: f64) -> Option<Vec<u8>> {
    let picture = decode(bytes)?;
    let width = ((picture.width() as f64 * fit) as u32).max(1);
    let height = ((picture.height() as f64 * fit) as u32).max(1);
    let scaled = if (width, height) == (picture.width(), picture.height()) {
        picture
    } else {
        picture.resize_exact(width, height, image::imageops::FilterType::Lanczos3)
    };
    let mut png = Vec::new();
    scaled.write_to(&mut Cursor::new(&mut png), ImageFormat::Png).ok()?;
    Some(png)
}

/// A picture's pixels for the clipboard: upright, RGBA, with the same picture as PNG.
pub fn clipboard_pixels(bytes: &[u8]) -> Option<(u32, u32, Vec<u8>, Vec<u8>)> {
    let picture = decode(bytes)?.to_rgba8();
    let (width, height) = picture.dimensions();
    let mut png = Vec::new();
    DynamicImage::ImageRgba8(picture.clone()).write_to(&mut Cursor::new(&mut png), ImageFormat::Png).ok()?;
    Some((width, height, picture.into_raw(), png))
}
