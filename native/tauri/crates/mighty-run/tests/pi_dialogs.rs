//! C# PiDialogVerification (MightyCode.Core.Tests/PiDialogVerification.cs, M/Tests/PiTests.swift): the extension dialogs pi sends in
//! RPC mode (`extension_ui_request`) shown as the question card, against the recordings pi-rpc-question.jsonl and pi-rpc-dialogs.jsonl.
//! The shipped extension's arguments are in pi_arguments.rs (they move the shared `AgentFiles` folder).

mod common;

use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::model::AgentActivity;
use mighty_run::parser::{OutputParser, ParserHooks};
use mighty_run::permissions::ToolPermissionRequest;
use mighty_run::pi_dialogs::{PiDialogChannel, PiDialogHooks};
use mighty_run::questionnaire::{UserQuestionAnswer, UserQuestionnaire};
use mighty_run::strings;
use serde_json::Value;

#[derive(Default)]
struct Lists {
    logs: Vec<(String, String)>,
    activities: Vec<AgentActivity>,
    cards: Vec<ToolPermissionRequest>,
    writes: Vec<Value>,
    cancelled: Vec<String>,
    warnings: Vec<String>,
    timers: Vec<(String, f64)>,
}

struct Asked {
    lists: Arc<Mutex<Lists>>,
    parser: Arc<OutputParser>,
    dialogs: Arc<PiDialogChannel>,
}

impl Asked {
    fn lists(&self) -> std::sync::MutexGuard<'_, Lists> {
        self.lists.lock().unwrap()
    }
    /// The cards still waiting: pending, and never settled since.
    fn pending(&self) -> Vec<ToolPermissionRequest> {
        let cards = self.lists().cards.clone();
        cards
            .iter()
            .filter(|card| card.state == "pending" && !cards.iter().any(|c| c.id == card.id && c.state != "pending"))
            .cloned()
            .collect()
    }
    fn states(&self) -> Vec<String> {
        self.lists().cards.iter().map(|c| c.state.clone()).collect()
    }
    fn last_write(&self) -> Value {
        self.lists().writes.last().expect("a write").clone()
    }
}

fn ask() -> Asked {
    let lists: Arc<Mutex<Lists>> = Arc::default();
    let (log, activity, reply) = (lists.clone(), lists.clone(), lists.clone());
    let parser = Arc::new(OutputParser::new(
        "pi",
        "run-pi",
        ParserHooks {
            log: Some(Arc::new(move |kind, text| log.lock().unwrap().logs.push((kind.into(), text.into())))),
            resume: Some(Arc::new(|_| {})),
            activity: Some(Arc::new(move |value| activity.lock().unwrap().activities.push(value))),
            pi_reply: Some(Arc::new(move |text| reply.lock().unwrap().cancelled.push(text.into()))),
            ..ParserHooks::default()
        },
    ));
    let (write, emit, warning, timer) = (lists.clone(), lists.clone(), lists.clone(), lists.clone());
    let row = Arc::downgrade(&parser);
    let dialogs = Arc::new(PiDialogChannel::new(
        "run-pi",
        PiDialogHooks {
            write: Arc::new(move |text| write.lock().unwrap().writes.push(common::json(text))),
            emit: Arc::new(move |card| emit.lock().unwrap().cards.push(card)),
            activity: Arc::new(move |card, state| {
                if let Some(parser) = row.upgrade() {
                    parser.permission_activity(card, state);
                }
            }),
            warning: Arc::new(move |message| warning.lock().unwrap().warnings.push(message.into())),
            timer: Some(Arc::new(move |id: &str, seconds: f64| timer.lock().unwrap().timers.push((id.into(), seconds)))),
        },
    ));
    parser.set_pi_dialogs(Some(dialogs.clone()));
    Asked { lists, parser, dialogs }
}

fn fixture(name: &str) -> Vec<String> {
    common::recording(&format!("pi-rpc-{name}"))
}

fn answer(card: &ToolPermissionRequest, picked: &[&str], typed: Option<&str>) -> IndexMap<String, UserQuestionAnswer> {
    let question = UserQuestionnaire::parse(&card.input_json).unwrap().questions[0].question.clone();
    IndexMap::from([(question, UserQuestionAnswer::new(picked, typed))])
}

/// PiDialogVerification.cs:41 `TheAskUserToolIsTheQuestionCardAndItsAnswerGoesBackAsTheDialogsValue`.
#[test]
fn the_ask_user_tool_is_the_question_card_and_its_answer_goes_back_as_the_dialogs_value() {
    common::korean();
    let run = ask();
    let mut answered = false;
    for line in fixture("question") {
        run.parser.parse(&line);
        if answered {
            continue;
        }
        let Some(card) = run.pending().first().cloned() else { continue };
        answered = true;
        let questions = UserQuestionnaire::parse(&card.input_json).unwrap().questions;
        assert!(
            card.tool_name == "AskUserQuestion" && card.can_answer_questions && !card.can_allow && card.tool_use_id == "toolu_question_1",
            "the card: {card:?}"
        );
        assert!(
            questions.iter().map(|q| q.question.as_str()).eq(["What is your favorite color?"])
                && questions[0].options.iter().map(|o| o.label.as_str()).eq(["Red", "Green", "Blue"])
                && !questions[0].multi_select,
            "the question"
        );
        assert_eq!(run.lists().activities.last().unwrap().state, "waiting", "the ask_user row waits");
        assert!(run.dialogs.answer_questions(&card.id, &answer(&card, &[], None)).is_err(), "nothing picked is no answer");
        run.dialogs.answer_questions(&card.id, &answer(&card, &["Red"], None)).unwrap();
    }
    assert!(answered && run.lists().cancelled.is_empty() && run.lists().warnings.is_empty(), "answered, nothing cancelled");
    let writes = run.lists().writes.clone();
    assert!(
        writes.len() == 1
            && writes[0]["type"] == "extension_ui_response"
            && writes[0]["value"] == "Red"
            && writes[0]["id"] == "7dcf54f5-170a-4f79-9fbb-3e627cc9a099",
        "the dialog's value"
    );
    assert_eq!(run.states(), ["pending", "answered"], "pending, answered");
    let replies: Vec<String> = run.lists().logs.iter().filter(|(kind, _)| kind == "assistant").map(|(_, text)| text.clone()).collect();
    assert_eq!(replies, ["Your favorite color is red."], "the turn goes on: {:?}", run.lists().logs);
    assert_eq!(
        run.lists().activities.iter().rev().find(|a| a.tool_name.as_deref() == Some("ask_user")).unwrap().state,
        "completed",
        "the ask_user row completes"
    );
}

/// PiDialogVerification.cs:63 `TheAppsToolTakesATypedAnswerAndSeveralPicksAndAnyOtherSelectOnlyItsOptions`.
#[test]
fn the_apps_tool_takes_a_typed_answer_and_several_picks_and_any_other_select_only_its_options() {
    common::korean();
    let asking = |multiple: bool, tool: &str| {
        let run = ask();
        run.parser.parse(&format!(
            "{{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"{tool}\",\"args\":{{\"question\":\"Which fruits?\",\"options\":[\"Apple\",\"Banana\",\"Cherry\"],\"multiple\":{multiple}}}}}"
        ));
        run.parser.parse(
            "{\"type\":\"extension_ui_request\",\"id\":\"dialog-1\",\"method\":\"select\",\"title\":\"Which fruits?\",\"options\":[\"Apple\",\"Banana\",\"Cherry\"]}",
        );
        let card = run.pending().first().cloned().expect("a card");
        (run, card)
    };
    let (run, card) = asking(true, "ask_user");
    assert!(UserQuestionnaire::parse(&card.input_json).unwrap().questions[0].multi_select, "several picks");
    run.dialogs.answer_questions(&card.id, &answer(&card, &["Cherry", "Apple"], Some(" Mango "))).unwrap();
    assert_eq!(run.last_write()["value"], "Apple, Cherry, Mango", "in the offered order, the typed answer last");
    let (run, card) = asking(false, "ask_user");
    run.dialogs.answer_questions(&card.id, &answer(&card, &[], Some("Mango"))).unwrap();
    assert_eq!(run.last_write()["value"], "Mango", "a typed answer");
    let (run, _card) = asking(false, "ask_user");
    run.parser.parse(
        "{\"type\":\"tool_execution_end\",\"toolCallId\":\"call-1\",\"toolName\":\"ask_user\",\"result\":{\"content\":[]},\"isError\":true}",
    );
    assert!(run.states() == ["pending", "cancelled"] && run.lists().writes.is_empty(), "an aborted tool withdraws its card");
    let (run, card) = asking(true, "pick_fruit");
    assert!(
        !UserQuestionnaire::parse(&card.input_json).unwrap().questions[0].multi_select && card.tool_use_id == "pi-dialog-dialog-1",
        "another extension's select"
    );
    assert!(
        run.dialogs.answer_questions(&card.id, &answer(&card, &[], Some("Mango"))).is_err()
            && run.lists().writes.is_empty()
            && run.pending().len() == 1,
        "reads only its options"
    );
    run.dialogs.answer_questions(&card.id, &answer(&card, &["Banana"], None)).unwrap();
    assert_eq!(run.last_write()["value"], "Banana", "one option");
}

/// PiDialogVerification.cs:94 `ConfirmAndInputAreCardsATimedDialogClosesItselfAndAnEditorIsStillCancelled`.
#[test]
fn confirm_and_input_are_cards_a_timed_dialog_closes_itself_and_an_editor_is_still_cancelled() {
    common::korean();
    let yes = strings::get("questionnaire.option.yes");
    let no = strings::get("questionnaire.option.no");
    let run = ask();
    let lines = fixture("dialogs");
    run.parser.parse(&lines[0]);
    let card = run.pending().first().cloned().unwrap();
    let question = UserQuestionnaire::parse(&card.input_json).unwrap().questions[0].clone();
    assert!(
        question.header == "Clear cache?"
            && question.question == "All cached files will be removed."
            && question.options.iter().map(|o| o.label.clone()).eq([yes.clone(), no.clone()]),
        "confirm"
    );
    assert!(run.dialogs.answer_questions(&card.id, &answer(&card, &[], Some("maybe"))).is_err(), "confirm takes yes or no");
    run.dialogs.answer_questions(&card.id, &answer(&card, &[no.as_str()], None)).unwrap();
    assert!(run.last_write()["confirmed"] == false && run.last_write()["id"] == "4b4a8f51-a5ac-41bd-a77d-a13763abd897", "confirmed false");
    run.parser.parse(&lines[1]);
    let card = run.pending().first().cloned().unwrap();
    let question = UserQuestionnaire::parse(&card.input_json).unwrap().questions[0].clone();
    assert!(question.question == "Project name" && question.options.is_empty(), "input: nothing to pick");
    run.dialogs.answer_questions(&card.id, &answer(&card, &[], Some("demo"))).unwrap();
    assert_eq!(run.last_write()["value"], "demo", "the typed text");
    run.parser.parse(&lines[2]);
    let card = run.pending().first().cloned().unwrap();
    let timers = run.lists().timers.clone();
    assert!(timers.len() == 1 && timers[0].0 == card.id && timers[0].1 == 1.5, "a deadline");
    run.dialogs.timed_out(&card.id);
    assert!(
        run.pending().is_empty()
            && run.lists().cards.last().unwrap().state == "cancelled"
            && run.lists().warnings == [strings::get_with("run.notice.piDialogTimedOut", &[("title", "Pick a color")])],
        "pi closed it itself"
    );
    assert!(run.dialogs.answer_questions(&card.id, &answer(&card, &["Red"], None)).is_err(), "too late");
    run.parser.parse(&lines[3]);
    let cancelled = run.lists().cancelled.clone();
    assert!(
        run.pending().is_empty()
            && cancelled.len() == 1
            && common::json(&cancelled[0])["cancelled"] == true
            && common::json(&cancelled[0])["id"] == "b0b081e4-bba2-4b64-a094-7886256af76f",
        "an editor is cancelled"
    );
    assert_eq!(
        run.lists().logs.last().unwrap().1,
        strings::get_with("run.notice.piDialogDismissed", &[("title", "Edit notes")]),
        "and named"
    );
    run.parser.parse(&lines[4]);
    assert!(run.lists().writes.len() == 2 && run.lists().cancelled.len() == 1, "notify needs no answer");
    // The card's Cancel dismisses the dialog; a stopped run withdraws it; more options than the card holds is cancelled as before.
    let cancel = ask();
    cancel.parser.parse(&lines[0]);
    let card = cancel.pending().first().cloned().unwrap();
    assert!(cancel.dialogs.respond(&card.id, true).is_err(), "a dialog is not allowed");
    cancel.dialogs.respond(&card.id, false).unwrap();
    assert!(cancel.last_write()["cancelled"] == true && cancel.lists().cards.last().unwrap().state == "denied", "dismissed");
    cancel.parser.parse(&lines[1]);
    let card = cancel.pending().first().cloned().unwrap();
    cancel.dialogs.cancel_all();
    assert!(
        cancel.lists().cards.last().unwrap().state == "cancelled"
            && cancel.lists().writes.len() == 1
            && cancel.dialogs.answer_questions(&card.id, &answer(&card, &[], Some("late"))).is_err(),
        "withdrawn when the run stops"
    );
    let many = ask();
    let options: Vec<String> = (1..=13).map(|i| format!("\"option {i}\"")).collect();
    many.parser.parse(&format!(
        "{{\"type\":\"extension_ui_request\",\"id\":\"dialog-9\",\"method\":\"select\",\"title\":\"Pick\",\"options\":[{}]}}",
        options.join(",")
    ));
    assert!(
        many.lists().cards.is_empty()
            && many.lists().cancelled.len() == 1
            && many.lists().logs.last().unwrap().1 == strings::get_with("run.notice.piDialogDismissed", &[("title", "Pick")]),
        "thirteen options are cancelled"
    );
}
