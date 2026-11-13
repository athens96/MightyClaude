# mighty-graph: checks left for the integration stage

Every C# check of the graph's suites that this crate does not run yet, because it needs a part of the app that is not here. Each entry names
the C# check (native/windows/MightyCode.Core.Tests/, file:line), what it needs, and the Rust test that holds the rest of it. Port each one
when that part lands, next to the test named, and delete its entry here.

## The run engine (W2-A, `crates/mighty-run`)

### OutputParser: recordings replayed as `RunManager` feeds them (`FlowGraphVerification.ReplayParsed`, FlowGraphVerification.cs:33-45)

- FlowLayoutVerification.cs:421-464 `TheRecordedBackgroundAgentIsACardThatLeadsToItsCopy` (whole): claude-stream-subagents through the
  tracker and the parser, live cards from each snapshot, the copy node. Goes in tests/flow_layout.rs.
- FlowLayoutVerification.cs:477-492 `BackgroundCommandsStandInTheRow`, its first block (claude-stream-background-commands replayed: the
  flow agents, their command lines, exit codes and times, the line, the way back, both cards, the copy). The rest is
  tests/flow_layout.rs `background_commands_stand_in_the_row`.
- FlowGraphVerification.cs:202-215 `TheRecordedRequestLogsItsSubagentsWork`, its first block (the log's rows of claude-stream-subagents:
  starts, ends, the background agent's reads, its notice, the status). The rest is tests/flow_graph.rs
  `the_recorded_request_logs_its_subagents_work`.
- AcpGraphVerification.cs:67-79 `EveryRecordingIsTheMacsGraph` (whole): every opencode/hermes recording through `AcpChannel`, the parser and
  the tracker against native/contracts/fixtures/acp-graphs.json. `OpencodeAndHermesPanesDrawTheGraph` is ported
  (tests/tracker_views.rs).

### The pane's record of run events (`RunSession.Apply` / `AppSnapshot.Apply(RunEvent)`)

- MightyGraphLiveVerification.cs:9-36 `TheRunningRequestShowsTheMainAgentsWork`: the log and graph_run events applied to the pane. The merge
  rules it relies on are tests/live.rs.
- FlowStepsVerification.cs:101-108 `ThePaneKeepsTheStepsThroughItsEvents`: the `todos` and `graph_run` events through `AppSnapshot.Apply`
  (a graph run keeps the steps). The `RecordSteps` half and the save are tests/flow_steps.rs `the_pane_keeps_the_steps_through_its_events`.
- BackgroundNoticeRunsVerification.cs:43-49, 192, 198-202, 212 `ThePaneKeepsTheNoticeRunApart`: the pane fed by `Hand`/`Line`, and "the
  notice turn's lines are its own" (the transcript routing). The tracker's half is tests/background_notice_runs.rs
  `the_pane_keeps_the_notice_run_apart`.
- BackgroundNoticeRunsVerification.cs:355-360, 364-371 `AbsorbedAndEndedNoticeRunsLeaveTheRequestItsPlace`: "the request's later lines are
  its own" and "a running notice turn takes the lines" (`Line` → `Apply`). The rest is tests/background_notice_runs.rs
  `absorbed_and_ended_notice_runs_leave_the_request_its_place`.

### TodoProgressTracker

- FlowStepsVerification.cs:221-224 `AnACPAgentsPlanIsTheRequestsSteps`: the ACP plan update read into a checklist. The `RecordSteps` half
  is tests/flow_steps.rs `an_acp_agents_plan_is_the_requests_steps`.

### BackgroundTaskTracker, ClaudeStream.ClosesInput, BackgroundQueuePolicy, QueueRunNow, RunManager

- ResumedAgentVerification.cs:16-46 (`Replay`), 70-73, 76-77, 80-86, 103-104, 107-108
  `AnAgentResumedFromANoticeTurnKeepsThePaneWaitingUntilItsNotification`: the background work, the input's close, the queue, 바로 실행. The
  graph's half and the log strip's status line are tests/resumed_agent.rs.
- ResumedAgentVerification.cs:116-131 `AnAgentResumedFromASteeredTurnWaitsOnceThatTurnEnds` (whole).
- ResumedAgentVerification.cs:136-164 `TheRunnerKeepsTheInputOpenForAResumedAgent` (whole: `RunManager` with the fake CLI).
- GraphVectorVerification.cs:503-551 `RecordedFromARun` (whole: Claude, Codex, Gemini and shell runs through `RunManager` record their
  graph, or none).

### SessionHistory (the session record reader)

- AgentNoticeVerification.cs:280-296 (`Replay` writes a Claude record and reads it with `SessionHistory.Load`), used by
  :308 `ARecordedHandBackTurnSettlesTheAgentAsLive` and :338 `AQueuedHandBackSettlesTheAgentByItsName` (both whole). The shared cases are
  tests/agent_notices.rs.

## The phone (W8)

- `MobileMightySupport.Digest`: BackgroundNoticeRunsVerification.cs:163-179 (`TheRunRulesAndTheViews`), :304, 308, 310, 314, 329-333,
  343-344 (`ARequestAwaitingNoticesIsTheAwaitingNoticeBlock`), :446-448 (`ANoticeRunReadsTheRequestsWaitingCount`).
- `MobileMightySupport.Runs`: BackgroundNoticeRunsVerification.cs:214-221 (`ThePaneKeepsTheNoticeRunApart`), :293-297, 308, 314, 335-339
  (`ARequestAwaitingNoticesIsTheAwaitingNoticeBlock`), :444 (`ANoticeRunReadsTheRequestsWaitingCount`).
- `MobileMightySupport.Blocks`: FlowLayoutVerification.cs:259 (`AShownRunCarriesWhatOnlyTheFlowDraws`; its timeline and pipeline checks
  are ported in tests/flow_layout.rs).
- `PlanCardSupport.BackgroundStripRows`, `MobileBackgroundWork`, `MobileBackgroundDone`, `BackgroundStatus`, `BackgroundCounts`,
  `ShowsBackgroundStrip`: BackgroundNoticeRunsVerification.cs:222-234, ResumedAgentVerification.cs:82, 88-91, 104.
- `MobileDesktopRouter`: MobileStructureVerification.cs:158-203 `RouteSendsTheStructureOnlyWhenAskedAndChanged` (whole). The phone's
  pipeline itself (`MobileStructure.Make`) is ported: tests/mobile_structure.rs, and its awaiting and title checks in
  tests/background_notice_runs.rs.

## The host (wiring `view::project`)

What `MainWindow.WebShell.Mighty.cs` does around the projection that is not pure, for the stage that wires `mightyVisible` and the page
methods to `view::MightyPushes`, `view::MightyPane` and `view::project`:

- The 120 ms timer (`StartWebMighty`, `FlushWebMighty` :125-179): call `MightyPushes::due` each tick with the snapshot's sessions, project
  each pane it returns and push `MightyPushes::message`; stop the timer when `shown()` is empty. Every page method that changes what a
  projection reads calls `MightyPushes::dirty`.
- `Sources`: the record reader's older runs and history words (`GraphHistoryRuns`, `SessionHistoryState`, MainWindow.SessionHistory.cs),
  `mightyLoadOlder`'s read (`MightyPane::load_older` gives the request; `SessionHistoryState.Begin`/`Finish` and the read are the record
  reader's), the Ouroboros dashboard (`view::ExecutionDashboard`: `OuroborosDashboard.BlockStatus` and `WebExecutionCard`, polled only
  while the diagram shows, `WatchOuroboros`/`StopOuroboros`), the custom agents read off the page's thread (`FlowDefinitions`
  :466-488), the image locator, the background output, the model catalogue and the guided style.
- The result files: `MightyPane::take_result_file_lookups` after each projection, `result_files::files_in` off the page's thread, then
  `MightyPane::found_result_files` and `dirty` (`LookThroughResultFiles`, MainWindow.MightyGraph.cs:1503-1511).
- `mightyImages`: `MightyPane::images` gives the step's pictures; their bytes and data URLs (`WebImageUrl`, :666-683) are the host's.
- `mightyOpenDashboard`: `MightyPane::open_dashboard` gives the execution; opening it (`OpenOuroborosDashboard`) is the host's.
- The page smoke (`RunWebMightySmoke`, :190-351) runs against the real page.
