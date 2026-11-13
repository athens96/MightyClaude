//! Renaming a workspace or a pane (C# `RenameSupport`, MightyCode.Core/RenameSupport.cs, with `RenameStrings`' locale keys) and an
//! agent pane's title following its requests until a rename fixes it (`PaneTitle`, MightyCode.Core/PaneTitle.cs; the rules it shares
//! with the normalize are `normalize::shortened`, `tooltip`, `auto_title`).

use std::sync::Arc;

use crate::AppSnapshot;
use crate::locale::Locale;
use crate::model::RunSession;
use crate::normalize::{self, auto_title, follows_requests, provider_name, shortened, tooltip};
use crate::wire;

/// The most text elements a name holds.
pub const MAXIMUM_TEXT_ELEMENTS: usize = 120;
/// `PaneTitle.Automatic` and `Fixed`.
pub const AUTOMATIC: &str = "auto";
pub const FIXED: &str = "fixed";

/// `RenameStrings`: the sheet's words, by locale key.
pub mod strings {
    pub const MENU_ENTRY: &str = "menu.rename";
    pub const FIELD_LABEL: &str = "pane.rename.namePlaceholder";
    pub const HEADING_WORKSPACE: &str = "pane.rename.workspaceTitle";
    pub const HEADING_SESSION: &str = "pane.rename.paneTitle";
    pub const HINT_WORKSPACE: &str = "pane.rename.workspaceNote";
    pub const HINT_SESSION: &str = "pane.rename.paneNote";
    pub const ERROR_TOO_LONG: &str = "pane.rename.tooLong";
    pub const ERROR_CONTROL_CHARACTER: &str = "pane.rename.noNewlines";
    pub const BUTTON_CANCEL: &str = "resume.cancel";
    pub const BUTTON_SAVE: &str = "pane.rename.save";
    pub const ERROR_NOT_FOUND: &str = "pane.rename.targetMissing";
    pub const AUTOMATIC: &str = "pane.rename.automatic";
}

/// Unicode Cc: U+0000–U+001F, U+007F, U+0080–U+009F.
fn has_control_character(text: &str) -> bool {
    text.chars().any(|c| (c as u32) < 0x20 || (0x7f..=0x9f).contains(&(c as u32)))
}

fn too_long(text: &str) -> bool {
    wire::text_elements(text, MAXIMUM_TEXT_ELEMENTS).1
}

/// `DisplayName`: the name trimmed, or none when it is empty, longer than 120 text elements or holds a control character.
pub fn display_name(value: &str) -> Option<String> {
    let name = value.trim();
    (!name.is_empty() && !too_long(name) && !has_control_character(name)).then(|| name.to_string())
}

/// `IsValid`: whether 저장 may be pressed.
pub fn is_valid(value: &str) -> bool {
    display_name(value).is_some()
}

/// `Messages`: the red captions under the field, in the Mac's order; an empty name shows none (it only disables 저장).
pub fn messages(value: &str, locale: &Locale) -> Vec<String> {
    let name = value.trim();
    let mut messages = vec![];
    if too_long(name) {
        messages.push(locale.get(strings::ERROR_TOO_LONG));
    }
    if has_control_character(name) {
        messages.push(locale.get(strings::ERROR_CONTROL_CHARACTER));
    }
    messages
}

/// `RenameWorkspace`: the workspace named `name`; an invalid name or an unknown workspace is refused with the sheet's words.
pub fn rename_workspace(snapshot: &AppSnapshot, workspace_id: &str, name: &str, locale: &Locale) -> Result<AppSnapshot, String> {
    let name = display_name(name).ok_or_else(|| locale.get(strings::ERROR_TOO_LONG))?;
    let at = snapshot.workspaces().iter().position(|w| w.id() == workspace_id).ok_or_else(|| locale.get(strings::ERROR_NOT_FOUND))?;
    let mut next = snapshot.clone();
    next.workspaces_mut()[at].name = Some(name);
    Ok(next)
}

/// `RenameSession`: the pane titled `name`, its title fixed from now on.
pub fn rename_session(snapshot: &AppSnapshot, session_id: &str, name: &str, locale: &Locale) -> Result<AppSnapshot, String> {
    let name = display_name(name).ok_or_else(|| locale.get(strings::ERROR_TOO_LONG))?;
    let at = snapshot.sessions().iter().position(|s| s.id() == session_id).ok_or_else(|| locale.get(strings::ERROR_NOT_FOUND))?;
    let mut next = snapshot.clone();
    let pane = &mut next.sessions_mut()[at];
    *pane = Arc::new(RunSession { title: Some(name), title_mode: Some(FIXED.into()), ..(**pane).clone() });
    Ok(next)
}

/// `PaneTitle.SetAutomatic`: the rename sheet's 자동, back to automatic and titled now from the newest request or the provider's name.
pub fn set_automatic(snapshot: &AppSnapshot, session_id: &str, locale: &Locale) -> Result<AppSnapshot, String> {
    let at = snapshot.sessions().iter().position(|s| s.id() == session_id).ok_or_else(|| locale.get(strings::ERROR_NOT_FOUND))?;
    let mut next = snapshot.clone();
    let pane = &mut next.sessions_mut()[at];
    let title = auto_title(pane, provider_name(pane.provider()));
    *pane = Arc::new(RunSession { title_mode: Some(AUTOMATIC.into()), title: Some(title), ..(**pane).clone() });
    Ok(next)
}

/// `PaneTitle.Requested`: a pane that just sent `input`, retitled when it follows its requests and the input has words.
pub fn requested(session: &RunSession, input: &str) -> RunSession {
    match shortened(normalize::ask_question(input)).filter(|_| follows_requests(session)) {
        Some(title) => RunSession { title: Some(title), ..session.clone() },
        None => session.clone(),
    }
}

/// `PaneTitle.Help`: a pane title's hover text, the whole request behind an automatic agent title, otherwise the title.
pub fn help(session: &RunSession) -> String {
    if follows_requests(session) { tooltip(session).unwrap_or_else(|| session.title().to_string()) } else { session.title().to_string() }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{LogEntry, Workspace};

    fn ko() -> Locale {
        Locale::new("ko")
    }

    fn user(text: &str) -> LogEntry {
        LogEntry::new(wire::new_id(), "user", text.into(), crate::time::now_o(), None)
    }

    fn agent(provider: &str, requests: &[&str]) -> RunSession {
        RunSession {
            workspace_id: Some("ws".into()),
            title: Some(provider_name(provider).into()),
            provider: Some(provider.into()),
            logs: Some(requests.iter().map(|r| user(r)).collect()),
            ..RunSession::default()
        }
    }

    fn state(workspaces: Vec<Workspace>, sessions: Vec<RunSession>) -> AppSnapshot {
        AppSnapshot { workspaces: Some(workspaces), sessions: Some(sessions.into_iter().map(Arc::new).collect()), ..AppSnapshot::default() }
    }

    /// `RenameVerification.renameValidationAcceptsValidNames`.
    #[test]
    fn rename_validation_accepts_valid_names() {
        assert_eq!(display_name("Hello").as_deref(), Some("Hello"));
        assert_eq!(display_name("  Hello  ").as_deref(), Some("Hello"));
        assert_eq!(display_name("안녕하세요").as_deref(), Some("안녕하세요"));
        assert_eq!(display_name("🎉").as_deref(), Some("🎉"));
        assert!(display_name(&"a".repeat(120)).is_some());
        assert!(display_name(&"가".repeat(120)).is_some());
    }

    /// `renameValidationRefusesEmpty`, `renameValidationRefusesOver120`, `renameValidationRefusesControlCharacters`.
    #[test]
    fn rename_validation_refuses_empty_long_and_control_names() {
        for refused in ["", "   ", "\n\t\r", "hello\nworld", "hello\rworld", "a\u{0}b", "a\u{1b}b"] {
            assert!(display_name(refused).is_none(), "{refused:?}");
        }
        assert!(display_name(&"a".repeat(121)).is_none());
        assert!(display_name(&"가".repeat(121)).is_none());
        assert!(display_name(&"🎉".repeat(121)).is_none());
    }

    /// `renameWorkspaceStoresNameInSnapshot`, `renameSessionStoresTitleInSnapshot`.
    #[test]
    fn a_rename_stores_the_name_in_the_snapshot() {
        let workspace = Workspace { name: Some("Old Name".into()), ..Workspace::default() };
        let session = RunSession { workspace_id: workspace.id.clone(), title: Some("Claude".into()), ..RunSession::default() };
        let snapshot = state(vec![workspace.clone()], vec![session.clone()]);
        let renamed = rename_workspace(&snapshot, workspace.id(), "새 이름", &ko()).unwrap();
        assert_eq!(renamed.workspaces()[0].name(), "새 이름");
        assert_eq!(renamed.workspaces()[0].path(), workspace.path(), "the path never changes");
        assert_eq!(renamed.sessions()[0].title(), "Claude");
        let renamed = rename_session(&snapshot, session.id(), "내 실행 창", &ko()).unwrap();
        assert_eq!(renamed.sessions()[0].title(), "내 실행 창");
        assert_eq!(renamed.sessions()[0].title_mode.as_deref(), Some(FIXED), "a rename fixes the title");
        assert_eq!(renamed.workspaces()[0].name(), "Old Name");
    }

    /// `renameRejectsInvalidName`, `renameRejectsUnknownTarget`.
    #[test]
    fn a_rename_refuses_an_invalid_name_or_an_unknown_target() {
        let workspace = Workspace::default();
        let session = RunSession { workspace_id: workspace.id.clone(), ..RunSession::default() };
        let snapshot = state(vec![workspace.clone()], vec![session.clone()]);
        assert!(rename_workspace(&snapshot, workspace.id(), "", &ko()).is_err());
        assert!(rename_session(&snapshot, session.id(), "   ", &ko()).is_err());
        let empty = AppSnapshot::default();
        assert_eq!(rename_workspace(&empty, "no-such-id", "X", &ko()), Err(ko().get(strings::ERROR_NOT_FOUND)));
        assert_eq!(rename_session(&empty, "no-such-id", "X", &ko()), Err(ko().get(strings::ERROR_NOT_FOUND)));
    }

    /// `renameMessagesMirrorTheMacOSCaptions`, `renameSaveButtonIsDisabledExactlyWhenMacOSDisablesIt`.
    #[test]
    fn rename_messages_mirror_the_mac_captions() {
        let l = ko();
        let (long, control) = (l.get(strings::ERROR_TOO_LONG), l.get(strings::ERROR_CONTROL_CHARACTER));
        assert!(messages("Hello", &l).is_empty() && messages("", &l).is_empty() && messages("   ", &l).is_empty());
        assert_eq!(messages(&"a".repeat(121), &l), std::slice::from_ref(&long));
        assert_eq!(messages(&"가".repeat(121), &l), std::slice::from_ref(&long));
        assert_eq!(messages("hello\nworld", &l), std::slice::from_ref(&control));
        assert_eq!(messages(&format!("{}\n", "a".repeat(121)), &l), std::slice::from_ref(&long), "a trailing line break is trimmed away");
        assert_eq!(messages(&format!("{}\nb", "a".repeat(121)), &l), [long, control], "both, length first");
        assert!(is_valid("Hello") && is_valid("  가  "));
        assert!(!is_valid("") && !is_valid("   ") && !is_valid(&"a".repeat(121)) && !is_valid("hello\nworld"));
    }

    /// `renameStringsMatchMacOS`: the sheet's Korean words are the Mac's.
    #[test]
    fn rename_strings_match_the_mac() {
        let l = ko();
        for (key, mac) in [
            (strings::MENU_ENTRY, "이름 변경…"),
            (strings::FIELD_LABEL, "이름"),
            (strings::HEADING_WORKSPACE, "워크스페이스 이름 변경"),
            (strings::HEADING_SESSION, "실행 창 이름 변경"),
            (strings::HINT_WORKSPACE, "앱에 표시되는 이름만 바뀌며 폴더 이름과 경로는 유지됩니다."),
            (strings::HINT_SESSION, "사이드바와 탭에 같은 이름이 표시됩니다."),
            (strings::ERROR_TOO_LONG, "이름은 120자 이내로 입력하세요."),
            (strings::ERROR_CONTROL_CHARACTER, "이름은 줄바꿈 없이 입력하세요."),
            (strings::BUTTON_CANCEL, "취소"),
            (strings::BUTTON_SAVE, "저장"),
            (strings::ERROR_NOT_FOUND, "대상을 찾을 수 없습니다. 창을 닫고 다시 시도하세요."),
        ] {
            assert_eq!(l.get(key), mac, "{key}");
        }
    }

    /// `renameKeepsALongEmojiNameWholeAcrossARestart`, `renameClampTitleStillBoundsAnOverlongStoredTitle`: the stored title is cut by
    /// text elements.
    #[test]
    fn a_long_emoji_name_stays_whole() {
        let name = "🎉".repeat(120);
        assert_eq!(display_name(&name).as_deref(), Some(name.as_str()));
        assert_eq!(normalize::clamp_title(Some(&name)), name);
        assert_eq!(normalize::clamp_title(Some(&"a".repeat(500))).len(), 120);
        assert_eq!(normalize::clamp_title(Some(&"🎉".repeat(500))), "🎉".repeat(120));
        assert_eq!(normalize::clamp_title(None), "");
        assert_eq!(normalize::clamp_title(Some("a\u{0}b")), "ab");
    }

    /// `PaneTitleVerification.titleFollowsTheLatestRequestUntilARenameFixesIt`.
    #[test]
    fn the_title_follows_the_latest_request_until_a_rename_fixes_it() {
        let mut pane = agent("claude", &[]);
        assert!(pane.title_mode.is_none() && follows_requests(&pane));
        pane = requested(&pane, "first request");
        assert_eq!(pane.title(), "first request");
        pane = requested(&pane, "second request\nwith a second line");
        assert_eq!(pane.title(), "second request with a second line");
        pane = requested(&pane, "   ");
        assert_eq!(pane.title(), "second request with a second line", "an empty request keeps the title");
        pane = requested(&pane, &"x".repeat(60));
        assert_eq!(pane.title(), format!("{}…", "x".repeat(40)));
        let workspace = Workspace::default();
        let session = RunSession { workspace_id: workspace.id.clone(), ..agent("claude", &[]) };
        let renamed = rename_session(&state(vec![workspace], vec![session.clone()]), session.id(), "내 실행 창", &ko()).unwrap();
        let renamed = renamed.sessions()[0].as_ref().clone();
        assert_eq!(renamed.title_mode.as_deref(), Some(FIXED));
        assert_eq!(requested(&renamed, "a later request").title(), "내 실행 창");
        for kind in ["shell", "browser", "files", "workmap"] {
            let other = RunSession { kind: Some(kind.into()), title: Some("터미널".into()), ..RunSession::default() };
            assert_eq!(requested(&other, "ls -la").title(), "터미널", "{kind} panes keep their titles");
        }
    }

    /// `PaneTitleVerification.automaticChoiceRetitlesFromTheLatestTypedRequest`.
    #[test]
    fn automatic_retitles_from_the_latest_typed_request() {
        let workspace = Workspace::default();
        let requests = ["first request", "fix the build\n\nfirst line kept\n\n첨부: a.png (1,024 bytes)", "첨부: b.txt (12 bytes)"];
        let pane = RunSession {
            workspace_id: workspace.id.clone(),
            title: Some("My pane".into()),
            title_mode: Some(FIXED.into()),
            ..agent("claude", &requests)
        };
        let codex = RunSession {
            workspace_id: workspace.id.clone(),
            title: Some("renamed".into()),
            title_mode: Some(FIXED.into()),
            ..agent("codex", &[])
        };
        let snapshot = state(vec![workspace], vec![pane.clone(), codex.clone()]);
        assert_eq!(tooltip(&pane).as_deref(), Some("fix the build\n\nfirst line kept"));
        let auto = set_automatic(&snapshot, pane.id(), &ko()).unwrap().sessions()[0].as_ref().clone();
        assert_eq!((auto.title_mode.as_deref(), auto.title()), (Some(AUTOMATIC), "fix the build first line kept"));
        assert_eq!(help(&auto), "fix the build\n\nfirst line kept");
        assert_eq!(help(&pane), "My pane");
        let fallback = set_automatic(&snapshot, codex.id(), &ko()).unwrap().sessions()[1].as_ref().clone();
        assert_eq!(fallback.title(), provider_name("codex"));
        assert!(set_automatic(&snapshot, "no-such-id", &ko()).is_err());
    }
}
