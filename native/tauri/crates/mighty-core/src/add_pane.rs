//! The "창 추가" menu and the ways a pane joins a workspace (C# `AddPaneMenu`, MightyCode.Core/AddPaneMenu.cs;
//! `RemoteWorkspaceFeatures.AddPaneEntries`; the window's `AddPaneTitle`, MainWindow.Navigation.cs:242; `SessionTemplate.Inherit`;
//! `AddToLayout`, MainWindow.Layout.cs:172): the agents, a terminal, a browser tab and the files pane, then the three ways to add a
//! workspace (open a folder, make a new one, create one from a Git address).

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::layout::{self, MAXIMUM_SESSIONS};
use crate::locale::Locale;
use crate::model::{PaneLayoutNode, RunSession, Workspace};
use crate::shell::effective_layout;
use crate::{AppSnapshot, model_label, shell, time, wire};

pub const SEPARATOR: &str = "-";
pub const AGENT_PREFIX: &str = "agent:";
pub const SHELL: &str = "shell";
pub const BROWSER: &str = "browser";
/// `FilePaneKind.Kind`.
pub const FILES: &str = "files";
pub const OPEN_PROJECT: &str = "openProject";
/// The shared locale key of the open-folder entry.
pub const OPEN_PROJECT_KEY: &str = "menu.openProject";
/// Make a new folder (optionally a Git repository) and add it.
pub const NEW_FOLDER: &str = "newFolder";
/// Create a workspace from a Git address this computer's credentials reach.
pub const FROM_GIT: &str = "fromGit";
pub const NEW_FOLDER_KEY: &str = "workspace.add.newFolder";
pub const FROM_GIT_KEY: &str = "workspace.add.fromGit";
pub const OPEN_FOLDER_SHORTCUT: &str = "Ctrl+O";
/// `FilePaneKind.Shortcut`.
pub const FILES_SHORTCUT: &str = "Ctrl+Shift+E";
/// Ctrl+N adds a Claude pane at once.
pub const NEW_PANE_SHORTCUT_PROVIDER: &str = "claude";
/// `AddPaneMenu.QuietLookUp`: how long the look-up for earlier sessions runs before the choice sheet shows in its checking state.
pub const QUIET_LOOK_UP_MS: u64 = 300;
/// `ResumableSessions.Providers`: the agents whose earlier sessions can be continued.
pub const RESUME_PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];

/// `AddPaneMenu.Entries`: `agent:<provider>` for each agent, then the others.
pub fn entries() -> Vec<String> {
    let mut entries: Vec<String> = wire::PROVIDERS.iter().map(|p| format!("{AGENT_PREFIX}{p}")).collect();
    entries.extend([SEPARATOR, SHELL, SEPARATOR, BROWSER, FILES, SEPARATOR, OPEN_PROJECT, NEW_FOLDER, FROM_GIT].map(str::to_string));
    entries
}

/// `ProviderCatalog.IsLocalOnly`: pi, opencode and Hermes run on this computer only.
pub fn is_local_only(provider: &str) -> bool {
    matches!(provider, "pi" | "opencode" | "hermes")
}

/// `RemoteWorkspaceFeatures.AddPaneEntries`: a remote workspace's menu offers its agents, a remote command and the three ways to add
/// a workspace.
pub fn entries_for(workspace: Option<&Workspace>) -> Vec<String> {
    if workspace.is_none_or(|w| w.remote.is_none()) {
        return entries();
    }
    let mut entries: Vec<String> = wire::PROVIDERS.iter().filter(|p| !is_local_only(p)).map(|p| format!("{AGENT_PREFIX}{p}")).collect();
    entries.extend([SEPARATOR, SHELL, SEPARATOR, OPEN_PROJECT, NEW_FOLDER, FROM_GIT].map(str::to_string));
    entries
}

/// `AddPaneMenu.AgentProvider`: the provider an `agent:` entry adds.
pub fn agent_provider(entry: &str) -> Option<&str> {
    entry.strip_prefix(AGENT_PREFIX)
}

/// `AddPaneMenu.Filtered`: the sidebar's workspaces for a search, name or path, ignoring case and surrounding spaces.
pub fn filtered<'a>(workspaces: &'a [Workspace], search: Option<&str>) -> Vec<&'a Workspace> {
    let needle = search.unwrap_or("").trim().to_lowercase();
    workspaces
        .iter()
        .filter(|w| needle.is_empty() || w.name().to_lowercase().contains(&needle) || w.path().to_lowercase().contains(&needle))
        .collect()
}

/// `AddPaneMenu.ShowsOpenFolderButton`: only when the list the sidebar shows is empty.
pub fn shows_open_folder_button(workspaces: &[Workspace], search: Option<&str>) -> bool {
    filtered(workspaces, search).is_empty()
}

/// `ResumableSessions.OffersResume`.
pub fn offers_resume(kind: &str, provider: &str) -> bool {
    kind == "claude" && RESUME_PROVIDERS.contains(&provider)
}

/// `AddPaneMenu.MayAskResume`: only a pick from the menu, and only for an agent whose sessions can be continued.
pub fn may_ask_resume(provider: &str, from_menu: bool) -> bool {
    from_menu && offers_resume("claude", provider)
}

/// `ResumableSessions.InUse`: the sessions open panes continue, in lower case.
pub fn in_use<'a>(sessions: impl IntoIterator<Item = &'a Arc<RunSession>>) -> HashSet<String> {
    sessions.into_iter().filter(|s| s.kind() == "claude").filter_map(|s| s.resume_id.as_deref()).map(str::to_lowercase).collect()
}

/// `AddPaneMenu.AsksResume`: whether the look-up found a session of this agent (`(provider, session id)`) that no open pane
/// continues.
pub fn asks_resume<'a>(provider: &str, sessions: impl IntoIterator<Item = (&'a str, &'a str)>, in_use: &HashSet<String>) -> bool {
    offers_resume("claude", provider) && sessions.into_iter().any(|(owner, id)| owner == provider && !in_use.contains(&id.to_lowercase()))
}

/// `ProviderCatalog.BetaLabel`: the badge rides in the text of a menu row.
pub fn beta_label(provider: &str, text: &str, locale: &Locale) -> String {
    if shell::is_beta(provider) { format!("{text} · {}", locale.get("badge.beta")) } else { text.to_string() }
}

/// The window's `AddPaneTitle`: an entry's words as the Mac's menu says them; none for an entry that keeps its own words.
pub fn title(entry: &str, remote: bool, locale: &Locale) -> Option<String> {
    match entry {
        SHELL => Some(locale.get(if remote { "remote.menu.newCommand" } else { "workspace.newTerminal" })),
        BROWSER => Some(locale.get("browser.newTab")),
        _ => agent_provider(entry).map(|provider| {
            let words = locale.get_with("workspace.newAgentPane", &[("provider", model_label::provider_label(provider))]);
            beta_label(provider, &words, locale)
        }),
    }
}

/// The add menu's label for an entry (`WebAddMenu`): its title, else its own words.
pub fn label(entry: &str, remote: bool, locale: &Locale) -> String {
    title(entry, remote, locale).unwrap_or_else(|| match entry {
        FILES => locale.get("menu.showFiles"),
        OPEN_PROJECT => locale.get(OPEN_PROJECT_KEY),
        NEW_FOLDER => locale.get(NEW_FOLDER_KEY),
        FROM_GIT => locale.get(FROM_GIT_KEY),
        other => other.to_string(),
    })
}

/// The shortcut the add menu names beside an entry.
pub fn shortcut(entry: &str) -> Option<&'static str> {
    match entry {
        OPEN_PROJECT => Some(OPEN_FOLDER_SHORTCUT),
        FILES => Some(FILES_SHORTCUT),
        _ => None,
    }
}

/// `PaneLayout.HasRoom`: the app holds at most 128 panes, every workspace together.
pub fn has_room(state: &AppSnapshot) -> bool {
    state.sessions().len() < MAXIMUM_SESSIONS
}

/// `SessionTemplate.LastUsed`: the newest log entry's time, else when the pane was made.
fn last_used(session: &RunSession) -> i64 {
    session
        .logs()
        .iter()
        .rev()
        .find_map(|log| time::parse(log.timestamp.as_deref()))
        .or_else(|| time::parse(session.created_at()))
        .map_or(i64::MIN, |t| t.utc_ticks())
}

/// `SessionTemplate.Find`: the agent pane of the same kind and provider used last (never a delegated child), whose choices a new one
/// takes.
pub fn template<'a>(target: &RunSession, sessions: &'a [Arc<RunSession>]) -> Option<&'a RunSession> {
    if target.kind() != "claude" {
        return None;
    }
    let mut best: Option<(&RunSession, i64)> = None;
    for session in sessions.iter().map(Arc::as_ref) {
        if session.id() == target.id()
            || session.parent_session_id.is_some()
            || session.kind() != target.kind()
            || session.provider() != target.provider()
        {
            continue;
        }
        let used = last_used(session);
        // OrderByDescending(LastUsed).ThenByDescending(CreatedAt, Ordinal).FirstOrDefault(): the first of the greatest.
        let better = match best {
            None => true,
            Some((current, current_used)) => {
                used > current_used || used == current_used && session.created_at().unwrap_or("") > current.created_at().unwrap_or("")
            }
        };
        if better {
            best = Some((session, used));
        }
    }
    best.map(|(session, _)| session)
}

/// `SessionTemplate.Inherit`: a new agent pane takes the model, the settings and the view of the last one used.
pub fn inherit(mut target: RunSession, sessions: &[Arc<RunSession>]) -> RunSession {
    if let Some(source) = template(&target, sessions) {
        target.model = source.model.clone();
        target.settings = source.settings.clone();
        target.agent_view_mode = source.agent_view_mode.clone();
    }
    target
}

/// The window's `SaveLayout`: the workspace's tree, or none.
pub fn save_layout(state: &mut AppSnapshot, workspace: &str, node: Option<PaneLayoutNode>) {
    let layouts = state.pane_layouts.get_or_insert_with(Default::default);
    match node {
        Some(node) => {
            layouts.insert(workspace.to_string(), Some(node));
        }
        None => {
            layouts.shift_remove(workspace);
        }
    }
}

/// The window's `SaveLayoutSelection`.
pub fn save_selection(state: &mut AppSnapshot, workspace: &str, id: &str) {
    state.pane_layout_active_session_ids.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(id.to_string()));
}

/// The window's `SaveLayoutMode`.
pub fn save_mode(state: &mut AppSnapshot, workspace: &str, mode: &str) {
    state.pane_layout_modes.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(mode.to_string()));
}

/// The window's `ActiveGroupId`: the tab group holding the workspace's active pane, where a new pane joins.
fn active_group(state: &AppSnapshot, workspace: &str, defaults: &mut HashMap<String, PaneLayoutNode>) -> Option<String> {
    let active = state.active_session_id.as_deref()?;
    let tree = effective_layout(state, workspace, defaults)?;
    layout::groups(&tree).into_iter().find(|g| g.session_ids().any(|s| s == active)).map(|g| g.id().to_string())
}

/// The window's `AddToLayout`: the state with a new pane in it, in `group`, else in the active pane's group, shown there and made the
/// active pane. It joins the tree before it becomes the active pane (as the active pane it would first show in the first group).
pub fn add_to_layout(
    state: &AppSnapshot,
    workspace: &str,
    pane: RunSession,
    group: Option<&str>,
    defaults: &mut HashMap<String, PaneLayoutNode>,
) -> AppSnapshot {
    let home = group.map(str::to_string).or_else(|| active_group(state, workspace, defaults));
    let id = pane.id().to_string();
    let mut joined = state.clone();
    joined.sessions_mut().push(Arc::new(pane));
    let tree = effective_layout(&joined, workspace, defaults).map(|tree| match &home {
        Some(home) => layout::move_tab(&tree, &id, home, "center", None),
        None => layout::select(&tree, &id),
    });
    joined.active_session_id = Some(id.clone());
    save_layout(&mut joined, workspace, tree);
    save_selection(&mut joined, workspace, &id);
    joined
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `AddPaneMenuVerification.TheMenuEndsWithTheThreeWaysToAddAWorkspace`.
    #[test]
    fn the_menu_ends_with_the_three_ways_to_add_a_workspace() {
        let entries = entries();
        let agents: Vec<&str> = entries.iter().take(wire::PROVIDERS.len()).filter_map(|e| agent_provider(e)).collect();
        assert_eq!(agents, wire::PROVIDERS, "agents come first, in provider order");
        assert_eq!(
            entries[entries.len() - 4..],
            [SEPARATOR, OPEN_PROJECT, NEW_FOLDER, FROM_GIT],
            "the three ways are last, after a separator"
        );
        for one in [OPEN_PROJECT, NEW_FOLDER, FROM_GIT] {
            assert_eq!(entries.iter().filter(|e| *e == one).count(), 1, "one entry of {one}");
        }
        assert!([SHELL, BROWSER, FILES].iter().all(|e| entries.iter().any(|x| x == e)), "terminal, browser and files entries stay");
        assert!(
            entries.windows(2).all(|pair| !(pair[0] == SEPARATOR && pair[1] == SEPARATOR)) && entries[0] != SEPARATOR,
            "no empty sections"
        );
        assert!(
            [OPEN_PROJECT, NEW_FOLDER, FROM_GIT, SHELL].iter().all(|e| agent_provider(e).is_none()),
            "only agent entries name a provider"
        );
        for (language, open, new, git) in [
            ("ko", "기존 폴더 열기…", "새 폴더 만들기…", "Git으로 생성…"),
            ("en", "Open Existing Folder…", "New Folder…", "Create from Git…"),
        ] {
            let locale = Locale::new(language);
            assert_eq!(
                (locale.get(OPEN_PROJECT_KEY), locale.get(NEW_FOLDER_KEY), locale.get(FROM_GIT_KEY)),
                (open.into(), new.into(), git.into())
            );
        }
        assert_eq!(OPEN_FOLDER_SHORTCUT, "Ctrl+O", "⌘O maps to Ctrl+O");
    }

    /// `AddPaneMenuVerification.TheOpenFolderButtonShowsOnlyWhenNoWorkspaceIsListed`.
    #[test]
    fn the_open_folder_button_shows_only_when_no_workspace_is_listed() {
        assert!(shows_open_folder_button(&[], None) && shows_open_folder_button(&[], Some("x")));
        let workspace = |name: &str, path: &str| Workspace { name: Some(name.into()), path: Some(path.into()), ..Workspace::default() };
        let workspaces = [workspace("MightyCode", r"C:\Work\MightyCode"), workspace("Notes", r"D:\Docs\notes")];
        assert!(
            !shows_open_folder_button(&workspaces, None)
                && !shows_open_folder_button(&workspaces, Some(""))
                && !shows_open_folder_button(&workspaces, Some("   "))
        );
        assert!(!shows_open_folder_button(&workspaces, Some("mighty")) && !shows_open_folder_button(&workspaces, Some(" DOCS ")));
        assert!(shows_open_folder_button(&workspaces, Some("no-such-folder")));
        assert!(filtered(&workspaces, Some("NotesD:")).is_empty(), "name and path are not joined");
        assert_eq!(filtered(&workspaces, Some("o")).len(), 2);
        assert_eq!(filtered(&workspaces, Some("code")).iter().map(|w| w.name()).collect::<Vec<_>>(), ["MightyCode"]);
    }

    /// `AddPaneMenuVerification.OnlyClaudeAndCodexFromTheMenuMayAskToResume`.
    #[test]
    fn only_resumable_agents_from_the_menu_may_ask_to_resume() {
        assert!(may_ask_resume("claude", true) && may_ask_resume("codex", true));
        assert!(may_ask_resume("gemini", true), "Antigravity may ask, as its conversations can be continued");
        for provider in wire::PROVIDERS {
            assert!(!may_ask_resume(provider, false), "{provider}: Ctrl+N starts at once");
        }
        assert_eq!(NEW_PANE_SHORTCUT_PROVIDER, "claude");
        assert!(!may_ask_resume("unknown", true));
    }

    #[test]
    fn a_session_another_pane_continues_is_not_asked_about() {
        let used: HashSet<String> = ["abc".to_string()].into();
        assert!(!asks_resume("claude", [("claude", "ABC")], &used), "the ids compare without regard to case");
        assert!(asks_resume("claude", [("claude", "ABC"), ("claude", "def")], &used));
        assert!(!asks_resume("claude", [("codex", "def")], &used), "another agent's session is not this one's");
    }

    #[test]
    fn a_remote_workspace_offers_its_agents_a_command_and_the_ways_to_add() {
        let reference = crate::model::RemoteWorkspaceReference {
            connection_id: Some("c".into()),
            workspace_id: Some("w".into()),
            host_name: Some("host".into()),
            version: 2,
        };
        let remote = Workspace { remote: Some(reference), ..Workspace::default() };
        let entries = entries_for(Some(&remote));
        assert!(!entries.iter().any(|e| [BROWSER, FILES, "agent:pi", "agent:opencode", "agent:hermes"].contains(&e.as_str())));
        assert!(entries.iter().any(|e| e == SHELL) && entries.ends_with(&[OPEN_PROJECT.into(), NEW_FOLDER.into(), FROM_GIT.into()]));
        let en = Locale::new("en");
        assert_eq!(title(SHELL, true, &en), Some(en.get("remote.menu.newCommand")));
        assert_eq!(title(SHELL, false, &en), Some(en.get("workspace.newTerminal")));
        assert!(label("agent:codex", false, &en).ends_with(&en.get("badge.beta")), "a beta agent's entry carries the badge in its words");
        assert_eq!(label(FILES, false, &en), en.get("menu.showFiles"));
    }

    fn pane(id: &str, workspace: &str, provider: &str, model: &str, at: &str) -> Arc<RunSession> {
        Arc::new(RunSession {
            id: Some(id.into()),
            workspace_id: Some(workspace.into()),
            provider: Some(provider.into()),
            model: Some(model.into()),
            created_at: Some(at.into()),
            ..RunSession::default()
        })
    }

    #[test]
    fn a_new_agent_pane_takes_the_choices_of_the_one_used_last() {
        let sessions = vec![
            pane("a", "w", "claude", "opus", "2026-01-01T00:00:00.0000000+00:00"),
            pane("b", "w", "claude", "sonnet", "2026-02-01T00:00:00.0000000+00:00"),
            pane("c", "w", "codex", "gpt", "2026-03-01T00:00:00.0000000+00:00"),
        ];
        let new = RunSession { workspace_id: Some("w".into()), ..RunSession::default() };
        assert_eq!(inherit(new, &sessions).model.as_deref(), Some("sonnet"));
        let shell = RunSession { kind: Some("shell".into()), ..RunSession::default() };
        assert_eq!(inherit(shell, &sessions).model.as_deref(), Some("default"), "a terminal takes nothing");
    }

    #[test]
    fn a_pane_joins_the_active_group_and_becomes_the_active_pane() {
        let mut state = AppSnapshot::default();
        state.workspaces_mut().push(Workspace { id: Some("w".into()), ..Workspace::default() });
        for id in ["a", "b"] {
            state.sessions_mut().push(pane(id, "w", "claude", "default", "2026-01-01T00:00:00.0000000+00:00"));
        }
        state.active_workspace_id = Some("w".into());
        state.active_session_id = Some("b".into());
        state.layout = Some("columns".into());
        let mut defaults = HashMap::new();
        let next = add_to_layout(
            &state,
            "w",
            RunSession { id: Some("c".into()), workspace_id: Some("w".into()), ..RunSession::default() },
            None,
            &mut defaults,
        );
        assert_eq!(next.active_session_id.as_deref(), Some("c"));
        let tree = next.pane_layouts.as_ref().unwrap()["w"].clone().unwrap();
        let groups = layout::groups(&tree);
        assert_eq!(groups.len(), 2, "the columns stay");
        assert_eq!(groups[1].session_ids().collect::<Vec<_>>(), ["b", "c"], "beside the active pane, in its group");
        assert_eq!(groups[1].selected_session_id.as_deref(), Some("c"));
        assert_eq!(next.pane_layout_active_session_ids.as_ref().unwrap()["w"].as_deref(), Some("c"));
    }
}
