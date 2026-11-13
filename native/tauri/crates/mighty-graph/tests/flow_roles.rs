//! Who a flow block is and what its card says (C# FlowRolesVerification, MightyCode.Core.Tests/FlowRolesVerification.cs): the roles and
//! their inks, the type chip, the marks, the model before a block answers, the header's room, custom agents' definition files, and the card.

mod view_support;

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use mighty_core::Wire;
use mighty_core::graph::{GraphAgentFigures, GraphAgentIdentity, MightyGraphAgent, MightyGraphRun};
use mighty_core::json::JsonOut;
use mighty_graph::agent_definitions::{self, AgentDefinition, Cache, DiskFiles};
use mighty_graph::flow::card;
use mighty_graph::flow::roles::{self, FlowIdentity, FlowInk, FlowMark, FlowRole, HeaderFit};
use mighty_graph::node::{self, ExecutionGraphNode};
use mighty_graph::{labels, support, usage_format};
use serde_json::Value;
use view_support::flow::{self, a, r};
use view_support::*;

const CHILD_KINDS: [&str; 4] = ["task", "steer", "compact", "question"];

fn identity_of(kind: Option<&str>, requested: Option<&str>, resolved: Option<&str>, isolation: Option<&str>) -> GraphAgentIdentity {
    identity(kind, requested, resolved, isolation)
}

/// FlowRolesVerification's own `Agent(title, type, requested, resolved, isolation, background, kind)`.
fn subagent_of(title: &str, kind: Option<&str>, requested: Option<&str>, resolved: Option<&str>, isolation: Option<&str>, background: bool, block: Option<&str>) -> MightyGraphAgent {
    let mut builder = a("a").title(title).input("").identity(identity_of(kind, requested, resolved, isolation));
    if background {
        builder = builder.background();
    }
    if let Some(b) = block {
        builder = builder.kind(b);
    }
    builder.build()
}

fn plain_agent() -> MightyGraphAgent {
    subagent_of("Map the repo", None, None, None, None, false, None)
}

fn typed(kind: &str) -> MightyGraphAgent {
    subagent_of("Map the repo", Some(kind), None, None, None, false, None)
}

fn definition(name: &str, color: Option<&str>, model: Option<&str>) -> AgentDefinition {
    AgentDefinition { name: name.into(), color: color.map(str::to_string), model: model.map(str::to_string) }
}

fn palette(dark: bool) -> HashMap<String, String> {
    let tokens = read_json(&contracts().join("fixtures/design-tokens.json"));
    tokens[if dark { "dark" } else { "light" }].as_object().unwrap().iter().map(|(k, v)| (k.clone(), v.as_str().unwrap_or("").to_string())).collect()
}

fn luminance(hex: &str) -> f64 {
    let value = u32::from_str_radix(hex.trim_start_matches('#'), 16).unwrap();
    let channel = |shift: u32| {
        let c = ((value >> shift) & 0xFF) as f64 / 255.0;
        if c <= 0.03928 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
    };
    0.2126 * channel(16) + 0.7152 * channel(8) + 0.0722 * channel(0)
}

fn contrast(a: &str, b: &str) -> f64 {
    let (x, y) = (luminance(a), luminance(b));
    (x.max(y) + 0.05) / (x.min(y) + 0.05)
}

const ALL_INKS: [FlowInk; 9] =
    [FlowInk::Task, FlowInk::Agent, FlowInk::Steer, FlowInk::Accent, FlowInk::Done, FlowInk::Err, FlowInk::Wait, FlowInk::Question, FlowInk::Neutral];

/// "flow roles: the four roles' inks, Claude's eight colours and a custom agent's colour" (TheRolesTheirInksAndACustomAgentsColour).
#[test]
fn the_roles_their_inks_and_a_custom_agents_colour() {
    let inks: Vec<FlowInk> = [FlowRole::Lead, FlowRole::Subagent, FlowRole::Task, FlowRole::Advisor].iter().map(|r| roles::ink(*r)).collect();
    same(inks.iter().collect::<HashSet<_>>().len(), 4, "four roles, four inks");
    same(roles::ink(FlowRole::Subagent), FlowInk::Agent, "the sub-agent's violet");
    check(roles::ink(FlowRole::Task) == FlowInk::Neutral && roles::ink(FlowRole::Task) != roles::ink(FlowRole::Lead), "a command's ink is the secondary one, not the lead's");
    for dark in [false, true] {
        let colours = palette(dark);
        let values: Vec<&String> = inks.iter().map(|ink| &colours[roles::token(*ink)]).collect();
        same(values.iter().collect::<HashSet<_>>().len(), 4, &format!("four colours in {}", if dark { "dark" } else { "light" }));
        for colour in values {
            check(contrast(colour, &colours["card"]) >= 4.5, format!("an AA ink on a card: {colour}"));
        }
    }
    let light = palette(false);
    for ink in ALL_INKS {
        check(light.contains_key(roles::token(ink)), format!("a token for {ink:?}"));
    }
    same(roles::AGENT_COLORS.len(), 8, "Claude Code's eight colours");
    for colour in roles::AGENT_COLORS {
        check(roles::color_ink(Some(colour)).is_some(), format!("{colour} is a palette ink"));
    }
    same(roles::color_ink(Some("red")), Some(FlowInk::Err), "red");
    same(roles::color_ink(Some("blue")), Some(FlowInk::Accent), "blue");
    same(roles::color_ink(Some("green")), Some(FlowInk::Done), "green");
    same(roles::color_ink(Some("yellow")), Some(FlowInk::Wait), "yellow");
    same(roles::color_ink(Some("purple")), Some(FlowInk::Agent), "purple");
    same(roles::color_ink(Some("orange")), Some(FlowInk::Question), "orange");
    same(roles::color_ink(Some("pink")), Some(FlowInk::Steer), "pink");
    same(roles::color_ink(Some(" Cyan ")), Some(FlowInk::Task), "cyan, trimmed and in any case");
    same(roles::color_ink(Some("teal")), None, "a colour Claude Code lacks");
    same(roles::color_ink(None), None, "none");
    let values: HashSet<&String> = roles::AGENT_COLORS.iter().filter_map(|c| roles::color_ink(Some(c))).map(|ink| &light[roles::token(ink)]).collect();
    same(values.len(), 7, "seven values");
    same(roles::role_ink(FlowRole::Subagent, Some("green")), FlowInk::Done, "a custom colour replaces the sub-agent ink");
    same(roles::role_ink(FlowRole::Subagent, Some("mauve")), FlowInk::Agent, "an unknown one does not");
    same(roles::role_ink(FlowRole::Lead, Some("green")), roles::ink(FlowRole::Lead), "the lead keeps its ink");
    same(roles::role_ink(FlowRole::Advisor, Some("green")), roles::ink(FlowRole::Advisor), "the advisor keeps its ink");
}

/// "flow roles: who the lead and a sub-agent are, the type chip, the marks and the model before it answers" (WhoTheLeadAndASubagentAre).
#[test]
fn who_the_lead_and_a_subagent_are() {
    let ko = ko();
    let mut run = r("r").effort("high").build();
    let lead = roles::lead(&run, &ko);
    check(lead.role == FlowRole::Lead && lead.ink == roles::ink(FlowRole::Lead), "the lead and its ink");
    same(lead.effort.as_deref(), Some("High"), "the pane's effort");
    same(lead.summary.as_str(), "메인 · 사고 강도 · High", "the lead in one line");
    check(lead.marks.is_empty() && lead.type_chip.is_none() && lead.model_label.is_none(), "no marks, chip or model");
    run.configured_effort = Some("default".into());
    same(roles::lead(&run, &ko).effort, None, "the CLI decides");
    same(roles::lead(&run, &ko).summary.as_str(), "메인", "the lead alone");
    run.configured_effort = None;
    same(roles::lead(&run, &ko).effort, None, "an older save");
    run.configured_effort = Some("minimal".into());
    same(roles::lead(&run, &ko).effort.as_deref(), Some("Minimal"), "pi's levels are efforts too");

    let who = roles::subagent(&subagent_of("Map the repo", Some("Explore"), Some("haiku"), None, None, true, None), None, &ko).unwrap();
    check(who.role == FlowRole::Subagent && who.ink == FlowInk::Agent, "a sub-agent and its ink");
    check(who.kind.as_deref() == Some("Explore") && who.type_chip.as_deref() == Some("Explore"), "its type and chip");
    same(who.model_label.as_deref(), Some("haiku · 요청"), "asked for, not yet said to run");
    same(who.marks.clone(), vec![FlowMark::Background], "its mark");
    same(who.summary.as_str(), "하위 에이전트 · Explore · 요청한 모델 Haiku · 백그라운드", "it in one line");
    let launched = roles::subagent(&subagent_of("Map the repo", Some("Explore"), Some("haiku"), Some("claude-haiku-5-5"), None, false, None), None, &ko).unwrap();
    same(launched.model_label.as_deref(), Some("claude-haiku-5-5"), "the resolved model outranks the alias");
    same(launched.summary.as_str(), "하위 에이전트 · Explore · Haiku 5.5", "the resolved model in words");
    same(launched.marks.len(), 0, "no mark");
    let fork = roles::subagent(&subagent_of("fork count a", Some("fork"), None, Some("claude-haiku-5-5"), None, true, None), None, &ko).unwrap();
    same(fork.marks.clone(), vec![FlowMark::Background, FlowMark::Fork], "a fork's marks");
    check(fork.type_chip.is_none() && fork.kind.as_deref() == Some("fork"), "a fork has no chip");
    same(fork.summary.as_str(), "하위 에이전트 · Haiku 5.5 · 백그라운드 · 포크", "a fork in one line");
    let isolated = roles::subagent(&subagent_of("Map the repo", Some("general-purpose"), None, None, Some("worktree"), false, None), None, &ko).unwrap();
    same(isolated.marks.clone(), vec![FlowMark::Worktree], "a worktree");
    same(isolated.type_chip.as_deref(), Some("general-purpose"), "its chip");
    same(
        roles::subagent(&subagent_of("Map the repo", Some("general-purpose"), None, None, Some("remote"), false, None), None, &ko).unwrap().marks.len(),
        0,
        "a remote isolation is no worktree",
    );
    let reviewer_definition = definition("reviewer", Some("green"), Some("sonnet"));
    let reviewer = roles::subagent(&typed("reviewer"), Some(&reviewer_definition), &ko).unwrap();
    same(reviewer.ink, FlowInk::Done, "the definition's colour");
    same(reviewer.model_label.as_deref(), Some("sonnet · 요청"), "the definition's model stands in");
    same(
        roles::subagent(&subagent_of("Map the repo", Some("reviewer"), Some("opus"), None, None, false, None), Some(&reviewer_definition), &ko)
            .unwrap()
            .model_label
            .as_deref(),
        Some("opus · 요청"),
        "the call's own model wins",
    );
    let plain = roles::subagent(&typed("reviewer"), Some(&definition("reviewer", Some("plaid"), Some("inherit"))), &ko).unwrap();
    check(plain.model_label.is_none() && plain.ink == FlowInk::Agent, "inherit asks for none; plaid is no colour");
    same(plain.summary.as_str(), "하위 에이전트 · reviewer", "in one line");
    same(labels::agent_model_label(Some("claude-opus-5-5"), Some("opus"), &ko).as_deref(), Some("claude-opus-5-5"), "resolved first");
    same(labels::agent_model_label(None, Some("opus"), &ko).as_deref(), Some("opus · 요청"), "requested, marked");
    same(labels::agent_model_label(Some(""), Some(""), &ko), None, "nothing");
    same(labels::agent_model_label(None, Some("inherit"), &ko), None, "inherit");
    same(usage_format::versioned_node_label(Some("opus · 요청"), &ko).as_deref(), Some("Opus · 요청"), "a requested label read through its model's name");
    same(usage_format::versioned_node_label(Some("opus · 설정"), &ko).as_deref(), Some("Opus · 설정"), "a configured one");
    same(usage_format::versioned_node_label(Some("claude-haiku-5-5"), &ko).as_deref(), Some("Haiku 5.5"), "a resolved one");
    same(usage_format::block_capsule(None, &[], Some("haiku · 요청"), None, true, &ko).as_deref(), Some("Haiku · 요청"), "the capsule");

    same(roles::subagent(&subagent_of("Explore", Some("Explore"), None, None, None, false, None), None, &ko).unwrap().type_chip, None, "the chip does not repeat a title that is the type");
    let plugin = roles::subagent(&subagent_of("Fix the build", Some("oh-my-claudecode:executor"), None, None, None, false, None), None, &ko).unwrap();
    check(plugin.type_chip.as_deref() == Some("executor") && plugin.kind.as_deref() == Some("oh-my-claudecode:executor"), "a plugin's agent by its own name");
    same(
        roles::subagent(&subagent_of("executor", Some("oh-my-claudecode:executor"), None, None, None, false, None), None, &ko).unwrap().type_chip,
        None,
        "nor a title that is the plugin agent's name",
    );
    same(roles::type_name("a:b:c"), "c", "the last part");
    same(roles::type_name("plain"), "plain", "a plain type");
    same(roles::type_name("trailing:"), "trailing:", "a trailing colon");
    check(roles::subagent(&plain_agent(), None, &ko).is_some_and(|w| w.type_chip.is_none()), "a sub-agent that said nothing is still one");
    for k in CHILD_KINDS {
        same(roles::subagent(&subagent_of("Map the repo", None, None, None, None, false, Some(k)), None, &ko), None, &format!("{k} is no sub-agent"));
    }
    let runs = vec![
        r("one").agents(vec![typed("Explore"), typed("reviewer"), plain_agent()]).build(),
        r("two").agents(vec![typed("Explore")]).build(),
    ];
    let mut types: Vec<String> = roles::types(&runs).into_iter().collect();
    types.sort();
    same(types, vec!["Explore".to_string(), "reviewer".to_string()], "the types the blocks name");
    same(roles::types(&[]).len(), 0, "none");
}

fn who() -> FlowIdentity {
    FlowIdentity {
        role: FlowRole::Subagent,
        ink: FlowInk::Agent,
        kind: Some("Explore".into()),
        type_chip: Some("Explore".into()),
        model_label: None,
        effort: None,
        marks: vec![FlowMark::Background, FlowMark::Worktree],
        summary: String::new(),
    }
}

fn lead_identity(effort: Option<&str>) -> FlowIdentity {
    FlowIdentity {
        role: FlowRole::Lead,
        ink: FlowInk::Task,
        kind: None,
        type_chip: None,
        model_label: None,
        effort: effort.map(str::to_string),
        marks: vec![],
        summary: String::new(),
    }
}

/// "flow roles: the header gives way first for the chips" (TheHeaderGivesWayFirstForTheChips).
#[test]
fn the_header_gives_way_first_for_the_chips() {
    let who = who();
    let title = "Map the repository layout";
    same(
        roles::fit_header(mighty_core::graph::MINIMUM_WIDTH, title, Some("실행 중"), Some("12.3K · Haiku 5.5"), 1, &who),
        HeaderFit::NONE,
        "at the least width the title, status and tokens keep their room",
    );
    let wide = roles::fit_header(520.0, title, Some("실행 중"), Some("12.3K · Haiku 5.5"), 1, &who);
    check(
        wide.shows_marks && wide.type_width.is_some_and(|w| (roles::TYPE_FLOOR..=roles::TYPE_CEILING).contains(&w)),
        "a wide header shows the marks and the chip",
    );
    let mut seen_marks_alone = false;
    let mut previous = HeaderFit::NONE;
    let mut width = 300.0;
    while width <= 700.0 {
        let fit = roles::fit_header(width, title, Some("실행 중"), Some("12.3K · Haiku 5.5"), 1, &who);
        if fit.shows_marks && fit.type_width.is_none() {
            seen_marks_alone = true;
        }
        if previous.shows_marks {
            check(fit.shows_marks, format!("the marks stay as it widens at {width}"));
        }
        if let Some(before) = previous.type_width {
            check(fit.type_width.unwrap_or(0.0) >= before, format!("the chip only grows at {width}"));
        }
        if fit.type_width.is_some() {
            check(fit.shows_marks, format!("no chip without the marks at {width}"));
        }
        previous = fit;
        width += 4.0;
    }
    check(seen_marks_alone && previous.type_width.is_some(), "the marks come before the chip");
    let busy = roles::fit_header(360.0, "Map", Some("실행 중"), Some("12.3K · Haiku 5.5 +1"), 1, &who);
    let quiet = roles::fit_header(360.0, "Map", None, None, 1, &who);
    check(quiet.type_width.is_some() && quiet.type_width.unwrap_or(0.0) >= busy.type_width.unwrap_or(0.0), "a block without status or tokens has more room");
    let lead = lead_identity(Some("High"));
    check(!roles::fit_header(300.0, "실행 · 요청 12", Some("턴 완료 · 백그라운드 2개 실행 중"), Some("120.4K · Opus 5.5"), 1, &lead).shows_effort, "no room for the effort");
    check(roles::fit_header(500.0, "실행 · 요청 12", Some("실행 중"), Some("120.4K · Opus 5.5"), 1, &lead).shows_effort, "room for it");
    same(roles::fit_header(900.0, "실행", None, None, 1, &lead_identity(None)), HeaderFit::NONE, "nothing to show");
    check(roles::estimated_width("실행 중", 10.0, false) > roles::estimated_width("abc d", 10.0, false), "a wide character counts a full em");
    same(roles::estimated_width("", 10.0, false), 0.0, "nothing");
    check(roles::estimated_width("aaaa", 10.0, true) > roles::estimated_width("aaaa", 10.0, false), "monospaced is wider");
}

fn write(root: &Path, path: &str, text: &str) {
    let file = root.join(path);
    std::fs::create_dir_all(file.parent().unwrap()).unwrap();
    std::fs::write(file, text).unwrap();
}

fn temp_root() -> PathBuf {
    let root = std::env::temp_dir().join(format!("mighty-graph-agents-{}", mighty_core::wire::new_id()));
    std::fs::create_dir_all(&root).unwrap();
    root
}

/// "flow roles: custom agents are read from their definition files (project, user, plugin; only a head, read again on change)"
/// (CustomAgentsAreReadFromTheirDefinitionFiles).
#[test]
fn custom_agents_are_read_from_their_definition_files() {
    let parse = agent_definitions::parse;
    same(
        parse("---\nname: reviewer\ndescription: Reviews a diff\ncolor: green\nmodel: sonnet\ntools: Read, Grep\n---\n\nYou review code.\n", "code-reviewer.md"),
        Some(definition("reviewer", Some("green"), Some("sonnet"))),
        "name, colour and model",
    );
    same(parse("---\ncolor: Blue\n---\n", "scout.md"), Some(definition("scout", Some("blue"), None)), "the file's own name");
    same(
        parse("\u{feff}---\nname: \"deep scout\"   # the long one\ncolor: 'purple'\nmodel: claude-opus-5-5 # pinned\n---\n", "x.md"),
        Some(definition("deep scout", Some("purple"), Some("claude-opus-5-5"))),
        "quotes, comments and a BOM",
    );
    same(agent_definitions::scalar("  'a # b'  ").as_deref(), Some("a # b"), "a quoted comment mark");
    same(agent_definitions::scalar("plain # note").as_deref(), Some("plain"), "a trailing comment");
    same(agent_definitions::scalar("   "), None, "nothing");
    same(agent_definitions::scalar("[a, b]"), None, "a list");
    same(agent_definitions::scalar("|"), None, "a block");
    same(agent_definitions::scalar("\"unclosed"), None, "an unclosed quote");
    same(
        parse("---\nname: fixer\ntools:\n\t- Read\n  - : broken\ndescription: >\n  folded\n  text: not a key\ncolor: orange\nmodel: [haiku\n---\nbody\n", "fixer.md"),
        Some(definition("fixer", Some("orange"), None)),
        "what YAML would reject still gives what it can",
    );
    same(parse("---\nname: odd\ncolor: teal\nmodel: two words\n---\n", "odd.md"), Some(definition("odd", None, None)), "an unknown colour and a model no CLI names");
    same(parse("---\ncolor: red\ncolor: blue\n---\n", "twice.md").and_then(|d| d.color).as_deref(), Some("red"), "the first of a repeated key");
    same(parse("# Just notes\n\ncolor: red\n", "notes.md"), None, "no frontmatter");
    same(parse("---\nname: open\ncolor: red\n", "open.md"), None, "an unclosed one");
    same(parse("", "empty.md"), None, "an empty file");
    same(parse(&format!("---\n{}color: red\n---\n", "x: y\n".repeat(agent_definitions::MAXIMUM_LINES + 5)), "long.md"), None, "a block past its first lines");
    same(parse("---\r\nname: crlf\r\ncolor: cyan\r\n---\r\n", "crlf.md"), Some(definition("crlf", Some("cyan"), None)), "Windows line endings");

    let root = temp_root();
    let files = DiskFiles;
    let config = root.join("config");
    let workspace = root.join("project");
    write(&config, "agents/reviewer.md", "---\nname: reviewer\ncolor: red\nmodel: opus\n---\n");
    write(&config, "agents/scout.md", "---\nname: scout\ncolor: cyan\n---\n");
    write(&workspace, ".claude/agents/reviewer.md", "---\nname: reviewer\ncolor: green\n---\n");
    write(&workspace, ".claude/agents/README.txt", "not an agent");
    let cache = Cache::default();
    let types = |list: &[&str]| -> HashSet<String> { list.iter().map(|s| s.to_string()).collect() };
    let found = agent_definitions::load(&types(&["reviewer", "scout", "Explore"]), Some(&workspace), &config, &cache, &files);
    let mut pairs: Vec<(String, AgentDefinition)> = found.into_iter().collect();
    pairs.sort_by(|x, y| x.0.cmp(&y.0));
    same(
        pairs,
        vec![("reviewer".to_string(), definition("reviewer", Some("green"), None)), ("scout".to_string(), definition("scout", Some("cyan"), None))],
        "the project's agent wins over the user's, only asked types",
    );
    same(
        agent_definitions::load(&types(&["reviewer"]), None, &config, &cache, &files).get("reviewer").and_then(|d| d.color.clone()).as_deref(),
        Some("red"),
        "without a project folder the user's",
    );
    same(
        agent_definitions::load(&types(&["scout"]), Some(&workspace), &config, &cache, &files).into_keys().collect::<Vec<_>>(),
        vec!["scout".to_string()],
        "only what was asked",
    );
    same(agent_definitions::load(&types(&[]), Some(&workspace), &config, &cache, &files).len(), 0, "nothing asked");
    same(
        agent_definitions::load(&types(&["reviewer"]), Some(&root.join("missing")), &root.join("none"), &cache, &files).len(),
        0,
        "folders that are not there",
    );

    let install = root.join("cache").join("tools").join("1.2.0");
    write(&install, "agents/executor.md", "---\nname: executor\ncolor: pink\nmodel: sonnet\n---\n");
    let index = serde_json::json!({
        "version": 2,
        "plugins": { "tools@market": [{ "scope": "user", "installPath": install.to_string_lossy() }], "empty@market": [] },
    });
    write(&config, "plugins/installed_plugins.json", &index.to_string());
    let plugins = Cache::default();
    let scoped = agent_definitions::load(&types(&["tools:executor", "tools:missing", "other:executor", "tools:../escape", "tools:..\\escape"]), None, &config, &plugins, &files);
    same(
        scoped.into_iter().collect::<Vec<_>>(),
        vec![("tools:executor".to_string(), definition("tools:executor", Some("pink"), Some("sonnet")))],
        "the plugin's agent",
    );
    same(plugins.plugin_folders(&config, &files).into_keys().collect::<Vec<_>>(), vec!["tools".to_string()], "the installed plugins");

    let agents = root.join("solo").join("agents");
    write(&root.join("solo"), "agents/scout.md", "---\nname: scout\ncolor: cyan\n---\n");
    let folders = Cache::default();
    same(folders.definitions(&agents, &files).into_iter().map(|d| d.color).collect::<Vec<_>>(), vec![Some("cyan".to_string())], "read");
    same(folders.definitions(&agents, &files), vec![definition("scout", Some("cyan"), None)], "the same");
    write(&root.join("solo"), "agents/scout.md", "---\nname: scout\ncolor: yellow\nmodel: haiku\n---\n");
    same(folders.definitions(&agents, &files), vec![definition("scout", Some("yellow"), Some("haiku"))], "a file that grew is read again");
    write(&root.join("solo"), "agents/second.md", "---\nname: second\n---\n");
    same(folders.definitions(&agents, &files).into_iter().map(|d| d.name).collect::<Vec<_>>(), vec!["scout".to_string(), "second".to_string()], "a new file");
    std::fs::remove_dir_all(&agents).unwrap();
    same(folders.definitions(&agents, &files).len(), 0, "a folder that went");

    let heads = root.join("heads");
    let padding = format!("# {}\n", "x".repeat(98)).repeat(agent_definitions::MAXIMUM_BYTES / 100 + 10);
    write(&heads, "agents/huge.md", &format!("---\nname: huge\n{padding}color: red\n---\n"));
    write(&heads, "agents/small.md", &format!("---\nname: small\ncolor: red\n---\n{}", "body\n".repeat(50_000)));
    same(Cache::default().definitions(&heads.join("agents"), &files), vec![definition("small", Some("red"), None)], "the closing line past what is read");
    let _ = std::fs::remove_dir_all(&root);

    let home = std::env::temp_dir().join("someone");
    same(agent_definitions::config_directory(&home, Some(&HashMap::new())), home.join(".claude"), "the home's .claude");
    same(
        agent_definitions::config_directory(&home, Some(&HashMap::from([("CLAUDE_CONFIG_DIR".to_string(), String::new())]))),
        home.join(".claude"),
        "an empty variable",
    );
    let configured = std::env::temp_dir().join("claude-config");
    same(
        agent_definitions::config_directory(&home, Some(&HashMap::from([("CLAUDE_CONFIG_DIR".to_string(), configured.to_string_lossy().into_owned())]))),
        configured,
        "the variable's folder",
    );
}

fn card_agent(status: &str, background: bool, kind: Option<&str>, source: Option<&str>) -> MightyGraphAgent {
    let mut b = a("a").status(status);
    if background {
        b = b.background();
    }
    if let Some(k) = kind {
        b = b.kind(k);
    }
    if let Some(s) = source {
        b = b.source(s);
    }
    b.build()
}

/// "flow card: a sub-agent or a background command is a card while it is one" (ASubagentOrACommandIsACardWhileItIsOne).
#[test]
fn a_subagent_or_a_command_is_a_card_while_it_is_one() {
    check(card::is_compact(&card_agent("running", false, None, None), false), "a running sub-agent is a card");
    check(!card::is_compact(&card_agent("running", false, None, None), true), "until the user opens it");
    check(card::is_compact(&card_agent("waiting", false, None, None), false), "a waiting one too");
    for status in ["completed", "error", "stopped", "failed", "cancelled", "interrupted"] {
        let a = card_agent(status, false, None, None);
        check(card::is_compact(&a, false) && card::folds_to_card(&a) && !card::is_compact(&a, true), format!("a foreground sub-agent that ended stays a card until opened: {status}"));
    }
    for status in ["running", "completed", "error", "stopped"] {
        let a = card_agent(status, true, None, None);
        check(card::is_compact(&a, false) && card::folds_to_card(&a), format!("a background sub-agent stays a card: {status}"));
        check(!card::is_compact(&a, true), format!("opened in place: {status}"));
    }
    let copy = card_agent("completed", true, None, Some("original"));
    check(card::is_compact(&copy, false) && !card::is_compact(&copy, true), "a copy is a card too, until opened");
    for k in CHILD_KINDS.iter().filter(|k| **k != "task") {
        check(
            !card::is_compact(&card_agent("running", true, Some(k), None), false) && !card::folds_to_card(&card_agent("running", false, Some(k), None)),
            format!("{k} is never a card"),
        );
    }
    for status in ["running", "completed", "error"] {
        let task = card_agent(status, true, Some("task"), None);
        check(card::is_compact(&task, false) && !card::is_compact(&task, true), format!("a command is a card until opened: {status}"));
    }
    check(card::is_compact(&card_agent("completed", true, Some("task"), Some("original")), false), "a command's copy is a card too");
}

/// "flow card: what a card says" (WhatACardSays).
#[test]
fn what_a_card_says() {
    let ko = ko();
    let run = r("r").build();
    let asked_identity = identity_of(Some("Explore"), Some("haiku"), None, None);
    let make = |who: Option<GraphAgentIdentity>, background: bool, records: Option<Vec<mighty_core::graph::GraphResponseRecord>>| {
        let mut b = a("a").title("Map the repo").input("");
        if let Some(w) = who {
            b = b.identity(w);
        }
        if background {
            b = b.background();
        }
        if let Some(r) = records {
            b = b.records(r);
        }
        b.build()
    };
    let asked = card::of(&make(Some(asked_identity.clone()), false, None), &run, None, &[], &ko);
    same(asked.title.as_str(), "Map the repo", "the name");
    same(asked.identity.as_deref(), Some("Explore · Haiku · 요청"), "asked for, nothing answered yet");
    check(asked.status == "running" && asked.state_word == "실행 중" && asked.running(), "running");
    let mut resolved = asked_identity.clone();
    resolved.resolved_model = Some("claude-haiku-5-5".into());
    same(card::of(&make(Some(resolved), false, None), &run, None, &[], &ko).identity.as_deref(), Some("Explore · Haiku 5.5"), "resolved at launch");
    let answered = vec![mighty_graph::node::response_record("m1", Some("claude-sonnet-5-5"), flow::usage(10, 5), &[], false)];
    same(card::of(&make(Some(asked_identity), false, Some(answered)), &run, None, &[], &ko).identity.as_deref(), Some("Explore · Sonnet 5.5"), "the model that answered");
    same(card::of(&make(Some(identity_of(Some("tools:executor"), None, None, None)), false, None), &run, None, &[], &ko).identity.as_deref(), Some("executor"), "a plugin's agent by its own name");
    let fork = card::of(&make(Some(identity_of(Some("fork"), None, Some("claude-opus-5-5"), None)), true, None), &run, None, &[], &ko);
    same(fork.identity.as_deref(), Some("Opus 5.5"), "a fork by its mark alone");
    same(fork.marks.clone(), vec![FlowMark::Background, FlowMark::Fork], "its marks");
    same(card::of(&make(None, false, None), &run, None, &[], &ko).identity, None, "nothing known, no line");
    same(card::of(&make(None, false, None), &run, None, &[], &ko).ink, FlowInk::Agent, "the sub-agent ink");
    same(
        card::of(&make(Some(identity_of(Some("reviewer"), None, None, None)), false, None), &run, Some(&definition("reviewer", Some("green"), None)), &[], &ko).ink,
        FlowInk::Done,
        "a custom agent's colour",
    );

    let asked2 = "Look for call sites of the tracker.\nThen report.";
    let doing = |entries: Vec<mighty_core::model::LogEntry>, input: &str| card::doing(&a("a").input(input).entries(entries).build());
    same(doing(vec![], asked2).as_deref(), Some("Look for call sites of the tracker."), "the first line of its prompt");
    same(doing(vec![flow::said("t1", "Starting.\n\nReading the tracker next.\n", 0)], asked2).as_deref(), Some("Reading the tracker next."), "its last line");
    same(
        doing(
            vec![
                flow::read("a", "Sources/A.swift 읽기"),
                flow::said("t1", "Found it.", 0),
                flow::tool("b", "grep tracker", 0, "Grep", "tool", "running"),
                flow::said("t2", "Now the tests.", 0),
            ],
            asked2,
        )
        .as_deref(),
        Some("grep tracker"),
        "the latest tool call outranks its words",
    );
    same(doing(vec![flow::tool("a", "", 0, "WebFetch", "tool", "completed")], "").as_deref(), Some("WebFetch"), "a call without a summary is its tool");
    same(
        doing(vec![flow::said("t1", "Thinking.", 0), flow::tool("turn", "응답 생성", 0, "", "turn", "completed")], "").as_deref(),
        Some("Thinking."),
        "the lifecycle row is no tool call",
    );
    same(doing(vec![], ""), None, "nothing");
    let longest = doing(vec![flow::read("a", &format!("{}\nsecond", "가".repeat(400)))], "");
    check(longest.as_deref().unwrap_or("").len() <= card::LINE_BYTES && !longest.unwrap_or_default().contains('\n'), "one bounded line");

    let plain_run = r("r").build();
    let done = a("a").status("completed").background().entries(vec![flow::read("a", "a.txt 읽기"), flow::said("answer", "\nb.txt: 3\nc.txt: 5", 0)]).build();
    let content = card::of(&done, &plain_run, None, &[], &ko);
    same(content.activity.as_deref(), Some("b.txt: 3"), "an ended card's answer");
    check(!content.running(), "ended");
    let stopped = a("a").status("stopped").background().entries(vec![flow::read("a", "a.txt 읽기"), flow::said_as("note", "응답 없이 끝났습니다.", 0, "system")]).build();
    same(card::outcome(&stopped).as_deref(), Some("응답 없이 끝났습니다."), "the last thing it recorded");
    same(card::outcome(&a("a").status("completed").entries(vec![flow::read("a", "a.txt 읽기")]).build()), None, "nothing but a tool call");

    let agents = vec![
        a("root").build(),
        a("child").parent("root").build(),
        a("grand").parent("child").build(),
        a("note").parent("child").kind("compact").build(),
        a("other").build(),
        a("loop-a").parent("loop-b").build(),
        a("loop-b").parent("loop-a").build(),
    ];
    let nested_run = r("r").agents(agents.clone()).build();
    same([0, 1, 4, 5].iter().map(|i| card::nested(&agents[*i], &nested_run)).collect::<Vec<_>>(), vec![2, 1, 0, 1], "the sub-agents under each, a cycle ending");
    same(card::of(&agents[0], &nested_run, None, &[], &ko).nested, 2, "the card's count");
}

/// "flow card: a card's figures are what was observed, and a finished background card leads to its copy" (ACardsFiguresAreWhatWasObserved).
#[test]
fn a_cards_figures_are_what_was_observed() {
    let ko = ko();
    let run = r("r").build();
    let start = flow::date(0);
    let stamp = flow::stamp(0);
    let usage = mighty_core::graph::GraphTokenUsage { input_tokens: 300, output_tokens: 45, cache_read_tokens: 12_000, cache_creation_tokens: 0 };
    let at = |seconds: i64| flow::date(seconds);
    let running = card::of(&a("a").figures(figures(Some(&stamp), None, Some(3), None)).usage(usage).build(), &run, None, &[], &ko);
    same(card::figures(&running, at(42), &ko).as_str(), "실행 중 · 42초 · 도구 3 · 토큰 12K", "running");
    same(card::figures(&running, at(125), &ko).as_str(), "실행 중 · 2분 5초 · 도구 3 · 토큰 12K", "minutes");
    same(card::figures(&running, at(-5), &ko).as_str(), "실행 중 · 0초 · 도구 3 · 토큰 12K", "a clock before the start");
    same(card::figures(&card::of(&a("a").build(), &run, None, &[], &ko), start, &ko).as_str(), "실행 중", "nothing live: the state alone");
    same(
        card::figures(&card::of(&a("a").figures(figures(Some(&stamp), None, Some(0), None)).build(), &run, None, &[], &ko), at(1), &ko).as_str(),
        "실행 중 · 1초",
        "no 도구 0",
    );
    let done = card::of(
        &a("a").status("completed").background().figures(figures(Some(&stamp), Some(&flow::stamp(65)), Some(2), None)).usage(usage).build(),
        &run,
        None,
        &[],
        &ko,
    );
    same(card::figures(&done, at(9_999), &ko).as_str(), "완료 · 1분 5초 · 도구 2 · 토큰 12K", "how long it ran");
    let restored = card::of(&a("a").status("stopped").background().figures(figures(Some(&stamp), None, Some(2), None)).build(), &run, None, &[], &ko);
    same(card::figures(&restored, at(9_999), &ko).as_str(), "중단 · 도구 2", "an end the app did not see");
    same(card::figures(&card::of(&a("a").status("error").background().build(), &run, None, &[], &ko), start, &ko).as_str(), "실패", "failed");
    check(!card::figures(&running, at(42), &ko).contains('%') && !card::figures(&done, start, &ko).contains('%'), "no percentage");
    let (before, after) = card::figure_parts(&running, &ko);
    same(before, vec!["실행 중".to_string()], "before the time");
    same(after, vec!["도구 3".to_string(), "토큰 12K".to_string()], "after it");

    let normalized = |f: GraphAgentFigures| mighty_core::normalize::figures(&f);
    same(normalized(figures(Some("yesterday"), Some(&"9".repeat(90)), Some(-1), None)), None, "nothing that reads back");
    same(
        normalized(figures(Some("2026-11-10T18:56:44.496Z"), None, Some(1_000_001), None)),
        Some(figures(Some("2026-11-10T18:56:44.496Z"), None, None, None)),
        "a count out of range",
    );
    check(mighty_core::time::parse(Some("2026-11-10T18:56:44Z")).is_some(), "a time reads back");
    check(GraphAgentFigures::default() == figures(None, None, None, None) && figures(None, None, None, Some(0)) != GraphAgentFigures::default(), "empty, and an exit code of 0 is something");
    same(normalized(figures(None, None, None, Some(3))), Some(figures(None, None, None, Some(3))), "an exit code");
    let mut task = ExecutionGraphNode::new("n".into(), "r".into(), None, "task", "running", "t".into());
    task.figures = Some(figures(None, None, None, Some(2)));
    same(node::normalized(&task, false).and_then(|n| n.figures), Some(figures(None, None, None, Some(2))), "a command's");
    let mut steer = ExecutionGraphNode::new("s".into(), "r".into(), None, "steer", "running", "s".into());
    steer.figures = Some(figures(None, None, Some(2), None));
    same(node::normalized(&steer, false).and_then(|n| n.figures), None, "no steer's");
    let mut budget = 100_000;
    let saved = support::normalized(
        &[r("r")
            .status("completed")
            .agents(vec![
                a("a").status("completed").figures(figures(Some("2026-11-10T18:56:44Z"), Some("soon"), Some(4), None)).build(),
                a("t").status("completed").kind("task").figures(figures(Some("2026-11-10T18:56:44Z"), None, None, Some(0))).build(),
                a("q").status("completed").kind("question").figures(figures(None, None, Some(4), None)).build(),
            ])
            .build()],
        true,
        &mut budget,
        None,
    );
    same(saved[0].agents()[0].figures.clone(), Some(figures(Some("2026-11-10T18:56:44Z"), None, Some(4), None)), "a sub-agent's, its unreadable end dropped");
    same(saved[0].agents()[1].figures.clone(), Some(figures(Some("2026-11-10T18:56:44Z"), None, None, Some(0))), "a command's");
    same(saved[0].agents()[2].figures.clone(), None, "a question's");
    let mut old = MightyGraphAgent::read(&serde_json::from_str::<Value>(r#"{"id":"a","title":"Old","input":"","status":"completed","entries":[]}"#).unwrap()).unwrap();
    old.figures = Some(figures(Some("2026-11-10T18:56:44Z"), None, Some(2), None));
    let mut out = JsonOut::new();
    old.write(&mut out);
    let text = out.finish();
    check(text.contains("\"figures\":{") && text.contains("\"toolCalls\":2") && !text.contains("endedAt"), format!("only what is there: {text}"));
    same(MightyGraphAgent::read(&serde_json::from_str::<Value>(&text).unwrap()).unwrap().figures, old.figures.clone(), "it reads back");

    let original = a("block").status("completed").background().build();
    let request = r("r").status("completed").agents(vec![original.clone()]).build();
    let first = r("r:notice:1").status("completed").agents(vec![a("r:notice:1:block").status("completed").background().source("block").build()]).notice().build();
    let second = r("r:notice:2")
        .status("completed")
        .agents(vec![
            a("r:notice:2:other").status("completed").background().source("other").build(),
            a("r:notice:2:block").status("completed").background().source("block").build(),
        ])
        .notice()
        .build();
    let runs = |list: &[&MightyGraphRun]| list.iter().map(|r| (*r).clone()).collect::<Vec<_>>();
    same(card::copy_node_id(&original, &runs(&[&request])), None, "no copy yet");
    same(card::copy_node_id(&original, &runs(&[&request, &first])), Some(flow::node_of(&first, "agent:r:notice:1:block")), "its copy");
    same(card::copy_node_id(&original, &runs(&[&request, &first, &second])), Some(flow::node_of(&second, "agent:r:notice:2:block")), "the newest copy");
    let impostor = r("x").status("completed").agents(vec![a("x-copy").status("completed").source("block").build()]).build();
    same(card::copy_node_id(&original, &runs(&[&request, &impostor])), None, "only a notice run holds a copy");
}
