//! Parity with C# itself: generated vectors (header sniffing, the SVG rules, text decoding, next actions, attachment names,
//! frontmatter) run through MightyCode.Core by `tools/parity` (`composer`, Composer.cs) and through this crate, compared line by
//! line. Without the .NET 10 SDK the test says so and passes; `MIGHTY_PARITY=require` makes it fail instead.

mod common;

use std::fmt::Write as _;
use std::path::{Path, PathBuf};
use std::process::Command;

use mighty_composer::images::{self, AgentImageError};
use mighty_composer::svg::{self, SafeSvgDocument};
use mighty_composer::text::{self, TextEncodingKind};
use mighty_composer::{attachments, next_actions, slash};

#[allow(clippy::disallowed_methods, reason = "the check's own .NET build, no child of the app")]
fn csharp_tool() -> Option<PathBuf> {
    let tauri = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let out = tauri.join("target/parity-cs-composer");
    let built = Command::new("dotnet")
        .arg("build")
        .arg(tauri.join("tools/parity/Parity.csproj"))
        .args(["-c", "Release", "-o"])
        .arg(&out)
        .args(["--nologo", "-v", "quiet"])
        .status();
    match built {
        Ok(status) if status.success() => Some(out.join("parity.dll")),
        other => {
            let why = format!("the C# parity tool did not build ({other:?})");
            assert!(std::env::var("MIGHTY_PARITY").as_deref() != Ok("require"), "{why}");
            eprintln!("skipped: {why}");
            None
        }
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
}

/// xorshift64*: the same vectors every run.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }
    fn below(&mut self, n: usize) -> usize {
        (self.next() % n.max(1) as u64) as usize
    }
}

fn mutations(base: &[u8], random: &mut Random, count: usize) -> Vec<Vec<u8>> {
    let mut out = vec![base.to_vec()];
    for _ in 0..count {
        let mut bytes = base.to_vec();
        match random.below(3) {
            0 if !bytes.is_empty() => bytes.truncate(random.below(bytes.len())),
            1 if !bytes.is_empty() => {
                let at = random.below(bytes.len());
                bytes[at] = random.next() as u8;
            }
            _ => {
                let at = random.below(bytes.len() + 1);
                bytes.insert(at, random.next() as u8);
            }
        }
        out.push(bytes);
    }
    out
}

fn le16(v: u16) -> [u8; 2] {
    v.to_le_bytes()
}

fn bitmaps() -> Vec<Vec<u8>> {
    let mut list = vec![common::png(12, 9, 1), common::png(1, 1, 2)];
    let mut big = common::png(1, 1, 3);
    big[16..20].copy_from_slice(&8000u32.to_be_bytes());
    big[20..24].copy_from_slice(&8000u32.to_be_bytes());
    list.push(big.clone());
    big[20..24].copy_from_slice(&8001u32.to_be_bytes());
    list.push(big);
    list.push([b"GIF89a".as_slice(), &[3, 0, 2, 0, 0, 0, 0]].concat());
    list.push([b"GIF87a".as_slice(), &[0, 0, 2, 0]].concat());
    for (header, w, h) in [(40u32, 6i32, -4i32), (12, 7, 3), (20, 1, 1), (124, -9, 9)] {
        let mut bmp = vec![0u8; 54];
        bmp[0] = b'B';
        bmp[1] = b'M';
        bmp[14..18].copy_from_slice(&header.to_le_bytes());
        if header == 12 {
            bmp[18..20].copy_from_slice(&le16(w as u16));
            bmp[20..22].copy_from_slice(&le16(h as u16));
        } else {
            bmp[18..22].copy_from_slice(&w.to_le_bytes());
            bmp[22..26].copy_from_slice(&h.to_le_bytes());
        }
        list.push(bmp);
    }
    for chunk in [b"VP8 ", b"VP8L", b"VP8X", b"ABCD"] {
        let mut webp = vec![0u8; 30];
        webp[..4].copy_from_slice(b"RIFF");
        webp[8..12].copy_from_slice(b"WEBP");
        webp[12..16].copy_from_slice(chunk);
        webp[20] = 0x2F;
        webp[21..25].copy_from_slice(&0x0123_4567u32.to_le_bytes());
        webp[23] = 0x9D;
        webp[24] = 0x01;
        webp[25] = 0x2A;
        webp[26..28].copy_from_slice(&le16(0x4321));
        webp[28..30].copy_from_slice(&le16(0x8765));
        list.push(webp);
    }
    list.push(vec![0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x30, 0x03]);
    list.push(vec![0xFF, 0xD8, 0xFF, 0xFF, 0xD0, 0xFF, 0xC4, 0x00, 0x02, 0xFF, 0xC2, 0x00, 0x11, 0x08, 0x01, 0x00, 0x02, 0x00]);
    list.push(vec![0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]);
    list.push(vec![0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x01]);
    for little in [true, false] {
        for (count, kind, values) in [(2u16, 4u16, 1u32), (2, 3, 1), (1, 5, 1), (2, 4, 2), (5000, 4, 1)] {
            let mut tiff = vec![0u8; 38];
            tiff[0] = if little { b'I' } else { b'M' };
            tiff[1] = tiff[0];
            let mut put16 = |at: usize, v: u16| tiff[at..at + 2].copy_from_slice(&if little { v.to_le_bytes() } else { v.to_be_bytes() });
            put16(2, 42);
            put16(8, count);
            put16(10, 256);
            put16(12, kind);
            put16(22, 257);
            put16(24, kind);
            let mut put32 = |at: usize, v: u32| tiff[at..at + 4].copy_from_slice(&if little { v.to_le_bytes() } else { v.to_be_bytes() });
            put32(4, 8);
            put32(14, values);
            put32(18, 60);
            put32(26, values);
            put32(30, 40);
            list.push(tiff);
        }
    }
    list.push([&[0, 0, 0, 24][..], b"ftypheic", &[0, 0, 0, 0]].concat());
    list.push([&[0, 0, 0, 24][..], b"ftypavif", &[0, 0, 0, 0]].concat());
    list.push(b"%PDF-1.7".to_vec());
    list.push(b"plain text".to_vec());
    list
}

fn svgs() -> Vec<String> {
    let mut list: Vec<String> = [
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 80 40'><defs><linearGradient id='g'/></defs><rect fill='url(#g)' width='80' height='40'/></svg>",
        "<svg/>",
        "<SVG width=\"10cm\" height=\"2in\"></SVG>",
        "<svg width='100%' height='5'/>",
        "<svg width=' 12.5px ' height='+3e1pt' viewBox='0,0,1e3,2'/>",
        "<svg height='7' viewBox='0 0 9 8'/>",
        "<svg viewBox=\"0 0 9\"/>",
        "<svg viewbox='0 0 9 8 '/>",
        "<svg width='3' height='4\"/>",
        "<svgx width='3' height='4'/>",
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- c --><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2\" height=\"3\">\n  <text x=\"1\">a &amp; b &#60; &#x3e; c</text>\n</svg>",
        "<?xml version='1.0' encoding='shift_jis'?><svg/>",
        "<?xml version='1.0' encoding='ISO-8859-1'?><svg/>",
        "<?xml version='1.0' encoding=utf-8?><svg/>",
        "<?xml version='1.0'><svg/>",
        "<!DOCTYPE svg><svg/>",
        "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/x.dtd'><svg/>",
        "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>",
        "<?xml-stylesheet href='https://example.com/style.css'?><svg/>",
        "<?xml-stylesheet type='text/css'?><svg/>",
        "<svg><script>alert(1)</script></svg>",
        "<svg onload='alert(1)'/>",
        "<svg ONCLICK='x'/>",
        "<svg><foreignObject><body/></foreignObject></svg>",
        "<svg><image href='https://example.com/private.png'/></svg>",
        "<svg><image href='#local'/></svg>",
        "<svg><image xlink:href='data:image/png;base64,AAAA' xmlns:xlink='http://www.w3.org/1999/xlink'/></svg>",
        "<svg><image href=' data:image/svg+xml;base64,AAAA'/></svg>",
        "<svg><use href='&#104;ttps://example.com/shape.svg#x'/></svg>",
        "<svg><set attributeName='href' to='https://example.com'/></svg>",
        "<svg><animate attributeName='x'/></svg>",
        "<svg><style>@import 'https://example.com/a.css';</style></svg>",
        "<svg><style>rect { fill: url( #g ) }</style></svg>",
        "<svg><style>rect { fill: url(x.png) }</style></svg>",
        "<svg><style>a { b: c\\0041 }</style></svg>",
        "<svg xml:base='https://x/'/>",
        "<svg width='-1' height='40'/>",
        "<svg width='20000000' height='1'/>",
        "<svg width='0' height='1'/>",
        "<svg><![CDATA[ a < b ]]><g></g><g/></svg>",
        "<svg>&unknown;</svg>",
        "<svg><a></b></svg>",
        "<svg></svg><svg></svg>",
        "<svg/>trailing",
        "<html/>",
        "  \u{FEFF}<svg/>",
        "<svg xmlns:x='urn:other'><x:a/></svg>",
        "<svg><y:a/></svg>",
        "<svg a='1' a='2'/>",
        "<svg a='x\ty\nz'/>",
        "<svg a='&#9;&#10;'/>",
        "<svg>text\r\nline</svg>",
        "<svg>a\rb<![CDATA[c\nd]]></svg>",
        "<?xml version='1.0' standalone='yes'?>\n<svg/>\n",
        "<?xml version='1.1'?><svg/>",
        "<?xml version='1.0' encoding='latin1'?><svg>\u{e9}</svg>",
        "<!-- c --><?xml version='1.0'?><svg/>",
        "<svg><text>안녕</text></svg>",
        // Names XmlReader cannot read, which an HTML parser reads as a script, an event or a SMIL change of href.
        "<svg><set/attributeName='href'/to='https://example.com'/></svg>",
        "<svg><g/onload='alert(1)'/></svg>",
        "<svg><script/x>alert(1)</script/x></svg>",
        "<svg><g a/b='1'/></svg>",
        "<svg><script a='1' a='2'>x</script></svg>",
        "<svg><svg:set xmlns:svg='http://www.w3.org/2000/svg' attributeName='href' to='https://example.com'/></svg>",
        "<svg><SET attributeName='href' to='https://example.com'/></svg>",
        // A comment or a CDATA section inside text that spells nothing external.
        "<svg><style>rect { fill: red }<!-- c --></style><text>ur<!-- -->l</text></svg>",
        "<svg><style><![CDATA[rect { fill: ]]><![CDATA[url(#g) }]]></style></svg>",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect();
    list.push(format!("<svg>{}{}</svg>", "<g>".repeat(64), "</g>".repeat(64)));
    list.push(format!("<svg>{}{}</svg>", "<g>".repeat(65), "</g>".repeat(65)));
    list.push(format!("<svg {}/>", (0..128).map(|i| format!("a{i}='1'")).collect::<Vec<_>>().join(" ")));
    list.push(format!("<svg {}/>", (0..129).map(|i| format!("a{i}='1'")).collect::<Vec<_>>().join(" ")));
    list.push(format!("<svg width='{}' height='1'/>", "1".repeat(600)));
    list
}

fn decode_samples() -> Vec<Vec<u8>> {
    let mut list = vec![
        "plain".as_bytes().to_vec(),
        "한글".as_bytes().to_vec(),
        vec![0xC7, 0xD1, 0xB1, 0xDB],
        vec![0xC7, 0xD1, 0xB1],
        vec![0xEF, 0xBB, 0xBF, b'a'],
        vec![0xFF, 0xFE, b'a', 0, 0x5C, 0xD5],
        vec![0xFE, 0xFF, 0, b'a', 0xD8],
        vec![0xFF, 0xFE, 0, 0, b'a', 0, 0, 0],
        vec![0, 0, 0xFE, 0xFF, 0, 0, 0, b'a'],
        vec![b'a', 0, b'b'],
        vec![0xE2, 0x82],
        vec![0x80, 0x81, 0xFF],
        vec![0xED, 0xA0, 0x80],
    ];
    list.push("x".repeat(10).into_bytes());
    list
}

fn names() -> Vec<String> {
    vec![
        "../picture.txt".into(),
        "a\\b/c.png".into(),
        "..".into(),
        " . ".into(),
        "\u{1}\u{7f}name\u{85}.txt".into(),
        format!("{}😀.txt", "a".repeat(179)),
        format!("{}😀", "b".repeat(178)),
        "".into(),
        "/".into(),
    ]
}

fn texts() -> Vec<String> {
    let fixture = common::fixture("next-actions.json");
    let mut lines: Vec<String> = fixture.as_array().unwrap().iter().map(|r| r["line"].as_str().unwrap().to_string()).collect();
    let extra = [
        "◆ a → next: x, 또는 y 중 하나",
        "◆ a -> next: `/a:b c` or `ooo`",
        "◆ a → next: (a or b) or `c or d`",
        "◆ a → next: OR first or second",
        "◆ a → next:",
        "no diamond → next: a",
        "◆ one → next: a\n◆ two",
        "◆ a → next: `x`",
        "◆ a → next: ``",
    ];
    lines.extend(extra.iter().map(|s| s.to_string()));
    lines
}

fn frontmatters() -> Vec<String> {
    [
        "---\nname: x\ndescription: >\n  folded\n---",
        "---\nname: \"q\"\n  indented: no\nkey:value\n: empty\nother: 'a'b'\n---\nname: after",
        "--- \r\nname: crlf\r\n---",
        "# none",
        "---\na: \"\"\nb: '\nc: |\n---",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect()
}

fn kind_name(kind: TextEncodingKind) -> &'static str {
    match kind {
        TextEncodingKind::Utf8 => "Utf8",
        TextEncodingKind::Utf8Bom => "Utf8Bom",
        TextEncodingKind::Utf16Le => "Utf16LE",
        TextEncodingKind::Utf16Be => "Utf16BE",
        TextEncodingKind::Utf32Le => "Utf32LE",
        TextEncodingKind::Utf32Be => "Utf32BE",
        TextEncodingKind::Cp949 => "Cp949",
    }
}

fn error_name(error: AgentImageError) -> String {
    format!("{error:?}")
}

fn bits(value: f64) -> String {
    format!("{:016x}", value.to_bits())
}

/// The Rust answer to one vector line, as Composer.cs writes C#'s.
fn answer(line: &str) -> String {
    let l = common::ko();
    let parts: Vec<&str> = line.split('\t').collect();
    let bytes = |at: usize| unhex(parts[at]);
    let text = |at: usize| String::from_utf8(bytes(at)).unwrap();
    match parts[0] {
        "inspect" => match images::inspect(&bytes(2), parts[1]) {
            Ok((kind, w, h)) => format!("ok {kind} {w} {h}"),
            Err(e) => format!("error {}", error_name(e)),
        },
        "external" => svg::svg_loads_external_content(&bytes(1)).to_string(),
        "svgsize" => svg::svg_size(&bytes(1)).map_or("null".into(), |(w, h)| format!("{} {}", bits(w), bits(h))),
        "svgdoc" => svg::svg_document(&bytes(1)).to_string(),
        "safesvg" => match SafeSvgDocument::parse(&bytes(1)) {
            Ok(safe) => format!("ok {} {} {}", bits(safe.width), bits(safe.height), hex(safe.xml.as_bytes())),
            Err(e) => format!("error {}", error_name(e)),
        },
        "decode" => text::decode_text(&bytes(2), parts[1] == "1")
            .map_or("null".into(), |d| format!("{} {}", kind_name(d.encoding), hex(d.text.as_bytes()))),
        "next" => next_actions::parse(&text(1))
            .iter()
            .map(|a| format!("{}:{}", hex(a.label.as_bytes()), hex(a.fill.as_bytes())))
            .collect::<Vec<_>>()
            .join(","),
        "media" => attachments::detect_media_type(&bytes(1)).into(),
        "make" => attachments::make(&text(1), b"x", &l).map_or("error".into(), |a| hex(a.name.as_bytes())),
        "frontmatter" => {
            let mut pairs: Vec<(String, String)> = slash::frontmatter(&text(1)).into_iter().collect();
            pairs.sort_by(|a, b| mighty_core::normalize::ordinal(&a.0, &b.0));
            pairs.iter().map(|(k, v)| format!("{}:{}", hex(k.as_bytes()), hex(v.as_bytes()))).collect::<Vec<_>>().join(",")
        }
        "permission" => {
            let shown = mighty_composer::presentation::ToolPermissionPresentation::make(&text(1), &text(2), &l);
            let fields: Vec<String> = shown
                .fields
                .iter()
                .map(|f| format!("{}:{}:{}", hex(f.label.as_bytes()), hex(f.value.as_bytes()), u8::from(f.code)))
                .collect();
            format!("{} {} {}", hex(shown.title.as_bytes()), shown.headline.map_or("-".into(), |h| hex(h.as_bytes())), fields.join(","))
        }
        "questionnaire" => match mighty_composer::presentation::UserQuestionnaire::parse(&text(1)) {
            Some(asked) => asked
                .questions
                .iter()
                .map(|q| {
                    let options: Vec<String> =
                        q.options.iter().map(|o| format!("{}={}", hex(o.label.as_bytes()), hex(o.description.as_bytes()))).collect();
                    format!(
                        "{}:{}:{}:{}",
                        hex(q.header.as_bytes()),
                        hex(q.question.as_bytes()),
                        u8::from(q.multi_select),
                        options.join(";")
                    )
                })
                .collect::<Vec<_>>()
                .join(","),
            None => "null".into(),
        },
        "markdown" => images::markdown_images(&text(1))
            .iter()
            .map(|i| format!("{}:{}", hex(i.alt.as_bytes()), hex(i.source.as_bytes())))
            .collect::<Vec<_>>()
            .join(","),
        "base64" => match images::decode_base64(&text(1)) {
            Ok(bytes) => hex(&bytes),
            Err(e) => format!("error {}", error_name(e)),
        },
        "filter" => {
            let commands: Vec<slash::SlashCommand> =
                ["archify", "sc:analyze", "sc:build", "oh-my-claudecode:autopilot", "review", "Plan-Review", "İstanbul", "ß-sharp"]
                    .iter()
                    .map(|i| {
                        let description = match *i {
                            "review" => "Analyze a PR",
                            "İstanbul" => "Straße",
                            _ => "",
                        };
                        slash::SlashCommand::new(i, description, "x", slash::SlashCommandOrigin::User)
                    })
                    .collect();
            slash::filter(&commands, &text(1)).iter().map(|c| hex(c.invocation.as_bytes())).collect::<Vec<_>>().join(",")
        }
        "headline" => hex(mighty_composer::presentation::plan::headline(&text(1)).as_bytes()),
        "display" => {
            let (w, h) = images::rtf::display_size(parts[1].parse().unwrap(), parts[2].parse().unwrap());
            format!("{w} {h}")
        }
        other => format!("unknown {other}"),
    }
}

fn presentation_vectors(out: &mut String) {
    let inputs = [
        ("Bash", r#"{"command":"ls","description":"List","timeout":1.50,"run_in_background":true}"#),
        ("Bash", r#"{"command":"ls","timeout":"5","run_in_background":"yes"}"#),
        ("command_execution", r#"{"command":"x"}"#),
        ("Read", r#"{"file_path":"a","offset":10,"limit":2e3}"#),
        ("Edit", r#"{"file_path":"a","old_string":"x","new_string":"y","replace_all":false}"#),
        ("MultiEdit", r#"{"file_path":"a","edits":[1,2,3]}"#),
        ("Write", r#"{"file_path":"a","content":"c"}"#),
        ("NotebookEdit", r#"{"notebook_path":"n","cell_number":3,"edit_mode":"replace","new_source":"s"}"#),
        ("Glob", r#"{"pattern":"*.rs","path":"src"}"#),
        ("Grep", r#"{"pattern":"x","path":"p","glob":"*.cs"}"#),
        ("WebFetch", r#"{"url":"https://x"}"#),
        ("WebSearch", r#"{"query":"q"}"#),
        ("Task", r#"{"subagent_type":"t","model":"m","prompt":"p"}"#),
        ("mcp__srv__tool", r#"{"a":"b","n":1,"t":true,"f":false,"z":null,"o":{"k": 1},"l":[1, 2]}"#),
        ("mcp__x", r#"{"a":"b"}"#),
        ("file_change", r#"{"changes":[]}"#),
        ("Unknown", r#"{"a1":"1","a2":"2","a3":"3","a4":"4","a5":"5","a6":"6","a7":"7","a8":"8","a9":"9","a10":"10","a11":"11"}"#),
        ("Bash", "not json"),
        ("Bash", "[1]"),
    ];
    for (tool, input) in inputs {
        let _ = writeln!(out, "permission\t{}\t{}", hex(tool.as_bytes()), hex(input.as_bytes()));
    }
    let question = |header: &str, q: &str, multi: &str, options: &str| {
        format!(r#"{{"header":"{header}","question":"{q}","multiSelect":{multi},"options":[{options}]}}"#)
    };
    let red = r#"{"label":"Red","description":"warm"}"#;
    let blue = r#"{"label":"Blue","description":""}"#;
    let questionnaires = [
        format!(r#"{{"questions":[{}]}}"#, question("Color", "Which?", "false", &format!("{red},{blue}"))),
        format!(r#"{{"questions":[{},{}]}}"#, question("A", "Same?", "true", red), question("B", "Same?", "false", red)),
        format!(r#"{{"questions":[{}]}}"#, question(" ", "Q", "false", red)),
        format!(r#"{{"questions":[{}]}}"#, question("H", "Q", "\"no\"", red)),
        format!(r#"{{"questions":[{}]}}"#, question("H", "Q", "false", &format!("{red},{red}"))),
        format!(r#"{{"questions":[{}]}}"#, question("H\\u0001", "Q", "false", red)),
        format!(r#"{{"questions":[{}]}}"#, question("H", "Q", "false", "")),
        r#"{"questions":[]}"#.into(),
        "  {\"questions\":3}".into(),
        "plain".into(),
    ];
    for input in questionnaires {
        let _ = writeln!(out, "questionnaire\t{}", hex(input.as_bytes()));
    }
    let markdown = [
        "See ![shot](shots/a.png) and ![](<my file.png> \"title\")\n```\n![no](code.png)\n```\n![remote](https://example.com/x.png)",
        "![a](b 'c') ![d]( e ) ![f](g\"h\")",
        "  ~~~\n![x](y)\n  ~~~\n![z](w)",
        "![](  )",
        "![한글](경로/그림.png)",
    ];
    for text in markdown {
        let _ = writeln!(out, "markdown\t{}", hex(text.as_bytes()));
    }
    for text in ["aGVsbG8=", " aGVs\nbG8= ", "aGVsbG8", "aGVsbG9=", "", "====", "@@@", "aGVsbA==\u{3000}"] {
        let _ = writeln!(out, "base64\t{}", hex(text.as_bytes()));
    }
    for query in ["", "a", "auto", "SC:", "zzz", "plan", "İ", "i", "ss", "STRASSE", "straße", "-"] {
        let _ = writeln!(out, "filter\t{}", hex(query.as_bytes()));
    }
    for plan in ["# Title\nbody", "\n\n  ## Second  \n", "", "#", "#### a\tb\u{1b}[31mred", "x".repeat(300).as_str()] {
        let _ = writeln!(out, "headline\t{}", hex(plan.as_bytes()));
    }
    for (w, h) in [(1920, 1080), (600, 1600), (40, 30), (0, 30), (961, 1), (1, 1281), (1441, 721), (2, 3), (-5, 4)] {
        let _ = writeln!(out, "display\t{w}\t{h}");
    }
}

fn vectors() -> String {
    let mut random = Random(0x9E37_79B9_7F4A_7C15);
    let mut out = String::new();
    let declared = ["image/png", "image/jpeg", "IMAGE/JPG ", "image/tiff", "image/heic", "image/svg+xml", "application/pdf"];
    for base in bitmaps() {
        for sample in mutations(&base, &mut random, 12) {
            let kind = declared[random.below(declared.len())];
            let _ = writeln!(out, "inspect\t{kind}\t{}", hex(&sample));
            let _ = writeln!(out, "media\t{}", hex(&sample));
        }
    }
    for source in svgs() {
        for sample in mutations(source.as_bytes(), &mut random, 4) {
            for kind in ["external", "svgsize", "svgdoc", "safesvg"] {
                let _ = writeln!(out, "{kind}\t{}", hex(&sample));
            }
            let _ = writeln!(out, "inspect\timage/svg+xml\t{}", hex(&sample));
        }
        let utf16: Vec<u8> = [0xFF, 0xFE].into_iter().chain(source.encode_utf16().flat_map(u16::to_le_bytes)).collect();
        let _ = writeln!(out, "external\t{}", hex(&utf16));
        let _ = writeln!(out, "svgsize\t{}", hex(&utf16));
    }
    // Every byte and byte pair CP949 might read (the decoder behind every non-UTF-8 text check).
    for lead in 0x80u8..=0xFF {
        let _ = writeln!(out, "decode\t0\t{}", hex(&[lead]));
        for trail in 0x00u8..=0xFF {
            let _ = writeln!(out, "decode\t0\t{}", hex(&[lead, trail]));
        }
    }
    for sample in decode_samples() {
        for sampled in ["0", "1"] {
            let _ = writeln!(out, "decode\t{sampled}\t{}", hex(&sample));
        }
        let _ = writeln!(out, "media\t{}", hex(&sample));
    }
    for line in texts() {
        let _ = writeln!(out, "next\t{}", hex(line.as_bytes()));
    }
    for name in names() {
        let _ = writeln!(out, "make\t{}", hex(name.as_bytes()));
    }
    for source in frontmatters() {
        let _ = writeln!(out, "frontmatter\t{}", hex(source.as_bytes()));
    }
    presentation_vectors(&mut out);
    out
}

#[test]
#[allow(clippy::disallowed_methods, reason = "the check's own .NET tool, no child of the app")]
fn the_composer_core_answers_what_csharp_answers() {
    let Some(tool) = csharp_tool() else { return };
    let work = common::Temp::new("composer-parity");
    let input = work.path().join("vectors.txt");
    let output = work.path().join("cs.txt");
    let vectors = vectors();
    std::fs::write(&input, &vectors).unwrap();
    let status = Command::new("dotnet").arg(&tool).arg("composer").arg(&input).arg(&output).status().expect("dotnet");
    assert!(status.success(), "the C# tool failed");
    let csharp = std::fs::read_to_string(&output).unwrap();
    let lines: Vec<&str> = vectors.lines().collect();
    let answers: Vec<&str> = csharp.lines().collect();
    assert_eq!(lines.len(), answers.len());
    let mut differences = Vec::new();
    for (line, cs) in lines.iter().zip(&answers) {
        let rs = answer(line);
        if rs != *cs {
            let short = |s: &str| s.chars().take(400).collect::<String>();
            differences.push(format!("{}\n  C#:   {}\n  Rust: {}", short(line), short(cs), short(&rs)));
        }
    }
    if !differences.is_empty() {
        let dump = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/composer-parity-differences.txt");
        let _ = std::fs::write(&dump, differences.join("\n"));
        eprintln!("every difference: {}", dump.display());
    }
    let mut by_kind: std::collections::BTreeMap<&str, Vec<&String>> = Default::default();
    for d in &differences {
        by_kind.entry(d.split('\t').next().unwrap_or("")).or_default().push(d);
    }
    let shown: Vec<String> = by_kind
        .iter()
        .flat_map(|(kind, list)| std::iter::once(format!("== {kind}: {}", list.len())).chain(list.iter().take(12).map(|d| d.to_string())))
        .collect();
    assert!(differences.is_empty(), "{} of {} vectors differ:\n{}", differences.len(), lines.len(), shown.join("\n"));
    eprintln!("{} vectors answered the same", lines.len());
}
