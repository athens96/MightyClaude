//! C# QuestionnaireVerification (the parsing and answer checks; the channel's two are in tool_permission.rs) and
//! QuestionnaireProgressVerification (ThePetAnswersFromItsBubble is the companion's bubble, not the run engine's).

mod common;

use indexmap::IndexMap;
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::questionnaire::{QuestionnaireProgress, QuestionnaireStep, UserQuestionAnswer, UserQuestionnaire, guided};

const INPUT: &str = r#"{"questions":[{"header":"방식","question":"어떻게 처리할까요?","multiSelect":false,"options":[{"label":"첫 선택","description":"설명"},{"label":"다음 선택","description":""}]},{"header":"범위","question":"범위는?","multiSelect":true,"options":[{"label":"A","description":"첫 범위"},{"label":"B","description":"다음 범위"}]}],"response":"model-supplied response must not win","extra":"preserved"}"#;

fn answer(picks: &[&str], text: Option<&str>) -> UserQuestionAnswer {
    UserQuestionAnswer::new(picks, text)
}

#[test]
fn questionnaire_parsing_requires_complete_bounded_choices() {
    common::korean();
    let parsed = UserQuestionnaire::parse(INPUT).unwrap();
    assert!(
        parsed.questions.len() == 2 && parsed.questions[0].question == "어떻게 처리할까요?",
        "complete original Korean question text stays a protocol key"
    );
    assert!(
        UserQuestionnaire::parse(r#"{"questions":[{"question":"Incomplete"}]}"#).is_none(),
        "partial inputs cannot create an answer form"
    );
    assert!(UserQuestionnaire::parse(&INPUT.replace("다음 선택", "첫 선택")).is_none(), "duplicate option labels are refused");
    assert!(UserQuestionnaire::parse(&INPUT.replace("범위는?", "어떻게 처리할까요?")).is_none(), "duplicate question keys are refused");
    assert!(UserQuestionnaire::parse(&INPUT.replace("설명", &"가".repeat(30_000))).is_none(), "complete-display UTF-8 bound is enforced");
    // A Rust string cannot hold a lone surrogate; its JSON escape is the nearest input, and is refused, not thrown.
    assert!(
        UserQuestionnaire::parse("{\"questions\":\"\\ud83d\"}").is_none() && UserQuestionnaire::parse("prose \u{FFFD}").is_none(),
        "a surrogate without its pair is refused, not thrown"
    );
    assert!(
        UserQuestionnaire::parse(&format!(" \r\n\t{INPUT}")).map(|q| q.questions.len()) == Some(2)
            && UserQuestionnaire::parse(&format!("[{INPUT}]")).is_none()
            && UserQuestionnaire::parse("").is_none()
            && UserQuestionnaire::parse("# heading {\"questions\":[]}").is_none(),
        "only a JSON object is read, whatever whitespace leads it"
    );
}

#[test]
fn questionnaire_answers_enforce_selection_mode_and_original_order() {
    common::korean();
    let questionnaire = UserQuestionnaire::parse(INPUT).unwrap();
    let mut answers: IndexMap<String, UserQuestionAnswer> = IndexMap::new();
    answers.insert("어떻게 처리할까요?".into(), answer(&["첫 선택"], None));
    answers.insert("범위는?".into(), answer(&["B", "A"], Some("  자유 응답  ")));
    let validated = questionnaire.validate_answers(&answers).unwrap();
    assert_eq!(validated["범위는?"], "A, B, 자유 응답", "multiple answers follow displayed order, not click order");
    assert!(questionnaire.validate_answers(&IndexMap::new()).is_err());
    answers.insert("어떻게 처리할까요?".into(), answer(&["첫 선택", "다음 선택"], None));
    assert!(questionnaire.validate_answers(&answers).is_err());
    answers.insert("어떻게 처리할까요?".into(), answer(&["첫 선택"], Some("other")));
    assert!(questionnaire.validate_answers(&answers).is_err());
    answers.insert("어떻게 처리할까요?".into(), answer(&[], Some("내 답변")));
    assert_eq!(questionnaire.validate_answers(&answers).unwrap()["어떻게 처리할까요?"], "내 답변", "free text may replace a single option");
    answers.insert("범위는?".into(), answer(&["unoffered"], None));
    assert!(questionnaire.validate_answers(&answers).is_err());
}

// ── QuestionnaireProgressVerification ───────────────────────────────────────────────────────────────────────────────────

const MIXED: &str = r#"{"questions":[{"header":"범위","question":"어디까지 할까요?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":"b"}]},{"header":"대상","question":"무엇을 포함할까요?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""},{"label":"Z","description":""}]}]}"#;
const MULTI_FIRST: &str = r#"{"questions":[{"header":"대상","question":"무엇을?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""}]},{"header":"범위","question":"어디까지?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}"#;
const SINGLES: &str = r#"{"questions":[{"header":"하나","question":"첫째?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]},{"header":"둘","question":"둘째?","multiSelect":false,"options":[{"label":"C","description":""},{"label":"D","description":""}]}]}"#;
const LONE: &str = r#"{"questions":[{"header":"하나","question":"하나만?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}"#;

fn form(json: &str) -> UserQuestionnaire {
    UserQuestionnaire::parse(json).expect("fixture questionnaire did not parse")
}

fn same(answer: Option<&UserQuestionAnswer>, picks: &[&str], text: Option<&str>) -> bool {
    answer.is_some_and(|a| a.selected_options == picks && a.custom_text.as_deref() == text)
}

fn moved_on(step: Option<QuestionnaireStep>) -> bool {
    step.is_some_and(|s| !s.complete())
}

fn completed(step: Option<QuestionnaireStep>) -> IndexMap<String, UserQuestionAnswer> {
    step.and_then(|s| s.answers).expect("expected completion")
}

#[test]
fn progress_walks_questions_and_produces_valid_answers() {
    common::korean();
    let form_mixed = form(MIXED);
    let mut progress = QuestionnaireProgress::new("k");
    assert_eq!(progress.current(&form_mixed).map(|q| q.header.as_str()), Some("범위"), "the first question is shown first");
    assert!(progress.commit(Some("   "), &form_mixed).is_none(), "nothing to answer with");
    assert!(progress.choose("missing", &form_mixed).is_none() && progress.index == 0, "a label that was not offered answers nothing");
    assert!(moved_on(progress.choose("B", &form_mixed)) && progress.index == 1, "a single-select tile answers and moves on");
    // Multi-select tiles toggle; Enter (or 선택 완료) commits them with optional text, in option order.
    assert!(
        progress.choose("Z", &form_mixed).is_none()
            && progress.choose("X", &form_mixed).is_none()
            && progress.choose("Y", &form_mixed).is_none(),
        "a multi-select tile only toggles"
    );
    assert!(progress.choose("Y", &form_mixed).is_none() && progress.selected() == ["Z", "X"], "a second tap takes the pick back");
    let answers = completed(progress.commit(Some("그리고 W"), &form_mixed));
    assert!(
        same(answers.get("어디까지 할까요?"), &["B"], None) && same(answers.get("무엇을 포함할까요?"), &["X", "Z"], Some("그리고 W")),
        "picks in option order, the text with them"
    );
    let validated = form_mixed.validate_answers(&answers).unwrap();
    assert!(
        validated["어디까지 할까요?"] == "B" && validated["무엇을 포함할까요?"] == "X, Z, 그리고 W",
        "the answers are the ones the CLI accepts"
    );
    // Free text alone answers a single-select question; back returns to it.
    let mut typed = QuestionnaireProgress::new("k2");
    assert!(moved_on(typed.commit(Some("직접 입력"), &form_mixed)), "typed text answers a single-select question");
    typed.back(&form_mixed);
    assert!(typed.index == 0 && typed.selected().is_empty(), "back returns to the first question");
    // Going back to a multi-select question shows its earlier picks again.
    let mut revisit = QuestionnaireProgress::new("k3");
    let multi_first = form(MULTI_FIRST);
    revisit.choose("Y", &multi_first);
    assert!(moved_on(revisit.commit(Some(""), &multi_first)), "picks alone answer a multi-select question");
    revisit.back(&multi_first);
    assert!(revisit.index == 0 && revisit.selected() == ["Y"], "the earlier picks are shown again");
    assert!(
        moved_on(typed.choose("A", &form_mixed)) && same(typed.answers().get("어디까지 할까요?"), &["A"], None),
        "a tile replaces the typed answer"
    );
    // The pet highlights the recorded pick after 이전; answers given further on survive re-answering an earlier question.
    let mut pet = QuestionnaireProgress::new("k4");
    let singles = form(SINGLES);
    assert!(moved_on(pet.choose("A", &singles)), "the first of two answers moves on");
    pet.back(&singles);
    assert!(pet.index == 0 && pet.answers()["첫째?"].selected_options == ["A"], "the recorded pick is kept");
    pet.back(&singles);
    assert_eq!(pet.index, 0, "already at the first question");
    assert!(moved_on(pet.choose("B", &singles)), "re-answering moves on again");
    let revised = completed(pet.choose("D", &singles));
    let sent = singles.validate_answers(&revised).unwrap();
    assert!(sent["첫째?"] == "B" && sent["둘째?"] == "D", "the revised answers are sent");
    // A failed send leaves the last multi-choice question as it was, ready to send again.
    let mut retry = QuestionnaireProgress::new("k5");
    retry.choose("A", &form_mixed);
    retry.choose("X", &form_mixed);
    let first = completed(retry.commit(Some(""), &form_mixed));
    assert!(retry.index == 1 && retry.selected() == ["X"], "the last question stays as it was");
    let second = completed(retry.commit(Some(""), &form_mixed));
    let mut first_keys: Vec<&String> = first.keys().collect();
    let mut second_keys: Vec<&String> = second.keys().collect();
    first_keys.sort();
    second_keys.sort();
    assert!(
        first_keys == second_keys
            && first.iter().all(|(key, value)| {
                let picks: Vec<&str> = value.selected_options.iter().map(String::as_str).collect();
                same(second.get(key), &picks, value.custom_text.as_deref())
            }),
        "the same answers are ready to send again"
    );
}

#[test]
fn the_style_panel_takes_the_waiting_question() {
    common::korean();
    let bash = ToolPermissionRequest::new("perm-1", "pane", "toolu_b", "Bash", "{\"command\":\"ls\"}", "ls");
    let mut ask = ToolPermissionRequest::new("ask-1", "pane", "toolu_a", "AskUserQuestion", MIXED, "ask");
    ask.can_allow = false;
    ask.can_answer_questions = true;
    let mut later = ask.clone();
    later.id = "ask-2".into();
    later.input_json = LONE.into();
    let with = |change: &dyn Fn(&mut ToolPermissionRequest)| {
        let mut value = ask.clone();
        change(&mut value);
        value
    };
    assert!(guided::waiting(&[]).is_none() && guided::waiting(std::slice::from_ref(&bash)).is_none(), "no question, nothing for the panel");
    let waiting = guided::waiting(&[bash.clone(), ask.clone(), later.clone()]);
    assert!(
        waiting.is_some_and(|(request, questionnaire)| request.id == "ask-1" && questionnaire.questions.len() == 2),
        "the first waiting question, wherever it stands in the queue"
    );
    assert!(
        guided::waiting(&[with(&|r| r.can_answer_questions = false)]).is_none()
            && guided::waiting(&[with(&|r| r.state = "answered".into())]).is_none()
            && guided::waiting(&[with(&|r| r.input_json = "{}".into())]).is_none()
            && guided::waiting(&[with(&|r| r.tool_name = "Other".into())]).is_none(),
        "only a pending question that can be answered whole"
    );
    let visible: Vec<String> = guided::visible(&[bash.clone(), ask.clone(), later.clone()]).into_iter().map(|r| r.id).collect();
    assert_eq!(visible, ["perm-1", "ask-2"], "the card shows every other request, the next question among them");
    let mut allowed = bash.clone();
    allowed.state = "allowed".into();
    assert!(guided::visible(&[allowed, ask.clone()]).is_empty() && guided::visible(&[]).is_empty(), "settled requests are not shown");
}
