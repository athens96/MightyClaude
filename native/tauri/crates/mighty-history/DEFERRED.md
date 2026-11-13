# mighty-history: what is left for a later stage

Every C# check of session history and resume this crate does not run whole yet, with what it needs and where the rest of it is.

## Antigravity's replay (W5: Antigravity's live parser and sub-agent watcher)

C# replays an Antigravity request through `OutputParser("gemini")` (its tool rows, routed by `tracker.Activity`) and reads the request's
sub-agents' own transcripts with `AntigravitySubagentWatcher` up to the next request (`HistoryReplay.Antigravity`, SessionHistory.cs:919).
mighty-run has no Antigravity parser yet (its parser.rs says Antigravity's stream is W5's), so `replay::antigravity_replay` feeds the
tracker the transcript's steps as the stream events agy printed (`antigravity::stream_events`, ported whole) and hands each answer step's
text to `tracker.antigravity_answer`, as the parser would: the requests, their answers, statuses, usage and the sub-agents the stream
names are read back; the tool rows and the sub-agents' own work are not.

- AntigravityVerification.cs:449 `OlderRequestsAreReadFromTheConversationTranscript`, the parts that need them: :465 the sub-agents'
  titles in order (the Checker is the File Reader's helper, named only in the File Reader's transcript), :467-469 the reader's tool rows,
  "file contents are never copied", the checker under the reader, :475 the older chunk's two sub-agents. The rest is
  tests/resumable_sessions.rs `antigravity_older_requests_are_read_from_the_conversation_transcript`.
- AntigravityVerification.cs:493 `TheConversationListAndSkills`: its skills part (agy's own, the user's and the workspace's
  `.agents/skills`) is the slash commands' (W5). Its conversation list is tests/resumable_sessions.rs `antigravity_the_conversation_list`.

## Elsewhere

- The resume sheets' flow (`AddAgentPane` / `WebAddAgentPane`: the look-up, `resumeChoice`, `resumeList`, `dialogAnswer`, the pane added
  with `ResumableSessions.Apply`) is the page-drawn dialogs' stage; the app's `history.rs` gives it `look_up`, `asks_resume`,
  `open_resume_list`, `close_resume_list` and `pick`.
- A delegated child's records are its worktree's (`DelegationPanes.RunWorkspace`): W7's. Until then a child pane reads its workspace's
  folder.
