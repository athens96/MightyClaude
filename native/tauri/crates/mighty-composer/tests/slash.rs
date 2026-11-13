//! C# `SlashCommandVerification` and `SlashPaletteVerification` (MightyCode.Core.Tests), one test per registered check
//! (Verification.cs:1037-1040, 1063-1075), names kept.

mod common;

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use common::{Temp, en, ko, write};
use mighty_composer::slash::{
    self, DiskFiles, EntryKind, SlashArgument, SlashChoiceEffect, SlashCommand, SlashCommandAction, SlashCommandOrigin, SlashFiles,
    SlashPaletteState, palette, strings,
};
use mighty_core::locale::Locale;

fn invocations(list: &[SlashCommand]) -> Vec<&str> {
    list.iter().map(|c| c.invocation.as_str()).collect()
}

/// Verification.cs:1037 "slash query parsing" (SlashCommandVerification.QueryParsing).
#[test]
fn query_parsing() {
    assert_eq!(slash::query("/"), Some(""));
    assert_eq!(slash::query("/ar"), Some("ar"));
    assert_eq!(slash::query("/archify make a diagram"), None);
    assert_eq!(slash::query("hello /ar"), None);
    assert_eq!(slash::query(&format!("/{}", "a".repeat(81))), None);
    assert_eq!(slash::query(&format!("/{}", "a".repeat(80))), Some("a".repeat(80).as_str()));
    assert_eq!(slash::query("hello"), None);
}

/// Verification.cs:1038 "slash argument query" (SlashCommandVerification.ArgumentQuery).
#[test]
fn argument_query() {
    let l = ko();
    assert_eq!(slash::argument_query("/model "), Some(("model", "")));
    assert_eq!(slash::argument_query("/model cla"), Some(("model", "cla")));
    assert_eq!(slash::argument_query("/approval-mode plan"), Some(("approval-mode", "plan")));
    assert_eq!(slash::argument_query("/model"), None);
    assert_eq!(slash::argument_query("/model a b"), None);
    assert_eq!(slash::argument_query("/model  "), None);
    assert_eq!(slash::argument_query("/bad name! x"), None);
    assert_eq!(slash::argument_query("model x"), None);
    let choices: Vec<SlashCommand> = ["default", "claude-opus-5", "claude-sonnet-5"]
        .iter()
        .map(|v| {
            SlashCommand::new(&format!("model {v}"), v, &strings::model_source(&l), SlashCommandOrigin::App)
                .with_action(SlashCommandAction::SetModel, Some(v))
        })
        .collect();
    assert_eq!(
        invocations(&slash::filter(&choices, "model cla")),
        ["model claude-opus-5", "model claude-sonnet-5"],
        "argument completion filter must match by invocation prefix"
    );
    assert_eq!(slash::filter(&choices, "model ").len(), 3, "empty argument query returns all choices");
}

/// Verification.cs:1039 "slash builtins per provider" (SlashCommandVerification.BuiltinsPerProvider).
#[test]
fn builtins_per_provider() {
    let l = ko();
    let claude = slash::catalog_builtins("claude", &l);
    assert_eq!(invocations(&claude), ["plugin", "model", "permissions", "clear", "cost", "usage", "config", "rename", "help"]);
    assert!(
        claude.iter().all(|c| c.source == strings::app_source(&l) && c.action.is_some() != c.argument.is_some()),
        "exactly one of action or argument"
    );
    assert_eq!(claude.iter().find(|c| c.invocation == "plugin").unwrap().action, Some(SlashCommandAction::OpenPlugins));
    assert_eq!(claude.iter().find(|c| c.invocation == "model").unwrap().argument, Some(SlashArgument::Model));
    assert_eq!(claude.iter().find(|c| c.invocation == "permissions").unwrap().argument, Some(SlashArgument::Permission));
    assert_eq!(
        invocations(&slash::catalog_builtins("codex", &l)),
        ["plugins", "model", "approvals", "new", "status", "settings", "rename", "help"]
    );
    let gemini = slash::catalog_builtins("gemini", &l);
    assert_eq!(invocations(&gemini), ["model", "permissions", "clear", "usage", "config", "rename", "help"]);
    assert!(!gemini.iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins)), "Antigravity must not have plugin browser");
    assert!(slash::catalog_builtins("shell", &l).is_empty());
    let help = slash::help_text("claude", &ko());
    assert!(
        help.starts_with("앱 명령 · Claude 실행 창\n/plugin · ") && help.contains("\n/clear · ") && help.contains("CLI에 전달"),
        "{help}"
    );
    assert!(slash::help_text("claude", &en()).starts_with("App commands · Claude pane\n/plugin · Open the plugin marketplace"));
    let mut mixed = claude.clone();
    mixed.push(SlashCommand::new("plan-review", "", "x", SlashCommandOrigin::User));
    assert_eq!(invocations(&slash::filter(&mixed, "pl")), ["plugin", "plan-review"]);
}

/// Verification.cs:1040 "slash filter" (SlashCommandVerification.Filter).
#[test]
fn filter() {
    let commands: Vec<SlashCommand> = ["archify", "sc:analyze", "sc:build", "oh-my-claudecode:autopilot", "review"]
        .iter()
        .map(|inv| SlashCommand::new(inv, if *inv == "review" { "Analyze a PR" } else { "" }, "x", SlashCommandOrigin::User))
        .collect();
    assert_eq!(invocations(&slash::filter(&commands, "")), invocations(&commands));
    assert_eq!(invocations(&slash::filter(&commands, "a")), ["archify", "sc:analyze", "oh-my-claudecode:autopilot", "review"]);
    assert_eq!(invocations(&slash::filter(&commands, "auto")), ["oh-my-claudecode:autopilot"]);
    assert_eq!(invocations(&slash::filter(&commands, "SC:")), ["sc:analyze", "sc:build"]);
    assert!(slash::filter(&commands, "zzz").is_empty());
    let fixture = [
        SlashCommand::new("review", "Fixture command", "fixture", SlashCommandOrigin::Project),
        SlashCommand::new("deploy", "", "fixture", SlashCommandOrigin::User),
    ];
    for language in ["ko", "en"] {
        let l = Locale::new(language);
        for provider in mighty_core::wire::PROVIDERS {
            let expected: Vec<&str> = if language == "en" {
                vec!["review", if matches!(provider, "codex" | "pi" | "opencode" | "hermes") { "new" } else { "clear" }]
            } else {
                vec!["review"]
            };
            assert_eq!(invocations(&palette::rows(provider, "claude", "/rev", &fixture, None, &l)), expected, "{language} {provider}");
            assert_eq!(invocations(&palette::rows(provider, "claude", "/review", &fixture, None, &l)), ["review"], "{language} {provider}");
        }
    }
}

/// Verification.cs:1063 "slash frontmatter" (SlashCommandVerification.Frontmatter).
#[test]
fn frontmatter() {
    let fm = slash::frontmatter("---\nname: x\ndescription: >\n  folded\n---");
    assert!(fm.len() == 1 && fm["name"] == "x");
    assert!(slash::frontmatter("# no frontmatter").is_empty());
    let normal = slash::frontmatter("---\nname: gstack\ndescription: \"Router for the suite\"\n---\n# body");
    assert_eq!(normal.get("name").map(String::as_str), Some("gstack"));
    assert_eq!(normal.get("description").map(String::as_str), Some("Router for the suite"));
    assert_eq!(slash::frontmatter("---\nname: 'archify'\n---").get("name").map(String::as_str), Some("archify"));
}

fn discovery_tree(root: &Path) -> (PathBuf, PathBuf) {
    let home = root.join("home");
    let workspace = root.join("repo");
    write(&home.join(".claude/skills/_gstack-command/SKILL.md"), "---\nname: gstack\ndescription: \"Router for the suite\"\n---\n# body\n");
    write(&home.join(".claude/skills/archify/SKILL.md"), "---\ndescription: 'Make diagrams'\n---\n");
    write(&home.join(".claude/skills/plain/SKILL.md"), "no frontmatter here\n");
    write(&home.join(".claude/skills/weird dir/SKILL.md"), "---\nname: bad name!\n---\n");
    write(&home.join(".claude/commands/sc/analyze.md"), "---\nallowed-tools: [Read]\ndescription: \"Analyze code\"\n---\n");
    write(&home.join(".claude/commands/deploy.md"), "Just a prompt body line\nmore\n");
    let plugin = root.join("cache/official/ralph/1.0.0");
    write(&plugin.join("skills/ralph-loop/SKILL.md"), "---\nname: ralph-loop\ndescription: Loop\n---\n");
    write(&plugin.join("commands/cancel-ralph.md"), "---\ndescription: Cancel\n---\n");
    let escaped = |p: &Path| p.to_string_lossy().replace('\\', "\\\\");
    write(
        &home.join(".claude/plugins/installed_plugins.json"),
        &format!(
            "{{\"version\":2,\"plugins\":{{\"ralph@official\":[{{\"scope\":\"user\",\"installPath\":\"{}\"}}],\"ghost@official\":[{{\"installPath\":\"{}\"}}],\"../x@y\":[{{\"installPath\":\"{}\"}}]}}}}",
            escaped(&plugin),
            escaped(&root.join("missing")),
            escaped(&plugin)
        ),
    );
    write(&workspace.join(".claude/skills/archify/SKILL.md"), "---\nname: archify\ndescription: Project override\n---\n");
    write(&home.join(".codex/skills/hatch-pet/SKILL.md"), "---\nname: hatch-pet\ndescription: Pets\n---\n");
    (home, workspace)
}

/// Verification.cs:1064 "slash discovery from temp home and workspace" (SlashCommandVerification.Discovery).
#[test]
fn discovery() {
    let l = ko();
    let root = Temp::new("slash");
    let (home, workspace) = discovery_tree(root.path());
    let claude = slash::commands("claude", Some(&workspace), &home, &DiskFiles, &l);
    assert_eq!(invocations(&claude), ["archify", "deploy", "gstack", "plain", "ralph:cancel-ralph", "ralph:ralph-loop", "sc:analyze"]);
    let find = |name: &str| claude.iter().find(|c| c.invocation == name).unwrap();
    assert_eq!(find("archify").description, "Project override");
    assert_eq!(find("archify").source, strings::project_skill_source(&l));
    assert_eq!(find("gstack").description, "Router for the suite");
    assert_eq!(find("deploy").description, "Just a prompt body line");
    assert_eq!(find("ralph:ralph-loop").source, "플러그인 ralph");
    assert_eq!(find("sc:analyze").description, "Analyze code");
    let codex = slash::commands("codex", Some(&workspace), &home, &DiskFiles, &l);
    assert!(codex.len() == 1 && codex[0].invocation == "hatch-pet" && codex[0].source == strings::codex_skill_source(&l));
    assert!(slash::commands("gemini", None, &home, &DiskFiles, &l).is_empty());
    assert!(slash::commands("claude", None, &root.path().join("nowhere"), &DiskFiles, &l).is_empty());
}

/// Verification.cs:1065 "slash 400 cap" (SlashCommandVerification.CapAt400).
#[test]
fn cap_at_400() {
    let root = Temp::new("slash-cap");
    let home = root.path().join("home");
    for i in 0..500 {
        write(&home.join(format!(".claude/skills/skill{i:04}/SKILL.md")), &format!("---\ndescription: skill {i}\n---\n"));
    }
    assert_eq!(slash::commands("claude", None, &home, &DiskFiles, &ko()).len(), slash::MAXIMUM_COMMANDS);
}

/// Command groups nest eight folders deep at most, and a folder linked to itself or to one above it is read once (C# reads on until the
/// path is too long for the system).
#[test]
fn command_groups_are_read_once_and_eight_deep_at_most() {
    let root = Temp::new("slash-depth");
    let home = root.path().join("home");
    let commands = home.join(".claude").join("commands");
    let deep = |levels: usize| (1..=levels).fold(commands.clone(), |path, level| path.join(format!("g{level}")));
    write(&deep(8).join("eighth.md"), "eight down");
    write(&deep(9).join("ninth.md"), "nine down");
    write(&commands.join("top.md"), "top");
    let found = |l: &Locale| slash::commands("claude", None, &home, &DiskFiles, l).into_iter().map(|c| c.invocation).collect::<Vec<_>>();
    assert_eq!(found(&ko()), ["g1:g2:g3:g4:g5:g6:g7:g8:eighth", "top"]);
    #[cfg(windows)]
    {
        #[allow(clippy::disallowed_methods, reason = "the test's own mklink, no child of the app")]
        let junction = |link: &Path, target: &Path| {
            std::process::Command::new("cmd")
                .arg("/c")
                .arg("mklink")
                .arg("/J")
                .arg(link)
                .arg(target)
                .output()
                .is_ok_and(|o| o.status.success())
        };
        // A junction anyone may make: to the commands folder itself, and to a group above it.
        if junction(&commands.join("again"), &commands) && junction(&deep(2).join("up"), &deep(1)) {
            assert_eq!(found(&ko()), ["g1:g2:g3:g4:g5:g6:g7:g8:eighth", "top"], "each folder once");
        } else {
            eprintln!("skipped: no junction could be made here");
        }
    }
}

/// The catalogue through a fixture tree instead of the disk: the same answers as `discovery` (the trait the host's tests use).
#[test]
fn discovery_through_fixture_files() {
    #[derive(Default)]
    struct Fixture(HashMap<PathBuf, Vec<u8>>);
    impl SlashFiles for Fixture {
        fn is_dir(&self, path: &Path) -> bool {
            self.0.keys().any(|k| k.starts_with(path) && k != path)
        }
        fn is_file(&self, path: &Path) -> bool {
            self.0.contains_key(path)
        }
        fn entries(&self, dir: &Path) -> std::io::Result<Vec<(String, EntryKind)>> {
            let mut out: Vec<(String, EntryKind)> = Vec::new();
            for key in self.0.keys() {
                if let Ok(rest) = key.strip_prefix(dir) {
                    let mut parts = rest.components();
                    let Some(first) = parts.next() else { continue };
                    let name = first.as_os_str().to_string_lossy().into_owned();
                    let kind = if parts.next().is_some() { EntryKind::Directory } else { EntryKind::File };
                    if !out.iter().any(|(n, _)| *n == name) {
                        out.push((name, kind));
                    }
                }
            }
            Ok(out)
        }
        fn len(&self, path: &Path) -> Option<u64> {
            self.0.get(path).map(|b| b.len() as u64)
        }
        fn read(&self, path: &Path) -> std::io::Result<Vec<u8>> {
            self.0.get(path).cloned().ok_or_else(|| std::io::Error::from(std::io::ErrorKind::NotFound))
        }
    }
    let root = Temp::new("slash-fixture");
    let (home, workspace) = discovery_tree(root.path());
    let mut fixture = Fixture::default();
    let mut stack = vec![root.path().to_path_buf()];
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(&dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                stack.push(path);
            } else {
                let bytes = std::fs::read(&path).unwrap();
                fixture.0.insert(path, bytes);
            }
        }
    }
    let l = ko();
    assert_eq!(
        slash::commands("claude", Some(&workspace), &home, &fixture, &l),
        slash::commands("claude", Some(&workspace), &home, &DiskFiles, &l)
    );
}

// ── SlashPaletteVerification ────────────────────────────────────────────────────────────────────────────────────────────

fn scanned(l: &Locale) -> Vec<SlashCommand> {
    vec![
        SlashCommand::new("review", "코드 검토", &strings::project_skill_source(l), SlashCommandOrigin::Project),
        SlashCommand::new("deploy", "", &strings::user_command_source(l), SlashCommandOrigin::User),
        SlashCommand::new("rename", "스캔된 rename 은 앱 명령에 밀린다", &strings::user_command_source(l), SlashCommandOrigin::User),
    ]
}

fn choices(argument: SlashArgument, command: &str) -> Vec<SlashCommand> {
    let l = ko();
    let model = |v: &str, d: &str| {
        SlashCommand::new(&format!("{command} {v}"), d, &strings::model_source(&l), SlashCommandOrigin::App)
            .with_action(SlashCommandAction::SetModel, Some(v))
    };
    let permission = |v: &str, d: &str| {
        SlashCommand::new(&format!("{command} {v}"), d, &strings::permission_source(&l), SlashCommandOrigin::App)
            .with_action(SlashCommandAction::SetPermission, Some(v))
    };
    match argument {
        SlashArgument::Model => {
            vec![model("default", "CLI 기본값"), model("claude-opus-5", "Opus 5 · 현재"), model("claude-sonnet-5", "Sonnet 5")]
        }
        SlashArgument::Permission => vec![permission("manual", "기본 권한"), permission("plan", "계획")],
    }
}

fn state_for(draft: &str, dismissed_for: Option<&str>, kind: &str) -> SlashPaletteState {
    let l = ko();
    palette::state("claude", kind, draft, dismissed_for, &scanned(&l), Some(&choices), &l)
}

fn state(draft: &str) -> SlashPaletteState {
    state_for(draft, None, "claude")
}

/// Verification.cs:1066 "slash palette opens on a query with matches" (SlashPaletteVerification.Opens).
#[test]
fn opens() {
    let all = state("/");
    assert!(all.is_open() && all.highlighted_index == 0);
    assert!(all.commands.iter().any(|c| c.invocation == "model") && all.commands.iter().any(|c| c.invocation == "review"));
    assert_eq!(all.commands.iter().filter(|c| c.invocation == "rename").count(), 1);
    assert_eq!(all.commands.iter().find(|c| c.invocation == "rename").unwrap().origin, SlashCommandOrigin::App);
    let typed = state("/re");
    assert!(typed.is_open() && typed.commands[0].invocation == "rename" && typed.commands[1].invocation == "review");
    assert_eq!(invocations(&state("/rev").commands), ["review"]);
}

/// Verification.cs:1067 "slash palette closes with no match or no query" (SlashPaletteVerification.Closes).
#[test]
fn closes() {
    let l = ko();
    assert!(!state("/zzzznope").is_open());
    assert!(!state("안녕하세요").is_open());
    assert!(!state("/review 진행해 줘").is_open());
    assert!(!state_for("/review", None, "shell").is_open());
    let empty = palette::state("claude", "claude", "/", None, &[], Some(&choices), &l);
    assert!(empty.commands.iter().any(|c| c.origin == SlashCommandOrigin::App));
    assert!(!empty.commands.iter().any(|c| c.origin != SlashCommandOrigin::App));
    assert_eq!(palette::draft("claude", "/re", None), Some("/re"));
    assert_eq!(palette::draft("claude", "안녕", None), None);
}

/// Verification.cs:1068 "slash palette highlight wraps and clamps" (SlashPaletteVerification.HighlightMoves).
#[test]
fn highlight_moves() {
    let state = state("/");
    let count = state.commands.len();
    assert!(count > 2);
    assert_eq!(state.move_down().highlighted_index, 1);
    assert_eq!(state.move_up().highlighted_index as usize, count - 1);
    assert_eq!(state.move_up().move_down().highlighted_index, 0);
    let stale = SlashPaletteState { highlighted_index: 99, ..state.clone() };
    assert!(stale.safe_index() == count - 1 && stale.highlighted() == Some(&state.commands[count - 1]));
    assert_eq!(stale.move_down().highlighted_index, 0);
    let closed = SlashPaletteState::closed();
    assert!(!closed.is_open() && closed.highlighted().is_none() && closed.move_up().highlighted_index == 0);
}

/// Verification.cs:1069 "slash palette chooses a plain command" (SlashPaletteVerification.ChoosesPlainCommand).
#[test]
fn chooses_plain_command() {
    let choice = state("/rev").choose();
    assert!(choice.effect == SlashChoiceEffect::Insert && choice.draft == "/review " && choice.action.is_none());
    assert!(!state(&choice.draft).is_open());
}

/// Verification.cs:1070 "slash palette chooses an app action" (SlashPaletteVerification.ChoosesAppAction).
#[test]
fn chooses_app_action() {
    let l = ko();
    let choice = state("/clear").choose();
    assert!(
        choice.effect == SlashChoiceEffect::AppAction
            && choice.action == Some(SlashCommandAction::NewConversation)
            && choice.draft.is_empty()
    );
    let model = palette::state("claude", "claude", "/model claude-opus-5", None, &scanned(&l), Some(&choices), &l).choose();
    assert!(model.effect == SlashChoiceEffect::AppAction && model.action == Some(SlashCommandAction::SetModel));
    assert!(model.action_arg.as_deref() == Some("claude-opus-5") && model.draft.is_empty());
}

/// Verification.cs:1071 "slash palette chooses an argument command" (SlashPaletteVerification.ChoosesArgumentCommand).
#[test]
fn chooses_argument_command() {
    let choice = state("/mod").choose();
    assert!(choice.effect == SlashChoiceEffect::ArgumentCompletion && choice.draft == "/model " && choice.action.is_none());
    let next = state(&choice.draft);
    assert!(next.is_open());
    assert_eq!(invocations(&next.commands), ["model default", "model claude-opus-5", "model claude-sonnet-5"]);
    assert_eq!(invocations(&state("/model claude").commands), ["model claude-opus-5", "model claude-sonnet-5"]);
    let permission = state("/permissions ");
    assert!(permission.is_open() && permission.commands.iter().all(|c| c.action == Some(SlashCommandAction::SetPermission)));
    assert!(!state("/model 없는모델").is_open());
    assert!(!state("/review ").is_open());
}

/// Verification.cs:1072 "slash palette Esc closes without changing the draft" (SlashPaletteVerification.EscapeClosesWithoutChangingTheDraft).
#[test]
fn escape_closes_without_changing_the_draft() {
    let draft = "/re";
    assert!(state(draft).is_open());
    assert!(!state_for(draft, Some(draft), "claude").is_open());
    assert!(state_for("/rev", Some(draft), "claude").is_open());
}

/// Verification.cs:1073 "slash palette rows and footer use the macOS copy" (SlashPaletteVerification.RowsAndFooterUseMacCopy).
#[test]
fn rows_and_footer_use_mac_copy() {
    let l = ko();
    let state = state("/");
    let review = state.commands.iter().find(|c| c.invocation == "review").unwrap();
    let deploy = state.commands.iter().find(|c| c.invocation == "deploy").unwrap();
    assert_eq!(palette::description(review, &l), "코드 검토");
    assert!(palette::description(deploy, &l) == strings::palette_no_description(&l) && strings::palette_no_description(&l) == "설명 없음");
    assert!(review.source == strings::project_skill_source(&l) && deploy.source == strings::user_command_source(&l));
    assert!(
        palette::count_label(3, &l) == "3개" && palette::count_label(state.commands.len(), &l) == format!("{}개", state.commands.len())
    );
    assert!(
        strings::palette_move(&l) == "↑↓ 이동"
            && strings::palette_select(&l) == "Enter · Tab 선택"
            && strings::palette_dismiss(&l) == "Esc 닫기"
    );
    assert!(
        strings::palette_action_tooltip(&l) == "앱에서 바로 실행됩니다" && strings::palette_argument_tooltip(&l) == "이어서 선택합니다"
    );
    assert!(state.commands.iter().find(|c| c.invocation == "clear").unwrap().action.is_some());
    assert!(state.commands.iter().find(|c| c.invocation == "model").unwrap().argument.is_some());
    assert!(review.action.is_none() && review.argument.is_none());
}

/// Verification.cs:1074 "slash palette leaves out app actions Windows cannot do" (SlashPaletteVerification.LeavesOutActionsWindowsCannotDo).
#[test]
fn leaves_out_actions_windows_cannot_do() {
    let l = ko();
    assert!(palette::UNAVAILABLE_ACTIONS.is_empty());
    for provider in ["claude", "codex"] {
        assert!(palette::builtins(provider, &l).iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins)));
    }
    assert!(!palette::builtins("gemini", &l).iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins)));
    for provider in ["claude", "codex", "gemini"] {
        assert_eq!(invocations(&palette::builtins(provider, &l)), invocations(&slash::catalog_builtins(provider, &l)));
    }
    assert!(state("/plugin").is_open());
    assert_eq!(
        invocations(&palette::builtins("codex", &l)),
        ["plugins", "model", "approvals", "new", "status", "settings", "rename", "help"]
    );
    assert_eq!(
        invocations(&palette::builtins("claude", &l)),
        ["plugin", "model", "permissions", "clear", "cost", "usage", "config", "rename", "help"]
    );
}

/// Verification.cs:1075 "slash palette caps the list at 60 rows" (SlashPaletteVerification.CapsRowsAt60).
#[test]
fn caps_rows_at_60() {
    let l = ko();
    let many: Vec<SlashCommand> =
        (0..200).map(|i| SlashCommand::new(&format!("zz{i:03}"), "", &strings::user_skill_source(&l), SlashCommandOrigin::User)).collect();
    let state = palette::state("claude", "claude", "/zz", None, &many, Some(&choices), &l);
    assert!(state.commands.len() == palette::MAXIMUM_ROWS && palette::MAXIMUM_ROWS == 60);
    assert_eq!(state.move_up().highlighted_index, 59);
}

/// C# `SlashPalette.Submitted` (SlashCommands.cs:487), the rule `ComposerParityVerification.SentBuiltinNeverReachesTheCli` relies on:
/// a typed built-in runs, `/model opus`-style picks apply, an unmatched argument is a notice, and a shell or files make it a request.
#[test]
fn a_typed_builtin_is_submitted_as_the_app_action() {
    let l = ko();
    let sub = |draft: &str, files: bool| palette::submitted("claude", "claude", draft, files, Some(&choices), &l);
    assert_eq!(sub(" /clear ", false).unwrap().command.unwrap().action, Some(SlashCommandAction::NewConversation));
    assert_eq!(sub("/model claude-opus-5", false).unwrap().command.unwrap().action_arg.as_deref(), Some("claude-opus-5"));
    assert_eq!(sub("/model", false).unwrap().command.unwrap().invocation, "model");
    let mismatch = sub("/model zzz", false).unwrap();
    assert!(mismatch.command.is_none() && mismatch.mismatch.as_deref().is_some_and(|m| m.contains("zzz")), "{mismatch:?}");
    assert!(sub("/clear", true).is_none() && sub("hello", false).is_none());
    assert!(palette::submitted("claude", "shell", "/clear", false, None, &l).is_none());
}
