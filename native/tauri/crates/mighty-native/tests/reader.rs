//! The reader's checks (plan §1.8 item 6): one hand-built tree per control kind, the wrapper merge rule, the nulls, the colour
//! names shared by two tokens per theme, `Unscaled` at every text scale, ids and the act table a read fills. Each expected text is
//! what C#'s `WebNativeReader` (WebNative.cs:174-371) writes for the same controls.

use std::sync::{Arc, Mutex};

use mighty_core::design_tokens::Token;
use mighty_core::locale::Locale;
use mighty_core::text_appearance::TextAppearance;
use mighty_native::act::Acts;
use mighty_native::ids::Ids;
use mighty_native::kit::{ControlSize, TextStyle, sheet_fit};
use mighty_native::reader::{Reader, render, write};
use mighty_native::tokens::{Brush, Theme};
use mighty_native::ui::*;
use serde_json::json;

fn env(theme: Theme) -> Env {
    Env { theme, text: TextAppearance::default(), locale: Locale::new("ko") }
}

fn dark() -> Env {
    env(Theme::Dark)
}

/// Runs `build` with a builder context over `env`.
fn with_ui<T>(env: &Env, build: impl FnOnce(&mut Ui) -> T) -> T {
    let mut locals = Locals::default();
    let mut ui = Ui::new(env, &mut locals);
    build(&mut ui)
}

#[test]
fn a_text_block_at_the_root_is_14_in_the_themes_ink_and_does_not_wrap() {
    assert_eq!(render(&El::text_block("Hi"), &dark()), r#"{"t":"text","text":"Hi","size":14,"ink":"ink","nowrap":true}"#);
    assert_eq!(render(&El::text_block(""), &dark()), "null", "no words: dropped");
    let mut inlines = El::text_block("");
    inlines.text_mut().unwrap().inlines = vec![Inline::Run("a".into()), Inline::LineBreak, Inline::Span(vec![Inline::Run("b".into())])];
    assert_eq!(render(&inlines, &dark()), r#"{"t":"text","text":"a\nb","size":14,"ink":"ink","nowrap":true}"#);
}

#[test]
fn settings_text_carries_its_size_weight_ink_and_selection() {
    let e = dark();
    with_ui(&e, |ui| {
        assert_eq!(
            render(&ui.settings_text("Label", TextStyle::default()), &e),
            r#"{"t":"text","text":"Label","size":13,"ink":"ink","va":"center"}"#
        );
        let caption = ui.settings_text("Why", TextStyle::size(11.0).ink(Token::Ink2).medium().selectable());
        assert_eq!(
            render(&caption, &e),
            r#"{"t":"text","text":"Why","size":11,"weight":"medium","ink":"ink2","selectable":true,"va":"center"}"#
        );
        let mono = ui.settings_text("C:\\x", TextStyle::size(10.0).mono());
        assert_eq!(render(&mono, &e), r#"{"t":"text","text":"C:\\x","size":10,"mono":true,"ink":"ink","va":"center"}"#);
        let tertiary = ui.settings_tertiary("none", 10.0, false, false);
        assert_eq!(render(&tertiary, &e), r#"{"t":"text","text":"none","size":10,"va":"center"}"#, "the tertiary ink has no name");
    });
}

/// `Unscaled(Size(s))` (both rounded half away from zero) gives the size the form wrote at every scale for the form's whole sizes, but
/// not always for a half size (12.5 at 90% is drawn 11.5 and reads 13, as in C#); a TextBlock that names none keeps WinUI's raw 14,
/// which reads smaller at a larger scale (C#'s own behaviour).
#[test]
fn sizes_are_unscaled_at_every_text_scale() {
    let read = |scale: f64, size: f64| {
        let e = Env { text: TextAppearance::new(scale, None, None, None), ..dark() };
        with_ui(&e, |ui| {
            let node: serde_json::Value = serde_json::from_str(&render(&ui.settings_text("x", TextStyle::size(size)), &e)).unwrap();
            node["size"].as_f64().unwrap()
        })
    };
    for scale in [0.85, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5] {
        for size in [9.0, 10.0, 11.0, 13.0, 17.0] {
            assert_eq!(read(scale, size), size, "{size} at {scale}");
        }
    }
    assert_eq!(read(0.9, 12.5), 13.0, "Size(12.5) = 11.5 at 90%, Unscaled(11.5) = 13");
    assert_eq!(read(1.1, 12.5), 12.5);
    let e = Env { text: TextAppearance::new(1.2, None, None, None), ..dark() };
    assert_eq!(render(&El::text_block("x"), &e), r#"{"t":"text","text":"x","size":11.5,"ink":"ink","nowrap":true}"#);
}

/// A code font without "Mono", "Consol" or "Code" in its name gives no `mono` (AppText.cs:49).
#[test]
fn mono_follows_the_chosen_code_font() {
    let hack = Env { text: TextAppearance::new(1.0, None, None, Some("Hack")), ..dark() };
    with_ui(&hack, |ui| {
        assert_eq!(
            render(&ui.settings_text("x", TextStyle::default().mono()), &hack),
            r#"{"t":"text","text":"x","size":13,"ink":"ink","va":"center"}"#
        );
        let field = ui.settings_field(El::text_box("a"), 11.0);
        assert!(render(&field, &hack).contains(r#""mono":false"#));
    });
    let fira = Env { text: TextAppearance::new(1.0, None, None, Some("Fira Code")), ..dark() };
    with_ui(&fira, |ui| {
        assert!(render(&ui.settings_text("x", TextStyle::default().mono()), &fira).contains(r#""mono":true"#));
        assert!(render(&ui.settings_field(El::text_box("a"), 11.0), &fira).contains(r#""mono":false"#), "a field's mono is Mono only");
    });
}

#[test]
fn colours_shared_by_two_tokens_read_as_the_first_per_theme() {
    let light = env(Theme::Light);
    with_ui(&light, |ui| {
        let white = ui.settings_text("x", TextStyle::default().ink(Token::OnAccent));
        assert!(render(&white, &light).contains(r#""ink":"card""#));
    });
    let dark = dark();
    with_ui(&dark, |ui| {
        let white = ui.settings_text("x", TextStyle::default().ink(Token::OnStatus));
        assert!(render(&white, &dark).contains(r#""ink":"on-status""#));
        let stop = ui.settings_text("x", TextStyle::default().ink(Token::StopText));
        assert!(render(&stop, &dark).contains(r#""ink":"ink2""#));
    });
}

/// The main trap (plan §1.3): a wrapper answers its child's dictionary and writes its own tail into it — a key the child had keeps
/// its place, a new one is appended.
#[test]
fn a_wrapper_writes_its_tail_into_its_childs_dictionary() {
    let inner = El::vstack(vec![El::text_block("a")]).margin(Thickness::new(1.0, 2.0, 3.0, 4.0)).mark("row");
    let scroll = El::new(Kind::ScrollViewer { content: inner.into() }).margin(Thickness::all(5.0)).max_height(300.0);
    assert_eq!(
        render(&scroll, &dark()),
        r#"{"t":"stack","dir":"v","gap":0,"c":[{"t":"text","text":"a","size":14,"ink":"ink","nowrap":true}],"role":"row","margin":[5,5,5,5],"maxH":300}"#
    );
    let bare = El::border(Border { child: Some(Box::new(El::text_block("b").aid("inner"))), ..Border::default() }).aid("outer");
    assert_eq!(render(&bare, &dark()), r#"{"t":"text","text":"b","size":14,"ink":"ink","nowrap":true,"aid":"outer"}"#);
    let disabled = El::new(Kind::ContentControl { content: El::button("Go").named("Go").into() }).enabled(false);
    assert_eq!(render(&disabled, &dark()), r#"{"t":"button","id":1,"name":"Go","label":"Go","disabled":true}"#);
}

#[test]
fn hidden_parts_are_dropped() {
    let stack = El::vstack(vec![
        El::text_block("shown"),
        El::text_block("hidden").visible(false),
        El::text_block("clear").opacity(0.0),
        El::shape(),
        El::new(Kind::SymbolIcon),
        El::new(Kind::Ellipse { fill: None }),
        El::new(Kind::Ellipse { fill: Some(Brush::TRANSPARENT) }),
        El::new(Kind::ProgressRing { active: false }),
        El::new(Kind::Image { src: None }),
        El::new(Kind::InfoBar { open: false, title: None, message: None, severity: "informational" }),
        El::vstack(vec![]),
    ]);
    assert_eq!(
        render(&stack, &dark()),
        r#"{"t":"stack","dir":"v","gap":0,"c":[{"t":"text","text":"shown","size":14,"ink":"ink","nowrap":true}]}"#
    );
    assert_eq!(render(&El::vstack(vec![El::shape()]), &dark()), "null", "an empty stack is null");
    assert_eq!(render(&El::text_block("x").mark("loading").margin(Thickness::all(3.0)), &dark()), r#"{"t":"loading"}"#);
}

#[test]
fn shapes_glyphs_and_pictures() {
    let e = dark();
    let accent = e.theme.brush(Token::Accent);
    assert_eq!(render(&El::new(Kind::Ellipse { fill: Some(accent) }), &e), r#"{"t":"dot","fill":"accent","size":8}"#);
    assert_eq!(render(&El::new(Kind::Ellipse { fill: Some(accent) }).width(6.0), &e), r#"{"t":"dot","fill":"accent","size":6,"w":6}"#);
    let glyph = El::new(Kind::FontIcon { glyph: "\u{E713}".into(), size: Some(14.0), foreground: Some(Brush::TRANSPARENT) });
    assert_eq!(render(&glyph, &e), "{\"t\":\"glyph\",\"glyph\":\"\\uE713\",\"size\":14,\"ink\":null}", "a glyph's ink is always written");
    assert_eq!(render(&El::font_icon("x", None), &e), r#"{"t":"glyph","glyph":"x","size":20,"ink":"ink"}"#);
    let picture = El::new(Kind::Image { src: Some("data:image/png;base64,AA==".into()) }).height(40.0);
    assert_eq!(render(&picture, &e), r#"{"t":"image","src":"data:image/png;base64,AA==","h":40}"#);
    assert_eq!(render(&El::new(Kind::ProgressRing { active: true }), &e), r#"{"t":"spinner"}"#);
    let bar = El::new(Kind::ProgressBar { value: 0.45, max: 1.0, indeterminate: false });
    assert_eq!(render(&bar, &e), r#"{"t":"progress","value":0.45,"max":1,"indeterminate":false}"#);
    let info = El::new(Kind::InfoBar { open: true, title: Some("T".into()), message: None, severity: "warning" });
    assert_eq!(render(&info, &e), r#"{"t":"info","title":"T","message":null,"severity":"warning"}"#);
}

#[test]
fn borders_rules_and_boxes() {
    let e = dark();
    let line = e.theme.brush(Token::Line);
    let rule = El::border(Border { background: Some(line), ..Border::default() }).height(1.0);
    assert_eq!(render(&rule, &e), r#"{"t":"rule","fill":"line","vertical":false,"h":1}"#);
    let vertical = El::border(Border { background: Some(line), ..Border::default() }).width(1.0);
    assert_eq!(render(&vertical, &e), r#"{"t":"rule","fill":"line","vertical":true,"w":1}"#);
    assert_eq!(render(&El::border(Border { background: Some(line), ..Border::default() }), &e), "null", "no child and not thin");
    let boxed = El::border(Border {
        child: Some(Box::new(El::text_block("in"))),
        background: Some(e.theme.brush(Token::CardRaised)),
        border_brush: Some(line),
        border_thickness: Thickness::all(1.0),
        padding: Thickness::new(4.0, 1.0, 4.0, 1.0),
        corner_radius: 6.0,
    });
    assert_eq!(
        render(&boxed, &e),
        r#"{"t":"box","c":[{"t":"text","text":"in","size":14,"ink":"ink","nowrap":true}],"fill":"card-raised","line":[1,1,1,1],"lineInk":"line","pad":[4,1,4,1],"radius":6}"#
    );
}

#[test]
fn a_grid_writes_its_cells_tracks_and_gaps() {
    let grid = El::grid(Grid {
        children: vec![El::text_block("a"), El::text_block("b").cell(1, 2).span(2, 3), El::text_block("gone").visible(false).cell(0, 1)],
        cols: vec![Track::Star(1.0), Track::Star(0.45), Track::Pixel(1000.0), Track::Auto],
        rows: vec![Track::Star(2.0), Track::Pixel(30.0)],
        row_spacing: 2.0,
        col_spacing: 8.0,
        ..Grid::default()
    });
    assert_eq!(
        render(&grid, &dark()),
        concat!(
            r#"{"t":"grid","c":[{"t":"text","text":"a","size":14,"ink":"ink","nowrap":true,"row":0,"col":0},"#,
            r#"{"t":"text","text":"b","size":14,"ink":"ink","nowrap":true,"row":1,"col":2,"rowSpan":2,"colSpan":3}],"#,
            r#""cols":["minmax(0,1fr)","minmax(0,0.45fr)","1000px","auto"],"rows":["auto","30px"],"gap":[2,8]}"#
        )
    );
    let bare = El::grid(Grid { children: vec![El::text_block("x")], ..Grid::default() });
    assert!(render(&bare, &dark()).contains(r#""cols":null,"rows":null,"gap":[0,0]"#));
    assert_eq!(render(&El::grid(Grid::default()), &dark()), "null");
}

#[test]
fn controls_carry_their_ids_and_state() {
    let e = dark();
    let stack = El::vstack(vec![
        El::new(Kind::Hyperlink { content: Content::None, uri: Some("https://x".into()) }).named("Guide"),
        El::check_box(true, "Never"),
        El::new(Kind::RadioButton { on: false, content: "One".into() }),
        El::new(Kind::ToggleSwitch { on: true, header: Content::None }).named("Sw"),
        El::combo_box(1, vec!["a".into(), El::text_block("b").into()]).named("Pick"),
        El::new(Kind::PasswordBox { text: "s".into(), placeholder: String::new() }).named("Key"),
        El::new(Kind::Slider { value: 2.0, min: 0.0, max: 5.0, step: 0.5 }).named("Sl"),
        El::new(Kind::ListView { index: -1, items: vec!["r".into(), Content::None] }),
        El::new(Kind::ItemsControl { items: vec!["i".into()] }),
    ]);
    let text = render(&stack, &e);
    let node: serde_json::Value = serde_json::from_str(&text).unwrap();
    let c = &node["c"];
    assert_eq!(c[0], json!({"t":"link","id":1,"label":"Guide"}));
    assert_eq!(c[1], json!({"t":"check","id":2,"on":true,"label":"Never"}));
    assert_eq!(c[2], json!({"t":"radio","id":3,"on":false,"label":"One"}));
    assert_eq!(c[3], json!({"t":"switch","id":4,"on":true,"name":"Sw"}));
    assert_eq!(c[4], json!({"t":"select","id":5,"index":1,"name":"Pick","items":["a","b"]}));
    assert_eq!(c[5], json!({"t":"field","id":6,"text":"s","password":true,"placeholder":"","name":"Key"}));
    assert_eq!(c[6], json!({"t":"slider","id":7,"value":2,"min":0,"max":5,"step":0.5,"name":"Sl"}));
    assert_eq!(c[7], json!({"t":"list","id":8,"index":-1,"items":[{"t":"text","text":"r"},{"t":"text","text":""}]}));
    assert_eq!(c[8], json!({"t":"stack","dir":"v","c":[{"t":"text","text":"i"}]}));
    let mut field = El::text_box("typed");
    if let Kind::TextBox { header, placeholder, .. } = &mut field.kind {
        *header = "Model id".into();
        *placeholder = "claude-…".into();
    }
    assert_eq!(
        render(&field, &e),
        "{\"t\":\"field\",\"id\":1,\"text\":\"typed\",\"placeholder\":\"claude-\\u2026\",\"multiline\":false,\"readonly\":false,\"name\":\"Model id\",\"mono\":false}"
    );
}

#[test]
fn an_expander_writes_null_children_when_closed_and_its_header() {
    let e = dark();
    let mut locals = Locals::default();
    let closed = {
        let mut ui = Ui::new(&e, &mut locals);
        ui.expander("more", false, "More", El::text_block("inside"))
    };
    assert_eq!(render(&closed, &e), r#"{"t":"expander","id":1,"open":false,"header":{"t":"text","text":"More"},"c":null}"#);
    let open = {
        let mut ui = Ui::new(&e, &mut locals);
        let kept = ui.local::<bool>("more", || false);
        kept.set(true);
        ui.expander("more", false, Content::None, El::text_block("inside"))
    };
    assert_eq!(
        render(&open, &e),
        r#"{"t":"expander","id":1,"open":true,"header":null,"c":[{"t":"text","text":"inside","size":14,"ink":"ink","nowrap":true}]}"#
    );
}

#[test]
fn buttons_labels_inner_content_accent_destructive_and_menus() {
    let e = dark();
    with_ui(&e, |ui| {
        assert_eq!(render(&ui.button("Save", || {}), &e), r#"{"t":"button","id":1,"name":"Save","label":"Save"}"#);
        let plain = ui.settings_push(ui.button("Remove", || {}), ControlSize::Regular, false, true);
        assert_eq!(render(&plain, &e), r#"{"t":"button","id":1,"name":"Remove","label":"Remove","destructive":true,"va":"center"}"#);
        let prominent = ui.settings_push(ui.button("Install", || {}), ControlSize::Small, true, false);
        assert_eq!(render(&prominent, &e), r#"{"t":"button","id":1,"name":"Install","label":"Install","accent":true,"va":"center"}"#);
        let off = ui.settings_push(ui.button("Install", || {}).enabled(false), ControlSize::Regular, true, false);
        assert_eq!(
            render(&off, &e),
            r#"{"t":"button","id":1,"name":"Install","label":"Install","accent":true,"va":"center","dim":true,"disabled":true}"#
        );
        let mut glyph = ui.button("Reload", || {});
        glyph.button_mut().unwrap().content = ui.settings_glyph_label("\u{E72C}", "Reload", 11.0).into();
        let glyph = ui.settings_push(glyph, ControlSize::Small, true, false);
        assert_eq!(
            render(&glyph, &e),
            concat!(
                "{\"t\":\"button\",\"id\":1,\"name\":\"Reload\",\"c\":[{\"t\":\"stack\",\"dir\":\"h\",\"gap\":4,\"c\":[",
                "{\"t\":\"glyph\",\"glyph\":\"\\uE72C\",\"size\":11,\"ink\":\"page\",\"va\":\"center\"},",
                "{\"t\":\"text\",\"text\":\"Reload\",\"size\":11,\"ink\":\"page\",\"nowrap\":true,\"va\":\"center\"}],\"va\":\"center\"}],",
                "\"accent\":true,\"va\":\"center\"}"
            ),
            "the content takes the prominent button's on-accent (by night the page's colour)"
        );
        let menu = ui.settings_menu_button(
            "Account",
            vec![
                MenuItem::item("Sign out", || {}),
                MenuItem::Separator,
                MenuItem::toggle("Auto", true, |_| {}),
                MenuItem::Sub { text: "More".into(), items: vec![MenuItem::item("Deep", || {})], enabled: false },
                MenuItem::item("", || {}),
            ],
        );
        let node: serde_json::Value = serde_json::from_str(&render(&menu, &e)).unwrap();
        assert_eq!(node["t"], "menu", "a button with a flyout is a menu, in the same place");
        assert_eq!(node.as_object().unwrap().keys().next().unwrap(), "t");
        assert_eq!(
            node["items"],
            json!([
                {"id":2,"label":"Sign out","disabled":false},
                {"sep":true},
                {"id":3,"label":"Auto","checked":true,"disabled":false},
                {"label":"More","items":[{"id":4,"label":"Deep","disabled":false}],"disabled":true}
            ])
        );
        assert_eq!(
            node["c"][0]["c"][1],
            json!({"t":"box","c":[{"t":"glyph","glyph":"\u{E70D}","size":8,"ink":"ink","ha":"center","va":"center"}],"radius":4,"w":16,"h":16,"va":"center"})
        );
    });
}

#[test]
fn the_kit_rows_labels_switches_and_segments() {
    let e = dark();
    with_ui(&e, |ui| {
        let mut rows = ui.settings_rows();
        rows.add(ui.settings_labeled(
            ui.settings_text("One", TextStyle::default()),
            ui.settings_switch("One", true, Some("one"), |_| {}),
            false,
        ));
        rows.add(ui.settings_text("Two", TextStyle::default())).visible = false;
        rows.add(ui.settings_text("Three", TextStyle::default()));
        let body = rows.finish();
        assert_eq!(
            render(&body, &e),
            concat!(
                r#"{"t":"stack","dir":"v","gap":0,"c":["#,
                r#"{"t":"box","c":[{"t":"grid","c":[{"t":"text","text":"One","size":13,"ink":"ink","va":"center","row":0,"col":0},"#,
                r#"{"t":"switch","id":1,"on":true,"name":"One","aid":"one","ha":"end","va":"center","w":26,"row":0,"col":1}],"#,
                r#""cols":["minmax(0,1fr)","auto"],"rows":null,"gap":[0,8],"role":"row"}],"pad":[0,7,0,8],"role":"settingsRow","margin":[7,0,7,0]},"#,
                r#"{"t":"box","c":[{"t":"text","text":"Three","size":13,"ink":"ink","va":"center"}],"line":[0,1,0,0],"lineInk":"line","pad":[0,8,0,8],"role":"settingsRow","margin":[7,0,7,0]}]}"#
            )
        );
        let options = [("dark".to_string(), "다크".to_string()), ("light".to_string(), "라이트".to_string())];
        let picked = Arc::new(Mutex::new(vec![]));
        let into = picked.clone();
        let segmented =
            ui.settings_segmented("테마", "settings-theme", &options, "dark", move |v| into.lock().unwrap().push(v), false, true);
        assert_eq!(
            render(&segmented, &e),
            "{\"t\":\"segmented\",\"name\":\"\\uD14C\\uB9C8\",\"options\":[{\"id\":1,\"label\":\"\\uB2E4\\uD06C\",\"on\":true},{\"id\":2,\"label\":\"\\uB77C\\uC774\\uD2B8\",\"on\":false}],\"disabled\":false,\"aid\":\"settings-theme\",\"h\":20}"
        );
        let off = ui.settings_segmented("t", "a", &options, "dark", |_| {}, true, false);
        assert!(render(&off, &e).ends_with(r#""disabled":true,"aid":"a","h":16,"dim":true}"#));
    });
}

/// A read fills the act table: an input on an id reaches the control's handler with C#'s argument; the segment already chosen does
/// nothing; an id the last read did not reach answers false; ids stay put across reads of the same tree.
#[test]
fn a_read_fills_the_act_table_and_keeps_ids() {
    let e = dark();
    let seen = Arc::new(Mutex::new(vec![]));
    let build = |seen: Arc<Mutex<Vec<String>>>, show_extra: bool| {
        with_ui(&e, move |ui| {
            let (a, b, c) = (seen.clone(), seen.clone(), seen.clone());
            let options = [("dark".to_string(), "Dark".to_string()), ("light".to_string(), "Light".to_string())];
            El::vstack(vec![
                ui.button("Extra", || {}).visible(show_extra),
                ui.button("Save", move || a.lock().unwrap().push("save".into())),
                El::text_box("").named("Name").on_text(move |t| b.lock().unwrap().push(format!("text {t}"))),
                ui.settings_segmented(
                    "Theme",
                    "theme",
                    &options,
                    "dark",
                    move |v| c.lock().unwrap().push(format!("pick {v}")),
                    false,
                    true,
                ),
            ])
        })
    };
    let (mut ids, mut acts) = (Ids::default(), Acts::default());
    let tree = build(seen.clone(), false);
    let first = write(&Reader::new(&e, "settings", &mut ids, &mut acts).node(&tree).unwrap());
    assert_eq!(acts.ids(), vec![1, 2, 3, 4], "the hidden button gets no id");
    let run = |acts: &Acts, id, op, value| acts.resolve(id, op, &value).run(|_| {});
    assert!(run(&acts, 1, "invoke", json!(null)));
    assert!(run(&acts, 2, "text", json!("abc")));
    assert!(run(&acts, 3, "toggle", json!(null)), "the chosen segment takes the toggle");
    assert!(run(&acts, 4, "toggle", json!(null)));
    assert!(!run(&acts, 9, "invoke", json!(null)));
    assert_eq!(*seen.lock().unwrap(), vec!["save", "text abc", "pick light"]);
    acts.clear();
    let again = write(&Reader::new(&e, "settings", &mut ids, &mut acts).node(&build(seen.clone(), false)).unwrap());
    assert_eq!(first, again, "the same tree read again has the same ids");
    acts.clear();
    let shown = write(&Reader::new(&e, "settings", &mut ids, &mut acts).node(&build(seen, true)).unwrap());
    assert!(
        shown.contains(r#""id":5,"name":"Extra""#) && shown.contains(r#""id":1,"name":"Save""#),
        "a new control gets a new id: {shown}"
    );
}

#[test]
fn tone_secondary_text_and_painted_buttons() {
    let e = env(Theme::Light);
    with_ui(&e, |ui| {
        let body = El::vstack(vec![
            El::text_block("dim").opacity(0.7),
            El::text_block("faint").opacity(0.5),
            ui.button("Plain", || {}).enabled(false),
        ]);
        let mut toned = ui.section_body(body);
        let text = render(&toned, &e);
        assert!(text.contains(r#""text":"dim","size":14,"ink":"ink2""#), "{text}");
        assert!(text.contains(r#""text":"faint","size":14,"ink":"ink3""#), "{text}");
        assert!(!text.contains("dim\":true"), "the opacity went back to 1");
        ui.tone_secondary(&mut toned, true);
        assert_eq!(text, render(&toned, &e), "toning twice changes nothing");
    });
}

#[test]
fn sheet_fit_takes_what_the_window_leaves() {
    assert_eq!(sheet_fit(560.0, 1440.0), 560.0);
    assert_eq!(sheet_fit(560.0, 500.0), 468.0);
    assert_eq!(sheet_fit(560.0, 200.0), 240.0);
    assert_eq!(sheet_fit(560.0, 0.0), 560.0);
}

#[test]
fn the_beta_badge_is_a_stop_capsule() {
    let e = dark();
    with_ui(&e, |ui| {
        assert_eq!(
            render(&ui.beta_badge(), &e),
            "{\"t\":\"box\",\"c\":[{\"t\":\"text\",\"text\":\"\\uBCA0\\uD0C0\",\"size\":9,\"weight\":\"medium\",\"ink\":\"ink2\",\"nowrap\":true}],\"fill\":\"stop-soft\",\"pad\":[4,1,4,1],\"radius\":7,\"va\":\"center\"}"
        );
    });
}
