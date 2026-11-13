//! C# AttachmentVerification (the run engine's half: StagedAttachments and validation; the wire round trip is the remote's) and
//! BackgroundOutputVerification (the fixture, the cases and the read; the phone's blocks and the strip's words are the views').

mod common;

use base64::Engine;
use mighty_run::attachments::{self, MAXIMUM_FILE_BYTES, RunAttachment, StagedAttachments};
use mighty_run::background_output::{self, MAX_BYTES, MAX_LINE_CHARS, MAX_LINES};
use mighty_run::model::StartRunRequest;

const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";

fn png() -> Vec<u8> {
    base64::engine::general_purpose::STANDARD.decode(PNG).unwrap()
}

fn with(attachment: &RunAttachment, change: impl FnOnce(&mut RunAttachment)) -> RunAttachment {
    let mut value = attachment.clone();
    change(&mut value);
    value
}

/// "attachment validation, Unicode wire, safe staging and multimodal CLI payloads".
#[test]
fn run() {
    common::korean();
    let image = attachments::make("../picture.txt", &png()).unwrap();
    let text = attachments::make("source😀.cs", "\u{FEFF}a\u{200D}b".as_bytes()).unwrap();
    let pdf = attachments::make("file.pdf", b"%PDF-1.7\nfixture").unwrap();
    assert!(image.name == "picture.txt" && image.media_type == "image/png", "Image type must come from bytes.");
    assert_eq!(text.media_type, "text/plain", "BOM and ZWJ text must match Swift sniffing.");
    let long_name = attachments::make(&format!("{}😀.txt", "a".repeat(179)), b"text").unwrap();
    assert!(long_name.name.encode_utf16().count() <= 180, "Name truncation split a surrogate pair.");
    let mut request = StartRunRequest::new("attachment-run", "workspace", "claude", "").with_provider("codex");
    request.attachments = Some(vec![image.clone(), text.clone(), pdf.clone()]);
    let request = request.validate().unwrap();
    // C#'s Wire.Clone round trip, its upload-size and legacy-shape checks are the remote's wire (W8).
    let mut none = request.clone();
    none.attachments = None;
    assert!(none.validate().is_err());
    let mut shell = request.clone();
    shell.kind = "shell".into();
    assert!(shell.validate().is_err());
    assert!(attachments::decode(&with(&image, |a| a.name = "../escape".into())).is_err());
    assert!(attachments::decode(&with(&image, |a| a.name = "bad\0name".into())).is_err());
    assert!(attachments::decode(&with(&image, |a| a.data_base64.push('\n'))).is_err());
    assert!(attachments::decode(&with(&image, |a| a.media_type = "image/jpeg".into())).is_err());
    assert!(attachments::decode(&with(&image, |a| a.media_type = "application/octet-stream".into())).is_err());
    assert!(attachments::validate(Some(&[image.clone(), image.clone()])).is_err());
    let nine: Vec<RunAttachment> = (0..9).map(|_| with(&image, |a| a.id = mighty_core::wire::new_id())).collect();
    assert!(attachments::validate(Some(&nine)).is_err());
    assert!(attachments::make("big", &vec![0u8; MAXIMUM_FILE_BYTES + 1]).is_err());
    let large = attachments::make("large.bin", &vec![0u8; 5 * 1024 * 1024]).unwrap();
    assert!(attachments::validate(Some(&[large.clone(), with(&large, |a| a.id = mighty_core::wire::new_id())])).is_err());

    let staged = StagedAttachments::create(request.attachments.as_deref().unwrap()).unwrap();
    let directory = staged.directory.clone();
    assert!(
        staged.files.iter().all(|(attachment, path)| path.parent() == Some(directory.as_path())
            && !path.file_name().unwrap().to_string_lossy().contains(&attachment.name)),
        "Untrusted file names reached staging paths."
    );
    assert!(
        staged.files[0].1.to_string_lossy().ends_with(".png") && staged.files[1].1.to_string_lossy().ends_with(".cs"),
        "Staged extensions are wrong."
    );
    assert_eq!(std::fs::read(&staged.files[0].1).unwrap(), png(), "Staged image changed.");
    for resume in [None, Some("resume-id")] {
        let mut codex = request.clone();
        codex.resume_id = resume.map(str::to_string);
        let args = staged.arguments_for(&codex, "/plugin").unwrap();
        assert!(
            args.contains(&"--image".to_string())
                && args.last().map(String::as_str) == Some("-")
                && !args.contains(&"--add-dir".to_string()),
            "Codex image/resume mapping widened permissions or omitted stdin."
        );
    }
    let claude = request.clone().with_provider("claude");
    let input = common::json(&staged.input_for(&claude));
    let content = input["message"]["content"].as_array().unwrap().clone();
    assert!(
        content.iter().any(|c| c["type"] == "image") && content.iter().any(|c| c["type"] == "document"),
        "Claude multimodal blocks are missing."
    );
    assert!(staged.arguments_for(&claude, "/plugin").unwrap().contains(&"--input-format".to_string()), "Claude needs stream JSON input.");
    let gemini = request.clone().with_provider("gemini");
    let agy_args = staged.arguments_for(&gemini, "/plugin").unwrap();
    let gemini_input = staged.input_for(&gemini);
    assert!(
        common::after(&agy_args, "--add-dir") == staged.directory_text()
            && staged.files.iter().all(|(_, path)| gemini_input.contains(&format!("\"{}\"", path.display()))),
        "Antigravity's folder and plain file references are missing."
    );
    staged.dispose().unwrap();
    assert!(!directory.exists(), "Staging directory was not removed.");

    // Copies a run never disposed of (it was stopped right after the staging) go with their value.
    let dropped = StagedAttachments::create(request.attachments.as_deref().unwrap()).unwrap();
    let directory = dropped.directory.clone();
    assert!(directory.exists());
    drop(dropped);
    assert!(!directory.exists(), "Staged copies outlived their value.");
}

// ── BackgroundOutputVerification ────────────────────────────────────────────────────────────────────────────────────────

#[test]
fn the_constants_are_the_fixtures() {
    let fixture = common::fixture_json("background-output.json");
    assert_eq!(fixture["maxBytes"].as_u64(), Some(MAX_BYTES as u64), "maxBytes");
    assert_eq!(fixture["maxLines"].as_u64(), Some(MAX_LINES as u64), "maxLines");
    assert_eq!(fixture["maxLineChars"].as_u64(), Some(MAX_LINE_CHARS as u64), "maxLineChars");
}

#[test]
fn every_case_holds() {
    let fixture = common::fixture_json("background-output.json");
    let cases = fixture["cases"].as_array().unwrap();
    assert!(cases.len() >= 10, "the fixture has every case; got {}", cases.len());
    for entry in cases {
        let name = entry["name"].as_str().unwrap();
        let data = base64::engine::general_purpose::STANDARD.decode(entry["base64"].as_str().unwrap()).unwrap();
        let expected: Vec<String> = entry["lines"].as_array().unwrap().iter().map(|l| l.as_str().unwrap().to_string()).collect();
        let lines = background_output::lines(&data, entry["truncated"].as_bool().unwrap());
        assert_eq!(lines, expected, "{name}");
    }
}

#[test]
fn read_takes_the_end_of_a_file_another_process_writes() {
    let root = common::temp("background-output");
    let path = |name: &str| root.join(name).to_string_lossy().into_owned();
    assert!(background_output::read(&path("missing.output")).is_none(), "a missing file reads as none");
    std::fs::write(path("short.output"), "one\ntwo\n").unwrap();
    assert_eq!(
        background_output::read(&path("short.output")),
        Some(vec!["one".to_string(), "two".to_string()]),
        "a short file reads whole"
    );
    // Longer than maxBytes: the read starts inside a line, which is dropped.
    let mut text = String::new();
    let mut index = 0;
    while text.len() <= MAX_BYTES + 100 {
        text.push_str(&format!("line {index}\n"));
        index += 1;
    }
    std::fs::write(path("long.output"), &text).unwrap();
    let all: Vec<String> = text.trim_end_matches('\n').split('\n').map(str::to_string).collect();
    let tail = background_output::read(&path("long.output"));
    assert_eq!(tail.as_deref(), Some(&all[all.len() - MAX_LINES..]), "a long file reads its last lines");
    // The run still holds the file open for writing (std opens with read, write and delete sharing, as the C# check does).
    {
        use std::io::Write;
        let mut writer = std::fs::File::create(path("open.output")).unwrap();
        writer.write_all(b"building\r50%\r100%\n").unwrap();
        writer.flush().unwrap();
        assert_eq!(
            background_output::read(&path("open.output")),
            Some(vec!["100%".to_string()]),
            "a file still being written reads, its overwritten line as its last segment"
        );
    }
    std::fs::remove_dir_all(root).unwrap();
}
