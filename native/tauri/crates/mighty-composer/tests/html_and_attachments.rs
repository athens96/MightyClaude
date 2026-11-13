//! C# `GraphPreviewVerification.LocalHtmlReadsOnlyItsOwnBoundedDirectory` (Verification.cs:235) and the `AttachmentSupport` part of
//! `AttachmentVerification.Run` (Verification.cs:769). The rest of that check (the request's validation, the wire round trip,
//! staging on disk and the CLI payloads) is the run engine's (W2), which owns `StartRunRequest` and `StagedAttachments`.

mod common;

use base64::Engine as _;
use common::{Temp, ko};
use mighty_composer::attachments;
use mighty_composer::html::LocalHtmlDocument;
use mighty_core::attachment::RunAttachment;
use mighty_core::json::Wire as _;

/// Verification.cs:235 "GraphPreviewVerification LocalHtmlReadsOnlyItsOwnBoundedDirectory".
#[test]
fn local_html_reads_only_its_own_bounded_directory() {
    let root = Temp::new("html");
    let docs = root.path().join("docs");
    std::fs::create_dir_all(&docs).unwrap();
    std::fs::write(docs.join("한글.html"), "<h1>한글</h1><script src='chart.js'></script>").unwrap();
    std::fs::write(docs.join("chart.js"), "document.title='chart'").unwrap();
    std::fs::write(root.path().join("private.txt"), "outside document directory").unwrap();
    let preview = LocalHtmlDocument::new(root.path(), "docs/한글.html").unwrap();
    assert_eq!(preview.read(preview.address(), "GET").unwrap().media_type, "text/html; charset=utf-8");
    assert_eq!(preview.read(&format!("https://{}/chart.js", preview.host()), "GET").unwrap().media_type, "text/javascript; charset=utf-8");
    assert!(preview.read(&format!("https://{}/%2e%2e%2fprivate.txt", preview.host()), "GET").is_none());
    assert!(preview.read("https://example.com/chart.js", "GET").is_none());
    assert!(preview.read(preview.address(), "POST").is_none());
    assert!(!preview.allows("file:///private.txt") && !preview.allows(&format!("https://{}:444/", preview.host())));
    if common::symlink_file(&root.path().join("private.txt"), &docs.join("escape.txt")) {
        assert!(preview.read(&format!("https://{}/escape.txt", preview.host()), "GET").is_none());
    }
    assert!(
        LocalHtmlDocument::new(root.path(), "private.txt").is_err() && LocalHtmlDocument::new(root.path(), "docs/missing.html").is_err()
    );
}

const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";

/// Verification.cs:769 "attachment validation, Unicode wire, safe staging and multimodal CLI payloads" (AttachmentVerification.Run),
/// the `AttachmentSupport` checks.
#[test]
fn attachment_verification_run() {
    let l = ko();
    let png = base64::engine::general_purpose::STANDARD.decode(PNG).unwrap();
    let image = attachments::make("../picture.txt", &png, &l).unwrap();
    let text = attachments::make("source😀.cs", "\u{FEFF}a\u{200D}b".as_bytes(), &l).unwrap();
    let pdf = attachments::make("file.pdf", b"%PDF-1.7\nfixture", &l).unwrap();
    assert!(image.name == "picture.txt" && image.media_type == "image/png", "Image type must come from bytes.");
    assert_eq!(text.media_type, "text/plain", "BOM and ZWJ text must match Swift sniffing.");
    assert_eq!(pdf.media_type, "application/pdf");
    let long = attachments::make(&format!("{}😀.txt", "a".repeat(179)), b"text", &l).unwrap();
    assert!(long.name.encode_utf16().count() <= 180 && long.name.ends_with('a'), "Name truncation split a surrogate pair.");
    // The wire keeps every byte and character (Wire.Clone).
    for attachment in [&image, &text, &pdf] {
        let mut out = mighty_core::json::JsonOut::new();
        attachment.write(&mut out);
        let json = out.finish();
        assert_eq!(&RunAttachment::read(&serde_json::from_str(&json).unwrap()).unwrap(), attachment);
    }
    let plus: Vec<u8> = (0..600_000).map(|i| [251u8, 239, 190][i % 3]).collect();
    let plus = attachments::make("binary.bin", &plus, &l).unwrap();
    let mut out = mighty_core::json::JsonOut::new();
    plus.write(&mut out);
    assert!(out.finish().len() < 850_000, "Base64 escaping inflated the negotiated upload limit.");
    let reject = |attachment: RunAttachment| assert!(attachments::decode(&attachment, &l).is_err(), "{:?} accepted", attachment.name);
    reject(RunAttachment { name: "../escape".into(), ..image.clone() });
    reject(RunAttachment { name: "bad\0name".into(), ..image.clone() });
    reject(RunAttachment { data_base64: format!("{}\n", image.data_base64), ..image.clone() });
    reject(RunAttachment { media_type: "image/jpeg".into(), ..image.clone() });
    reject(RunAttachment { media_type: "application/octet-stream".into(), ..image.clone() });
    assert!(attachments::validate(&[image.clone(), image.clone()], &l).is_err());
    let nine: Vec<RunAttachment> = (0..9).map(|_| RunAttachment { id: mighty_core::wire::new_id(), ..image.clone() }).collect();
    assert!(attachments::validate(&nine, &l).is_err());
    assert!(attachments::make("big", &vec![0u8; attachments::MAXIMUM_FILE_BYTES + 1], &l).is_err());
    let large = attachments::make("large.bin", &vec![0u8; 5 * 1024 * 1024], &l).unwrap();
    assert!(attachments::validate(&[large.clone(), RunAttachment { id: mighty_core::wire::new_id(), ..large }], &l).is_err());
    assert_eq!(attachments::validate(&[image.clone(), text.clone(), pdf.clone()], &l).unwrap().unwrap().len(), 3);
    assert_eq!(attachments::validate(&[], &l), Ok(None));
    // The staged copies' extensions (AttachmentSupport.Extension): ".png" from the type, ".cs" from the name.
    assert_eq!(attachments::extension(&image), ".png");
    assert_eq!(attachments::extension(&text), ".cs");
    assert_eq!(attachments::extension(&RunAttachment { name: "noext".into(), ..text.clone() }), ".txt");
    assert_eq!(attachments::decoded_length(&image), png.len());
    assert_eq!(attachments::summary(std::slice::from_ref(&image)), format!("첨부: picture.txt ({} bytes)", png.len()));
    assert_eq!(attachments::summary(&[]), "");
}
