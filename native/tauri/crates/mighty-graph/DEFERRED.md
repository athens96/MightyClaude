# mighty-graph: checks left for the integration stage

Every C# check of the graph's suites that this crate does not run yet, because it needs a part of the app that is not here. Each entry names
the C# check (native/windows/MightyCode.Core.Tests/, file:line), what it needs, and the Rust test that holds the rest of it. Port each one
when that part lands, next to the test named, and delete its entry here.

## The run engine (W2-A, `crates/mighty-run`) — ported at the integration (2026-11-14)

mighty-graph reads mighty-run now (`src/run.rs` is the tracker as the runner's `GraphFactory`/`GraphSink`), so the checks that needed both
crates run here: `tests/view_support` has `replay`/`replay_parsed` (C# `FlowGraphVerification.ReplayParsed`: each frame to the tracker and
to mighty-run's `OutputParser`, its tool rows routed by `ExecutionGraphTracker.activity`).

| C# check | Rust |
|---|---|
| FlowLayoutVerification.cs:421-464 `TheRecordedBackgroundAgentIsACardThatLeadsToItsCopy` (whole) | tests/flow_layout.rs `the_recorded_background_agent_is_a_card_that_leads_to_its_copy` |
| FlowLayoutVerification.cs:477-492 `BackgroundCommandsStandInTheRow`, its first block | tests/flow_layout.rs `background_commands_stand_in_the_row` (now whole) |
| FlowGraphVerification.cs:202-215 `TheRecordedRequestLogsItsSubagentsWork`, its first block | tests/flow_graph.rs `the_recorded_request_logs_its_subagents_work` (now whole) |
| MightyGraphLiveVerification.cs:9-36 `TheRunningRequestShowsTheMainAgentsWork`, the pane's `Apply` | tests/live.rs `the_running_request_shows_the_main_agents_work_on_the_pane` |
| BackgroundNoticeRunsVerification.cs:186-212 `ThePaneKeepsTheNoticeRunApart`, the pane fed by `Hand`/`Line` | tests/background_notice_runs.rs `the_pane_keeps_the_notice_run_apart_through_its_events` |
| BackgroundNoticeRunsVerification.cs:349-372 `AbsorbedAndEndedNoticeRunsLeaveTheRequestItsPlace`, the pane's lines | tests/background_notice_runs.rs `absorbed_and_ended_notice_runs_leave_the_request_its_place_through_its_events` |
| RuntimeParityVerification.cs:103-126 `ChildActivitiesStayInTheirGraph` (whole; not listed here before) | tests/runtime_parity.rs `child_activities_stay_in_their_graph` |
| FlowStepsVerification.cs:98-108 `ThePaneKeepsTheStepsThroughItsEvents`, FlowStepsVerification.cs:219-224 `AnACPAgentsPlanIsTheRequestsSteps` | mighty-run tests/flow_steps.rs (the W2-A port already held both whole) |
| ResumedAgentVerification.cs:116-131 and :136-164 | mighty-run tests/stream_parser.rs `an_agent_resumed_from_a_steered_turn_waits_once_that_turn_ends`, tests/runner.rs `the_runner_keeps_the_input_open_for_a_resumed_agent` |
| AcpGraphVerification.cs:67-79 `EveryRecordingIsTheMacsGraph` (whole; ported in W5-B) | tests/acp_graphs.rs `every_recording_is_the_macs_graph` |
| AcpRunVerification.cs:71-132, the graph parts (`LastGraph`, the pane's block of `AnOpencodeRunsBlockKeepsItsRepliesAndToolRows`; W5-B) | tests/acp_graphs.rs `runs::*`, over real runs: this crate builds mighty-run's stand-in CLI as its own `[[bin]]` (`mighty-graph-fake-cli`) |

Still left (each needs more than the two crates' public rules, or a fixture the port has not made):

- ResumedAgentVerification.cs:82, 84-86, 88-91 (inside `AnAgentResumedFromANoticeTurnKeepsThePaneWaitingUntilItsNotification`): the queue
  policy's answers on the replayed pane (`BackgroundQueuePolicy.WaitsOnBackground`, `SteersRunNext`, `StopKeepsQueue`, `QueueRunNow.Decide`
  giving Steer) and the strip's rows. The rules are mighty-composer's (`queue::background`, `queue::decide`) and the replay mighty-run's
  (tests/stream_parser.rs:540); the strip's rows are `PlanCardSupport.BackgroundStripRows` (the phone's module, below).
- GraphVectorVerification.cs:503-551 `RecordedFromARun` (whole): Claude, Codex, Gemini and shell runs through `RunManager` over a real
  child (C#'s fake in its `--fake-cli <provider>` mode) with the real tracker. mighty-run's `mighty-fake-cli` plays that mode, but it is a
  `[[bin]]` only mighty-run's own tests can name, and those cannot reach the tracker without a dependency cycle. Since W5-B this crate
  builds the same source as its own `[[bin]]` (`mighty-graph-fake-cli`, `env!("CARGO_BIN_EXE_mighty-graph-fake-cli")`, as tests/acp_graphs.rs
  uses it), so the check can be ported here; it has not been yet.
- RuntimeParityVerification.cs:128-200 `ChildImagesReachTheirGraphAndGallery` and :202-260 `RunnerPreservesChildOwnershipAndImagesAcrossRestore`:
  the parser's picture path into the tracker (`graphImages`) with the picture cache, and C#'s fake in its `--runtime-parity-cli` mode
  (not in `mighty-fake-cli`), with the same binary question as `RecordedFromARun`.

### SessionHistory (the session record reader, W4) — ported in W4-C (2026-11-14)

| C# check | Rust |
|---|---|
| AgentNoticeVerification.cs:308 `ARecordedHandBackTurnSettlesTheAgentAsLive` (whole, with :280-296 `Replay`) | mighty-history tests/agent_notices.rs `agent_notices_a_recorded_hand_back_turn_settles_the_agent_as_live` |
| AgentNoticeVerification.cs:338 `AQueuedHandBackSettlesTheAgentByItsName` (whole) | mighty-history tests/agent_notices.rs `agent_notices_a_queued_hand_back_settles_the_agent_by_its_name` |

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

## The host (`src-tauri`, wired at the integration)

`src-tauri/src/mighty.rs` does what `MainWindow.WebShell.Mighty.cs` does around the projection: the 120 ms tick (`MightyPushes::due`,
every eighth whole), the page methods marking the pane (`dirty`), the result files looked through off the page's thread
(`take_result_file_lookups` → `result_files::files_in` → `found_result_files`), `mightyImages`' tiles as data URLs. The run steps of
`--smoke-test` (smoke_runs.rs) draw a real run's request, sub-agents and result and the progress panel against the page. The session
record's older requests and the history block's words (`GraphHistoryRuns`, `SessionHistoryState`) come from `src-tauri/src/history.rs`
over mighty-history since W4-C (`mightyLoadOlder` reads the next chunk). What the projection still gets empty (`Sources`), each its stage's:

- the Ouroboros dashboard (`ExecutionDashboard`: `NoDashboard`, `mightyOpenDashboard` opens nothing) and `OuroborosExecutionLinks.Extract`;
- the background output tails (`BackgroundOutputFor`), the custom agents' definitions (`FlowDefinitions`) and the guided style (W9).
