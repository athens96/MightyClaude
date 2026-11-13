//! C# `AgentImageVerification` (Verification.cs:1290-1292, 1294) and `NativeImageParityVerification` (Verification.cs:226-227).
//! The parser-driven checks (1286-1289, 1295) need the run engine's stream parser (W2) and stay there; 1293 is mighty-core's
//! state load (`normalize::images`).

mod common;

use std::time::{Duration, SystemTime};

use common::{Temp, png, png_default};
use mighty_composer::images::{self, AgentImageCache, AgentImageError, AgentImageLocation, rtf};
use mighty_composer::svg::SafeSvgDocument;

fn png_header(width: u32, height: u32) -> Vec<u8> {
    let mut bytes = png(1, 1, 255);
    bytes[16..20].copy_from_slice(&width.to_be_bytes());
    bytes[20..24].copy_from_slice(&height.to_be_bytes());
    bytes
}

fn refusal(data: &[u8], kind: &str) -> Option<AgentImageError> {
    images::inspect(data, kind).err()
}

/// Verification.cs:1290 "agent images: headers give type and size, and the caps refuse oversize pictures"
/// (HeadersGiveTypeAndSizeAndCapsRefuseOversizePictures).
#[test]
fn headers_give_type_and_size_and_caps_refuse_oversize_pictures() {
    assert_eq!(images::inspect(&png(12, 9, 255), "image/jpeg"), Ok(("image/png", 12, 9)));
    let gif = [b"GIF89a".as_slice(), &[3, 0, 2, 0, 0, 0, 0]].concat();
    assert_eq!(images::inspect(&gif, "image/gif"), Ok(("image/gif", 3, 2)));
    let mut bmp = vec![0u8; 54];
    bmp[0] = b'B';
    bmp[1] = b'M';
    bmp[14..18].copy_from_slice(&40u32.to_le_bytes());
    bmp[18..22].copy_from_slice(&6i32.to_le_bytes());
    bmp[22..26].copy_from_slice(&(-4i32).to_le_bytes());
    assert_eq!(images::inspect(&bmp, "image/bmp"), Ok(("image/bmp", 6, 4)));
    let mut webp = vec![0u8; 30];
    webp[..4].copy_from_slice(b"RIFF");
    webp[8..12].copy_from_slice(b"WEBP");
    webp[12..16].copy_from_slice(b"VP8X");
    webp[24] = 99;
    webp[27] = 49;
    assert_eq!(images::inspect(&webp, "image/webp"), Ok(("image/webp", 100, 50)));
    let jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x30, 0x03];
    assert_eq!(images::inspect(&jpeg, "image/jpeg"), Ok(("image/jpeg", 48, 32)));
    assert_eq!(refusal(&png_header(8_001, 10), "image/png"), Some(AgentImageError::TooManyPixels));
    assert_eq!(refusal(&png_header(8_000, 8_001), "image/png"), Some(AgentImageError::TooManyPixels));
    assert_eq!(refusal(&png_header(8_000, 8_000), "image/png"), None);
    assert_eq!(refusal(&vec![0u8; images::MAXIMUM_IMAGE_BYTES + 1], "image/png"), Some(AgentImageError::TooLarge));
    assert_eq!(refusal(&[], "image/png"), Some(AgentImageError::Empty));
    assert_eq!(refusal(&png_default(), "application/pdf"), Some(AgentImageError::UnsupportedType));
    assert_eq!(refusal(b"II*\0rest", "image/tiff"), Some(AgentImageError::Undecodable));
    assert_eq!(refusal(b"<html/>", "image/svg+xml"), Some(AgentImageError::Undecodable));
    let svg = br#"<?xml version="1.0"?><!-- c --><!DOCTYPE svg><svg xmlns="http://www.w3.org/2000/svg"/>"#;
    assert_eq!(images::inspect(svg, "image/svg"), Ok(("image/svg+xml", 0, 0)));
    assert!(images::media_type(Some("image/JPG")) == Some("image/jpeg") && images::media_type(Some("image/x-png")) == Some("image/png"));
    assert_eq!(images::media_type(Some("text/plain")), None);
    assert_eq!(images::media_type_for_file_name("a/b/Shot.JPEG"), Some("image/jpeg"));
    assert_eq!(images::media_type_for_file_name("x.tif"), Some("image/tiff"));
    assert_eq!(images::media_type_for_file_name("x.txt"), None);
    assert_eq!(images::decode_base64("@@@"), Err(AgentImageError::InvalidEncoding));
    assert_eq!(images::decode_base64(" aGVs\nbG8= ").unwrap(), b"hello");
}

fn set_time(path: &std::path::Path, ago: Duration) {
    std::fs::File::options().write(true).open(path).unwrap().set_modified(SystemTime::now() - ago).unwrap();
}

/// Verification.cs:1291 "agent images: the cache writes once by hash, rewrites a damaged file and evicts the least recently used"
/// (TheCacheWritesOnceByHashRewritesADamagedFileAndEvictsTheLeastRecentlyUsed).
#[test]
fn the_cache_writes_once_by_hash_rewrites_a_damaged_file_and_evicts_the_least_recently_used() {
    let directory = Temp::new("image-cache");
    let (first, second, third) = (png(4, 4, 1), png(4, 4, 2), png(4, 4, 3));
    let cache = AgentImageCache::new(directory.path().join("c"), (first.len() + second.len() + third.len() - 1) as u64);
    let a = cache.store(&first, "image/png", "a", None).unwrap();
    let path = cache.path_for(&a).unwrap();
    assert_eq!(path.file_name().unwrap().to_string_lossy(), format!("{}.png", a.hash.as_deref().unwrap()));
    std::fs::write(&path, b"damaged").unwrap();
    assert!(cache.data(&a).is_none(), "damaged bytes are not trusted");
    cache.store(&first, "image/png", "a", None).unwrap();
    assert_eq!(cache.data(&a).unwrap(), first, "storing again rewrites a damaged file");
    set_time(&path, Duration::from_secs(600));
    let b = cache.store(&second, "image/png", "b", None).unwrap();
    set_time(&cache.path_for(&b).unwrap(), Duration::from_secs(300));
    assert!(cache.data(&a).is_some(), "reading marks a used");
    let c = cache.store(&third, "image/png", "c", None).unwrap();
    assert!(
        cache.path_for(&b).is_none() && cache.path_for(&a).is_some() && cache.path_for(&c).is_some(),
        "over the cap the least recently used goes first"
    );
    assert!(cache.total_bytes() <= cache.maximum_bytes());
}

/// Verification.cs:1292 "agent images: Markdown pictures outside code fences follow the workspace and temporary folder rule"
/// (MarkdownPicturesOutsideCodeFencesFollowThePathRule).
#[test]
fn markdown_pictures_outside_code_fences_follow_the_path_rule() {
    let pictures = images::markdown_images(
        "See ![shot](shots/a.png) and ![](<my file.png> \"title\")\n```\n![no](code.png)\n```\n![remote](https://example.com/x.png)",
    );
    assert_eq!(pictures.iter().map(|p| p.source.as_str()).collect::<Vec<_>>(), ["shots/a.png", "my file.png", "https://example.com/x.png"]);
    assert_eq!(pictures[0].alt, "shot");
    assert!(images::markdown_images("no pictures here").is_empty());
    let directory = Temp::new("image-paths");
    let workspace = directory.path().join("workspace");
    let temporary = directory.path().join("temporary");
    let outside = directory.path().join("outside");
    for folder in [&workspace, &workspace.join("shots"), &temporary, &outside] {
        std::fs::create_dir_all(folder).unwrap();
    }
    std::fs::write(workspace.join("shots").join("a.png"), png_default()).unwrap();
    std::fs::write(temporary.join("스크린샷 1.png"), png_default()).unwrap();
    std::fs::write(outside.join("secret.png"), png_default()).unwrap();
    std::fs::write(workspace.join("notes.txt"), "text").unwrap();
    let roots = [temporary.clone()];
    let locate = |value: &str| images::locate(value, Some(&workspace), Some(&roots));
    let refused = |value: &str| locate(value) == AgentImageLocation::Refused;
    assert!(matches!(locate("shots/a.png"), AgentImageLocation::File { ref path, .. } if path.to_string_lossy().ends_with("a.png")));
    assert!(matches!(locate(&temporary.join("스크린샷 1.png").to_string_lossy()), AgentImageLocation::File { .. }));
    let encoded = percent_encoding::utf8_percent_encode("스크린샷 1.png", percent_encoding::NON_ALPHANUMERIC).to_string();
    assert!(
        matches!(locate(&temporary.join(encoded).to_string_lossy()), AgentImageLocation::File { .. }),
        "a percent-encoded Korean and spaced name"
    );
    assert!(refused(&outside.join("secret.png").to_string_lossy()));
    assert!(refused("../outside/secret.png"));
    assert!(refused(r"\\evil.invalid\share\a.png") && refused("//evil.invalid/share/a.png") && refused("file://evil.invalid/share/a.png"));
    assert!(refused("~/secret.png") && refused("notes.txt") && refused("shots/missing.png"));
    assert!(refused(&temporary.join("x.png").to_string_lossy()));
    if common::symlink_file(&outside.join("secret.png"), &workspace.join("link.png")) {
        assert!(refused("link.png"), "a link pointing outside");
    }
    assert!(matches!(locate("https://example.com/x.png"), AgentImageLocation::Remote { ref host, .. } if host == "example.com"));
    assert_eq!(locate("data:image/png;base64,AAAA"), AgentImageLocation::Inline { media_type: "image/png".into(), base64: "AAAA".into() });
    assert!(refused("data:text/plain;base64,AAAA") && refused("javascript:alert(1)"));
    let AgentImageLocation::File { path, .. } = locate("shots/a.png") else { panic!("located") };
    assert_eq!(images::read_located(&path, &workspace).unwrap(), png_default());
}

/// Verification.cs:1294 "agent images: the transcript draws a PNG thumbnail at most 480 x 640" (TheTranscriptDrawsAPngThumbnailAtMost480By640).
#[test]
fn the_transcript_draws_a_png_thumbnail_at_most_480_by_640() {
    assert_eq!(rtf::display_size(1920, 1080), (480, 270));
    assert_eq!(rtf::display_size(600, 1600), (240, 640));
    assert_eq!(rtf::display_size(40, 30), (40, 30));
    assert_eq!(rtf::display_size(0, 30), (0, 0));
    let small = png(2, 2, 255);
    let picture = rtf::picture(&small, 2, 2, (40, 30));
    assert!(picture.starts_with(r"{\pict\pngblip\picw2\pich2\picwgoal600\pichgoal450 ") && picture.ends_with('}'));
    let hex: String = picture[picture.find(' ').unwrap() + 1..picture.len() - 1].chars().filter(|c| *c != '\n').collect();
    let bytes: Vec<u8> = (0..hex.len()).step_by(2).map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap()).collect();
    assert_eq!(bytes, small);
    assert!(rtf::picture(&png(40, 40, 255), 40, 40, (40, 40)).split('\n').skip(1).all(|line| line.len() <= 129));
    assert!(
        rtf::picture(&[0xFF, 0xD8, 0xFF, 0xD9], 1, 1, (1, 1)).starts_with(r"{\pict\jpegblip\picw1\pich1\picwgoal15\pichgoal15 ffd8ffd9")
    );
}

fn tiff(little: bool, width: u32, height: u32) -> Vec<u8> {
    let mut bytes = vec![0u8; 38];
    bytes[0] = if little { b'I' } else { b'M' };
    bytes[1] = bytes[0];
    let u16_at =
        |b: &mut Vec<u8>, at: usize, v: u16| b[at..at + 2].copy_from_slice(&if little { v.to_le_bytes() } else { v.to_be_bytes() });
    let u32_at =
        |b: &mut Vec<u8>, at: usize, v: u32| b[at..at + 4].copy_from_slice(&if little { v.to_le_bytes() } else { v.to_be_bytes() });
    u16_at(&mut bytes, 2, 42);
    u32_at(&mut bytes, 4, 8);
    u16_at(&mut bytes, 8, 2);
    u16_at(&mut bytes, 10, 256);
    u16_at(&mut bytes, 12, 4);
    u32_at(&mut bytes, 14, 1);
    u32_at(&mut bytes, 18, width);
    u16_at(&mut bytes, 22, 257);
    u16_at(&mut bytes, 24, 4);
    u32_at(&mut bytes, 26, 1);
    u32_at(&mut bytes, 30, height);
    bytes
}

/// Verification.cs:226 "TIFF image headers and allocation bounds" (NativeImageParityVerification.TiffHeaderHandlesEndianAndRefusesInvalidDirectories).
#[test]
fn tiff_header_handles_endian_and_refuses_invalid_directories() {
    for little in [true, false] {
        assert_eq!(images::inspect(&tiff(little, 60, 40), "image/png"), Ok(("image/tiff", 60, 40)));
    }
    assert_eq!(refusal(&tiff(true, 9000, 30), "image/tiff"), Some(AgentImageError::TooManyPixels));
    let mut bad = tiff(true, 60, 40);
    bad[4..8].copy_from_slice(&u32::MAX.to_le_bytes());
    for data in [bad, tiff(false, 60, 40)[..20].to_vec(), tiff(true, 0, 40)] {
        assert_eq!(refusal(&data, "image/tiff"), Some(AgentImageError::Undecodable));
    }
}

/// Verification.cs:227 "SVG native raster boundary rejects active and external documents" (NativeImageParityVerification.SvgRasterInputRefusesActiveOrExternalContent).
#[test]
fn svg_raster_input_refuses_active_or_external_content() {
    let safe = SafeSvgDocument::parse(b"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 80 40'><defs><linearGradient id='g'/></defs><rect fill='url(#g)' width='80' height='40'/></svg>").unwrap();
    assert!(safe.width == 80.0 && safe.height == 40.0 && safe.xml.contains("linearGradient"), "{safe:?}");
    let deep = format!("<svg>{}{}</svg>", "<g>".repeat(70), "</g>".repeat(70));
    for svg in [
        "<!DOCTYPE svg><svg/>",
        "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>",
        "<?xml-stylesheet href='https://example.com/style.css'?><svg/>",
        "<svg><script>alert(1)</script></svg>",
        "<svg onload='alert(1)'/>",
        "<svg><foreignObject><body/></foreignObject></svg>",
        "<svg><image href='https://example.com/private.png'/></svg>",
        "<svg><use href='&#104;ttps://example.com/shape.svg#x'/></svg>",
        "<svg><set attributeName='href' to='https://example.com'/></svg>",
        "<svg><style>@import 'https://example.com/a.css';</style></svg>",
        "<svg width='-1' height='40'/>",
        deep.as_str(),
    ] {
        assert!(SafeSvgDocument::parse(svg.as_bytes()).is_err(), "unsafe SVG admitted: {svg}");
    }
}

/// The canonical document keeps the drawing and writes it as `XElement.ToString(DisableFormatting)` does: double quotes, `" />"`
/// for an empty element, references expanded and escaped again, comments gone.
#[test]
fn the_safe_document_is_written_canonically() {
    let safe = SafeSvgDocument::parse(
        b"<svg xmlns='http://www.w3.org/2000/svg' width='10' height='5'><!-- c --><text x='1'>a &amp; b &#60;</text><g></g><rect/></svg>",
    )
    .unwrap();
    assert_eq!(
        safe.xml,
        r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="5"><text x="1">a &amp; b &lt;</text><g></g><rect /></svg>"#
    );
    assert_eq!((safe.width, safe.height), (10.0, 5.0));
    assert_eq!(SafeSvgDocument::parse(b"<svg/>").unwrap().width, 300.0, "no size reads as 300 x 150");
    assert_eq!(SafeSvgDocument::parse(b"<svg xmlns:x='urn:other'><x:a/></svg>").err(), Some(AgentImageError::ExternalSvg));
}
