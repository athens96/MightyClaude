//! What the WinUI host builds for the page from the files pane's rules (MainWindow.WebShell.Panes.cs `WebFilesModel`, `WebFilePreview`,
//! `WebFileImage`; MainWindow.WebShell.cs `WebHtmlAddress`; NativeSvgRaster.cs), checked against the page's types
//! (native/windows/web/src/panes/files/types.ts) and the C# rules, and the Mac's tokenizer fixture
//! (native/contracts/fixtures/source-highlight.json), which the C# highlighter ports.

mod common;

use std::path::Path;

use base64::Engine as _;
use common::{Temp, fixture, ko, write};
use mighty_files::classify::SourceLanguage;
use mighty_files::frames::{self, FrameHosts};
use mighty_files::listing::{FileEntry, Listing};
use mighty_files::model::{self, Formats, Plain};
use mighty_files::source;
use mighty_files::tree::FilePaneTree;
use mighty_files::{preview, raster};
use mighty_platform::fake::FakePdf;
use serde_json::{Value, json};

fn language(name: &str) -> SourceLanguage {
    use SourceLanguage::*;
    match name {
        "swift" => Swift,
        "c" => C,
        "javascript" => JavaScript,
        "python" => Python,
        "kotlin" => Kotlin,
        "java" => Java,
        "go" => Go,
        "rust" => Rust,
        "shell" => Shell,
        "json" => Json,
        "yaml" => Yaml,
        "toml" => Toml,
        "xml" => Xml,
        "html" => Html,
        "css" => Css,
        "sql" => Sql,
        "gradle" => Gradle,
        "dockerfile" => Dockerfile,
        "makefile" => Makefile,
        "plain" => Plain,
        other => panic!("no language {other}"),
    }
}

/// native/contracts/fixtures/source-highlight.json: the Mac's (and the phone's) highlighter vectors hold for the port of it the files
/// pane draws with (C# `SourceHighlighter`, "the single-pass highlighter of macOS SourceHighlighter.swift").
#[test]
fn the_shared_highlighter_vectors_hold() {
    for (index, case) in fixture("source-highlight.json").as_array().unwrap().iter().enumerate() {
        let text = case["text"].as_str().unwrap();
        let found: Vec<Value> = source::tokens(text, language(case["language"].as_str().unwrap()), source::MAXIMUM_UNITS)
            .iter()
            .map(|t| json!({"kind": t.kind.name(), "location": t.location, "length": t.length}))
            .collect();
        assert_eq!(Value::Array(found), case["tokens"], "case {index} ({}): {text:?}", case["language"]);
    }
}

/// `WebFilesModel`: the rows as the page's `FileRow` reads them (symbol only for files, caption only on an opened folder, `reports`
/// when it says the folder was cut or could not be read), and the notes after them.
#[test]
fn the_files_model_is_the_pages() {
    let ko = ko();
    let mut tree = FilePaneTree::default();
    let listing = |entries: Vec<FileEntry>, truncated| Ok(Listing { entries, truncated });
    tree.apply(
        "",
        listing(
            vec![
                FileEntry::new("src", "src", true),
                FileEntry::new("node_modules", "node_modules", true),
                FileEntry::new("README.md", "README.md", false),
            ],
            false,
        ),
        &ko,
    );
    tree.apply(
        "src",
        listing(vec![FileEntry::new("a.ts", "src/a.ts", false), FileEntry::new("logo.png", "src/logo.png", false)], true),
        &ko,
    );
    tree.expanded.insert("src".into());
    tree.selected_path = Some("src/a.ts".into());
    let model = model::files_model(&tree, &ko, &Plain);
    assert_eq!(model["available"], true);
    assert_eq!(model["filtering"], false);
    assert_eq!(model["selected"], "src/a.ts");
    assert!(model.get("preview").is_none(), "no preview yet: left out, as Wire.Json leaves a null");
    let rows = model["rows"].as_array().unwrap();
    assert_eq!(
        rows.iter().map(|r| r["path"].as_str().unwrap()).collect::<Vec<_>>(),
        ["src", "src/a.ts", "src/logo.png", "node_modules", "README.md"]
    );
    assert_eq!(
        rows[0],
        json!({"path": "src", "name": "src", "depth": 0, "dir": true, "expanded": true, "noise": false, "symlink": false,
        "caption": ko.get_with("files.tree.truncated", &[("count", "5000")]), "reports": true})
    );
    assert_eq!(rows[1]["symbol"], "source");
    assert_eq!(rows[2]["symbol"], "image");
    assert_eq!(rows[3]["noise"], true);
    assert!(rows[3].get("symbol").is_none() && rows[3].get("caption").is_none(), "a closed folder has no symbol and no caption");
    assert_eq!(rows[4]["symbol"], "markdown");
    assert_eq!(model["notes"], json!([]));
    tree.filter = "zzz".into();
    let filtered = model::files_model(&tree, &ko, &Plain);
    assert_eq!(filtered["notes"], json!([ko.get("files.tree.noMatches")]), "no matches says only opened folders are searched");
    assert_eq!(model::unavailable(), json!({"available": false}));
}

/// `WebFilePreview` for text: the source drawn with its line count and its colours as flat `[kind, start, length]` triples (0 keyword,
/// 1 string, 2 comment, 3 number), CRLF read as LF, the encoding's display name, and the size as C#'s `FormatBytes` writes it.
#[test]
fn a_source_preview_is_the_pages() {
    let parent = Temp::new("model-source");
    let root = parent.path();
    write(root, "src/hello.ts", b"const smokeMarker = 42; // web files smoke\r\nlet two = \"2\";\r\n");
    write(root, "notes.md", b"# Notes\n");
    let (ko, handles) = (ko(), common::handles());
    let data = preview::load(root, "src/hello.ts", handles.as_ref(), &ko);
    let page = model::file_preview("src/hello.ts", &data, &ko, &Plain, &FakePdf::default());
    assert_eq!(page["kind"], "source");
    assert_eq!(page["name"], "hello.ts");
    assert_eq!(page["encoding"], "UTF-8");
    assert_eq!(page["text"], "const smokeMarker = 42; // web files smoke\nlet two = \"2\";\n", "CRLF read as LF");
    assert_eq!(page["lines"], 3);
    assert_eq!(page["wraps"], false);
    assert_eq!(page["tokens"], json!([0, 0, 5, 3, 20, 2, 2, 24, 18, 0, 43, 3, 1, 53, 3]), "keyword, number, comment, keyword, string");
    assert_eq!(page["banners"], json!([]));
    assert_eq!(page["renderedBanners"], json!([]));
    assert_eq!(page["markdown"], false);
    assert_eq!(page["size"], "60 B");
    assert!(page["modified"].is_string());
    let markdown =
        model::file_preview("notes.md", &preview::load(root, "notes.md", handles.as_ref(), &ko), &ko, &Plain, &FakePdf::default());
    assert!(
        markdown["kind"] == "markdown" && markdown["markdown"] == true && markdown["tokens"] == json!([]),
        "Markdown renders, uncoloured"
    );
    let missing = model::file_preview("gone.ts", &preview::load(root, "gone.ts", handles.as_ref(), &ko), &ko, &Plain, &FakePdf::default());
    assert_eq!(
        missing,
        json!({"path": "gone.ts", "name": "gone.ts", "kind": "unsupported", "failure": ko.get("files.preview.missing"), "banners": []})
    );
}

/// C# `FormatBytes`: B under a thousand, then KB, MB with one decimal and GB with two, trailing zeros dropped, the culture's point.
#[test]
fn sizes_are_written_as_csharp_writes_them() {
    struct Comma;
    impl Formats for Comma {
        fn n0(&self, value: i64) -> String {
            model::grouped(value)
        }
        fn decimal(&self) -> String {
            ",".into()
        }
        fn date_time(&self, at: std::time::SystemTime) -> String {
            Plain.date_time(at)
        }
    }
    assert_eq!(model::format_bytes(999, &Plain), "999 B");
    assert_eq!(model::format_bytes(1_000, &Plain), "1 KB");
    assert_eq!(model::format_bytes(1_250, &Plain), "1.3 KB", "half away from zero");
    assert_eq!(model::format_bytes(61_440, &Plain), "61.4 KB");
    assert_eq!(model::format_bytes(2_000_000, &Plain), "2 MB");
    assert_eq!(model::format_bytes(1_234_567_890, &Plain), "1.23 GB");
    assert_eq!(model::format_bytes(1_500, &Comma), "1,5 KB");
    assert_eq!(model::grouped(5_000), "5,000");
    assert_eq!(model::grouped(-1_234_567), "-1,234,567");
}

/// The smoke's SVG (C# RunWebPictureSmoke): drawn to a PNG of its own size, the rectangle's colour in it.
#[test]
fn an_svg_is_drawn_to_png_as_native_svg_raster_draws_it() {
    let svg =
        b"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"><rect width=\"40\" height=\"30\" fill=\"#c33\"/></svg>";
    let drawn = raster::svg_png(svg, 2048, false).unwrap();
    assert!(
        (drawn.width, drawn.height, drawn.original_width, drawn.original_height) == (40, 30, 40.0, 30.0),
        "no larger than its own size"
    );
    let picture = image::load_from_memory(&drawn.png).unwrap().to_rgba8();
    assert_eq!(picture.dimensions(), (40, 30));
    assert_eq!(picture.get_pixel(20, 15).0, [0xCC, 0x33, 0x33, 0xFF], "the rectangle drawn");
    let bigger = raster::svg_png(svg, 20, false).unwrap();
    assert_eq!((bigger.width, bigger.height), (20, 15), "fitted to the edge asked for");
    let enlarged = raster::svg_png(svg, 400, true).unwrap();
    assert_eq!((enlarged.width, enlarged.height), (400, 300), "a zoom enlarges a vector");
    assert!(raster::svg_png(b"<svg><script>alert(1)</script></svg>", 100, false).is_err(), "only the safe document is drawn");
    assert!(raster::svg_png(b"<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///etc/hosts'/></svg>", 100, false).is_err());
}

fn data(url: &Value) -> Vec<u8> {
    let text = url.as_str().unwrap();
    let (_, encoded) = text.split_once(";base64,").unwrap();
    base64::engine::general_purpose::STANDARD.decode(encoded).unwrap()
}

/// `WebFileImage`: an SVG as a PNG data URL with its own size, a bitmap the page draws as it is, a PDF's first page through the
/// platform's raster (fitted at most 4,096 and twice its size), a PDF without pages failed, and an undecodable picture failed.
#[test]
fn pictures_go_to_the_page_as_data_urls() {
    let parent = Temp::new("model-images");
    let root = parent.path();
    let (ko, handles) = (ko(), common::handles());
    write(
        root,
        "logo.svg",
        b"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"><rect width=\"40\" height=\"30\" fill=\"#c33\"/></svg>",
    );
    let mut png = Vec::new();
    image::DynamicImage::new_rgba8(8, 6).write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png).unwrap();
    write(root, "photo.png", &png);
    write(root, "broken.png", &[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
    write(root, "spec.pdf", b"%PDF-1.7 fake");
    let preview_of =
        |name: &str, pdf: &FakePdf| model::file_preview(name, &preview::load(root, name, handles.as_ref(), &ko), &ko, &Plain, pdf);

    let svg = preview_of("logo.svg", &FakePdf::default());
    assert_eq!(svg["kind"], "image");
    assert!(svg["image"]["url"].as_str().unwrap().starts_with("data:image/png;base64,"), "an SVG is drawn to PNG");
    assert!(
        svg["image"]["width"] == 40.0
            && svg["image"]["height"] == 30.0
            && svg["image"]["bitmap"] == false
            && svg["image"]["zoomable"] == true
    );
    assert_eq!(svg["image"]["label"], ko.get_with("files.image.pixels", &[("width", "40"), ("height", "30")]));

    let photo = preview_of("photo.png", &FakePdf::default());
    assert_eq!(data(&photo["image"]["url"]), png, "a PNG the page draws goes as it is");
    assert!(photo["image"]["url"].as_str().unwrap().starts_with("data:image/png;base64,") && photo["image"]["bitmap"] == true);
    assert!(photo["image"]["width"] == 8.0 && photo["image"]["height"] == 6.0 && photo["banners"] == json!([]));

    let broken = preview_of("broken.png", &FakePdf::default());
    assert!(broken.get("image").is_none() && broken["reason"] == ko.get("files.preview.failed"), "an undecodable picture says it failed");

    let pdf = FakePdf { pages: 1, width: 612.0, height: 792.0, png: png.clone(), ..FakePdf::default() };
    let page = preview_of("spec.pdf", &pdf);
    assert_eq!(data(&page["image"]["url"]), png, "the first page drawn by the platform");
    assert!(page["image"]["width"] == 612.0 && page["image"]["height"] == 792.0 && page["image"]["bitmap"] == false);
    assert_eq!(*pdf.drawn.lock().unwrap(), [(1224, 1584)], "at most twice its size within 4,096");
    let tall = FakePdf { pages: 1, width: 1000.0, height: 8192.0, png: png.clone(), ..FakePdf::default() };
    preview_of("spec.pdf", &tall);
    assert_eq!(*tall.drawn.lock().unwrap(), [(500, 4096)], "the long side fitted to 4,096");
    let empty = preview_of("spec.pdf", &FakePdf::default());
    assert!(empty.get("image").is_none() && empty["reason"] == ko.get("files.preview.failed"), "a document without a page");
    let huge = FakePdf { pages: 1, width: 2e7, height: 1.0, ..FakePdf::default() };
    let undrawn = preview_of("spec.pdf", &huge);
    assert!(undrawn.get("image").is_none() && undrawn.get("reason").is_none(), "a page too large to draw: neither picture nor reason");
    let failing = FakePdf { fails: true, ..FakePdf::default() };
    assert_eq!(preview_of("spec.pdf", &failing)["reason"], ko.get("files.preview.failed"));
}

/// A picture past 100 megapixels is fitted only (its banner says so); past 250 it is not decoded (`MaximumDecodePixels`).
#[test]
fn large_pictures_are_fitted_or_refused() {
    // A PNG header that says 12,000 × 10,000 (120 megapixels) with no pixels, and one past 250 megapixels.
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
    fn header(width: u32, height: u32) -> Vec<u8> {
        let mut chunk = b"IHDR".to_vec();
        chunk.extend_from_slice(&width.to_be_bytes());
        chunk.extend_from_slice(&height.to_be_bytes());
        chunk.extend_from_slice(&[8, 6, 0, 0, 0]);
        let mut bytes = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
        bytes.extend_from_slice(&chunk);
        bytes.extend_from_slice(&crc32(&chunk).to_be_bytes());
        // The start of the pixels (a zlib header): the decoder reads up to here for the size.
        let data = b"IDAT\x78\x01";
        bytes.extend_from_slice(&2u32.to_be_bytes());
        bytes.extend_from_slice(data);
        bytes.extend_from_slice(&crc32(data).to_be_bytes());
        bytes
    }
    let ko = ko();
    let huge = preview::PreviewData { image_bytes: Some(header(20_000, 20_000)), ..preview_data("huge.png") };
    let (image, reason, fit_only) = model::file_image(&huge, &ko, &FakePdf::default());
    assert!(image.is_none() && !fit_only);
    assert_eq!(reason, Some(ko.get_with("files.preview.tooManyPixels", &[("count", "250")])));
    assert_eq!(
        raster::bitmap_size(&header(12_000, 10_000)).map(|s| (s.width, s.height)),
        Some((12_000, 10_000)),
        "read from the header alone"
    );
}

fn preview_data(name: &str) -> preview::PreviewData {
    preview::PreviewData {
        name: name.into(),
        relative_path: name.into(),
        size: 1,
        modified: None,
        kind: mighty_files::classify::PreviewKind::Image,
        text: None,
        encoding: None,
        truncated: false,
        markdown_renderable: false,
        image_bytes: None,
        reason: None,
        failure: None,
    }
}

/// C# `WebHtmlAddress`: one host per folder (the same folder ignoring case keeps its host), the file's name escaped as
/// `Uri.EscapeDataString` escapes it, at most eight folders (the oldest dropped).
#[test]
fn html_frames_get_a_host_per_folder() {
    let mut hosts = FrameHosts::default();
    let origin = |host: &str| format!("http://mightyhtml.{host}");
    let docs = Path::new("work").join("docs");
    let first = hosts.address(&docs.join("한글 page.html"), origin).unwrap();
    assert!(first.starts_with("http://mightyhtml.h") && first.ends_with(".html.mighty.invalid/%ED%95%9C%EA%B8%80%20page.html"), "{first}");
    let host = first.trim_start_matches("http://mightyhtml.").split('/').next().unwrap().to_string();
    assert_eq!(host.len(), "h".len() + 32 + frames::HOST_SUFFIX.len());
    let again = hosts.address(&docs.join("other.html"), origin).unwrap();
    assert!(again.starts_with(&format!("http://mightyhtml.{host}/")), "a folder keeps its host");
    assert!(
        hosts.address(&Path::new("WORK").join("DOCS").join("x.html"), origin).unwrap().contains(&host),
        "folders compare ignoring case"
    );
    assert_eq!(hosts.folder(&host).as_deref(), Some(docs.as_path()));
    for index in 0..frames::MAXIMUM_HOSTS {
        hosts.address(&Path::new("more").join(index.to_string()).join("a.html"), origin);
    }
    assert_eq!(hosts.len(), frames::MAXIMUM_HOSTS, "at most eight folders");
    assert!(hosts.folder(&host).is_none(), "the oldest mapping went");
}
/// The frames' protocol serves GETs of files inside the folder only: no `..` or `.`, no backslash, no folder, no link out of it.
#[test]
fn html_frames_serve_their_folder_only() {
    let parent = Temp::new("frames");
    let folder = parent.path().join("docs");
    write(&folder, "page.html", b"<h1>page</h1><link rel=stylesheet href=style.css>");
    write(&folder, "style.css", b"h1{color:red}");
    write(&folder, "img/logo.svg", b"<svg/>");
    write(parent.path(), "private.txt", b"outside");
    let served = frames::serve(&folder, "GET", "/page.html").unwrap();
    assert!(served.media_type == "text/html; charset=utf-8" && served.bytes.starts_with(b"<h1>"));
    assert_eq!(frames::serve(&folder, "GET", "/style.css?v=1#x").unwrap().media_type, "text/css; charset=utf-8");
    assert_eq!(frames::serve(&folder, "GET", "/img/logo.svg").unwrap().media_type, "image/svg+xml");
    assert!(frames::serve(&folder, "POST", "/page.html").is_none(), "GET only");
    for refused in
        ["/../private.txt", "/%2e%2e/private.txt", "/%2E%2E%2Fprivate.txt", "/./page.html", "/img\\logo.svg", "/", "/img", "/missing.html"]
    {
        assert!(frames::serve(&folder, "GET", refused).is_none(), "{refused}");
    }
    if common::link(&folder.join("escape.txt"), &parent.path().join("private.txt"), false) {
        assert!(frames::serve(&folder, "GET", "/escape.txt").is_none(), "a link out of the folder is not served");
    }
}
