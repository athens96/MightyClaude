# mighty-history: what is left for a later stage

Every C# check of session history and resume this crate does not run whole yet, with what it needs and where the rest of it is.

## Elsewhere

- The resume sheets' flow (`AddAgentPane` / `WebAddAgentPane`: the look-up, `resumeChoice`, `resumeList`, `dialogAnswer`, the pane added
  with `ResumableSessions.Apply`) is the page-drawn dialogs' stage; the app's `history.rs` gives it `look_up`, `asks_resume`,
  `open_resume_list`, `close_resume_list` and `pick`.
- A delegated child's records are its worktree's (`DelegationPanes.RunWorkspace`): W7's. Until then a child pane reads its workspace's
  folder.
