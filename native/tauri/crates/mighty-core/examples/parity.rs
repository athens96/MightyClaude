//! The Rust side of the parity checks (native/tauri/tools/parity/run.ps1): the same commands as the C# tool
//! (`tools/parity/Program.cs`), run on this crate, and the comparison of the two.
//!
//!     parity save <state file | -> <out folder> [--lang ko|en|zh|ja] [--bak <file>] [--theme light|dark]
//!     parity save-corpus <corpus folder> <out folder>
//!     parity doubles|strings|times <in> <out>
//!     parity make-corpus <folder> [--repo <repository root>]   the synthetic cases and the repository's fixtures
//!     parity make-vectors <folder>                              doubles.txt, strings.txt, times.txt
//!     parity compare-corpus <corpus> <C# out> <Rust out>        exits 1 on any difference
//!     parity compare-lines <C# file> <Rust file>                exits 1 on any difference
//!
//! The comparison masks what C# cannot be told to make the same: a GUID neither side read (`Wire.Id()`), a time within ten
//! minutes of the run (`Wire.Now()`), and the time and id in the name of a damaged file put aside.

use std::path::{Path, PathBuf};
use std::process::ExitCode;

use mighty_core::json::JsonOut;
use mighty_core::locale::Locale;
use mighty_core::store::{STATE_FILE, Store};
use mighty_core::{normalize, time};
use serde_json::{Map, Value, json};

fn option<'a>(args: &'a [String], name: &str) -> Option<&'a str> {
    args.iter().position(|a| a == name).and_then(|at| args.get(at + 1)).map(String::as_str)
}

fn temp_profile() -> PathBuf {
    std::env::temp_dir().join(format!("mighty-parity-rs-{}", uuid::Uuid::new_v4().simple()))
}

pub fn save(args: &[String]) -> std::io::Result<()> {
    let (input, output) = (&args[0], Path::new(&args[1]));
    let locale = Locale::new(option(args, "--lang").unwrap_or("en"));
    std::fs::create_dir_all(output)?;
    for name in ["saved.json", "page.json", "report.json"] {
        let _ = std::fs::remove_file(output.join(name));
    }
    let profile = temp_profile();
    std::fs::create_dir_all(&profile)?;
    let state = profile.join(STATE_FILE);
    if input != "-" {
        std::fs::copy(input, &state)?;
    }
    if let Some(bak) = option(args, "--bak") {
        std::fs::copy(bak, profile.join(format!("{STATE_FILE}.bak")))?;
    }
    let mut report = Map::new();
    let mut store = Store::new(profile.clone(), None, true);
    match store.load(&locale) {
        Err(error) => {
            report.insert("load".into(), json!(error.kind()));
            report.insert("detail".into(), json!(error.to_string()));
            report.insert("notice".into(), json!(store.load_notice));
        }
        Ok(loaded) => {
            report.insert("load".into(), json!("ok"));
            report.insert("notice".into(), json!(store.load_notice));
            let mut page = JsonOut::new();
            loaded.write_with(&mut page, true, false);
            std::fs::write(output.join("page.json"), page.finish())?;
            let next = match option(args, "--theme") {
                Some(theme) => {
                    let mut changed = loaded.clone();
                    changed.theme = Some(theme.into());
                    normalize::normalize(&changed, false).unwrap_or(changed)
                }
                None => loaded,
            };
            match store.save(&next) {
                Ok(()) => {
                    report.insert("save".into(), json!("ok"));
                    std::fs::copy(&state, output.join("saved.json"))?;
                }
                Err(error) => {
                    report.insert("save".into(), json!(error.kind()));
                    report.insert("detail".into(), json!(error.to_string()));
                }
            }
            let mut files: Vec<String> =
                std::fs::read_dir(&profile)?.filter_map(|e| e.ok()).map(|e| e.file_name().to_string_lossy().to_string()).collect();
            files.sort_by(|a, b| normalize::ordinal(a, b));
            report.insert("files".into(), json!(files));
        }
    }
    std::fs::write(output.join("report.json"), Value::Object(report).to_string())?;
    let _ = std::fs::remove_dir_all(&profile);
    Ok(())
}

pub fn save_corpus(corpus: &Path, out: &Path) -> std::io::Result<()> {
    let mut folders: Vec<PathBuf> = std::fs::read_dir(corpus)?.filter_map(|e| e.ok()).map(|e| e.path()).filter(|p| p.is_dir()).collect();
    folders.sort_by(|a, b| normalize::ordinal(&a.to_string_lossy(), &b.to_string_lossy()));
    for folder in folders {
        let state = folder.join(STATE_FILE);
        let mut args = vec![if state.exists() { state.to_string_lossy().to_string() } else { "-".into() }];
        args.push(out.join(folder.file_name().unwrap_or_default()).to_string_lossy().to_string());
        if let Ok(options) = std::fs::read_to_string(folder.join("options.txt")) {
            args.extend(options.split_whitespace().map(str::to_string));
        }
        let bak = folder.join(format!("{STATE_FILE}.bak"));
        if bak.exists() {
            args.push("--bak".into());
            args.push(bak.to_string_lossy().to_string());
        }
        save(&args)?;
    }
    Ok(())
}

pub fn hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2).filter_map(|i| u8::from_str_radix(&text[2 * i..2 * i + 2], 16).ok()).collect()
}

pub fn lines(input: &str, output: &str, each: impl Fn(&str) -> String) -> std::io::Result<()> {
    let text = std::fs::read_to_string(input)?;
    let out: Vec<String> = text.lines().filter(|l| !l.is_empty()).map(each).collect();
    std::fs::write(output, out.join("\n") + "\n")
}

pub fn double(line: &str) -> String {
    let value = f64::from_bits(u64::from_str_radix(line, 16).unwrap_or(0));
    if value.is_finite() { mighty_core::json::format_double(value) } else { "error".into() }
}

pub fn time_line(line: &str) -> String {
    if let Some(ticks) = line.strip_prefix("ticks:") {
        return time::DateTimeOffset::from_utc_ticks(ticks.parse().unwrap_or(0)).to_o();
    }
    let text = String::from_utf8_lossy(&hex(&line[6..])).to_string();
    time::parse(Some(&text)).map(|t| t.to_o()).unwrap_or_else(|| "null".into())
}

// ── vectors ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02X}")).collect()
}

pub fn make_vectors(folder: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(folder)?;
    let mut random = Random(0x9E37_79B9_7F4A_7C15);
    let mut doubles: Vec<f64> = vec![
        0.0,
        -0.0,
        1.0,
        -1.0,
        0.5,
        252.0,
        300.5,
        0.1,
        0.2,
        0.3,
        1.0 / 3.0,
        2.0 / 3.0,
        1e15,
        1e14,
        999999999999999.0,
        1e16,
        1e17,
        1e21,
        1e22,
        1e-4,
        1e-5,
        1e-7,
        1.5e-7,
        123456789012345.67,
        1234567890123456.7,
        5e-324,
        f64::MAX,
        f64::MIN_POSITIVE,
        4.35,
        0.15,
        0.85,
        1400.0,
        1e308,
        100.0,
        1000000.0,
        9007199254740993.0,
        0.000123456789,
        12345.6789e-10,
    ];
    for _ in 0..4000 {
        let bits = random.next();
        let value = f64::from_bits(bits);
        if value.is_finite() {
            doubles.push(value);
        }
        // Short decimals, the common case.
        let short = (random.next() % 2_000_000) as f64 / 10f64.powi((random.next() % 12) as i32);
        doubles.push(short);
        doubles.push(-short * 10f64.powi((random.next() % 40) as i32 - 20));
    }
    let lines: Vec<String> = doubles.iter().map(|d| format!("{:016X}", d.to_bits())).collect();
    std::fs::write(folder.join("doubles.txt"), lines.join("\n") + "\n")?;

    let mut strings: Vec<String> = vec![
        "".into(),
        "plain ascii".into(),
        "quote \" backslash \\ slash /".into(),
        "<html> & 'apos' + `tick` =".into(),
        "\n\r\t\u{8}\u{c}\u{0}\u{1}\u{1f}\u{7f}\u{80}\u{9f}\u{a0}".into(),
        "한글 日本語 中文 é ñ ü".into(),
        "😀👍🏽👨‍👩‍👧‍👦🇰🇷".into(),
        "\u{2028}\u{2029}\u{feff}\u{200b}\u{fffd}\u{ffff}".into(),
        "\u{10ffff}\u{10000}".into(),
    ];
    for _ in 0..500 {
        let length = random.next() % 24;
        let s: String = (0..length)
            .filter_map(|_| {
                let pick = random.next();
                let code = match pick % 4 {
                    0 => (pick >> 8) % 0x80,
                    1 => (pick >> 8) % 0x800,
                    2 => (pick >> 8) % 0x10000,
                    _ => (pick >> 8) % 0x110000,
                };
                char::from_u32(code as u32)
            })
            .collect();
        strings.push(s);
    }
    let lines: Vec<String> = strings.iter().map(|s| to_hex(s.as_bytes())).filter(|l| !l.is_empty()).collect();
    std::fs::write(folder.join("strings.txt"), lines.join("\n") + "\n")?;

    let mut times = vec![];
    for ticks in [0i64, 621_355_968_000_000_000, 638_987_654_321_234_567, 3_155_378_975_999_999_999] {
        times.push(format!("ticks:{ticks}"));
    }
    for _ in 0..200 {
        times.push(format!("ticks:{}", 621_355_968_000_000_000 + (random.next() % 5_000_000_000_000_000) as i64));
    }
    for text in [
        "2026-11-13T10:20:30.1234567Z",
        "2026-11-13T10:20:30.1234567+00:00",
        "2026-11-13T10:20:30.123Z",
        "2026-11-13T10:20:30Z",
        "2026-11-13T10:20:30.123456789Z",
        "2026-11-13T10:20:30.12345675Z",
        "2026-11-13T10:20:30.12345674Z",
        "2026-11-13T10:20:30.9999999Z",
        "2026-11-13T10:20:30+09:00",
        "2026-11-13T10:20:30-05:30",
        "2026-11-13T10:20:30+0900",
        "2026-11-13T10:20Z",
        "2026-02-30T00:00:00Z",
        "2026-13-01T00:00:00Z",
        "not a date",
        "2026-11-13T25:00:00Z",
        "2026-11-13t10:20:30z",
        "0001-01-01T00:00:00Z",
        "9999-12-31T23:59:59.9999999Z",
    ] {
        times.push(format!("parse:{}", to_hex(text.as_bytes())));
    }
    std::fs::write(folder.join("times.txt"), times.join("\n") + "\n")
}

// ── corpus ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn case(corpus: &Path, name: &str, state: Option<&[u8]>, bak: Option<&[u8]>, options: &str) -> std::io::Result<()> {
    let folder = corpus.join(name);
    std::fs::create_dir_all(&folder)?;
    if let Some(state) = state {
        std::fs::write(folder.join(STATE_FILE), state)?;
    }
    if let Some(bak) = bak {
        std::fs::write(folder.join(format!("{STATE_FILE}.bak")), bak)?;
    }
    if !options.is_empty() {
        std::fs::write(folder.join("options.txt"), options)?;
    }
    Ok(())
}

/// A folder this OS calls fully qualified, as JSON text.
fn root(name: &str) -> String {
    if cfg!(windows) { format!("C:\\\\work\\\\{name}") } else { format!("/work/{name}") }
}

fn state_text(body: &str) -> String {
    format!(
        r#"{{"version":1,"workspaces":[{{"id":"w1","name":"Alpha","path":"{}","createdAt":"2026-10-01T00:00:00Z"}}],{body}}}"#,
        root("a")
    )
}

const SETTINGS: &str = r#"{"effort":"default","permissionMode":"manual","maxTurns":null,"maxBudgetUsd":null}"#;

fn pane(id: &str, extra: &str) -> String {
    format!(
        r#"{{"id":"{id}","workspaceId":"w1","title":"Pane {id}","kind":"claude","status":"idle","model":"default","provider":"claude","settings":{SETTINGS},"logs":[],"createdAt":"2026-10-01T00:00:00Z","draft":""{extra}}}"#
    )
}

pub fn synthetic(corpus: &Path) -> std::io::Result<usize> {
    let mut n = 0;
    let mut add = |name: &str, state: Option<String>, bak: Option<String>, options: &str| -> std::io::Result<()> {
        n += 1;
        case(corpus, &format!("synthetic-{name}"), state.as_deref().map(str::as_bytes), bak.as_deref().map(str::as_bytes), options)
    };
    let good = state_text(&format!(r#""sessions":[{}],"activeWorkspaceId":"w1","activeSessionId":"s1""#, pane("s1", "")));
    add("minimal", Some(good.clone()), None, "")?;
    add("theme-light", Some(good.clone()), None, "--theme light")?;
    add("theme-light-ko", Some(good.clone()), None, "--theme light --lang ko")?;
    add("no-file", None, None, "")?;
    add("empty-object", Some("{}".into()), None, "")?;
    add("not-json", Some(r#"{"version":1,"#.into()), None, "")?;
    add("trailing-comma", Some(r#"{"version":1,"workspaces":[],"sessions":[],}"#.into()), None, "")?;
    add("comment", Some("{\"version\":1,\"workspaces\":[],\"sessions\":[] /* x */}".into()), None, "")?;
    add("version-2", Some(r#"{"version":2,"workspaces":[],"sessions":[]}"#.into()), None, "")?;
    add("version-float", Some(r#"{"version":1.0,"workspaces":[],"sessions":[]}"#.into()), None, "")?;
    add("version-string", Some(r#"{"version":"1","workspaces":[],"sessions":[]}"#.into()), None, "")?;
    add("sessions-object", Some(r#"{"version":1,"workspaces":[],"sessions":{}}"#.into()), None, "")?;
    add("bom", Some(format!("\u{feff}{good}")), None, "")?;
    let mut invalid = good.replace("Pane s1", "Pane @@@@").into_bytes();
    if let Some(at) = invalid.windows(4).position(|w| w == b"@@@@") {
        invalid[at..at + 4].copy_from_slice(&[0xC3, 0x28, 0xFF, 0xE2]);
    }
    case(corpus, "synthetic-invalid-utf8", Some(&invalid), None, "")?;
    let nul = format!("{}\0\0\0\0", &good[..good.len() / 2]);
    add("nul-with-bak", Some(nul.clone()), Some(good.clone()), "--lang ko")?;
    add("nul-no-bak", Some(nul.clone()), None, "--lang ja")?;
    add("nul-bad-bak", Some(nul.clone()), Some("{oops".into()), "--lang zh")?;
    add("nul-only", Some("\0\0\0\0\0\0".into()), None, "")?;
    add(
        "unknown-fields",
        Some(format!(
            r#"{{"version":1,"futureTop":{{"a":[1,2,{{"b":null}}]}},"workspaces":[{{"id":"w1","name":"A","path":"{}","futureWs":true}}],"sessions":[{{"id":"s1","workspaceId":"w1","futurePane":"x","settings":{{"effort":"high","permissionMode":"plan","maxTurns":5,"maxBudgetUsd":2.5,"futureSetting":1}},"logs":[{{"id":"l1","kind":"user","text":"hi","timestamp":"2026-10-01T00:00:00Z","futureLog":[]}}]}}],"mobileRemote":{{"enabled":true,"relayURL":"relay.example.com","future":1}}}}"#,
            root("a")
        )),
        None,
        "",
    )?;
    add(
        "case-insensitive",
        Some(format!(
            r#"{{"version":1,"workspaces":[{{"ID":"w1","Name":"Upper","PATH":"{}"}}],"sessions":[{{"Id":"s1","WorkspaceId":"w1","TITLE":"T","titleMode":"fixed"}}],"Theme":"light","theme":"dark","SidebarWidth":300,"LAYOUT":"columns"}}"#,
            root("a")
        )),
        None,
        "",
    )?;
    add(
        "lenient-wrong-types",
        Some(state_text(&format!(
            r#""sessions":[{}],"autoLoginCLIs":"yes","autoUpdateCLIs":1,"backgroundWorkOpen":[],"antigravityMigrated":"no","referenceBubbleWidth":"wide","referenceBubbleHeight":{{}},"lastCreateLocation":5,"expandedWorkspaceIds":["w1",2]"#,
            pane(
                "s1",
                r#","agentViewMode":5,"graphSizesLayout":"1","graphViewMode":7,"flowLogCollapsed":1,"planHistory":{},"todoProgress":5,"backgroundWork":"x","parentSessionId":5,"workingFolder":[],"askTarget":{},"shellRuns":[1,{"key":"l1#0","command":"ls","state":"completed","exitCode":0,"output":"ok","startedAt":"2026-10-01T00:00:00Z"},{"key":5}],"runTiming":{"startedAt":"nope"},"sessionUsage":{"provider":"claude"}"#
            )
        ))),
        None,
        "",
    )?;
    for (name, body) in [
        ("fatal-sidebar-bool", r#""sessions":[],"sidebarWidth":true"#.to_string()),
        ("fatal-theme-number", r#""sessions":[],"theme":5"#.to_string()),
        ("fatal-title-number", format!(r#""sessions":[{}]"#, pane("s1", "").replace(r#""title":"Pane s1""#, r#""title":5"#))),
        ("fatal-settings-string", format!(r#""sessions":[{}]"#, pane("s1", "").replace(SETTINGS, r#""x""#))),
        ("fatal-maxturns-string", format!(r#""sessions":[{}]"#, pane("s1", "").replace(r#""maxTurns":null"#, r#""maxTurns":"5""#))),
        ("fatal-maxturns-float", format!(r#""sessions":[{}]"#, pane("s1", "").replace(r#""maxTurns":null"#, r#""maxTurns":5.5"#))),
        ("fatal-effort-null", format!(r#""sessions":[{}]"#, pane("s1", "").replace(r#""effort":"default""#, r#""effort":null"#))),
        ("fatal-logs-string", format!(r#""sessions":[{}]"#, pane("s1", "").replace(r#""logs":[]"#, r#""logs":"x""#))),
        ("fatal-collapsed-null", r#""sessions":[],"sidebarCollapsed":null"#.to_string()),
        ("fatal-mobile-port-float", r#""sessions":[],"mobileRemote":{"directPort":1.5}"#.to_string()),
        ("fatal-graph-run-null", format!(r#""sessions":[{}]"#, pane("s1", r#","graphRuns":[null]"#))),
        ("fatal-graph-input-null", format!(r#""sessions":[{}]"#, pane("s1", r#","graphRuns":[{"id":"r1","input":null}]"#))),
        ("fatal-agent-null", format!(r#""sessions":[{}]"#, pane("s1", r#","graphRuns":[{"id":"r1","agents":[null]}]"#))),
        ("fatal-deep", format!(r#""sessions":[],"deep":{}{}"#, "[".repeat(70), "]".repeat(70))),
        ("ok-deep-63", format!(r#""sessions":[],"deep":{}{}"#, "[".repeat(62), "]".repeat(62))),
    ] {
        add(name, Some(state_text(&body)), None, "")?;
    }
    add(
        "numbers-from-strings",
        Some(format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{}"}}],"sessions":[],"sidebarWidth":"300","mobileRemote":{{"directPort":"5000","relayURL":"  "}},"paneLayouts":{{"w1":{{"id":"n1","kind":"tabs","ratio":"0.3"}}}}}}"#,
            root("a")
        )),
        None,
        "",
    )?;
    // Every enum value the state holds, valid and not.
    let mut panes = vec![];
    let mut at = 0;
    for status in ["idle", "running", "completed", "error", "stopped", "waiting", "bogus"] {
        for kind in ["claude", "shell", "browser", "files", "workmap", "agent-terminal", "agent-browser", "bogus"] {
            at += 1;
            panes.push(
                pane(&format!("e{at}"), "")
                    .replace(r#""status":"idle""#, &format!(r#""status":"{status}""#))
                    .replace(r#""kind":"claude""#, &format!(r#""kind":"{kind}""#)),
            );
        }
    }
    for provider in ["claude", "codex", "gemini", "pi", "opencode", "hermes", "bogus"] {
        for mode in ["manual", "plan", "onRequest", "acceptEdits", "auto", "fullAccess", "bogus"] {
            for effort in ["default", "low", "xhigh", "max", "ultra", "off", "minimal", "bogus"] {
                at += 1;
                let settings = format!(
                    r#"{{"effort":"{effort}","permissionMode":"{mode}","maxTurns":{},"maxBudgetUsd":{},"fastMode":true,"webSearch":"{}","networkAccess":true,"advisor":"{}"}}"#,
                    at % 1200,
                    at as f64 * 1.25,
                    ["default", "disabled", "cached", "live", "bogus"][at % 5],
                    ["opus", "off", "", "bad model"][at % 4]
                );
                panes.push(
                    pane(&format!("p{at}"), "")
                        .replace(r#""provider":"claude""#, &format!(r#""provider":"{provider}""#))
                        .replace(SETTINGS, &settings),
                );
            }
        }
    }
    let modes = ["grid", "columns", "focus", "tabs", "custom", "bogus"];
    let layout_modes: Vec<String> = modes.iter().enumerate().map(|(i, m)| format!(r#""w{i}":"{m}""#)).collect();
    add(
        "every-enum",
        Some(state_text(&format!(
            r#""sessions":[{}],"layout":"focus","paneLayoutModes":{{{}}},"theme":"bogus","languagePreference":"ko","settingsPane":"remote","agentWebOpenChoices":{{"w1":"inApp","w2":"external","w9":"bogus"}},"trustedStatusLines":{{"key1":"0123456789abcdef","bad key":"x","key2":""}}"#,
            panes.join(","),
            layout_modes.join(",")
        ))),
        None,
        "",
    )?;
    for (i, layout) in ["grid", "columns", "focus", "tabs", "custom", "bogus"].iter().enumerate() {
        add(
            &format!("layout-{i}-{layout}"),
            Some(state_text(&format!(r#""sessions":[{},{}],"layout":"{layout}""#, pane("s1", ""), pane("s2", "")))),
            None,
            "",
        )?;
    }
    for (i, settings_pane) in
        ["general", "models", "styles", "tools", "cli", "mobile", "remote", "companion", "about", "bogus"].iter().enumerate()
    {
        add(
            &format!("settings-pane-{i}"),
            Some(state_text(&format!(
                r#""sessions":[],"settingsPane":"{settings_pane}","languagePreference":"{}""#,
                ["system", "ko", "en", "zh", "ja", "fr"][i % 6]
            ))),
            None,
            "",
        )?;
    }
    // Text: non-ASCII, emoji, surrogate escapes, controls, characters the encoder escapes, long titles.
    let long_title = "가".repeat(130);
    let emoji_title = "👨‍👩‍👧‍👦".repeat(125);
    add(
        "unicode",
        Some(state_text(&format!(
            r#""sessions":[{},{},{}]"#,
            pane("s1", r#","titleMode":"fixed","draft":"한글 \ud83d\ude00 \u0001\u007f <&>'+` \u2028 é","logs":[{"id":"l1","kind":"assistant","text":"日本語 中文 \ud83c\uddf0\ud83c\uddf7 \t\n\r \u0000 end","timestamp":"2026-10-01T00:00:00Z"}]"#)
                .replace("Pane s1", &long_title),
            pane("s2", r#","titleMode":"fixed""#).replace("Pane s2", &emoji_title),
            pane("s3", r#","logs":[{"id":"l1","kind":"user","text":"   /oh-my-claudecode:ask codex  explain   this\nplease  ","timestamp":"2026-10-01T00:00:00Z"},{"id":"l2","kind":"user","text":"첨부: a.png","timestamp":"2026-10-01T00:00:00Z"}]"#)
        ))),
        None,
        "",
    )?;
    add("lone-surrogate", Some(state_text(&format!(r#""sessions":[{}]"#, pane("s1", r#","draft":"\ud800 alone""#)))), None, "")?;
    // Times: 7 and 9 fractional digits, offsets, none, invalid, in the timing record and the graph's records.
    let timing = |start: &str, observed: &str, finished: &str| {
        format!(r#","runTiming":{{"startedAt":"{start}","lastObservedAt":"{observed}","finishedAt":"{finished}","isApproximate":false}}"#)
    };
    add(
        "timestamps",
        Some(state_text(&format!(
            r#""sessions":[{},{},{},{},{},{}]"#,
            pane("t1", &timing("2026-10-01T00:00:00.1234567Z", "2026-10-01T00:00:01.1234567+00:00", "2026-10-01T00:00:02.9999999Z")),
            pane("t2", &timing("2026-10-01T09:00:00+09:00", "2026-10-01T00:00:01Z", "2026-10-01T00:00:01Z")),
            pane("t3", &timing("2026-10-01T00:00:00.123456789Z", "2026-10-01T00:00:00.123456789Z", "2026-10-01T00:00:00.123456789Z")),
            pane("t4", &timing("2026-10-02T00:00:00Z", "2026-10-01T00:00:00Z", "2026-10-03T00:00:00Z")),
            pane("t5", r#","status":"running","logs":[{"id":"u1","kind":"user","text":"go","timestamp":"2026-10-01T00:00:00.5Z"},{"id":"a1","kind":"assistant","text":"done","timestamp":"2026-10-01T00:05:00.25-01:00"}]"#)
                .replace(r#""status":"idle","#, ""),
            pane("t6", r#","runTiming":{"startedAt":"2026-10-01T00:00:00Z"},"status":"running""#).replace(r#""status":"idle","#, ""),
        ))),
        None,
        "",
    )?;
    add(
        "missing-optional",
        Some(format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{}"}},{{"path":"{}"}}],"sessions":[{{"workspaceId":"w1"}},{{"id":"s2","workspaceId":"w1","kind":"shell"}},{{"id":"s3","workspaceId":"w1","settings":{{}},"logs":[{{"id":"l1","kind":"user","text":"x"}}]}}]}}"#,
            root("a"),
            root("b")
        )),
        None,
        "",
    )?;
    add(
        "nulls",
        Some(format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{}","name":null,"createdAt":null}},null,5],"sessions":[null,{{"id":"s1","workspaceId":"w1","title":null,"draft":null,"model":null,"provider":null,"status":null,"settings":null,"logs":[null,{{"id":"l1","kind":"user","text":null,"timestamp":null}},{{"id":"l2","kind":"user","text":"x","timestamp":null}}],"createdAt":null}}],"theme":null,"layout":null,"languagePreference":null,"settingsPane":null,"mobileRemote":null,"paneLayouts":{{"w1":null}},"paneLayoutModes":{{"w1":null}},"activeWorkspaceId":null}}"#,
            root("a")
        )),
        None,
        "",
    )?;
    add(
        "duplicates",
        Some(format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{0}"}},{{"id":"w1","path":"{1}"}},{{"id":"w2","path":"{1}"}}],"sessions":[{2},{2},{3}],"sessions":[{2},{3},{4}],"activeWorkspaceId":"w2","activeSessionId":"s1","expandedWorkspaceIds":["w2","w1","w2","gone"]}}"#,
            root("a"),
            root("b"),
            pane("s1", ""),
            pane("s2", "").replace(r#""workspaceId":"w1""#, r#""workspaceId":"w2""#),
            pane("s3", "")
        )),
        None,
        "",
    )?;
    add(
        "file-panes",
        Some(state_text(&format!(
            r#""sessions":[{},{{"id":"files:w1","workspaceId":"w1","kind":"files","title":"Files"}},{{"id":"workmap:w1","workspaceId":"w1","kind":"workmap"}},{{"id":"agent-terminal:s1","workspaceId":"w1","kind":"agent-terminal","ownerSessionId":"s1"}},{{"id":"b1","workspaceId":"w1","kind":"browser","title":"Web","workspaceProfileKey":"  key \u0001 ","ownerSessionId":"bad id","logs":[{{"id":"l1","kind":"user","text":"x","timestamp":"2026-10-01T00:00:00Z"}}],"resumeId":"r1","draft":"d"}}],"activeSessionId":"files:w1","paneLayoutActiveSessionIds":{{"w1":"files:w1"}}"#,
            pane("s1", "")
        ))),
        None,
        "",
    )?;
    add(
        "layouts",
        Some(format!(
            r#"{{"version":1,"workspaces":[{{"id":"w1","path":"{0}"}},{{"id":"w2","path":"{1}"}},{{"id":"w3","path":"{1}"}}],"sessions":[{2},{3},{4},{5},{6}],"activeWorkspaceId":"w1","activeSessionId":"s2","paneLayouts":{{"w1":{{"id":"root","kind":"split","axis":"vertical","ratio":0.95,"children":[{{"id":"bad id","kind":"tabs","sessionIds":["s1","gone","s1",null],"selectedSessionId":"gone"}},{{"id":"root","kind":"split","children":[{{"kind":"tabs","sessionIds":["s2"]}},{{"kind":null}}]}},{{"id":"third","kind":"tabs","sessionIds":["s3"]}}]}},"w2":{{"id":"x","kind":"bogus"}},"gone":{{"id":"y"}}}},"paneLayoutModes":{{"w2":"tabs"}},"paneLayoutActiveSessionIds":{{"w2":"s5","w3":"nope"}}}}"#,
            root("a"),
            root("b"),
            pane("s1", ""),
            pane("s2", ""),
            pane("s3", ""),
            pane("s4", "").replace(r#""workspaceId":"w1""#, r#""workspaceId":"w2""#),
            pane("s5", "").replace(r#""workspaceId":"w1""#, r#""workspaceId":"w2""#)
        )),
        None,
        "",
    )?;
    let graph = r#","graphRuns":[{"id":"r0","input":"old","status":"running","rootEntries":[],"agents":[]},{"id":"r1","input":"first request","status":"completed","rootEntries":[{"id":"e1","kind":"assistant","text":"answer","timestamp":"2026-10-01T00:00:00Z","provider":"codex","activity":{"id":"act1","provider":"claude","kind":"command","state":"running","summary":"  ls\n-la  ","toolName":"Bash","output":"x","durationMs":12.5}},{"id":"e1","kind":"assistant","text":"dup","timestamp":"t"},{"id":"bad id","kind":"assistant","text":"x","timestamp":"t"}],"agents":[{"id":"a1","parentID":"a2","title":"A1","input":"do","status":"running","entries":[],"kind":"bogus","usage":{"inputTokens":5,"outputTokens":0,"cacheReadTokens":0,"cacheCreationTokens":0},"activityGeneration":7,"background":true,"outputFile":"C:\\tmp\\out.txt","identity":{"type":"general\npurpose","requestedModel":"opus","resolvedModel":"bad model","isolation":"worktree"},"figures":{"startedAt":"2026-10-01T00:00:00Z","endedAt":"bogus","toolCalls":-1,"exitCode":2}},{"id":"a2","parentID":"a1","title":"A2","input":"","status":"completed","entries":[{"id":"x-answer","kind":"assistant","text":"beat","timestamp":"2026-10-01T00:00:00Z"}]},{"id":"a3","parentID":"a3","title":"","input":"q","status":"bogus","kind":"question","entries":[],"noticeReply":"","sourceBlockId":"bad id"}],"resultEntries":[{"id":"r1-result","kind":"assistant","text":"old","timestamp":"2026-10-01T00:00:00Z"}],"sourceRunID":"src-1","finalOutput":"the result\n\n---\n\n**notice**\n\nmore","usage":{"inputTokens":1,"outputTokens":2,"cacheReadTokens":3,"cacheCreationTokens":4},"responseRecords":[{"responseId":"resp","model":null,"usage":{"inputTokens":1},"activityIds":["x",null]},null],"provider":"claude","nodeModelLabel":"Opus","configuredModel":"opus","configuredEffort":"ultra","advisor":{"model":"opus","usage":{"inputTokens":3},"total":40,"calls":[{"id":"c1","state":"consulting","text":"  advice  ","redacted":false,"errorCode":"E\n1","beforeChange":true},{"id":"c1","state":"done"},{"id":"bad id","state":"done"},null]},"configuredAdvisor":"opus","steps":[{"key":"k1","content":"step one","activeForm":"stepping","status":"in_progress","startedAt":"2026-10-01T00:00:00Z","endedAt":"later"},{"key":"k2","content":"two","status":"bogus"}],"stepsCarried":["k0"]},{"id":"r2","input":"notice","status":"error","noticeRun":true,"noticeTitle":"Task · build","finalOutput":"partial","agents":[],"rootEntries":[]}],"graphBlockSizes":{"pending-input":{"width":10,"height":5000},"2:r1:request":{"width":500,"height":300},"2:r1:execution":{"width":600,"height":300},"2:r1:result":{"width":1e9,"height":-1},"2:r1:agent:a1":{"width":350.5,"height":200.25},"2:r1:plan-record:b":{"width":400,"height":100},"2:r1:plan-record:a":{"width":400,"height":90},"2:r2:request":{"width":320,"height":150},"bad\u0001id":{"width":400,"height":400}},"graphResultSize":{"width":100,"height":100},"graphPlanSize":{"width":2000,"height":20},"graphViewMode":"timeline","flowLogCollapsed":true,"mightyStyle":"style-a","mightyStyleHash":"abc","mightyStyleSince":"2026-10-01T00:00:00Z""#;
    add("graph-runs", Some(state_text(&format!(r#""sessions":[{}]"#, pane("s1", graph)))), None, "")?;
    add(
        "graph-runs-split-layout",
        Some(state_text(&format!(r#""sessions":[{}]"#, pane("s1", &format!("{graph},\"graphSizesLayout\":1"))))),
        None,
        "",
    )?;
    add(
        "plans-and-work",
        Some(state_text(&format!(
            r#""sessions":[{},{}]"#,
            pane(
                "s1",
                &format!(
                    r#","planHistory":[{{"id":"p1","runId":"r1","plan":"{}","receivedAt":"2026-10-01T00:00:00Z","decidedAt":"2026-10-01T00:00:01Z","outcome":"approvedAuto","feedback":"fb","graphRunId":"bad id","launchOverride":"x"}},{{"id":"p2","plan":"short","decidedAt":"2026-10-01T00:00:01Z","outcome":"revised","planTruncated":false}},{{"id":"p3","plan":"x","decidedAt":"no","outcome":"revised"}},{{"id":"p4","plan":"x","decidedAt":"2026-10-01T00:00:01Z","outcome":"bogus"}}],"todoProgress":{{"items":[{{"content":"  one  ","status":"in_progress","activeForm":"","id":"  "}},{{"content":"","status":"pending"}},{{"content":"two","status":"bogus","activeForm":"doing two","id":"t2"}},null],"runId":"r1"}},"backgroundWork":{{"tasks":[{{"id":"b1","kind":"agent","description":"desc\nline","startedAt":"2026-10-01T00:00:00Z","status":"running","toolUseId":"bad id","summary":"\u001b[31mred\u001b[0m","endedAt":"x"}},{{"id":"b 2","kind":"shell","description":"d","startedAt":"2026-10-01T00:00:00Z"}},{{"id":"b3","kind":"bogus","description":"d","startedAt":"2026-10-01T00:00:00Z","status":"bogus"}}],"turnEnded":false}},"shellRuns":[{{"key":"l1#0","command":"ls","state":"running","exitCode":null,"output":"{}","startedAt":"2026-10-01T00:00:00Z","endedAt":"{}"}},{{"key":"l1#0","command":"pwd","state":"completed","exitCode":0,"output":"o","startedAt":"2026-10-01T00:00:00Z"}},{{"key":"bad","command":"ls","state":"completed","output":"o","startedAt":"s"}}],"askTarget":"codex","parentSessionId":"s2","workingFolder":"{}""#,
                    "line\\n".repeat(9000),
                    "ab".repeat(9000),
                    "x".repeat(70),
                    root("wt")
                )
            ),
            pane("s2", r#","provider":"codex","todoProgress":{"items":[{"content":"c","status":"completed"}]},"planHistory":[{"id":"p1","plan":"x","decidedAt":"2026-10-01T00:00:01Z","outcome":"revised"}]"#)
                .replace(r#","provider":"claude""#, ""),
        ))),
        None,
        "",
    )?;
    add(
        "usage-and-activity",
        Some(state_text(&format!(
            r#""sessions":[{},{}]"#,
            pane("s1", r#","sessionUsage":{"provider":"claude","source":"claude.stream-json","tokenScope":"response","model":"opus","selectedModel":"bad model","providerSessionId":"sess-1","inputTokens":100,"outputTokens":50,"cacheReadTokens":200,"cacheWriteTokens":10,"reasoningTokens":60,"totalTokens":150,"contextUsedTokens":1000,"contextWindowTokens":200000,"costUSD":0.125,"costScope":"run","rateLimits":[{"kind":"five_hour","percentUsed":42.5,"resetsAt":"2026-10-01T05:00:00Z"},{"kind":"five_hour","percentUsed":1},{"kind":"spend_limit","percentUsed":5000},{"kind":"weekly"}],"rateLimitsUpdatedAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-01T00:00:00.1234567+00:00"},"logs":[{"id":"l1","kind":"assistant","text":"x","timestamp":"2026-10-01T00:00:00Z","provider":"bogus","activity":{"id":"a1","provider":"claude","kind":"turn","state":"waiting","summary":"s","durationMs":5}},{"id":"l2","kind":"image","text":"pic","timestamp":"2026-10-01T00:00:00Z","images":[{"hash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","mediaType":"IMAGE/JPG","width":10,"height":20,"bytes":30,"source":"  shot\n1 ","path":"C:\\pics\\a.png"},{"hash":"bad"},null]},{"id":"l3","kind":"assistant","text":"has images","timestamp":"t","images":[{"hash":"x"}]},{"id":"l4","kind":"bogus","text":"x","timestamp":"t"},{"id":"l5","kind":"user","text":"","timestamp":"t"}]"#),
            pane("s2", r#","provider":"codex","sessionUsage":{"provider":"claude","source":"x","tokenScope":"run","updatedAt":"2026-10-01T00:00:00Z"}"#).replace(r#","provider":"claude""#, "")
        ))),
        None,
        "",
    )?;
    add(
        "mobile-and-models",
        Some(state_text(
            r#""sessions":[],"mobileRemote":{"enabled":true,"relayURL":"HTTP://Relay.Example.COM:80/path?q#f","allowLegacyPhones":false,"directEnabled":false,"directPort":80},"phaseModels":{"claudeMain":"opus","claudeOpusAlias":"bad model","codexPlanModeReasoningEffort":"ultra","claudeMainEffort":"high","codexMainEffort":"bogus"},"modelDefaults":{"claude":{"modeDefaults":{"plan":"opus","x":null},"registeredModels":[{"name":"m1","supportsEffort":true,"supportedEffortLevels":["low",null]},null]}},"claudeDirectUsageLookupEnabled":true,"appUpdateAutoCheck":false,"appUpdateLastCheckedAt":"2026-10-01","appUpdateManifestUrlOverride":"https://x","browserEngineEnabled":true,"completionNotificationsEnabled":false,"statusLineEnabled":false,"autoUpdatePlugins":true,"sidebarCollapsed":true,"sidebarWidth":1000.75,"referenceBubbleWidth":120.5,"referenceBubbleHeight":1e-7"#,
        )),
        None,
        "",
    )?;
    for (i, relay) in [
        "wss://relay.example.com",
        "relay.example.com:8443",
        "ws://a b",
        "ftp://x",
        "https://u:p@h",
        "[::1]:9000",
        "wss://[FE80::1]",
        "http://h:443",
        "  ",
        "https://ex.com/",
    ]
    .iter()
    .enumerate()
    {
        add(
            &format!("relay-{i}"),
            Some(state_text(&format!(
                r#""sessions":[],"mobileRemote":{{"enabled":true,"relayURL":"{relay}","directPort":{}}}"#,
                [0, 1023, 1024, 65535, 65536, 43139][i % 6]
            ))),
            None,
            "",
        )?;
    }
    add(
        "antigravity",
        Some(state_text(&format!(
            r#""sessions":[{},{},{}]"#,
            pane("g1", r#","model":"gemini-2.5-pro","resumeId":"old-session""#)
                .replace(r#""provider":"claude""#, r#""provider":"gemini""#)
                .replace(r#""model":"default","#, ""),
            pane("g2", r#","model":"gemini-4-argon-high""#)
                .replace(r#""provider":"claude""#, r#""provider":"gemini""#)
                .replace(r#""model":"default","#, ""),
            pane("g3", r#","model":"weird \u0001 model <&>""#)
                .replace(r#""provider":"claude""#, r#""provider":"gemini""#)
                .replace(r#""model":"default","#, "")
        ))),
        None,
        "--lang ko",
    )?;
    add(
        "antigravity-migrated",
        Some(state_text(&format!(
            r#""sessions":[{}],"antigravityMigrated":true"#,
            pane("g1", r#","resumeId":"keep-me""#).replace(r#""provider":"claude""#, r#""provider":"gemini""#)
        ))),
        None,
        "",
    )?;
    // Near the 8 MiB cap: a long transcript (retention, the 2 MiB text budget, the 128 KiB answer limit), and one over it.
    let mut logs = vec![];
    for i in 0..450 {
        let text = if i % 50 == 0 { "답".repeat(60_000) } else { format!("entry {i} {}", "word ".repeat(2000)) };
        logs.push(format!(
            r#"{{"id":"l{i}","kind":"{}","text":"{text}","timestamp":"2026-10-01T00:00:00Z"}}"#,
            if i % 3 == 0 { "user" } else { "assistant" }
        ));
    }
    let big = state_text(&format!(
        r#""sessions":[{},{}]"#,
        pane("s1", &format!(r#","logs":[{}]"#, logs.join(","))).replace(r#","logs":[]"#, ""),
        pane("s2", r#","draft":"tail pane""#)
    ));
    add("huge-logs", Some(big.clone()), None, "")?;
    let over = state_text(&format!(r#""sessions":[],"pad":"{}""#, "x".repeat(8 * 1024 * 1024)));
    add("too-large", Some(over), None, "")?;
    // What C#'s own converters read with exact names and JSON elements (no case folding, no numbers from strings).
    add(
        "converters-exact-names",
        Some(state_text(&format!(
            r#""sessions":[{},{},{}]"#,
            pane(
                "c1",
                r#","settings":{"Effort":"high","permissionMode":"plan","maxTurns":1e2},"runTiming":{"StartedAt":"2026-10-01T00:00:00Z"}"#
            )
            .replace(&format!(r#","settings":{SETTINGS}"#), ""),
            pane("c2", r#","settings":{"effort":"high","permissionMode":"plan","maxBudgetUsd":"2"}"#)
                .replace(&format!(r#","settings":{SETTINGS}"#), ""),
            pane(
                "c3",
                r#","logs":[{"id":"l1","kind":"assistant","text":"x","timestamp":"2026-10-01T00:00:00Z","activity":{"ID":"a1","provider":"claude","kind":"tool","state":"completed","summary":"s"}},{"id":"l2","kind":"assistant","text":"y","timestamp":"2026-10-01T00:00:00Z","activity":{"id":"a2","provider":"claude","kind":"tool","state":"completed","summary":"first","summary":"second","durationMs":"5"}}],"runTiming":{"startedAt":"2026-10-01T00:00:00","finishedAt":"2026-10-01T01:00:00","isApproximate":1}"#
            ),
        ))),
        None,
        "",
    )?;
    for (name, extra) in [
        ("agent-title-null", r#","graphRuns":[{"id":"r1","agents":[{"id":"a1","title":null}]}]"#),
        ("entry-id-null", r#","graphRuns":[{"id":"r1","rootEntries":[{"kind":"assistant","text":"x","timestamp":"t"}]}]"#),
        ("tokens-float", r#","graphRuns":[{"id":"r1","usage":{"inputTokens":1e3}}]"#),
        ("tokens-string", r#","graphRuns":[{"id":"r1","usage":{"inputTokens":"12"}}]"#),
        ("block-size-string", r#","graphBlockSizes":{"pending-input":{"width":"400","height":"300.5"}}"#),
        (
            "negative-zero",
            r#","graphResultSize":{"width":-0,"height":-0.0},"settings":{"effort":"default","permissionMode":"manual","maxTurns":-0,"maxBudgetUsd":-0.0}"#,
        ),
    ] {
        add(
            name,
            Some(state_text(&format!(
                r#""sessions":[{}]"#,
                pane("s1", extra).replace(if name == "negative-zero" { SETTINGS } else { "\u{0}" }, "{}")
            ))),
            None,
            "",
        )?;
    }
    // Not here: a number literal beyond ±1.8e308 (C# reads it as infinity; serde_json refuses the document). See the README.
    add("port-negative-zero", Some(state_text(r#""sessions":[],"mobileRemote":{"directPort":-0}"#)), None, "")?;
    Ok(n)
}

pub fn fixtures(corpus: &Path, repo: &Path) -> std::io::Result<usize> {
    let mut n = 0;
    // The delegation records: their `@{root}` placeholder made a folder this OS calls absolute (and once kept as it is).
    let records = repo.join("scripts/fixtures/delegation-record");
    if let Ok(entries) = std::fs::read_dir(&records) {
        let mut entries: Vec<_> = entries.filter_map(|e| e.ok()).collect();
        entries.sort_by_key(|e| e.file_name());
        for entry in entries {
            let state = entry.path().join("profile").join(STATE_FILE);
            let Ok(text) = std::fs::read_to_string(&state) else { continue };
            let name = entry.file_name().to_string_lossy().to_string();
            let absolute = text.replace("@{root}", &root("record").replace("\\\\", "/"));
            case(corpus, &format!("delegation-{name}"), Some(absolute.as_bytes()), None, "")?;
            case(corpus, &format!("delegation-{name}-raw"), Some(text.as_bytes()), None, "")?;
            n += 2;
        }
    }
    let references = repo.join("native/contracts/fixtures/remote-v2/state-references.json");
    if let Ok(text) = std::fs::read_to_string(&references)
        && let Ok(value) = serde_json::from_str::<Value>(&text)
    {
        let cases = value["cases"].as_array().cloned().unwrap_or_default();
        let workspaces: Vec<Value> = cases.iter().map(|c| c["workspace"].clone()).collect();
        let sessions: Vec<Value> = cases
            .iter()
            .map(|c| json!({"id": format!("pane-{}", c["workspace"]["id"].as_str().unwrap_or("")), "workspaceId": c["workspace"]["id"], "title": c["name"], "titleMode": "fixed", "askTarget": "codex"}))
            .collect();
        let state = json!({"version": 1, "workspaces": workspaces, "sessions": sessions, "activeWorkspaceId": "far"});
        case(corpus, "state-references", Some(state.to_string().as_bytes()), None, "")?;
        n += 1;
        for (i, c) in cases.iter().enumerate() {
            let state = json!({"version": 1, "workspaces": [c["workspace"].clone()], "sessions": []});
            case(corpus, &format!("state-references-{i:02}"), Some(state.to_string().as_bytes()), None, "")?;
            n += 1;
        }
    }
    let unknown = repo.join("native/tauri/crates/mighty-core/tests/fixtures/unknown-fields.json");
    if let Ok(bytes) = std::fs::read(&unknown) {
        case(corpus, "unknown-fields-fixture", Some(&bytes), None, "")?;
        n += 1;
    }
    let demo = repo.join("native/contracts/fixtures/demo-profile.json");
    if let Ok(text) = std::fs::read_to_string(&demo)
        && let Ok(profile) = serde_json::from_str::<Value>(&text)
    {
        for language in ["ko", "en"] {
            let projects = if cfg!(windows) { PathBuf::from("C:\\work\\Projects") } else { PathBuf::from("/work/Projects") };
            let state = mighty_core::demo::demo_state(&profile, language, &projects);
            case(
                corpus,
                &format!("demo-{language}"),
                Some(serde_json::to_string_pretty(&state).unwrap_or_default().as_bytes()),
                None,
                &format!("--lang {language}"),
            )?;
            n += 1;
        }
    }
    Ok(n)
}

// ── comparison ──────────────────────────────────────────────────────────────────────────────────────────────────────────

fn is_guid_at(b: &[u8], at: usize) -> bool {
    const SHAPE: [usize; 5] = [8, 4, 4, 4, 12];
    let mut i = at;
    for (part, len) in SHAPE.iter().enumerate() {
        for _ in 0..*len {
            if !b.get(i).is_some_and(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()) {
                return false;
            }
            i += 1;
        }
        if part < 4 {
            if b.get(i) != Some(&b'-') {
                return false;
            }
            i += 1;
        }
    }
    true
}

/// The text with every GUID the input does not hold numbered in order (`<id1>`, ...), every `"O"` time of the last ten minutes
/// made `<now>`, and a damaged file's aside name made `<aside>`.
pub fn masked(text: &str, input: &str) -> String {
    let b = text.as_bytes();
    let mut out = String::with_capacity(text.len());
    let mut ids: Vec<String> = vec![];
    let now = time::DateTimeOffset::now().utc_ticks();
    let mut at = 0;
    while at < b.len() {
        if at + 36 <= b.len() && is_guid_at(b, at) && (at == 0 || !b[at - 1].is_ascii_hexdigit()) {
            let guid = &text[at..at + 36];
            if !input.contains(guid) {
                let index = ids.iter().position(|g| g == guid).unwrap_or_else(|| {
                    ids.push(guid.to_string());
                    ids.len() - 1
                });
                out.push_str(&format!("<id{}>", index + 1));
                at += 36;
                continue;
            }
        }
        if let Some(rest) = text.get(at..).filter(|r| r.starts_with("unreadable-")) {
            let tail = &rest.as_bytes()[11..];
            if tail.len() >= 24 && tail[8] == b'-' && tail[15] == b'-' {
                out.push_str("unreadable-<aside>");
                at += 11 + 24;
                continue;
            }
        }
        // 2026-11-13T10:20:30.1234567\u002B00:00 or +00:00
        if at + 27 <= b.len()
            && b[at + 4] == b'-'
            && b[at + 10] == b'T'
            && b[at + 19] == b'.'
            && b[at..at + 4].iter().all(u8::is_ascii_digit)
        {
            let tail = &text[at + 27..];
            let offset = if tail.starts_with("\\u002B00:00") {
                Some(11)
            } else if tail.starts_with("+00:00") {
                Some(6)
            } else {
                None
            };
            if let Some(offset) = offset {
                let stamp = format!("{}+00:00", &text[at..at + 27]);
                if let Some(t) = time::parse(Some(&stamp))
                    && (now - t.utc_ticks()).abs() < 600 * 10_000_000
                    && !input.contains(&text[at..at + 27])
                {
                    out.push_str("<now>");
                    at += 27 + offset;
                    continue;
                }
            }
        }
        let c = text[at..].chars().next().unwrap_or(' ');
        out.push(c);
        at += c.len_utf8();
    }
    out
}

fn first_difference(a: &str, b: &str) -> String {
    let at = a.bytes().zip(b.bytes()).position(|(x, y)| x != y).unwrap_or(a.len().min(b.len()));
    let start = at.saturating_sub(120);
    let window = |s: &str| {
        let mut from = start.min(s.len());
        while !s.is_char_boundary(from) {
            from -= 1;
        }
        let mut to = (at + 120).min(s.len());
        while !s.is_char_boundary(to) {
            to += 1;
        }
        s[from..to].to_string()
    };
    format!("at byte {at} (C# {} bytes, Rust {} bytes)\n    C#:   …{}…\n    Rust: …{}…", a.len(), b.len(), window(a), window(b))
}

fn report_of(path: &Path, input: &str) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_default();
    let mut value: Value = serde_json::from_str(&masked(&text, input)).unwrap_or(Value::Null);
    if let Some(map) = value.as_object_mut() {
        map.remove("detail");
    }
    value
}

pub fn compare_corpus(corpus: &Path, cs: &Path, rs: &Path) -> std::io::Result<bool> {
    let mut folders: Vec<PathBuf> = std::fs::read_dir(corpus)?.filter_map(|e| e.ok()).map(|e| e.path()).filter(|p| p.is_dir()).collect();
    folders.sort();
    let mut same = 0;
    let mut different = 0;
    for folder in &folders {
        let name = folder.file_name().unwrap_or_default().to_string_lossy().to_string();
        let input = String::from_utf8_lossy(&std::fs::read(folder.join(STATE_FILE)).unwrap_or_default()).to_string()
            + &String::from_utf8_lossy(&std::fs::read(folder.join(format!("{STATE_FILE}.bak"))).unwrap_or_default());
        let mut problems = vec![];
        let (a, b) = (report_of(&cs.join(&name).join("report.json"), &input), report_of(&rs.join(&name).join("report.json"), &input));
        if a != b {
            problems.push(format!("report differs:\n    C#:   {a}\n    Rust: {b}"));
        }
        for file in ["saved.json", "page.json"] {
            let x = std::fs::read(cs.join(&name).join(file)).ok();
            let y = std::fs::read(rs.join(&name).join(file)).ok();
            match (x, y) {
                (None, None) => {}
                (Some(x), Some(y)) if x == y => {}
                (Some(x), Some(y)) => {
                    let (x, y) = (masked(&String::from_utf8_lossy(&x), &input), masked(&String::from_utf8_lossy(&y), &input));
                    if x != y {
                        problems.push(format!("{file} differs {}", first_difference(&x, &y)));
                    }
                }
                (x, y) => problems.push(format!(
                    "{file}: C# {}, Rust {}",
                    if x.is_some() { "wrote it" } else { "did not" },
                    if y.is_some() { "wrote it" } else { "did not" }
                )),
            }
        }
        if problems.is_empty() {
            same += 1;
        } else {
            different += 1;
            println!("DIFF  {name}");
            for problem in problems {
                println!("  {problem}");
            }
        }
    }
    println!("parity: {} cases, {same} the same, {different} different", folders.len());
    Ok(different == 0)
}

pub fn compare_lines(a: &Path, b: &Path) -> std::io::Result<bool> {
    let x = std::fs::read_to_string(a)?;
    let y = std::fs::read_to_string(b)?;
    let mut different = 0;
    let (xs, ys): (Vec<&str>, Vec<&str>) = (x.lines().collect(), y.lines().collect());
    for (i, (p, q)) in xs.iter().zip(ys.iter()).enumerate() {
        if p != q {
            different += 1;
            if different <= 20 {
                println!("DIFF  line {}: C# {p} | Rust {q}", i + 1);
            }
        }
    }
    if xs.len() != ys.len() {
        different += 1;
        println!("DIFF  {} lines in C#, {} in Rust", xs.len(), ys.len());
    }
    println!("parity: {} lines of {}, {different} different", xs.len(), a.file_name().unwrap_or_default().to_string_lossy());
    Ok(different == 0)
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(command) = args.first() else {
        eprintln!("parity save|save-corpus|doubles|strings|times|make-corpus|make-vectors|compare-corpus|compare-lines ...");
        return ExitCode::from(2);
    };
    let rest = &args[1..];
    let outcome: std::io::Result<bool> = match command.as_str() {
        "save" => save(rest).map(|_| true),
        "save-corpus" => save_corpus(Path::new(&rest[0]), Path::new(&rest[1])).map(|_| true),
        "doubles" => lines(&rest[0], &rest[1], double).map(|_| true),
        "strings" => lines(&rest[0], &rest[1], |l| mighty_core::json::escape(&String::from_utf8_lossy(&hex(l)))).map(|_| true),
        "times" => lines(&rest[0], &rest[1], time_line).map(|_| true),
        "make-vectors" => make_vectors(Path::new(&rest[0])).map(|_| true),
        "make-corpus" => {
            let corpus = Path::new(&rest[0]);
            let repo =
                option(rest, "--repo").map(PathBuf::from).unwrap_or_else(|| Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../.."));
            synthetic(corpus).and_then(|a| fixtures(corpus, &repo).map(|b| a + b)).map(|count| {
                println!("corpus: {count} cases in {}", corpus.display());
                true
            })
        }
        "compare-corpus" => compare_corpus(Path::new(&rest[0]), Path::new(&rest[1]), Path::new(&rest[2])),
        "compare-lines" => compare_lines(Path::new(&rest[0]), Path::new(&rest[1])),
        other => {
            eprintln!("unknown command {other}");
            return ExitCode::from(2);
        }
    };
    match outcome {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::FAILURE,
        Err(error) => {
            eprintln!("parity {command}: {error}");
            ExitCode::from(2)
        }
    }
}
