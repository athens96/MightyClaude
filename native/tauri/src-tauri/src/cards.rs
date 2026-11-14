//! The cards' page methods (C# MainWindow.WebShell.Cards.cs `RegisterCardMethods`): the cards themselves (panes.rs makes them), the
//! answers to the run's requests through the run service — Allow once / Deny (`OnPermissionAllow`, `OnPermissionDeny`), the question
//! card's answers (`QuestionnaireAnswers`, `ValidateAnswers`, `AnswerQuestionnaire`) and the plan card's four (`AnswerPlanCard`) — a next
//! action's words into the draft, the background strip's fold, and the Antigravity card's answer (`antigravityAnswer`, antigravity.rs).
//! The sign-in card, web-open prompts and the status line are later stages' (README); their methods answer as C# does with nothing
//! waiting.

use std::sync::Arc;

use indexmap::IndexMap;
use mighty_composer::cards;
use mighty_run::plan_mode::{self, PlanDecision};
use mighty_run::questionnaire::{UserQuestionAnswer, UserQuestionnaire};
use serde_json::{Value, json};

use crate::composer::session;
use crate::host::{Host, Registry};
use crate::methods::{flag, text};
use crate::panes::{self, ensure};

/// `WebCardRequests.FirstOrDefault()`: the request the page's card answers.
fn current(host: &Host, id: &str) -> Option<mighty_run::permissions::ToolPermissionRequest> {
    let requests = panes::requests(host, id);
    cards::card_requests(&requests, false).first().map(|r| (*r).clone())
}

fn not_pending(host: &Host) -> String {
    host.locale.get("remote.error.notPendingPermission")
}

/// C# `QuestionnaireAnswers(questionnaire, draft)`: one answer per question, keyed by its text — the picked options in their order,
/// and the typed words where the question took them (or has no options).
fn questionnaire_answers(
    questionnaire: &UserQuestionnaire,
    answers: &[(Vec<String>, Option<String>)],
) -> IndexMap<String, UserQuestionAnswer> {
    let mut picked = IndexMap::new();
    for (index, question) in questionnaire.questions.iter().enumerate() {
        let (picks, custom) = answers.get(index).cloned().unwrap_or_default();
        let selected: Vec<&str> = question.options.iter().filter(|o| picks.contains(&o.label)).map(|o| o.label.as_str()).collect();
        let typed = if custom.is_some() || question.options.is_empty() { custom } else { None };
        picked.insert(question.question.clone(), UserQuestionAnswer::new(&selected, typed.as_deref()));
    }
    picked
}

pub fn register(methods: &mut Registry) {
    methods.add("cards", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let (cards, _) = panes::cards_of(&host, &mut view, &id).unwrap_or_default();
        Ok(serde_json::to_value(cards).unwrap_or(Value::Null))
    });
    methods.add("permissionAnswer", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), text(&args, "paneId"))?;
        let request = current(&host, &id).filter(|r| Some(r.id.as_str()) == text(&args, "requestId")).ok_or_else(|| not_pending(&host))?;
        let allow = flag(&args, "allow");
        // OnPermissionAllow answers only a request that may be allowed here.
        if allow && !request.can_allow {
            return Ok(Value::Null);
        }
        let answered = host.engine()?.service.respond_to_tool_permission(&id, &request.id, allow).await;
        if let Err(error) = answered {
            host.view().error = Some(error.message().to_string());
        }
        Ok(Value::Null)
    });
    methods.add("questionnaireAnswer", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), text(&args, "paneId"))?;
        let Some(request) = current(&host, &id).filter(|r| Some(r.id.as_str()) == text(&args, "requestId")) else {
            return Ok(json!(not_pending(&host)));
        };
        let Some(questionnaire) = UserQuestionnaire::parse(&request.input_json) else { return Ok(json!(not_pending(&host))) };
        let answers: Vec<(Vec<String>, Option<String>)> = args
            .get("answers")
            .and_then(Value::as_array)
            .map(|list| {
                list.iter()
                    .map(|answer| {
                        let picks = answer
                            .get("picks")
                            .and_then(Value::as_array)
                            .map(|p| p.iter().map(|v| v.as_str().unwrap_or("").to_string()).collect());
                        (picks.unwrap_or_default(), text(answer, "custom").map(str::to_string))
                    })
                    .collect()
            })
            .unwrap_or_default();
        let picked = questionnaire_answers(&questionnaire, &answers);
        if let Err(error) = questionnaire.validate_answers(&picked) {
            return Ok(json!(error.message()));
        }
        match host.engine()?.service.answer_questionnaire(&id, &request.id, &picked).await {
            Ok(()) => Ok(Value::Null),
            Err(error) => Ok(json!(error.message())),
        }
    });
    methods.add("planAnswer", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), text(&args, "paneId"))?;
        let request = current(&host, &id)
            .filter(|r| Some(r.id.as_str()) == text(&args, "requestId") && r.can_answer_plan)
            .ok_or_else(|| not_pending(&host))?;
        let decision = match text(&args, "decision").unwrap_or("") {
            "cancel" => PlanDecision::cancel(),
            "revise" => PlanDecision::revise(text(&args, "feedback").unwrap_or("")),
            "approveConfirm" => PlanDecision::approve_confirm_each(),
            "approveAuto" => PlanDecision::approve_auto_edit(),
            _ => return Err("Unknown decision.".into()),
        };
        // AnswerPlanCard: one answer at a time; a change request Core would refuse never leaves the card.
        {
            let mut view = host.view();
            let ui = panes::ui(&mut view, &id)?;
            let Some(draft) = ui.plan_draft.as_mut().filter(|d| d.request_id == request.id && !d.sending) else { return Ok(Value::Null) };
            if decision.kind == "revise"
                && let Err(error) = plan_mode::validated_feedback(decision.feedback.as_deref())
            {
                draft.error = Some(error.message().to_string());
                ui.dirty = true;
                return Ok(Value::Null);
            }
            draft.sending = true;
            draft.error = None;
            ui.dirty = true;
        }
        let answered = host.engine()?.service.answer_plan(&id, &request.id, &decision).await;
        let mut view = host.view();
        if let Some(ui) = view.panes.get_mut(&id) {
            if let Some(draft) = ui.plan_draft.as_mut().filter(|d| d.request_id == request.id) {
                draft.sending = false;
                if let Err(error) = &answered {
                    draft.error = Some(error.message().to_string());
                }
            }
            ui.dirty = true;
        }
        view.mighty.dirty(&id);
        Ok(Value::Null)
    });
    methods.add("nextActionFill", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        let id = ensure(&mut view, text(&args, "paneId"))?;
        let pane = session(&view, &id)?;
        let running = host.runs().is_some_and(|r| r.service.is_session_running(&id));
        let dialog = view.dialog_open;
        let ui = panes::ui(&mut view, &id)?;
        // WebNextActionFill: never while a dialog is open, the pane starts or runs.
        let may = !dialog && !ui.view.starting && !ui.view.queue_starting && pane.status() != "running" && !running;
        let index = args.get("index").and_then(Value::as_i64).unwrap_or(-1);
        let filled = cards::next_action_fill(&pane, may, text(&args, "entryId").unwrap_or(""), index, &ui.view.draft);
        Ok(filled.map_or(Value::Null, Value::from))
    });
    methods.add("backgroundWork", |host: Arc<Host>, args| async move {
        if text(&args, "action") != Some("toggle") {
            return Err("Unknown action.".into());
        }
        let mut view = host.view();
        ensure(&mut view, text(&args, "paneId"))?;
        let open = view.snapshot.background_work_open == Some(true);
        view.update(|state| state.background_work_open = Some(!open))?;
        for pane in view.panes.values_mut() {
            pane.dirty = true;
        }
        Ok(Value::Null)
    });
    // `WebAnswerAntigravity` (MainWindow.WebShell.Cards.cs:135): the card's answer, why nothing happened or null.
    methods.add("antigravityAnswer", |host: Arc<Host>, args| async move {
        let (pane, card, decision) = crate::antigravity::arguments(&args);
        let decision = crate::antigravity::decision(decision).ok_or_else(|| "Unknown decision.".to_string())?;
        let id = ensure(&mut host.view(), pane)?;
        let card = card.unwrap_or("").to_string();
        Ok(crate::antigravity::web_answer(&host, &id, &card, decision).await.map_or(Value::Null, Value::from))
    });
    // The status line's trust question is W6's: with no workspace command waiting for trust, there is nothing to answer (C# returns).
    methods.add("statusLineTrust", |host: Arc<Host>, args| async move {
        let id = ensure(&mut host.view(), text(&args, "paneId"))?;
        // The status line card's Allow / Not now (status_line.rs, W6-B: C# `StatusLineTrust`).
        crate::status_line::trust_answer(&host, &id, flag(&args, "allow"))?;
        Ok(Value::Null)
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// MainWindow.Questionnaire.cs:212 `QuestionnaireAnswers`: picks in the options' order, custom words only where typed or optionless.
    #[test]
    fn answers_follow_the_question_cards_draft() {
        let questionnaire = UserQuestionnaire::parse(
            r#"{"questions":[{"header":"Color","question":"Which colour?","multiSelect":true,"options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}]},{"header":"Name","question":"Your name?","multiSelect":false,"options":[]}]}"#,
        )
        .expect("a questionnaire");
        let picked = questionnaire_answers(
            &questionnaire,
            &[(vec!["Blue".into(), "Red".into(), "Green".into()], None), (vec![], Some("Ada".into()))],
        );
        assert_eq!(picked["Which colour?"], UserQuestionAnswer::new(&["Red", "Blue"], None));
        assert_eq!(picked["Your name?"], UserQuestionAnswer::new(&[], Some("Ada")));
    }
}
