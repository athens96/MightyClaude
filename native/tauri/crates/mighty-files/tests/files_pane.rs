//! C# `FilesPaneVerification` (Verification.cs:1182-1209; the same vectors as macOS WorkspaceFilesTests and SourceHighlighterTests,
//! plus the Windows pane rules), `WindowsParityVerification.SourceFindMatchesAndSteps` (:390), and the reference rules of
//! `DesktopParityVerification` (:738-739).

mod common;

use std::path::Path;

use common::{Temp, ko, link, write};
use mighty_core::json::Wire;
use mighty_core::layout;
use mighty_core::model::{AppSnapshot, PaneLayoutNode};
use mighty_core::{normalize, workspace_files};
use mighty_files::classify::{self, PreviewKind, SourceLanguage, TextEncodingKind};
use mighty_files::listing::{self, FileEntry, ListError};
use mighty_files::source::{self, TokenKind};
use mighty_files::tree::{self, FilePaneTree};
use mighty_files::{OpenError, open_file, pane, preview, reference};
use serde_json::json;

fn names(entries: &[FileEntry]) -> Vec<&str> {
    entries.iter().map(|e| e.name.as_str()).collect()
}

/// A root and a folder beside it, as C# `MakeRoot` makes them.
fn make_root(parent: &Temp) -> (std::path::PathBuf, std::path::PathBuf) {
    let (root, outside) = (parent.path().join("files-root"), parent.path().join("files-outside"));
    std::fs::create_dir_all(&root).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    (root, outside)
}

/// Verification.cs:1182 "files pane tree sorts folders first in natural order and keeps noise folders collapsed".
#[test]
fn tree_sorts_folders_first_in_natural_order_and_keeps_noise_collapsed() {
    let parent = Temp::new("sort");
    let (root, _) = make_root(&parent);
    for name in ["file10.txt", "file2.txt", "b.swift", "A.md", ".env", ".gitignore"] {
        write(&root, name, b"x");
    }
    for folder in ["src", "Docs", "node_modules", ".git"] {
        std::fs::create_dir_all(root.join(folder)).unwrap();
    }
    let listing = listing::list("", &root, listing::MAXIMUM_ENUMERATED_NAMES).unwrap();
    assert_eq!(
        names(&listing.entries),
        [".git", "Docs", "node_modules", "src", ".env", ".gitignore", "A.md", "b.swift", "file2.txt", "file10.txt"],
        "folders first, natural case-insensitive order, hidden files shown"
    );
    assert!(!listing.truncated, "a small folder is not truncated");
    assert_eq!(listing.entries.iter().filter(|e| e.is_noise()).map(|e| e.name.as_str()).collect::<Vec<_>>(), [".git", "node_modules"]);
    assert!(!FileEntry::new("build", "build", false).is_noise(), "a file named build is not a noise folder");
    for name in [".git", "node_modules", ".build", "build", "dist", "DerivedData", ".next", "Pods", ".venv", "__pycache__"] {
        assert!(listing::is_noise_folder(name), "{name} is a noise folder as on macOS");
    }
    write(&root, "src/inner/deep.swift", b"x");
    let paths = |relative: &str| {
        listing::list(relative, &root, listing::MAXIMUM_ENUMERATED_NAMES)
            .unwrap()
            .entries
            .into_iter()
            .map(|e| e.relative_path)
            .collect::<Vec<_>>()
    };
    assert_eq!(paths("src"), ["src/inner"], "nested listing uses / relative paths");
    assert_eq!(paths("src/inner"), ["src/inner/deep.swift"], "deeper listing");
    let sorted = listing::sorted(vec![
        FileEntry::new("z10", "z10", false),
        FileEntry::new("Z2", "Z2", false),
        FileEntry::new("lib", "lib", true),
        FileEntry::new("App", "App", true),
    ]);
    assert_eq!(names(&sorted), ["App", "lib", "Z2", "z10"], "pure sort matches macOS");
}

/// Verification.cs:1183 "files pane listing stops at its ceiling and says it is truncated".
#[test]
fn listing_stops_at_its_ceiling_and_says_it_is_truncated() {
    let parent = Temp::new("ceiling");
    let (root, _) = make_root(&parent);
    for index in 0..30 {
        write(&root, &format!("f{index}.txt"), b"x");
    }
    let capped = listing::list("", &root, 12).unwrap();
    assert!(capped.entries.len() == 12 && capped.truncated, "12 names read, listing truncated");
    let exact = listing::list("", &root, 30).unwrap();
    assert!(exact.entries.len() == 30 && !exact.truncated, "exactly the limit is not truncated");
    const { assert!(listing::MAXIMUM_ENUMERATED_NAMES == 20_000 && listing::MAXIMUM_ENTRIES_PER_FOLDER == 5_000, "the macOS caps") };
    std::fs::create_dir_all(root.join("한글 폴더")).unwrap();
    assert_eq!(listing::list("", &root, listing::MAXIMUM_ENUMERATED_NAMES).unwrap().entries[0].name, "한글 폴더");
}

/// Verification.cs:1184 "files pane refuses paths and links that leave the workspace".
#[test]
fn paths_and_links_that_leave_the_workspace_are_refused() {
    let parent = Temp::new("escape");
    let (root, outside) = make_root(&parent);
    let handles = common::handles();
    write(&outside, "secret.txt", b"secret");
    write(&root, "docs/readme.md", b"inside");
    let outside_name = outside.file_name().unwrap().to_string_lossy().into_owned();
    let resolve = |relative: &str| workspace_files::resolve(relative, &root);
    assert_eq!(resolve(&format!("../{outside_name}/secret.txt")), None, "a .. path out of the root is refused");
    assert_eq!(resolve(&format!("docs/../../{outside_name}")), None, "docs/../.. is refused");
    assert_eq!(resolve(&outside.to_string_lossy()), None, "an absolute path is refused");
    assert_eq!(resolve("docs/readme.md:secret"), None, "a Windows stream name is refused");
    assert_eq!(resolve("docs\0x"), None, "NUL is refused");
    assert!(resolve("docs/readme.md").is_some(), "a file inside resolves");
    assert_eq!(resolve(""), workspace_files::real_path(&root), "\"\" is the real root");
    let list = |relative: &str| listing::list(relative, &root, listing::MAXIMUM_ENUMERATED_NAMES);
    assert_eq!(list("docs/readme.md").unwrap_err(), ListError::NotDirectory, "listing a file says not a directory");
    assert_eq!(list(&format!("../{outside_name}")).unwrap_err(), ListError::OutsideRoot, "listing outside says outside the root");

    let links = link(&root.join("escape.txt"), &outside.join("secret.txt"), false)
        & link(&root.join("outdir"), &outside, true)
        & link(&root.join("alias"), &root.join("docs"), true)
        & link(&root.join("dangling"), &root.join("missing"), false);
    if links {
        let entries = list("").unwrap().entries;
        assert_eq!(names(&entries), ["alias", "docs"], "escaping and dangling links are not listed");
        let alias = entries.iter().find(|e| e.name == "alias").unwrap();
        assert!(alias.is_directory && alias.is_symlink, "an in-root folder link shows as a linked folder");
        assert_eq!(list("alias").unwrap().entries.iter().map(|e| e.relative_path.as_str()).collect::<Vec<_>>(), ["alias/readme.md"]);
        assert!(
            resolve("escape.txt").is_none() && resolve("outdir").is_none() && resolve("outdir/secret.txt").is_none(),
            "links out are refused"
        );
        assert_eq!(resolve("dangling"), None, "a dangling link is refused");
        assert_eq!(list("outdir").unwrap_err(), ListError::OutsideRoot, "listing a link out says outside the root");
        assert_eq!(open_file("escape.txt", &root, handles.as_ref()).unwrap_err(), OpenError::Missing, "opening a link out is refused");
    } else {
        eprintln!("  (symlink checks skipped: this account cannot create links)");
    }

    let mut opened = open_file("docs/readme.md", &root, handles.as_ref()).unwrap();
    assert_eq!(classify::read_head(&mut opened.file).unwrap(), b"inside");
    assert!(opened.size == 6 && opened.modified.is_some(), "a file inside opens and reads");
    assert_eq!(open_file("docs", &root, handles.as_ref()).unwrap_err(), OpenError::NotRegularFile, "a folder is not a regular file");
    assert_eq!(open_file("gone.txt", &root, handles.as_ref()).unwrap_err(), OpenError::Missing, "a missing file is missing");
}

/// The same rules with junctions, which any Windows account may make (a symbolic link needs a right the checks may not have): a
/// junction out of the root is neither listed nor opened, one inside it lists as a linked folder and opens through the handle check.
#[cfg(windows)]
#[test]
#[allow(clippy::disallowed_methods, reason = "the check's own mklink, no child of the app")]
fn junctions_that_leave_the_workspace_are_refused() {
    let parent = Temp::new("junction");
    let (root, outside) = make_root(&parent);
    write(&outside, "secret.txt", b"secret");
    write(&root, "docs/readme.md", b"inside");
    let junction = |at: &Path, target: &Path| {
        std::process::Command::new("cmd").arg("/c").arg("mklink").arg("/J").arg(at).arg(target).output().is_ok_and(|o| o.status.success())
    };
    if !(junction(&root.join("outdir"), &outside) && junction(&root.join("alias"), &root.join("docs"))) {
        eprintln!("  (junction checks skipped: no junction could be made here)");
        return;
    }
    let handles = common::handles();
    let list = |relative: &str| listing::list(relative, &root, listing::MAXIMUM_ENUMERATED_NAMES);
    let entries = list("").unwrap().entries;
    assert_eq!(names(&entries), ["alias", "docs"], "a junction out of the root is not listed");
    assert!(entries[0].is_directory && entries[0].is_symlink, "a junction inside lists as a linked folder");
    assert_eq!(list("outdir").unwrap_err(), ListError::OutsideRoot);
    assert!(workspace_files::resolve("outdir/secret.txt", &root).is_none());
    assert_eq!(open_file("outdir/secret.txt", &root, handles.as_ref()).unwrap_err(), OpenError::Missing);
    let mut opened = open_file("alias/readme.md", &root, handles.as_ref()).unwrap();
    assert_eq!(classify::read_head(&mut opened.file).unwrap(), b"inside", "through a junction inside, the handle is inside the root");
}

/// The handle check (C# `WorkspaceFiles.OpenFile` on Windows): a file whose handle says it is outside the root's final path is refused,
/// a handle that is no disk file is no regular file, one that cannot say is unreadable; without a handle check the path is walked again.
#[test]
fn the_open_handle_is_asked_where_it_is() {
    use mighty_platform::fake::FakeFileHandles;
    let parent = Temp::new("handles");
    let (root, outside) = make_root(&parent);
    write(&root, "a.txt", b"a");
    let real_root = workspace_files::real_path(&root).unwrap();
    let inside = FakeFileHandles::asked(Some(real_root.join("a.txt")), None);
    assert!(open_file("a.txt", &root, &inside).is_ok(), "a handle inside the root's final path");
    let swapped = FakeFileHandles::asked(Some(outside.join("secret.txt")), None);
    assert_eq!(
        open_file("a.txt", &root, &swapped).unwrap_err(),
        OpenError::Missing,
        "a folder swapped for a link between the walk and the open"
    );
    let silent = FakeFileHandles::asked(None, None);
    assert_eq!(open_file("a.txt", &root, &silent).unwrap_err(), OpenError::Unreadable, "a handle the OS cannot place");
    let device = FakeFileHandles { asks: true, not_disk: true, ..FakeFileHandles::default() };
    assert_eq!(open_file("a.txt", &root, &device).unwrap_err(), OpenError::NotRegularFile, "a handle that is no disk file");
    assert!(open_file("a.txt", &root, &FakeFileHandles::default()).is_ok(), "no handle check: the path walked again");
}

/// Verification.cs:1185 "files pane classifies by name then by sniffing the first bytes".
#[test]
fn classification_uses_names_then_sniffing() {
    let text = b"hello\n";
    let binary = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01];
    let source = |l| PreviewKind::Source(l);
    assert_eq!(classify::classify("README.md", text), PreviewKind::Markdown);
    assert_eq!(classify::classify("page.MDX", text), PreviewKind::Markdown);
    assert_eq!(classify::classify("App.swift", text), source(SourceLanguage::Swift));
    assert_eq!(classify::classify("index.tsx", text), source(SourceLanguage::JavaScript));
    assert_eq!(classify::classify("main.rs", text), source(SourceLanguage::Rust));
    assert_eq!(classify::classify("build.gradle", text), source(SourceLanguage::Gradle));
    assert_eq!(classify::classify("Dockerfile", text), source(SourceLanguage::Dockerfile));
    assert_eq!(classify::classify("Dockerfile.dev", text), source(SourceLanguage::Dockerfile));
    assert_eq!(classify::classify("Makefile", text), source(SourceLanguage::Makefile));
    assert_eq!(classify::classify(".env", text), source(SourceLanguage::Shell));
    assert_eq!(classify::classify(".env.local", text), source(SourceLanguage::Shell));
    assert_eq!(classify::classify("data.csv", text), source(SourceLanguage::Plain));
    assert_eq!(classify::classify("LICENSE", text), source(SourceLanguage::Plain), "sniffed text");
    assert_eq!(classify::classify("Program.cs", text), source(SourceLanguage::Plain), "unknown extension sniffed as text");
    assert_eq!(classify::classify("photo.JPG", &binary), PreviewKind::Image);
    assert_eq!(classify::classify("logo.svg", text), PreviewKind::Image);
    assert_eq!(classify::classify("spec.pdf", &binary), PreviewKind::Image);
    assert_eq!(classify::classify("program", &binary), PreviewKind::Unsupported);
    assert_eq!(classify::classify("Info.plist", b"bplist00\0"), PreviewKind::Unsupported, "binary plist");
    assert_eq!(classify::classify("archive.zip", &binary), PreviewKind::Unsupported);
    assert_eq!(classify::classify("empty", b""), source(SourceLanguage::Plain), "empty file is text");
    assert!(classify::file_extension(".gitignore").is_empty() && classify::file_extension("a.b.TS") == "ts", "extensions");
    let korean = "안녕".as_bytes();
    assert!(classify::looks_like_text(&korean[..korean.len() - 1]), "a UTF-8 sample cut mid-character is text");
    assert!(!classify::looks_like_text(&[0x41, 0xC3, 0x28, 0xFF, 0x41]), "bad bytes are not text");
}

/// "안녕하세요 똠방각하 abc" in CP949, as C#'s `Encoding.GetEncoding(949)` writes it.
fn cp949_sample() -> Vec<u8> {
    let (bytes, _, unmappable) = encoding_rs::EUC_KR.encode("안녕하세요 똠방각하 abc");
    assert!(!unmappable);
    bytes.into_owned()
}

/// Verification.cs:1186 "files pane text follows the byte order mark then UTF-8 then CP949".
#[test]
fn encodings_follow_the_byte_order_mark_then_utf8_then_cp949() {
    let decoded = |bytes: &[u8]| classify::decode_text(bytes, false).map(|d| (d.text, d.encoding));
    let with = |head: &[u8], body: &[u8]| [head, body].concat();
    assert_eq!(decoded(&with(&[0xEF, 0xBB, 0xBF], "가a".as_bytes())), Some(("가a".into(), TextEncodingKind::Utf8Bom)));
    assert_eq!(decoded(&[0xFF, 0xFE, 0x41, 0x00, 0x00, 0xAC]), Some(("A가".into(), TextEncodingKind::Utf16Le)));
    assert_eq!(decoded(&[0xFE, 0xFF, 0x00, 0x41, 0xAC, 0x00]), Some(("A가".into(), TextEncodingKind::Utf16Be)));
    assert_eq!(decoded(&[0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]), Some(("A".into(), TextEncodingKind::Utf32Le)));
    assert_eq!(decoded(&[0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41]), Some(("A".into(), TextEncodingKind::Utf32Be)));
    assert!(classify::looks_like_text(&[0xFF, 0xFE, 0x41, 0x00, 0x42]), "a UTF-16 sample cut mid-unit");
    assert_eq!(decoded("plain 안녕".as_bytes()), Some(("plain 안녕".into(), TextEncodingKind::Utf8)));
    let korean = cp949_sample();
    assert_eq!(decoded(&korean), Some(("안녕하세요 똠방각하 abc".into(), TextEncodingKind::Cp949)));
    let cut = classify::decode_text(&korean[..5], true).unwrap();
    assert!(cut.text == "안녕" && cut.encoding == TextEncodingKind::Cp949, "a CP949 sample may end on a lead byte");
    assert_eq!(classify::classify("notes.txt", &korean), PreviewKind::Source(SourceLanguage::Plain), "CP949 text is text");
    assert_eq!(classify::classify("notes.txt", b"a\0b"), PreviewKind::Unsupported, "NUL without a mark is binary");
    assert!(
        classify::display_name(TextEncodingKind::Cp949) == "CP949 (EUC-KR)"
            && classify::display_name(TextEncodingKind::Utf16Le) == "UTF-16 LE"
    );

    let parent = Temp::new("encodings");
    let (root, _) = make_root(&parent);
    let handles = common::handles();
    let wide: Vec<u8> = [0xFF, 0xFE].into_iter().chain("line\n".encode_utf16().flat_map(u16::to_le_bytes)).collect();
    write(&root, "wide.txt", &wide);
    write(&root, "euckr.txt", &korean);
    let read = |name: &str, limit: usize| {
        let mut opened = open_file(name, &root, handles.as_ref()).unwrap();
        classify::read_text(&mut opened.file, limit).unwrap()
    };
    let wide = read("wide.txt", classify::MAXIMUM_TEXT_BYTES);
    assert!(wide.text == "line\n" && wide.encoding == TextEncodingKind::Utf16Le, "UTF-16 file reads");
    let legacy = read("euckr.txt", classify::MAXIMUM_TEXT_BYTES);
    assert!(legacy.text == "안녕하세요 똠방각하 abc" && legacy.encoding == TextEncodingKind::Cp949, "CP949 file reads");
    let cut = read("euckr.txt", 5);
    assert!(cut.truncated && cut.text == "안녕" && cut.encoding == TextEncodingKind::Cp949, "a cut CP949 file drops the half character");
    write(&root, "big.txt", "가".repeat(10).as_bytes());
    let mut opened = open_file("big.txt", &root, handles.as_ref()).unwrap();
    let capped = classify::read_text(&mut opened.file, 7).unwrap();
    assert!(capped.truncated && capped.text == "가가" && capped.encoding == TextEncodingKind::Utf8, "text stops at the cap");
    let whole = classify::read_text(&mut opened.file, classify::MAXIMUM_TEXT_BYTES).unwrap();
    assert!(
        !whole.truncated && whole.text.chars().count() == 10 && classify::read_head(&mut opened.file).unwrap().len() == 30,
        "from the start again"
    );
    assert_eq!(classify::MAXIMUM_TEXT_BYTES, 1_048_576, "1 MB text cap");
}

/// Verification.cs:1187 "files pane preview shows source, markdown and images with the macOS caps".
#[test]
fn preview_loader_shows_source_markdown_and_images_with_the_mac_caps() {
    let parent = Temp::new("preview");
    let (root, _) = make_root(&parent);
    let (handles, ko) = (common::handles(), ko());
    write(&root, "Sources/App.swift", b"import Foundation\n\n// A comment\nlet answer = 42\nprint(\"hello\")\n");
    write(&root, "README.md", b"# Files pane\n\n- rendered **markdown**\n");
    write(&root, "big.md", "x".repeat(classify::MAXIMUM_MARKDOWN_RENDER_BYTES as usize + 1).as_bytes());
    write(&root, "image.png", &[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
    write(&root, "bad.svg", b"<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"file:///etc/hosts\"/></svg>");
    write(&root, "program.bin", &[0x00, 0x01, 0x02]);
    let load = |path: &str| preview::load(&root, path, handles.as_ref(), &ko);

    let swift = load("Sources/App.swift");
    assert_eq!(swift.kind, PreviewKind::Source(SourceLanguage::Swift));
    assert!(swift.encoding == Some(TextEncodingKind::Utf8) && source::scan(swift.text.as_deref().unwrap()).0.len() == 6, "six line starts");
    let markdown = load("README.md");
    assert!(markdown.kind == PreviewKind::Markdown && markdown.markdown_renderable, "small markdown renders");
    let big = load("big.md");
    assert!(big.kind == PreviewKind::Markdown && !big.markdown_renderable && classify::MAXIMUM_MARKDOWN_RENDER_BYTES == 131_072);
    let png = load("image.png");
    assert!(
        png.kind == PreviewKind::Image && png.image_bytes.as_ref().map(Vec::len) == Some(9),
        "an image keeps its bytes for the decoder"
    );
    let svg = load("bad.svg");
    assert!(
        svg.kind == PreviewKind::Unsupported && svg.reason == Some(ko.get("files.preview.svgExternal")),
        "an svg reading outside content"
    );
    assert_eq!(load("program.bin").kind, PreviewKind::Unsupported, "binary is unsupported");
    assert_eq!(load("gone.txt").failure, Some(ko.get("files.preview.missing")), "a vanished file says missing");
    const {
        assert!(
            classify::MAXIMUM_IMAGE_BYTES == 50 * 1_048_576
                && classify::MAXIMUM_DECODE_PIXELS == 250_000_000
                && classify::MAXIMUM_FULL_PIXELS == 100_000_000
                && classify::MAXIMUM_FIT_PIXELS == 4_096,
            "image caps match macOS"
        )
    };
    assert!(
        classify::pixel_count(40, 30) == Some(1200)
            && classify::pixel_count(0, 3).is_none()
            && classify::pixel_count(i64::MAX, 2).is_none()
    );
    assert!(classify::is_drawable(10.0, 10.0) && !classify::is_drawable(f64::INFINITY, 1.0) && !classify::is_drawable(1e30, 1.0));
}

/// Verification.cs:1188 "files pane refuses svg that loads outside content".
#[test]
fn svg_that_loads_outside_content_is_refused() {
    let outside = "/tmp/files-outside/secret.png";
    let head = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"10\" height=\"10\">";
    let external = [
        format!("<image href=\"file://{outside}\" width=\"10\" height=\"10\"/>"),
        format!("<image xlink:href=\"{outside}\" width=\"10\" height=\"10\"/>"),
        "<image href=\"secret.png\" width=\"10\" height=\"10\"/>".into(),
        "<image href=\"../files-outside/secret.png\"/>".into(),
        "<IMAGE XLINK:HREF = ' file:///etc/hosts ' />".into(),
        format!("<image href\n=\n\"{outside}\"/>"),
        format!("<image href={outside} />"),
        format!("<image href=\"&#102;ile://{outside}\"/>"),
        format!("<image href=\"&#x66;ile://{outside}\"/>"),
        "<image href=\"data:image/svg+xml;base64,PHN2Zy8+\"/>".into(),
        "<image href=\"\"/>".into(),
        "<rect style=\"fill:url(other.svg#paint)\" width=\"5\" height=\"5\"/>".into(),
        "<rect fill=\"URL( 'file:///x.svg#p' )\" width=\"5\" height=\"5\"/>".into(),
        "<rect style=\"fill:&#x75;rl(x.svg#p)\" width=\"5\" height=\"5\"/>".into(),
        "<style>rect { fill: \\75 rl(x.svg#p) }</style>".into(),
        "<style>@import \"x.css\";</style>".into(),
        "<g xml:base=\"file:///etc/\"><use href=\"#a\"/></g>".into(),
        "<use href=\"other.svg#icon\"/>".into(),
        "<script src=\"x.js\"/>".into(),
        format!("<g xmlns:q=\"http://www.w3.org/1999/xlink\"><image q:href=\"{outside}\"/></g>"),
        format!("<filter id=\"f\"><feImage href=\"{outside}\"/></filter>"),
        format!("<foreignObject width=\"5\" height=\"5\"><img xmlns=\"http://www.w3.org/1999/xhtml\" src=\"{outside}\"/></foreignObject>"),
    ];
    let prologues = [
        "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY e SYSTEM \"file:///etc/hosts\">]>",
        "<!DOCTYPE svg SYSTEM \"file:///etc/x.dtd\">",
        "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">",
        "<?xml version=\"1.0\" encoding=\"UTF-7\"?>",
        "<?xml-stylesheet href=\"file:///etc/x.css\"?>",
    ];
    let documents = external.iter().map(|e| format!("{head}{e}</svg>")).chain(prologues.iter().map(|p| format!("{p}{head}</svg>")));
    for document in documents {
        assert!(classify::svg_loads_external_content(document.as_bytes()), "external: {document}");
    }
    let wide: Vec<u8> = [0xFF, 0xFE]
        .into_iter()
        .chain(format!("{head}<image href=\"{outside}\"/></svg>").encode_utf16().flat_map(u16::to_le_bytes))
        .collect();
    assert!(classify::svg_loads_external_content(&wide), "UTF-16 hides nothing");
    assert!(classify::svg_loads_external_content(&[0x3C, 0x00, 0x73, 0x00, 0x76, 0x00, 0x67, 0x00]), "undecodable bytes count as external");
    let local = [
        "<defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\" width=\"5\" height=\"5\"/><use xlink:href=\"#g\"/>",
        "<image href=\"data:image/png;base64,iVBORw0KGgo=\" width=\"1\" height=\"1\"/>",
        "<rect style=\"fill: url( '#g' )\" width=\"5\" height=\"5\"/><text>src and href are just words &amp; &#160;</text>",
    ];
    for document in local.iter().map(|l| format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE svg>{head}{l}</svg>")) {
        assert!(!classify::svg_loads_external_content(document.as_bytes()), "local: {document}");
    }
}

/// Verification.cs:1189 "files pane line starts and highlighting match macOS".
#[test]
fn lines_break_and_highlighting_match_macos() {
    let (starts, longest) = source::scan("a\nbb\r\nccc\rd\u{2028}e\u{2029}f");
    assert!(starts == [0, 2, 6, 10, 12, 14] && longest == 3, "line starts break like NSTextView");
    assert_eq!(source::scan("").0, [0]);
    assert_eq!(source::scan("x\n").0, [0, 2]);
    assert_eq!(source::scan("\r\n\r\n").0, [0, 2, 4]);
    assert!(source::scan(&format!("{}\nshort", "x".repeat(6_000))).1 == 6_000 && source::WRAP_THRESHOLD == 5_000);

    let spans = |text: &str, language| {
        let units: Vec<u16> = text.encode_utf16().collect();
        source::tokens(text, language, source::MAXIMUM_UNITS)
            .into_iter()
            .map(|t| (t.kind, String::from_utf16_lossy(&units[t.location..t.location + t.length])))
            .collect::<Vec<_>>()
    };
    let texts = |found: Vec<(TokenKind, String)>| found.into_iter().map(|(_, t)| t).collect::<Vec<_>>();
    let swift = spans("let x = \"hi \\\"there\\\"\" // note\nreturn 42 /* a\nb */ letter", SourceLanguage::Swift);
    use TokenKind::{Comment, Keyword, Number};
    let string = TokenKind::String;
    assert_eq!(swift.iter().map(|s| s.0).collect::<Vec<_>>(), [Keyword, string, Comment, Keyword, Number, Comment]);
    assert_eq!(texts(swift), ["let", "\"hi \\\"there\\\"\"", "// note", "return", "42", "/* a\nb */"]);
    assert_eq!(
        texts(spans("def f():\n    \"\"\"doc\nmore\"\"\"\n    return x # done", SourceLanguage::Python)),
        ["def", "\"\"\"doc\nmore\"\"\"", "return", "# done"]
    );
    assert_eq!(texts(spans("echo $# # count", SourceLanguage::Shell)), ["echo", "# count"], "shell # needs a word boundary");
    assert_eq!(texts(spans("SELECT id FROM t -- all", SourceLanguage::Sql)), ["SELECT", "FROM", "-- all"], "sql is case-insensitive");
    assert_eq!(
        spans("{\"a\": true, \"b\": 1.5}", SourceLanguage::Json).iter().map(|s| s.0).collect::<Vec<_>>(),
        [string, Keyword, string, Number]
    );
    assert_eq!(texts(spans("\"open\nlet", SourceLanguage::Swift)), ["\"open", "let"], "unterminated strings stop at the line end");
    assert_eq!(texts(spans("`multi\nline`", SourceLanguage::JavaScript)), ["`multi\nline`"], "template strings span lines");
    assert!(source::tokens("let x = 1", SourceLanguage::Plain, source::MAXIMUM_UNITS).is_empty(), "plain text has no tokens");
    assert_eq!(spans("<a href=\"x\"><!-- c --></a>", SourceLanguage::Html).iter().map(|s| s.0).collect::<Vec<_>>(), [string, Comment]);
    assert!(
        source::tokens(&"let ".repeat(10), SourceLanguage::Swift, 8).len() == 2 && source::MAXIMUM_UNITS == 400_000,
        "scanning is capped"
    );
    assert!(!spans("x1 = 2", SourceLanguage::Python).iter().any(|s| s.0 == Number && s.1 == "1"), "digits inside a name are not numbers");
}

/// Verification.cs:1190 "files pane filter searches only opened folders and caps results".
#[test]
fn filter_searches_only_opened_folders_and_caps_results() {
    let ko = ko();
    let listing = |entries: Vec<FileEntry>, truncated| Ok(listing::Listing { entries, truncated });
    let mut tree = FilePaneTree::default();
    tree.apply(
        "",
        listing(
            vec![
                FileEntry::new("src", "src", true),
                FileEntry::new("node_modules", "node_modules", true),
                FileEntry::new("App.swift", "App.swift", false),
            ],
            false,
        ),
        &ko,
    );
    tree.apply("node_modules", listing(vec![FileEntry::new("app-lib", "node_modules/app-lib", true)], false), &ko);
    tree.apply("src", listing(vec![FileEntry::new("app.ts", "src/app.ts", false), FileEntry::new("inner", "src/inner", true)], false), &ko);
    tree.expanded.insert("src".into());
    let (rows, _) = tree.rows(&ko);
    let shape: Vec<(&str, usize)> = rows.iter().map(|r| (r.entry.relative_path.as_str(), r.depth)).collect();
    assert_eq!(shape, [("src", 0), ("src/app.ts", 1), ("src/inner", 1), ("node_modules", 0), ("App.swift", 0)], "opened folders indented");
    let modules = rows.iter().find(|r| r.entry.name == "node_modules").unwrap();
    assert!(!modules.is_expanded && modules.entry.is_noise(), "noise folders stay collapsed");
    tree.filter = "  APP \n".into();
    let (found, cap) = tree.rows(&ko);
    assert_eq!(
        found.iter().map(|r| r.entry.relative_path.as_str()).collect::<Vec<_>>(),
        ["App.swift", "src/app.ts"],
        "root and opened folders only"
    );
    assert!(!cap);
    tree.reveal("src/inner");
    assert!(
        tree.filter.is_empty()
            && tree.expanded.contains("src")
            && tree.expanded.contains("src/inner")
            && tree.selected_path.as_deref() == Some("src/inner"),
        "opening a filter result clears the filter and opens its folders"
    );
    tree.apply("src/inner", listing(vec![], false), &ko);
    let inner = tree.rows(&ko).0.into_iter().find(|r| r.entry.relative_path == "src/inner").unwrap();
    assert_eq!(inner.caption, Some(ko.get("files.tree.empty")), "an empty opened folder says so");
    let mut many = FilePaneTree::default();
    let entries = (0..tree::MAXIMUM_FILTER_RESULTS + 5).map(|i| FileEntry::new(&format!("f{i}"), &format!("f{i}"), false)).collect();
    many.apply("", listing(entries, true), &ko);
    many.filter = "f".into();
    let (rows, hit) = many.rows(&ko);
    assert!(rows.len() == 2_000 && hit, "the filter stops at 2,000 matches");
    assert_eq!(many.caption("", &ko), Some(ko.get_with("files.tree.truncated", &[("count", "5000")])), "a truncated folder says so");
    tree.expanded.remove("src/inner");
    assert_eq!(tree.refresh_targets(), ["", "src"]);
    assert!(!tree.children.contains_key("node_modules"), "refresh rereads the root and opened folders and drops closed listings");
}

/// Verification.cs:1191 "files pane opens with Ctrl+Shift+E, one per workspace, left of the current pane, never saved".
#[test]
fn pane_opens_with_ctrl_shift_e_one_per_workspace_left_and_is_never_saved() {
    assert_eq!(pane::SHORTCUT, "Ctrl+Shift+E", "macOS ⇧⌘E maps to Ctrl+Shift+E");
    assert!(pane::pane_id("ws1") == "files:ws1" && mighty_core::wire::identifier(&pane::pane_id(&mighty_core::wire::new_id())));
    for language in ["ko", "en"] {
        for key in [
            "menu.showFiles",
            "files.pane.title",
            "files.tree.filter",
            "files.tree.refresh",
            "files.preview.placeholder",
            "files.error.noRoom",
        ] {
            assert!(mighty_core::locale::catalogue(language).contains_key(key), "shared locale key {key} exists in {language}");
        }
    }
    let pane_id = pane::pane_id("ws1");
    let start = PaneLayoutNode::read(&json!({"sessionIds": ["a", "b"], "selectedSessionId": "b"})).unwrap();
    let ids = ["a".to_string(), "b".to_string(), pane_id.clone()];
    let tree = layout::normalize(Some(&start), &ids, Some("b")).unwrap();
    let placed = pane::place(&tree, &pane_id, Some("b"));
    let children: Vec<&PaneLayoutNode> = placed.children().collect();
    assert!(placed.kind() == "split" && placed.axis.as_deref() == Some("horizontal"), "a split");
    assert_eq!(children[0].session_ids().collect::<Vec<_>>(), [pane_id.as_str()], "the pane opens left of the current pane's group");
    assert_eq!(children[1].session_ids().collect::<Vec<_>>(), ["a", "b"]);
    let lone = layout::normalize(None, std::slice::from_ref(&pane_id), None).unwrap();
    assert_eq!(pane::place(&lone, &pane_id, None), lone, "alone it stays a tab");

    let root = Temp::new("pane");
    let snapshot = AppSnapshot::read(&json!({
        "version": 1,
        "workspaces": [{"id": "ws1", "name": "w", "path": root.text()}],
        "sessions": [
            {"id": "agent1", "workspaceId": "ws1", "title": "Claude"},
            {"id": pane_id, "workspaceId": "ws1", "title": "Files", "kind": "files", "draft": "never kept"},
            {"id": "files-other", "workspaceId": "ws1", "kind": "files"},
        ],
        "activeWorkspaceId": "ws1",
        "activeSessionId": pane_id,
    }))
    .unwrap();
    let live = normalize::normalize(&snapshot, false).unwrap();
    assert_eq!(
        live.sessions().iter().map(|s| s.id()).collect::<Vec<_>>(),
        ["agent1", pane_id.as_str()],
        "exactly one files pane per workspace"
    );
    assert_eq!(live.active_session_id.as_deref(), Some(pane_id.as_str()));
    assert_eq!(live.session(&pane_id).unwrap().draft.as_deref(), Some(""), "a files pane keeps no draft");
    let restored = normalize::normalize(&snapshot, true).unwrap();
    assert_eq!(restored.sessions().iter().map(|s| s.id()).collect::<Vec<_>>(), ["agent1"], "a restart never brings it back");
    let mut with_layout = live.clone();
    with_layout.pane_layout_active_session_ids = Some([("ws1".to_string(), Some(pane_id.clone()))].into_iter().collect());
    with_layout.pane_layouts = Some([("ws1".to_string(), Some(placed))].into_iter().collect());
    let stored = normalize::stored(&with_layout);
    assert!(!stored.to_json().contains(&pane_id), "the written state names the files pane nowhere");
    assert_eq!(stored.sessions().iter().map(|s| s.id()).collect::<Vec<_>>(), ["agent1"], "it is never written to disk");
    assert!(stored.active_session_id.is_none(), "no saved selection points at it");
    assert!(stored.pane_layout_active_session_ids.as_ref().is_some_and(|m| m.is_empty()));
}

/// Verification.cs:1192 "files pane links to another machine are refused on paper".
#[test]
fn links_to_another_machine_are_refused_on_paper() {
    use workspace_files::{follows_link_target, unc_share};
    assert_eq!(unc_share(r"\\server\share\dir\a.png").as_deref(), Some(r"server\share"), "plain UNC");
    assert_eq!(unc_share("//server/share/x").as_deref(), Some(r"server\share"), "forward-slash UNC");
    assert!(
        unc_share(r"\\?\UNC\server\share\x").as_deref() == Some(r"server\share")
            && unc_share(r"\\.\unc\Server\Share").as_deref() == Some(r"Server\Share")
    );
    assert!(
        unc_share(r"\\?\C:\Work").is_none() && unc_share(r"C:\Work\x").is_none() && unc_share("/Users/me/x").is_none(),
        "local paths are not UNC"
    );
    assert!(!follows_link_target(r"\\attacker\share\x", r"C:\Work\project\link"), "a local workspace link to a share is refused");
    assert!(!follows_link_target(r"\\?\UNC\attacker\s", r"C:\Work\link") && !follows_link_target(r"\\", r"C:\Work\link"), "every UNC form");
    assert!(!follows_link_target(r"\\other\share\x", r"\\nas\team\project\link"), "a share workspace may not reach another share");
    assert!(follows_link_target(r"\\NAS\Team\docs", r"\\nas\team\project\link"), "the same share is followed");
    assert!(follows_link_target(r"C:\Work\other", r"C:\Work\link") && follows_link_target("/tmp/x", "/Users/me/link"), "local targets");
}

/// Verification.cs:1193 "files pane svg size is read from its root element and checked like pdf".
#[test]
fn svg_size_is_read_from_its_root_element() {
    let size = |svg: &str| classify::svg_size(svg.as_bytes());
    assert_eq!(size("<svg xmlns='http://www.w3.org/2000/svg' width=\"40\" height=\"30\"/>"), Some((40.0, 30.0)), "plain numbers");
    let units = size("<?xml version='1.0'?><svg width='72pt' height='1in'></svg>").unwrap();
    assert!((units.0 - 96.0).abs() < 1e-9 && (units.1 - 96.0).abs() < 1e-9, "absolute units become CSS pixels");
    assert_eq!(size("<svg viewBox='0 0 120,80'/>"), Some((120.0, 80.0)), "viewBox when there is no size");
    assert_eq!(size("<svg width='50' viewBox='0 0 120 80'/>"), Some((50.0, 80.0)), "a missing side comes from the viewBox");
    assert!(
        size("<svg width='100%' height='100%'/>").is_none() && size("<g/>").is_none() && size("<svg width='2em' height='3em'/>").is_none()
    );
    let huge = size("<svg width='20000000' height='10'/>").unwrap();
    assert!(!classify::is_drawable(huge.0, huge.1), "a size past MaximumVectorPoints is not drawable");
    let infinite = size("<svg width='1e400' height='1'/>").unwrap();
    assert!(!classify::is_drawable(infinite.0, infinite.1), "an overflowing size is not drawable");
    let ok = size("<svg width='40' height='30'/>").unwrap();
    assert!(classify::is_drawable(ok.0, ok.1), "an ordinary svg is drawable");
}

/// Verification.cs:1209 "files pane on Windows draws at most 2,000 rows and 256 KB of source".
#[test]
fn the_windows_pane_draws_at_most_two_thousand_rows_and_256_kb() {
    let rows: Vec<usize> = (0..2_500).collect();
    let (drawn, capped) = tree::tree_rows(&rows);
    assert!(capped && drawn.len() == tree::MAXIMUM_TREE_ROWS && drawn[drawn.len() - 1] == 1_999, "2,000 rows drawn, the rest noted");
    let (all, none) = tree::tree_rows(&rows[..2_000]);
    assert!(!none && all.len() == 2_000, "exactly the cap is not cut");
    assert_eq!(tree::source_text("short\ntext"), ("short\ntext".to_string(), false), "a short file is drawn whole");
    let line = format!("{}\r\n", "a".repeat(99));
    let text = line.repeat(3_000);
    let (cut, cut_capped) = tree::source_text(&text);
    assert!(
        cut_capped && cut.encode_utf16().count() <= tree::MAXIMUM_SOURCE_UNITS && cut.ends_with('a') && text.starts_with(&cut),
        "no stray CR"
    );
    let one_line = format!("{}😀tail", "b".repeat(tree::MAXIMUM_SOURCE_UNITS - 1));
    let (single, single_capped) = tree::source_text(&one_line);
    assert!(
        single_capped && single.encode_utf16().count() == tree::MAXIMUM_SOURCE_UNITS - 1 && single.ends_with('b'),
        "never inside a pair"
    );
}

/// Verification.cs:390 "windows parity: find in the files pane source matches and steps as the Mac's find bar".
#[test]
fn source_find_matches_and_steps() {
    let matches = source::find_matches("let Foo = foo + FOO; fo", "foo");
    assert_eq!(matches, [4, 10, 16], "matches ignore case, in order");
    assert!(
        source::find_matches("aaaa", "aa") == [0, 2]
            && source::find_matches("abc", "").is_empty()
            && source::find_matches("abc", "abcd").is_empty()
    );
    assert_eq!(source::find_matches(&"a".repeat(5_000), "a").len(), source::MAXIMUM_MATCHES, "only the first 1,000 are marked");
    assert!(source::find_step(&matches, None, true, 0) == Some(0) && source::find_step(&matches, None, true, 10) == Some(1));
    assert_eq!(source::find_step(&matches, None, true, 17), Some(0), "a new search wraps round");
    assert_eq!(source::find_step(&matches, None, false, 10), Some(0), "back from a place goes to the match before it");
    assert!(
        source::find_step(&matches, Some(2), true, 0) == Some(0) && source::find_step(&matches, Some(0), false, 0) == Some(2),
        "wrapping round"
    );
    assert_eq!(source::find_step(&matches, Some(1), true, 0), Some(2));
    assert!(source::find_step(&[], None, true, 0).is_none() && source::find_step(&[], Some(0), false, 0).is_none(), "nowhere to go");
    for language in mighty_core::locale::LANGUAGES {
        for key in ["files.find.placeholder", "files.find.previous", "files.find.next", "files.find.done"] {
            let catalogue = mighty_core::locale::catalogue(language);
            let fallback = mighty_core::locale::catalogue("en");
            assert!(catalogue.get(key).or(fallback.get(key)).is_some_and(|t| !t.is_empty()), "{language}: the find bar's words: {key}");
        }
    }
}

/// Verification.cs:738 "desktop parity TranscriptAndResultFilesResolveTheSameSafePreview".
#[test]
fn transcript_and_result_files_resolve_the_same_safe_preview() {
    let parent = Temp::new("reference");
    let root = parent.path().join("workspace");
    write(&root, "src/한글.cs", "var text = \"한글\";\n".as_bytes());
    let root_text = root.to_string_lossy().into_owned();
    let from_list = reference::resolve("src/한글.cs", &root_text, Some(2));
    let source = "결과는 src/한글.cs:2 에 있어요";
    let at = source.encode_utf16().collect::<Vec<_>>().windows(2).position(|w| String::from_utf16_lossy(w) == "한글").unwrap();
    let tapped = reference::text_target_at(source, at);
    assert_eq!(tapped, Some(("src/한글.cs".into(), Some(2))), "a click recovers path and line without launching anything");
    let (path, line) = tapped.unwrap();
    let from_transcript = reference::resolve(&path, &root_text, line);
    assert!(from_list == from_transcript && from_list.as_ref().map(|t| t.relative_path.as_str()) == Some("src/한글.cs"), "the same target");
    let handles = common::handles();
    let loaded = preview::load(&root, &from_list.unwrap().relative_path, handles.as_ref(), &ko());
    assert!(loaded.text.is_some_and(|t| t.contains("한글")), "the preview reads the selected file");
    write(&root, "README.md", b"# Preview");
    assert_eq!(
        reference::text_target_at("설명 (README.md)", 6).map(|t| t.0).as_deref(),
        Some("README.md"),
        "root-level Markdown destinations"
    );
    assert_eq!(
        reference::text_target_at("설명 (my docs/한글 파일.md:3)", 17),
        Some(("my docs/한글 파일.md".into(), Some(3))),
        "spaces in names"
    );
    assert_eq!(reference::text_target_at("https://example.com/src/file.cs", 25), None, "a web URL's suffix never becomes a workspace file");
    use mighty_graph::result_files;
    let found: Vec<String> = result_files::matches(r"See C:\work\한글.cs:7 and src\other.cs").into_iter().map(|m| m.path).collect();
    assert_eq!(found, [r"C:\work\한글.cs", r"src\other.cs"], "Windows drive and relative backslash paths enter the inventory");
    assert_eq!(result_files::matches(r"C:\readme.md").into_iter().map(|m| m.path).collect::<Vec<_>>(), [r"C:\readme.md"]);
    assert_eq!(result_files::local_path(r"C:\work\file.cs").as_deref(), Some(r"C:\work\file.cs"), "drive-letter paths are local");
    assert_eq!(result_files::local_path("file:///C:/work/file.cs").as_deref(), Some("C:/work/file.cs"), "a Windows file URI");
}

/// Verification.cs:739 "desktop parity ReferencePreviewsRefuseEscapesAndFinalSymlinks".
#[test]
fn reference_previews_refuse_escapes_and_final_symlinks() {
    let parent = Temp::new("final-link");
    let root = parent.path().join("workspace");
    std::fs::create_dir_all(&root).unwrap();
    let outside = write(parent.path(), "private.txt", b"not displayed");
    let root_text = root.to_string_lossy().into_owned();
    assert!(
        reference::resolve("../private.txt", &root_text, None).is_none()
            && reference::resolve(&outside.to_string_lossy(), &root_text, None).is_none()
    );
    assert_eq!(reference::resolve("file://server/share/private.txt", &root_text, None), None, "remote file URLs are never accessed");
    if !link(&root.join("looks-local.txt"), &outside, false) {
        return;
    }
    use mighty_graph::result_files::{self, ResultFileLookup, StdFiles};
    let files = StdFiles { ignores_case: cfg!(windows) };
    assert_eq!(files.resolve("looks-local.txt", &root_text), None, "the last component's symlink cannot escape");
    assert_eq!(reference::resolve("looks-local.txt", &root_text, None), None);
    assert!(result_files::files_in(&["[open](looks-local.txt)"], Some(&root_text), &files).is_empty(), "preview and inventory agree");
}

/// C#'s `Path.IsPathRooted` refusal in `Resolve` for a root-relative name, which the page never sends but a link could.
#[test]
fn a_rooted_relative_name_is_refused() {
    let parent = Temp::new("rooted");
    let root = parent.path();
    write(root, "a.txt", b"a");
    assert!(workspace_files::resolve("/a.txt", root).is_none() && workspace_files::resolve("\\a.txt", root).is_none());
    assert!(workspace_files::resolve("a.txt", Path::new("relative-root")).is_none(), "a root that is not fully qualified");
}
