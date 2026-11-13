//! C# `QuestionnaireVerification`'s checks of `UserQuestionnaire` (Verification.cs:433-434), names kept. The channel's checks
//! (435-436) are the run engine's (W2).

mod common;

use std::collections::HashMap;

use mighty_composer::presentation::{UserQuestionAnswer, UserQuestionnaire};

const INPUT: &str = r#"{"questions":[{"header":"방식","question":"어떻게 처리할까요?","multiSelect":false,"options":[{"label":"첫 선택","description":"설명"},{"label":"다음 선택","description":""}]},{"header":"범위","question":"범위는?","multiSelect":true,"options":[{"label":"A","description":"첫 범위"},{"label":"B","description":"다음 범위"}]}],"response":"model-supplied response must not win","extra":"preserved"}"#;

/// Verification.cs:433 "QuestionnaireVerification QuestionnaireParsingRequiresCompleteBoundedChoices". A Rust string holds no lone
/// surrogate, so that check (a text C# cannot read as UTF-16) has nothing to port.
#[test]
fn questionnaire_parsing_requires_complete_bounded_choices() {
    let parsed = UserQuestionnaire::parse(INPUT).unwrap();
    assert!(parsed.questions.len() == 2 && parsed.questions[0].question == "어떻게 처리할까요?");
    assert!(UserQuestionnaire::parse(r#"{"questions":[{"question":"Incomplete"}]}"#).is_none());
    assert!(UserQuestionnaire::parse(&INPUT.replace("다음 선택", "첫 선택")).is_none(), "duplicate option labels are refused");
    assert!(UserQuestionnaire::parse(&INPUT.replace("범위는?", "어떻게 처리할까요?")).is_none(), "duplicate question keys are refused");
    assert!(UserQuestionnaire::parse(&INPUT.replace("설명", &"가".repeat(30_000))).is_none(), "complete-display UTF-8 bound is enforced");
    assert_eq!(UserQuestionnaire::parse(&format!(" \r\n\t{INPUT}")).map(|q| q.questions.len()), Some(2));
    assert!(UserQuestionnaire::parse(&format!("[{INPUT}]")).is_none());
    assert!(UserQuestionnaire::parse("").is_none());
    assert!(UserQuestionnaire::parse("# heading {\"questions\":[]}").is_none());
}

fn answer(picks: &[&str], custom: Option<&str>) -> UserQuestionAnswer {
    UserQuestionAnswer { selected_options: picks.iter().map(|p| p.to_string()).collect(), custom_text: custom.map(str::to_string) }
}

/// Verification.cs:434 "QuestionnaireVerification QuestionnaireAnswersEnforceSelectionModeAndOriginalOrder".
#[test]
fn questionnaire_answers_enforce_selection_mode_and_original_order() {
    let l = common::ko();
    let questionnaire = UserQuestionnaire::parse(INPUT).unwrap();
    let mut answers: HashMap<String, UserQuestionAnswer> = [
        ("어떻게 처리할까요?".to_string(), answer(&["첫 선택"], None)),
        ("범위는?".to_string(), answer(&["B", "A"], Some("  자유 응답  "))),
    ]
    .into();
    assert_eq!(
        questionnaire.validate_answers(&answers, &l).unwrap()["범위는?"],
        "A, B, 자유 응답",
        "multiple answers follow displayed order"
    );
    assert!(questionnaire.validate_answers(&HashMap::new(), &l).is_err());
    answers.insert("어떻게 처리할까요?".into(), answer(&["첫 선택", "다음 선택"], None));
    assert!(questionnaire.validate_answers(&answers, &l).is_err());
    answers.insert("어떻게 처리할까요?".into(), answer(&["첫 선택"], Some("other")));
    assert!(questionnaire.validate_answers(&answers, &l).is_err());
    answers.insert("어떻게 처리할까요?".into(), answer(&[], Some("내 답변")));
    assert_eq!(
        questionnaire.validate_answers(&answers, &l).unwrap()["어떻게 처리할까요?"],
        "내 답변",
        "free text may replace a single option"
    );
    answers.insert("범위는?".into(), answer(&["unoffered"], None));
    assert_eq!(questionnaire.validate_answers(&answers, &l), Err(l.get("questionnaire.error.selection")));
}
