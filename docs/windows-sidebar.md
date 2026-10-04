# Windows workspace sidebar

The workspace hierarchy follows `WorkspaceView.workspaceRow` and `AppStore.expandedWorkspaceSet` on macOS. Disclosure and selection are separate native buttons. Expanding or collapsing a workspace only changes its list; it does not select a workspace, replace the active pane, or modify its draft. Selecting a workspace opens its list without closing other lists.

`expandedWorkspaceIds` is an optional version-1 snapshot field. A missing or null value uses the active workspace for compatibility with older snapshots. An explicit empty array keeps every workspace collapsed. Persistence removes unknown IDs and duplicates, preserving stable ordering. A malformed optional value uses the legacy fallback instead of discarding the remaining snapshot.

Each expanded workspace contains its own sessions, Git badge, and the full shared Add Pane menu. Menu actions bind to that workspace and recheck the target after asynchronous model refresh. Search filters complete workspace groups. The footer provides a theme switch; changing theme retains pane and editor instances. Native buttons support keyboard focus and UI Automation invocation, and disclosure keyboard focus is restored after rendering. Session accessibility labels update when status or pending requests change.

Verification is implemented in `WorkspaceDisclosureVerification.MigrationSelectionAndPersistence` and the Windows `workspaceSidebar` smoke. The latter exercises both workspace disclosures, cross-workspace session selection, the actual Files menu action, explicit empty-array persistence, quick theme switching, and retained editor/draft identity. Source checks and Core tests do not substitute for the Windows runtime smoke or physical keyboard/IME testing.
