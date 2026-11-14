# Mighty Code — Rust + Tauri 2 host (skeleton)

The start of the Rust rewrite of the desktop app, Windows first. It opens one window, draws the same React page the C# app draws
(`native/windows/web`), loads the saved state and answers the page's first calls. It saves the state byte for byte as the C# app
does, but **only when started with `--allow-state-write`**; without it every change stays in memory. The C# app
(`native/windows/MightyCode.*`) is untouched and still the shipping app.

## Layout

| Path | What |
|---|---|
| `Cargo.toml` | The workspace: `crates/mighty-core`, `crates/mighty-composer`, `crates/mighty-files`, `crates/mighty-graph`, `crates/mighty-history`, `crates/mighty-platform`, `crates/mighty-platform-windows`, `crates/mighty-platform-macos`, `crates/mighty-run`, `crates/mighty-terminal`, `crates/mighty-workmap`, `src-tauri`. |
| `crates/mighty-core` | No Tauri, no OS code. JSON exactly as C# `Wire.Json` reads and writes it (`json.rs`), .NET's times (`time.rs`), the state models (`model.rs`, `graph.rs`: C# `Models.cs`, `MightyGraph.cs`, `ExecutionGraph.cs` and the records they hold), `StateStore.Normalize` with every rule it calls (`normalize.rs`), loading, saving and the save timing (`store.rs`, C# `StateStore.cs`, `SaveSchedule`, `DesktopService`'s save queue), the locale catalogues and `LocaleKeyLeak` (`locale.rs`), the pane layout rules (`layout.rs`, C# `PaneLayout.cs`), model labels (`model_label.rs`), the frame model (`shell.rs`, C# `MainWindow.WebShell.Shell.cs`), the demo state (`demo.rs`), the add-pane menu and a pane's place (`add_pane.rs`, C# `AddPaneMenu`, `SessionTemplate`, `AddToLayout`), renames and pane titles (`rename.rs`, `RenameSupport`, `PaneTitle`), the workspace create rules (`workspace_create.rs`, `WorkspaceCreate`), the browser pane's rules (`browser.rs`, `BrowserHistory`, `BrowserProfile`, `BrowserEngineService`), the text setting (`text_appearance.rs`). |
| `crates/mighty-composer` | No Tauri, no OS-specific code: the composer's C# Core classes and what the WinUI host builds from them for the page (W2-B). `slash` (`SlashCommands.cs`, `SlashCommandStrings.cs`; the scan reads files through `SlashFiles`), `next_actions`, `ask_targets` (`AskTargets`), `custom_model` (the custom model dialog's data, `PaneView.CustomModel`; W6 draws it), `queue` (the second send, 바로 실행, `QueuedInputBuffer`, `RunBlock`, `BackgroundQueuePolicy`), `pending` (`PendingDisplay`, `SubmitResponse`, `SubmitReceipt`, `SubmitScroll`), `attachments` (`AttachmentSupport`; staging is the run engine's), `images` (`AgentImages.cs`: header sniffing with TIFF, the SHA-256 cache, the path rule, Markdown pictures, the RTF picture), `svg` (`SafeSvgDocument`, the SVG rules of `FilePreviewClassifier`), `html` (`LocalHtmlDocument`), `text` and `files` (the decoding and path rules they read). Then the page's models as pure functions: `composer::composer_state` (`WebComposerState`), `menus::composer_menu`/`menu_action`/`run_settings_info`/`run_settings_from`/`slash_catalog`, `cards::cards` (`WebCards`) with `presentation` (`ToolPermissionPresentation`, `UserQuestionnaire`, `PlanCardSupport`). What later stages give them is typed (`composer::WindowRules`, `cards::CardInputs`) and listed (`composer::PENDING`, `cards::PENDING`). Its tests port the C# checks, read the shared fixtures, check the page's field names against `src/composer/*.ts` and `src/cards/types.ts`, and compare 35,597 vectors with C# itself (`tools/parity` `composer`). |
| `crates/mighty-files` | No Tauri, no OS-specific code (the OS through mighty-platform's files ports): the files pane, its previews and the 변경사항 view (W4-B). `listing` (C# `WorkspaceFiles.List`, `Sorted`, `NaturalComparer`), `classify` (`FilePreviewClassifier`, `TextEncodings`; the decoding and SVG rules are mighty-composer's), `source` (`SourceLines`, `SourceHighlighter`, `SourceFind`), `tree` (`FilePaneTree`, `FilePaneDrawing`), `preview` (`FilePreviewLoader`), `diff` (`FileDiff`, Myers), `patch` (`FilePatch`, `FileEdit` and the parser's `EditedOriginal` reading of a `tool_use_result`), `changes` (`FileOriginalStore`, `GitHeadFile` through `ProcessHost`, `FileChanges`), `reference` (`ReferencePreview`), `pane` (`FilePaneKind`), `frames` (the HTML frames' folders and what they serve), `raster` (an SVG drawn to PNG with resvg, as C#'s `NativeSvgRaster`; a bitmap measured and fitted with the image crate, as C#'s WIC decode) and `model` (the page's `FilesModel` and `FilePreview`: `WebFilesModel`, `WebFilePreview`, `WebFileImage`). Its tests port the C# checks under their names and read `file-diff.json` and `source-highlight.json`. |
| `crates/mighty-core/examples` | `seed_demo` (a test profile from the help capture's demo), `roundtrip` (any state file loaded and saved in memory, timed; read-only), `parity` (the Rust side of `tools/parity`). |
| `tools/parity` | The C# side of the parity checks (`Parity.csproj`, a .NET 10 console over `MightyCode.Core` with `WebDelta.cs` compiled in; `Composer.cs` answers mighty-composer's vectors), `run.ps1` (the corpus and the vectors compared byte for byte), `real-write.ps1` (the app's save of a real profile's copy against C#'s), and `run-replay` (C#'s `DesktopService` running a recorded Claude run through mighty-run's fake CLI, the expectation of `crates/mighty-run/tests/service.rs`). |
| `crates/mighty-platform` | No OS code. The ports (traits) between the shared logic and the OS, each with its contract, a fake of each (`fake.rs`) and `Unsupported` (`unsupported.rs`), and `PlatformProfile`, the deliberate Windows/Mac differences as named values (docs/tauri-architecture.md §2.1). |
| `crates/mighty-platform-windows` | The Windows ports, everything behind `cfg(windows)`: `Paths`, `RegistryPrefs` (HKCU `Software\MightyCode`, reads), `DpapiVault` (C# `DpapiRemoteKeyVault`'s files, read and written both ways), `WebviewSurfaces` (browser panes as WebView2 child webviews), and the process layer (W1-C): `WindowsProcessHost` (C# `ChildProcess`: suspended start, kill-on-close job, hidden UTF-8 console), `command` (C# `WindowsCommand`: PATH × PATHEXT, `.cmd` through `cmd.exe /d /s /c`), `CliRunner`, `discovery`, `WindowsShellEnvironment` (C# `CliEnvironment` + the PowerShell profile capture), `user_environment`, `WindowsPtyHost` (C# `PseudoTerminal`, ConPTY; its `busy` reads `shell_activity`) and `shell_activity`, and `WindowsClipboard` (the `Clipboard` port, W4-A), `setup_probe` (C# `SetupProber.Executables`, `SetupScript.WellKnownFolders`: where this PC has a command, for the "send to" switch). Its tests run real processes against a stand-in child (`tests/fake/child.rs`, a `[[bin]]`) and compare with C# itself (`tests/parity`, a tiny .NET program over MightyCode.Core). The rest is `Unsupported` for now. |
| `crates/mighty-run` | No Tauri, no OS code: the run engine (W2-A). `manager.rs` (C# `RunManager`: a pane's CLI started through `ProcessHost`, its frames read, its stdin kept for steers and answers), `providers.rs` (C# `ProviderCatalog`: every provider's arguments, Claude's and Codex's model reads, the runtime and discovery), `permissions.rs` and `presentation.rs` (C# `ToolPermissions`: `ProviderInput`, `ClaudePermissionChannel`, the card's copy), `codex_approval.rs` (C# `CodexApprovalChannel`: a Codex `onRequest` run's app-server turn, its approvals as the pane's tool-permission requests), `parser.rs`, `parser/pi.rs` and `stream.rs` (C# `OutputParser` for Claude, Codex, pi, Antigravity and the ACP agents, `ClaudeStream`), `pi_dialogs.rs` (C# `PiDialogChannel`: pi's extension dialogs as the question card), `plan_mode.rs` (C# `ClaudePlanMode`, the checklist and background-task trackers), `acp.rs` with `acp/agents.rs`, `acp/elicitation.rs`, `acp/accounts.rs` and the parser's `parser/acp.rs` (W5-B: C# `AcpChannel`, `OpencodeAgent`, `HermesAgent`, `HermesQuestionTool`, `AcpElicitationForm`, `OutputParser.Acp`, `OpencodeAccounts`, `HermesAccounts` with `CliAccountStatus`: opencode and Hermes runs over the Agent Client Protocol, their cards, their sign-in states), `mods.rs` (C# `ModBridge` + `HttpHost` over hyper), `service.rs` (the run half of C# `DesktopService`, its events through a channel, with `UpdateDraft` and the window's `ClearToolPermissions`; `service/antigravity.rs` its Antigravity cards, DesktopService.Antigravity.cs), `graph.rs` (`GraphFactory`/`GraphSink`, where mighty-graph's tracker plugs in, with `ReadChildren`'s sub-agent files: a Codex run's session files, an Antigravity run's sub-agents' transcripts), `antigravity/` (W5-C, AntigravityPermissions.cs: `permissions` — the denied actions, the card, the rules' safety, the denial collector, the board, what an answer does —, `settings` — `AntigravitySettingsText`, the byte-exact edits of agy's `permissions.allow` —, `leases` — `AntigravityRuleLeases` with its ledger, retries and look-again —, `records` — `AntigravityRecords`' helpers), `parser/antigravity.rs` (the Antigravity half of `OutputParser`), `kept_lists.rs` (C# `KeptModelLists`, `ModelListNotices`: the last list each CLI gave, kept in the catalog's storage folder, and the model menu's line), `model_selection.rs` (C# `ModelSelectionSupport`, `ModelCatalogRefresh`, `ClaudeModelReset`), `phase_models.rs`, `phase_routing.rs` and `phase_section.rs` (C# `PhaseModelPreferences`, `PhaseModelRouting`, `PhaseModelSection`: a run's phase models and Settings › 페이즈별 모델's rules and rows, the screen W6's), `model_settings.rs` (C# `ModelSettingsFileStore`, `OmcAgentCatalog`: omc's `config.jsonc` and Ouroboros's `config.yaml`, under a home folder always given), and the smaller C# classes each in its own file. Its tests port the C# checks under their names and run a stand-in CLI (`tests/fake/cli.rs`, a `[[bin]]` that replays recordings, plays Codex's app-server approval turn, a pi answering from `pi-rpc-*.jsonl`, C#'s fake agy and an ACP agent answering from a recorded session) through the Windows `ProcessHost`; `tests/service.rs` compares a replayed run's pane with C#'s; `tests/antigravity.rs` and `tests/antigravity_permissions.rs` are AntigravityVerification's and AntigravityPermissionVerification's run-engine checks (the fixture `antigravity-permissions.json`, the settings edits on temporary files). |
| `crates/mighty-graph` | No Tauri, no OS code: the execution graph (W3). `tracker/` (C# `ExecutionGraphTracker`: a run's stream as graph blocks and graph runs, notice runs, Codex and Antigravity sessions), `run.rs` (the tracker as mighty-run's `GraphFactory`/`GraphSink`, `TrackerFactory`, and the `SessionWatcher` a Codex run's sub-agents are read through, both behind the sink's `read_children`; mighty-graph reads mighty-run, never the other way round), `antigravity.rs` (C# `AntigravitySubagentWatcher` and `AntigravityChildRecord`: an Antigravity run's sub-agents read from their own transcripts), the rules every Mighty view reads (`support`, `notice`), the views' models (`layout`, `camera`, `flow/` role view, `timeline`, `structure/`, `view_model`) and the host's `mighty` push (`view.rs`: `MightyPane`, `Sources`, `project`, `MightySent`, `MightyPushes`). Its tests port the graph suites under their names and read the shared fixtures; the checks that need the run engine replay recordings through mighty-run's stream parser (`tests/view_support` `replay`), and the ACP runs of `tests/acp_graphs.rs` go through mighty-run's runner over its stand-in CLI, built here too as `mighty-graph-fake-cli`; what is still left is `DEFERRED.md`. |
| `crates/mighty-terminal` | No Tauri, no OS code: the terminals (W4-A). `pane` (C# MainWindow.Terminal.cs and the terminal half of MainWindow.WebShell.Panes.cs: a terminal pane's shell started by the page's `ready` through `PtyHost`, its output kept (1 Mi UTF-16 units) and flushed each 32 ms as base64 UTF-8, input, size, title, working directory, copy and paste through the `Clipboard` port with C#'s limits, the exit, the restart, the shell's activity, the footer; an `agent-terminal` pane shows its owner's commands), `agent` (C# `AgentTerminal`: an agent pane's commands, each in a terminal of its own, what the agent reads, readers with their own cursor, Ctrl+C then the stop), `prompt` (C# TerminalPrompt.cs: the detector, the answers, the monitor), `shell_blocks` (C# ShellBlockRuns.cs, ShellBlockScan.cs: the key, the record, the scan of a reply as the page's markdown reads it, the output), `activity` (`ShellActivity.NextStatus`, `SetShellStatus`'s change), `history` (`ShowTerminalHistory`'s content), `cleaner` (`TerminalTextCleaner`). Its tests port the C# checks under their names, drive panes and agent terminals over the fake terminal (a loopback), and run the AgentTerminal half of "Agent IO real ConPTY process ownership" over ConPTY. |
| `crates/mighty-history` | No Tauri, no OS code: session history and resume (W4-C). `history` (C# `SessionHistory`: where each CLI keeps a session's record, the next older chunk read back from its end), `backward` (`JsonlBackwardReader`), `scan` (`HistoryScan`: which line opens a request the user typed, the walk that places the retained request), `replay` (`HistoryReplay`: Claude's and Codex's records, and Antigravity's transcripts through `antigravity::stream_events` and the live parser, the sub-agents' own transcripts read up to the next request, fed to mighty-graph's tracker), `codex_sessions` (`CodexSessionWatcher`, `CodexSessionThread`: a running Codex run's sub-agents read from its session files for the live graph, through mighty-graph's `TrackerFactory`), `state` (`SessionHistoryState`), `resumable` (`ResumableSessions` for Claude, Codex, Antigravity, pi, opencode and Hermes, `KnownSessionIDs`), `antigravity`, `pi`, `acp` (opencode's `opencode.db`, Hermes's `state.db`), `sqlite` (the databases read-only through a built-in SQLite: `rusqlite` bundled). Its tests port SessionHistoryVerification, CodexSessionWatcherVerification, the resume parts of the Antigravity, pi, ACP-session, add-pane and model-label checks, and the two AgentNotice checks mighty-graph left; what is left is `DEFERRED.md`. |
| `crates/mighty-workmap` | No Tauri: the work map (W4-C, docs/work-map.md). `lib` (C# `WorkMap`: subjects to platform lanes, release tags, VERSION, release state, dot sizes, the git log by version, the user's requests attached to the commits that followed them; the model JSON keeps its nulls), `git` (`DelegationGit.Run` through the platform's `ProcessHost`), `summary` (`WorkMapSummary`: the prompt, the answer's reading, one headless Claude run; `WorkMapSummaryStore`), `service` (`WorkMapService`: one summary at a time per workspace), `originals` (what it reads of the file originals), `dashboard` (the rest of `WorkDashboard`). Its tests read `work-map.json`, run real git and a scripted Claude (`mighty-workmap-fake`, C#'s `--workmap-fake`) through the Windows `ProcessHost`. |
| `crates/mighty-platform-macos` | The Mac ports in Rust, everything behind `cfg(target_os = "macos")`: `Paths`, `cli_copies` (a command along the PATH); the surfaces say terminal and browser are native (Ghostty, CEF/WKWebView) but open nothing yet. |
| `src-tauri` | The app. `main.rs` (window, startup), `host.rs` (the bridge, the state's changes and saves, the run service's events), `delta.rs` (C# `WebBridge.Flush` and `WebDelta`), `methods.rs` (the page methods), `runs.rs` (the run engine: one `RunService` with mighty-graph's tracker and the picture cache, the CLIs' runtime), `models.rs` (the model lists kept right: C# MainWindow.ModelRefresh.cs), `ask.rs` (the composer's "send to" switch: C# MainWindow.AskTargets.cs), `panes.rs` (the never-shown pane views: composer and cards models and pushes), `composer.rs`, `cards.rs`, `mighty.rs` (their page methods; `mighty.rs` also the `mighty` push), `terminal.rs` (terminal panes, agent terminals, terminal prompts, shell block runs), `files.rs` (the files pane, the reference bubble, HTML frames and the picture viewer's actions, the file originals), `history.rs` (session history and the resume list's data), `antigravity.rs` (the Antigravity card's words and answer, agy's settings and the ledger for the run service), `workmap.rs` (the work map's methods, pushes and pane), `frame.rs` (the frame's remaining methods: close, focus, rename, remove, the add menu, panes, the folder picker, Explorer, the text size, the resume flow), `dialogs.rs` (the page-drawn dialogs), `workspace_create.rs` (the create sheets, git through `ProcessHost`), `platform.rs` (the one place the OS crate is chosen), `surface.rs` (`SurfaceManager`, the NativeSlot protocol and the browser pane), `smoke.rs`, `smoke_frame.rs`, `smoke_terminal.rs`, `smoke_files.rs`, `smoke_history.rs`, `smoke_acp.rs` and `smoke_runs.rs` (`--smoke-test`), `args.rs`, `logfile.rs`. |
| `scripts/env.ps1` | Loads the MSVC environment and `~/.cargo/bin` into the PowerShell session (dot-source it before any cargo or tauri command). |
| `scripts/smoke.ps1` | Seeds a demo profile and runs the smoke; `-Profile <folder>` runs it read-only against an existing profile. |
| `scripts/screenshot.mjs` | A picture of the running page over WebView2's DevTools protocol. |

## Build and run

```powershell
. .\native\tauri\scripts\env.ps1          # once per PowerShell session
cd native\tauri
npm install                               # the Tauri CLI (@tauri-apps/cli)
npm run tauri dev                         # Vite dev server (native/windows/web, --mode tauri) + the app
npm run tauri build -- --debug            # target\debug\MightyCode.exe with the page built into it (no installer: bundle.active is false)
npm run tauri build                       # the same, optimized: target\release\MightyCode.exe
cargo test -p mighty-core
cargo clippy --workspace -- -D warnings
```

`tauri build` first runs `npm run build -- --mode tauri` in `native/windows/web`, which writes `dist-tauri/` (never the `dist/`
the C# build ships). A plain `cargo build` makes a *dev* binary (Tauri's `custom-protocol` feature off) that loads the Vite dev
server at `http://localhost:5173` instead of the built page; build with the Tauri CLI to get one that runs on its own.

The VS 2022 Build Tools on the development PC are not registered with vswhere, so rustc cannot find `link.exe` unless
`scripts/env.ps1` has imported `vcvars64.bat`'s environment.

### Command line

| Switch | |
|---|---|
| `--profile <absolute folder>` | The state folder instead of `%APPDATA%\MightyCode` (C# `Program.cs`). An explicit profile never reads the pre-rename folders. |
| `--smoke-test`, `--smoke-exit` | The smoke (below); `--smoke-exit` quits with 0 (passed) or 1. Needs `--profile`. |
| `--smoke-out <file>` | The smoke's result (default `<profile>\smoke-result.json`). |
| `--log-dir <folder>` | The host's logs (default `<profile>\tauri-logs`). |
| `--remote-debugging-port <n>` | Opens WebView2's DevTools protocol (`scripts/screenshot.mjs`). |
| `--allow-state-write` | Lets the host write the profile's state as C# does: every change saved, the damaged-file recovery, the legacy copy. Off by default. |
| `--smoke-fake-cli <exe>` | The smoke's run steps against mighty-run's stand-in CLI (`mighty-fake-cli --replay-cli claude-stream-subagents` for Claude, `--fake-cli pi … --pi-fixture pi-rpc-question.jsonl --pi-dialogs` for pi, `--fake-cli gemini … --agy-denied` for agy), and its ACP step's opencode (`--fake-acp` replaying `opencode-acp-question`); every other provider reads as missing. Needs `--smoke-test`. |
| `--smoke-real-claude` | The smoke sends one real request with the installed `claude` CLI instead (below). Needs `--smoke-test`. |

## The bridge

The page talks to its host only through `HostPort` (`native/windows/web/src/bridge/bridge.ts`). In Tauri, `hostPort()` returns
`tauriPort()` (`src/bridge/tauriPort.ts`), checked before WebView2's `chrome.webview` because Tauri's own IPC runs over that:

- page → host: `invoke('host_post', { message })`, one at a time and in order, all held until the `host-message` listener is
  registered, so no answer can arrive before the page listens. A post that cannot be delivered fails its call instead of leaving
  it waiting.
- host → page: every message — a call's result and every push — is a `host-message` event to the `main` window, emitted by one
  thread in the order the host posted them (C# `WebBridge` keeps the same single ordered channel).

`host.rs` keeps C#'s contract: a call `{kind:"call", id, method, args?}` is answered `{kind:"result", id, ok, value?, error?}`
with null `value`/`error` left out; calls are handled one at a time; pushes are dropped until the page has called `ready`, which
pushes the whole `snapshot` before its own result; a page that loads again starts over. A change reaches the page as C#'s
`WebBridge` sends it (`delta.rs`): a `delta` at most 16 ms after the call (coalesced), a 100 ms poll for changes made elsewhere,
each pane as `{id, session}` when new or `{id, set, unset, logs?, graphRuns?}` (only the fields whose JSON changed, the two long
lists by id), `removed`, `order` when the ids or their order changed, `state` when its JSON text changed. `shell` (the frame model)
is pushed when its JSON changed, after every flush and on a 250 ms clock (its "3 min ago" texts move on).

A method the host does not have answers `ok:false, error:"unimplemented: <method>"`, is logged once to `tauri-logs/host.log`, and
is added to `tauri-logs/unimplemented.json` when the app quits (every run's names together, with the implemented ones): the
porting checklist.

### Implemented methods

Each is the C# handler's rule (named beside it). A change is C#'s `UpdateAsync` (normalized, then queued for saving); an action is C#'s
`Act` (the banner cleared first, what it fails with shown there, the call answering null). The layout actions (`SelectLayoutSession`,
`ActivatePane`, `SelectWorkspace`, `ApplyLayoutPreset`) live once, in `frame.rs`, and the layout saves in mighty-core's `add_pane`; every
module uses those. The host answers 123 methods (`tauri-logs/unimplemented.json`'s `implemented` lists them).

- **The frame** (`methods.rs`; C# MainWindow.WebShell.cs, MainWindow.Layout.cs, MainWindow.Navigation.cs, MainWindow.Sidebar.cs):
  `getLocale` (the saved `languagePreference`; "system" reads the OS's preferred UI languages in order, C# `Locale.ChosenLanguage`),
  `ready`, `shellState`, `log` (to `tauri-logs/host.log`), `selectSession`, `activatePane`, `selectWorkspace`, `setTheme` (the open
  terminals take their colours again, C# `Retheme`), `setSidebarCollapsed`, `setSidebarWidth`, `toggleDisclosure`, `applyLayoutPreset`,
  `resizeSplit`, `resetSplit`, `dockSession`, `showDashboard`, `hideDashboard`, `dismissError`, `renameCheck` (`RenameSupport`), `openUrl`
  (http, https, mailto through `ShellExecuteW`, as C#'s `UseShellExecute`).
- **The frame's actions** (`frame.rs`; C# MainWindow.WebShell.Shell.cs, MainWindow.Navigation.cs, MainWindow.ResumeSession.cs,
  MainWindow.SmallParity.cs, MainWindow.TextAppearance.cs): `closeSession` (the run stopped and ended, then the pane gone), `focusPane`
  (`toggle` back to custom), `textSize` (Ctrl+= / Ctrl+- / Ctrl+0 over the saved setting, pushed as `text`, and every open terminal takes
  its size; not saved: W6), `dismissResource`, `renameSession` and `renameWorkspace` (the page's rename sheet; Automatic for an agent pane),
  `removeWorkspace` (the page's danger confirmation, every pane's run stopped), `showInExplorer` (the folder's real path, ShellExecute's `explore` verb),
  `pickFolder` (the OS's folder picker owned by the main window; the folder approved for saving and added, a Claude pane in a workspace
  without one), `addMenu` (the entries in the Mac's words, the remote menu, Connect), `addPaneEntry` (terminal, browser, files — files.rs's
  pane through `frame::OPEN_FILES` —, an agent with the resume flow below, the three ways to add a workspace, another workspace's entry
  made active first), `newPane`.
- **The page-drawn dialogs** (`dialogs.rs`; C# `OpenWebDialog`, `CloseWebDialog`): `dialogAnswer`. A flow pushes `dialog{id, dialog}`
  and waits for the answer; the host closes one (`dialogClose`) or updates a resume choice's checking state (`dialogUpdate`). Every dialog
  still open is sent again, oldest first and as it stands, when the page calls `ready` (C# never sends one again after a reload, and its
  flow then waits for ever).
- **Resume, end to end** (`frame.rs`'s flow over `history.rs`'s `HistoryResume`, set as `frame::RESUME` at start; C# `WebAddAgentPane`,
  `WebPickResumedSession`, `WebResumeRows`): an agent pane from the add menu looks up the folder's newest session of that agent
  (`history::look_up`, head only, off the workers); with one no open pane continues, `resumeChoice` asks Start new / Resume… (checking
  while the look-up outlasts 300 ms); Resume… opens `resumeList` (`frame::open_resume` names its agent and folder, `history::open_resume_list`
  reads it afresh), whose rows are `resumeRows` (the page's search, the folder's sessions read again when "모든 세션 보기" changes;
  `hidden`, `empty`, `reason` as C# words them); the row picked is checked against the panes open now and remembered as the app's own
  (`history::close_resume_list`, `pick`, `remember_session`: `known-sessions.json`, written only with `--allow-state-write`), and the new
  pane continues it (`mighty_history::resumable::apply`), its older requests read back from the record into the diagram.
- **Workspace create** (`workspace_create.rs`; C# MainWindow.WorkspaceCreate.cs, `GitTool`, `GitCloneJob`; the rules are mighty-core's
  `workspace_create`, held to native/contracts/fixtures/workspace-create.json): `workspaceCreate`, `createCheck`, `pickLocation`,
  `makeFolder` (`git init` when asked), `repoName`, `gitProbe` (`git ls-remote -- <url> HEAD`, a newer one stops the older), `gitCreate`
  (`git clone --progress`, pushed as `gitProgress`; a second press waits for the running clone's answer, and a finished clone's answer is kept while its sheet is open, so a page loaded again mid-clone gets it on its next press) and `gitCreateCancel` (git and everything it started
  ended, what it wrote removed). git is the user's own, through `ProcessHost`, never prompting (an empty `GIT_ASKPASS`,
  `GCM_INTERACTIVE=never`, BatchMode ssh unless the user has an ssh command); the sheets start at the last location, else the active
  workspace's parent, else home.
- **Native views and the browser pane** (`surface.rs`; C# MainWindow.WebShell.Panes.cs, MainWindow.Browser.cs): `surfaceBounds`,
  `surfaceAction`, `browserOpen`, `browserNavigate` (C# `BrowserAddress.Resolve`), `browserBack`, `browserForward`, `browserReload`,
  `browserInstall`, `browserBounds`. Nothing starts while the setting `browserEngineEnabled` is off (read once per launch: `disabled`); the
  install offer without the WebView2 runtime (`missing`; `browserInstall` downloads Microsoft's bootstrapper only then, checks its
  Authenticode signer and runs it); else the pane's view opens over the slot in its workspace's web profile
  (`<local>\browser\<profile>\browser-profiles\<workspaceProfileKey or workspace id>`). The history (`BrowserHistory`) follows the engine's own
  navigations and every state is pushed (`browser{paneId, state}`); back and forward are the engine's; popups are refused and downloads
  cancelled; script dialogs never show; a press in the page makes its pane the active one; the picture shown while a page overlay hides
  the view is WebView2's `CapturePreview`.
- **The composer** (`composer.rs`, C# MainWindow.WebShell.Composer.cs over the never-shown `PaneView`): `composerState`, `updateDraft`
  (`DesktopService.UpdateDraft`: the pane alone, saved on the schedule), `send` (`PaneView.Submit`: a busy pane queues the draft, an idle
  one takes the draft and its attachments, waits for the model list as the CLI has it now (`models.rs`) and starts the request through
  the run service, a start that did not happen puts them back; a "send to" choice wraps the words once, before they are queued or started,
  and leaves the files in the composer; answered with `SubmitReceipt.SendAnswer`: outcome, user entry, graph run, the page's client id,
  the composer state), `stop`
  (`StopActiveRun`: the queue cancelled unless the turn only waits on background work), `queueRemove`, `queueRunNow` (바로 실행: `Here`
  starts the row now, `Steer` hands it to the running turn), `queueRunNowMenu`, `installCli`, `pickAttachments` (the OS's picker through
  Tauri's dialog plugin), `addAttachments`, `removeAttachment`, `dismissAttachmentError`, `attachmentPreview`, `slashCommands` (the
  built-ins and the scanned catalogue), `slashAction` (new conversation, help, model, permission, and rename: the pane's rename sheet),
  `composerMenu`, `composerMenuChoose` (the row the page showed: provider, model, effort, permission, Fast, models read again, new
  conversation), `runSettings`, `applyRunSettings`, `toggleFast`, `composerToggle` (status line), `newConversation`, `setAskTarget` (a
  target the pane offers, or null for Claude; any other refused with `remote.error.badBody`), `dismissAskNotice`.
- **The "send to" switch** (`ask.rs`, C# MainWindow.AskTargets.cs): a Claude agent pane of this computer offers the installed target CLIs
  (`AskTargetsFor`: codex, agy, gemini, grok, cursor-agent found along the CLI environment's PATH with PATHEXT and the installers' folders,
  read off the page's thread at most every 30 s, 2 s after a read that failed) once its slash catalogue (scanned when its composer is
  first drawn and by the slash palette) has `oh-my-claudecode:ask`. A send with a saved choice not yet known to stand waits up to 2 s for
  both. A saved choice no longer offered, judged only once both were read conclusively, falls back to Claude with the composer's notice
  (`composer.ask.fellBack`) until it is dismissed or another target chosen.
- **The cards** (`cards.rs`, C# MainWindow.WebShell.Cards.cs): `cards`, `permissionAnswer` (Allow once / Deny for the request the card
  shows: Claude's, a Codex `onRequest` run's approval, a pi dialog's Cancel), `questionnaireAnswer` (`QuestionnaireAnswers`,
  `ValidateAnswers`; the refusal's words back; Claude's AskUserQuestion or a pi extension's dialog), `planAnswer` (`AnswerPlanCard`: one
  answer at a time, a revise Core would refuse never leaves the card), `nextActionFill`, `backgroundWork`, `statusLineTrust`, and
  `antigravityAnswer` (antigravity.rs, C# `WebAnswerAntigravity` → `AnswerAntigravityPermission`: 거절 closes the card; 이번만 허용 / 항상 허용
  first read agy's settings as an allow would (a file that cannot take the rule is the card's reason), then send the continuation on the
  pane's conversation with the card's rules — held in agy's settings while that run goes, or kept — or queue it behind a busy pane, its
  rules put in place when it starts; a start that fails brings the card back with the reason). The card itself (`WebAntigravityCard`:
  each action's kind, name, target, rule and once-only words, 항상 허용 off with its reason) comes with the other cards.
- **The Mighty views** (`mighty.rs`, C# MainWindow.WebShell.Mighty.cs): `mightyVisible`, `mightySetPresentation`, `mightySaveSize`,
  `mightyExpand`, `mightyResultFiles`, `mightyLoadOlder` (the next ten older requests of the session record, read off the page's thread
  and drawn above the retained ones: `history.rs`), `mightyImages` (a step's tiles as data URLs, older runs' too), `mightyTimelineFlip`,
  `mightyLogCollapsed`, `mightyOpenDashboard`, `imageData` (a log picture from the cache by its hash; an SVG drawn to PNG at most 2,048
  on its long side, as C#'s `WebImageUrl` draws it, and so are `mightyImages`' tiles).
- **The terminals** (`terminal.rs`, C# MainWindow.Terminal.cs, MainWindow.WebShell.Panes.cs, MainWindow.TerminalPrompts.cs,
  MainWindow.ShellBlockRuns.cs, over mighty-terminal): `terminalOpen` (the footer; from then on a `footer` push whenever it changes),
  `terminalMessage` (ready starts the shell — the platform's default shell in the workspace's folder at the page's grid, with the CLI
  environment —, input, resize, title, directory, copy, paste, pressed), `terminalRestart`, `terminalHistory` (its content only: the
  dialog is a native panel, W6), `terminalPromptAnswer`, `terminalPromptShow`, `shellBlockRun` (only the command the reply shows, in
  the agent pane's terminal, its `agent-terminal` pane opened beside it, the record streamed at most every 200 ms), `shellBlockStop`.
  Pushes `{kind:'terminal', paneId, message}` (output each 32 ms, paste, reset, font, theme, footer); the font is the text setting's
  (12 pt at 100% to the nearest half point, the code font when one is chosen), given again to every open terminal when the theme or the
  text setting changes (C# `RefreshTerminalTheme`). A shell pane's status follows its shell through `RunService::update_pane` (C#
  `SetShellStatus`: idle once attached, running while a process of the shell's job sits on its console — read every 500 ms through
  `PtyControl::busy` —, completed, error); a terminal ends when its pane leaves the state (looked at every quarter second) and all of
  them when the app quits (each close on its own thread, the quit waiting at most 5 s). An agent pane's terminal (`Terminals::agent`) is
  followed for prompts every 400 ms while one of its commands runs, and a waiting one is the pane's terminal prompt card. In a smoke
  only the smoke's own shell pane starts a shell (C# `smokeTerminalEnabled`), without the user's profile.
- **The files pane, the reference bubble, HTML frames and the picture viewer** (`files.rs`, C# MainWindow.WebShell.Panes.cs's files
  part and MainWindow.WebShell.cs's conversation methods, over mighty-files): `openFiles` (C# `OpenFilePane`: the workspace's one files
  pane, left of the current pane; `open_slim_pane` places the work map's pane too), `filesState`, `filesToggle`, `filesReveal`,
  `filesFilter`, `filesSelect`, `filesRefresh` (each workspace's `FilePaneTree`, kept while the app runs; folders read off the page's
  thread), `filesPreview` (text cut to 256 KB with its colours as flat `[kind, start, length]` triples, pictures as data URLs: an SVG
  drawn by resvg, a PDF's first page by `Windows.Data.Pdf`, a bitmap as it is or fitted), `filesShowInExplorer`, `filesOpen`,
  `referencePreview`, `referenceChanges` (against the original the pane's run kept, else git's HEAD), `referenceOpen`, `referenceReveal`,
  `referenceSize`, `htmlFrame` (the file's folder served under `h<random>.html.mighty.invalid` through the `mightyhtml` protocol, at most
  eight folders, for the page's frame sandboxed to scripts) and `imageAction` (copy, save, reveal, open with the default app; a diagram
  tile of an older run too). A Claude run's Edit, Write and MultiEdit results keep the files' originals (`FileOriginalStore`: the
  profile's `file-originals` with `--allow-state-write`, else `%LOCALAPPDATA%\dev.mightycode.tauri\file-originals\<profile>`; the work map
  reads the same folder), pruned at the start to what the state names, a closed pane's dropped and a run's when its graph lets it go.
- **The work map** (`workmap.rs`, C# MainWindow.WorkMap.cs): `openWorkMap` (the workspace's `workmap:<id>` pane, placed as the files
  pane is, never saved), `workMapState` (the model, nulls kept), `workMapSummarize` (one at a time per workspace; the model is pushed
  again as `workmap` when it ends), `workMapOpenFile` (files.rs's `open_file_in_pane`: the file revealed, selected and previewed in the
  workspace's files pane, then `files{paneId, path}` pushed). The summaries are kept in `<profile>/work-map` with
  `--allow-state-write`, else in this machine's local folder for the profile.

### The run engine in the app

One `RunService` (mighty-run) owns the state once the host has started it (`runs.rs`): `View::update` goes through
`RunService::update` (normalized under the service's lock, then saved), and each `ServiceEvent` takes the page's copy from the service
again. Its runner makes each request's graph with mighty-graph's tracker (`TrackerFactory`: the graph run published every second and at
the end, with the notice runs) and keeps tool pictures in the picture cache (the profile's `image-cache` with `--allow-state-write`, else
`%LOCALAPPDATA%\dev.mightycode.tauri\image-cache\<profile>`). Claude runs load the mighty-bridge plugin (`claude-mods` beside the exe, else
the repository's `mods/mighty-bridge`). A Codex pane in `onRequest` runs Codex's `app-server` through `CodexApprovalChannel`: each
approval is the pane's `permission` card, a turn not started in 15 s or a protocol failure ends the run in an error, a stop cancels the
waiting cards. A Codex run's sub-agents are read from its session files under `$CODEX_HOME` (else `~/.codex`) every second and at its end
(mighty-history's `CodexSessionWatcher`, read only). pi runs `pi --mode rpc` with the app's `ask_user` extension (`Agents` beside the exe,
else the repository's `native/macos/Sources/MightyCore/Resources/Agents`, C# `AgentFiles`); its dialogs are question cards, a session pi
has no record of (`~/.pi/agent/sessions`, read only) is started anew. Antigravity runs (W5-C) read agy's stream through the parser, leave a card for what headless agy
was refused (`antigravityPermission`, then the run service's board), and have their sub-agents' own transcripts read about once a second
(`~/.gemini/antigravity-cli/brain`, read-only). The rules an allowed card puts in agy's own settings
(`%USERPROFILE%\.gemini\antigravity-cli\settings.json`, `permissions.allow`, written only when the user presses 이번만 허용 or 항상 허용) are
recorded in the ledger `antigravity-temporary-rules.json` — the profile's with `--allow-state-write`, else
`%LOCALAPPDATA%\dev.mightycode.tauri\antigravity\<profile>` — and what a crash left in either place is taken out as the run service
starts, before any agy run may start (the other place's ledger pruned too, the profile's never written without the flag). With the flag
the ledger is the same file the C# app keeps in the profile; the local one is this port's alone and the C# app never reads it, so the
C# app's own leftovers are its own launch's to take out. A file rule names only a plain local path (`X:\…` or `X:/…` on a fixed drive,
no further `:`, no `~`; `/…` but not `//…` on the Mac), read as text before the disk is asked (a UNC name is never handed to the file
system), and never reaches agy's `.gemini` folder, the ledger's folder, the profile or the app's local folder — compared as written,
with its links read (one leading to a share refuses it) and as the file system names the longest part that exists (mighty-platform
`PathProbe`: `GetDriveTypeW`, `GetFinalPathNameByHandleW`). A card's verdicts are worked out once, off the parser's thread, and kept on
it; an allow is judged again before its rules go in. A command rule is kept for good only when its line is plain ASCII, and a URL
rule names only a plain host. A send to an Antigravity pane whose model agy's live list lacks starts
on agy's default (the pane's model set back with a note). The CLIs are read once at start (`ProviderCatalog::get_runtime`) and a pane's provider again when
its models are asked for; until then the composer says it is checking the runtime.

The model lists keep themselves right (`models.rs`, C# MainWindow.ModelRefresh.cs over mighty-run's `ModelCatalogRefresh`): selecting a
pane reads its list again, a provider switch reads the new agent's, the model menu's refresh row and a new conversation read it again at
once (C# `RefreshPaneModels`), and a send or a queued request waits up to 3 s for the list as the CLI has it now (the editor says it
checks the models; a queued request removed, stopped or closed meanwhile does not start); a newer list becomes the provider's runtime, and
an idle pane whose model or effort the list no longer has takes the default with a line in its transcript
(`ModelSelectionSupport.Adjusted`). At quit nothing is read or reconciled any more before the last save. The last list each CLI gave is
kept (`KeptModelLists`: `model-lists.json` in the profile with `--allow-state-write`, else in
`%LOCALAPPDATA%\dev.mightycode.tauri\provider-catalog\<profile>`), so a read that fails, now or after a restart, shows that list, and the
model menu says which list it is (`ModelListNotices.Notice`: the built-in one, or the kept one and its age). A smoke reads and reconciles
nothing, as C#'s.

opencode and Hermes run over the Agent Client Protocol (`opencode acp`, `hermes acp`; mighty-run `acp`, W5-B), with C#'s rules: the
pane's mode as OpenCode 2's own rules for the run (`OPENCODE_CONFIG_CONTENT`, with the shipped question instructions: `Agents` beside the
exe, else the repository's `native/macos/Sources/MightyCore/Resources/Agents`) or as Hermes's edit policy and `HERMES_YOLO_MODE`; OpenCode 1
refused; Hermes's clarify switched on once per launch through its own `config`; a Hermes session continued only where its `state.db` says
it may (read-only through mighty-history's `acp`, `runs.rs` `hermes_session`); the initialize given 45 s (Hermes 90 s), the server ended
2 s after the prompt's answer, a stop sending Hermes `session/cancel` a second before it ends the process. Their approvals are the pane's
permission cards (`permissionAnswer`), their `elicitation/create` forms and Hermes's clarify the question card (`questionnaireAnswer`; the
card's Cancel declines the form), and a lost sign-in ends the run with the `authentication` reason. The picker a Hermes session offered is
Hermes's model list from then on (`HermesAgent.CachedCatalog`: `hermes-models.json` in the catalogue's storage folder, the profile with
`--allow-state-write`, else `%LOCALAPPDATA%\dev.mightycode.tauri\provider-catalog\<profile>`), read again when a Hermes pane's run ends (C#
`RefreshModelsAfterHermesRun`). The two agents' sign-in states (`mighty_run::acp::accounts`: `opencode_status` over `opencode auth list`,
`hermes_probe` and `hermes_status`, as `CliAccountStatus`) are ready for the accounts section and the sign-in card (W6), which do not ask
them yet.

Pushes, with C#'s rules and cadence: `event` (each run event; a log line and a graph run without their record, which the next delta
carries), `permission` (each tool-permission change; a pane's waiting requests end with its run, as the window's
`ClearToolPermissions`), `composer` and `cards` (`panes.rs`: a pane whose record changed or that a method or a request marked, looked at
every 30 ms and sent when its JSON changed; the cards again each second while an open background strip has a task running), `mighty`
(every 120 ms for the panes the page shows, a pane projected when its record changed or a method marked it and every one each eighth
tick; only the blocks whose copy changed).

### Pending inputs

What a later stage gives the composer, the cards and the Mighty views stays at its resting value (mighty-composer `composer::PENDING`,
`cards::PENDING`, mighty-graph `view::Sources`):

| Input | Until then | Stage |
|---|---|---|
| `BackgroundUpdateHolds`, `ManualMutationBlockReason`, `ManualUpdateReason` | nothing holds a pane | W6 |
| `SetupItemForMissingCli` (the blocked row's install button), `installCli` | no install offered; `installCli` does nothing | W6 |
| `SecondSend`, `WorktreeMenuOnly`, `HeldItems`, `Delegation.EndingRuns`, `queueRunNowMenu` | Ctrl+Enter queues as Enter; no held rows; the menu is empty | W7 |
| `RemoteWorkspaceFeatures.BlockedReason`, a remote pane's runs (`RemoteRunner`) | none | W8 |
| The style row, the guide panel, a guided question, a style's prompt and plan mode (`activeStyle`) | no style; typed text goes to the agent as typed | W9 |
| A typed built-in (`SubmitTypedBuiltin`), the slash palette's usage, settings and plugins actions | sent as text; those actions do nothing | W6 |
| The phone's "send to" switch and its `setAskTarget` route | none | W8 |
| The custom model dialog (typed: mighty-composer `custom_model`), Settings › 페이즈별 모델 (typed: mighty-run `phase_section::form` and its actions), the plugin window | none drawn | W6 |
| Background strip rows (`PlanCardSupport.BackgroundStripRows`) | the strip's line without rows | W8 |
| The agents' web-open prompts (Agent IO's `open_url`), the sign-in card, the status line | none | W7, W6, W6 |
| The shell pane's history dialog (`terminalHistory`: a native panel), the agent's `run_in_terminal` tools (Agent IO), a command typed into a new shell pane (`RunTerminalCommandAsync`: style installs, sign-ins), the phone's view of a shell's status and of a terminal prompt (`terminal::Pending`) | the history's content made, nothing shown; the agents' terminals exist for shell block runs only; nothing typed | W6, W7, W6/W9, W8 |
| The Ouroboros dashboard, background output tails, custom agents' definitions | empty; `mightyOpenDashboard` only marks the view | W9 |
| An Antigravity card on the phone and over the remote wire (`MobilePermission.FromAntigravity`, `MobileAntigravityCard`, the permission route's answers, a remote pane's host holding the card's rules in its own settings, `RemoteWireStart.AntigravityGrant`), the dashboard's count of a pane's card (`WorkDashboard.Pending`) | none: AntigravityPermissionVerification.cs:494, :511, :534 (its phone half), :683, :727 are W8's; the dashboard is the frame's later stage | W8, W6 |
| Saving the text setting (Settings › Text), the files and work map panes' phone routes, the work dashboard's usage chips | the setting lasts until the app quits; none | W6, W8, W6 |
| `ModelsAfterAccountChange` (a sign-in or sign-out drops every model list of that provider: models.rs over `ModelCatalogRefresh::invalidate`, before the accounts section's whole-runtime read) | nothing signs in or out in the app yet, so no list is dropped for it | W6 |
| The rest of AntigravityVerification.cs:158 `TheModelListLabelsAndInstall`: the catalog row's filtering and fallback, `Efforts`/`IsBeta`, `Executable` for codex and claude, the install command, the update label, the `ModelLabel` slugs, the off-Windows block | its model list and a fake agy's models are checked (tests/antigravity.rs, W5-D's model lists); the setup screen's rows are not | W6 |

Settings › 페이즈별 모델's form (W6) must check every model it saves with `wire::model` (the composer's `ChangeModel` rule) before it
reaches mighty-run's `model_settings` writes, which take what they are given, as C#'s `ModelSettingsFileStore` does: a value with a line
break adds lines to Ouroboros's `config.yaml`. The writes keep C#'s other limits too, for W6 to settle: the comments in omc's
`config.jsonc` are dropped, a rewritten YAML line loses its CRLF, a symlinked config is replaced by a plain file, the file's permissions
are not kept, and the temporary file is not flushed before it replaces the config.

### Unimplemented page methods

What the page calls that the host answers `unimplemented` (the page's `bridge.call`s against `implemented`; a full smoke meets only
`nativeState`):

| Methods | Stage |
|---|---|
| `nativeState`, `nativeAction`, `nativeNav`, `nativeButton`, `nativeClose` (the NativeNode forms: settings screens and dialogs) | W6 |
| `openSettings`, `openSetup`, `openHelp`, `openPlugins`, `sessionInfo`, `loginAction`, `usageRefresh`, `usageDirect` | W6 |
| `childAction` (a delegated child's row) | W7 |
| `connectRemote`, `companionStatus`, `companionToggle` | W8 |
| `guidedAction`, `postponeUpdate`, `restartForUpdate` | W9 |

## Platform ports and native surfaces

The host reaches the OS only through the `mighty-platform` ports; `platform.rs` picks `mighty-platform-windows` or
`mighty-platform-macos` by target (the only place either is named) and hands the rest of the host a `Platform` and a
`SurfaceFactory` for the main window. The data folder and the pre-rename folders come from `Platform::paths`.

Native views over the page (docs/tauri-architecture.md §3): when the page calls `ready`, before the snapshot, the host pushes
`{kind:'host', platform, surfaces:{terminal, browser}}` (Windows: terminal `web`, browser `native`). From then on the page reports
each native slot with `surfaceBounds{paneId, surface, rect (device px), visible, occluded}` and the `SurfaceManager` opens, places,
shows and hides the platform's view there; a page reload hides every view until it is reported again, and a pane that leaves the
state closes its view. On Windows a browser pane is a Tauri child webview of the main window (`unstable` multi-webview) with its
workspace's own WebView2 data (`%LOCALAPPDATA%\dev.mightycode.tauri\browser\<profile>\browser-profiles\<key>`; C# keeps the same
folders under the profile, which a running C# app holds, so this one never shares them), placed in physical pixels as the slot reports
them; the child webview never reaches the host (`host_post` only listens to the `main` webview, and Tauri refuses app commands from
remote origins). Its engine's navigations, refused popups and downloads and its focus come back through a `BrowserListener`
(mighty-platform `BrowserEvent`), and the picture shown while it hides under an overlay is `CapturePreview`'s.

The files ports (`mighty_platform::files`, chosen in `platform.rs` with the others; fakes in `mighty_platform::fake`): `FileHandles` (an
open file's handle asked where it is: `GetFileType`, `GetFinalPathNameByHandleW`; every reader of the user's files uses it, mighty-composer's
`read_under` included, so a folder swapped for a link between the walk and the open is caught as C# catches it on Windows), `PdfRaster` (a
PDF's first page through `Windows.Data.Pdf`), `Desktop` (show in Explorer through `SHOpenFolderAndSelectItems`, open with the default app
through `ShellExecuteExW` refusing a type `AssocIsDangerous` or `PATHEXT` names, as Windows' launcher refuses programs, and a picture on
the clipboard as `CF_DIBV5` and `PNG`) and `SaveDialog` (Tauri's dialog plugin, owned by the window). On the Mac they are stubs until its
stage: the path is walked again after an open (C# off Windows), and the rest refuses. Beside them `PathProbe` (`platform::path_probe`)
answers the Antigravity rule safety's questions about a path agy named, only once it passed the text check: a fixed drive
(`GetDriveTypeW`), a link read without following it, what the file system calls a path (`GetFinalPathNameByHandleW`; the Mac's is real:
`realpath`, no drive letters); its fake `RecordingPathProbe` records every question.

The C# host never sends `host`, and every page change for it sits behind `import.meta.env.MODE === 'tauri'`, so the C# page build
(`dist/`) is byte for byte what it was.

### Child processes

Every child process the app starts goes through the platform's `ProcessHost` (on Windows `WindowsProcessHost`: created
suspended, put in a kill-on-close job, only its three pipe ends inherited). A `std::process::Command` or `tokio::process::Command`
spawn passes `bInheritHandles = TRUE` with no handle list, so one that runs while `WindowsProcessHost` launches a child inherits
that child's pipe ends and keeps its stdin, stdout and stderr open after the child has closed them. `clippy.toml` therefore
disallows `std::process::Command::new` and `tokio::process::Command::new`, and `cargo clippy -- -D warnings` fails on either. Only
tests, the fakes and the tests' stand-in children (`mighty-fake-child`, `mighty-fake-cli`) start processes directly, each with an
`#[allow(clippy::disallowed_methods, reason = "…")]`; the app has no exception. A `SpawnRequest` names the variables the child
must not inherit (`env_remove`, `without`): `host.rs`'s `read_git` (the header's git status) takes the app's `GIT_*` variables out
of git's environment that way, as C# `WorkspaceGitInfo.Command` does (on the Mac it reads nothing until its `ProcessHost` exists).
`ProcessHost::cli_request` and `shell_request` say how an agent CLI and a shell pane's command start on each OS (Windows: a
`.cmd` shim and the command through `cmd.exe /d /s /c`).

The frame model (`shell.rs`) ports C# `WebShellModel` whole, field for field: the panes (status word, tone, agent mark, the
"· 20 days ago" / clock / context meta, the waiting words, the dashboard meta with the model's label), the counts, the sidebar with
the delegation tree (children under their parent, a closed parent's node), the header with the active workspace's git (read with
`git status --porcelain=v2` as C# reads it, off the UI thread) and the work-map and files buttons, the layout (saved tree, else the
mode's preset, kept stable between models as C#'s `layoutDefaults`; focus shows the active group), the dashboard, the banners and
the status bar. What a later stage feeds it is listed in `shell::Pending` (attention counts, delegation rows, runtime check,
usage, app update, companion, setup, resource banner, remote connections, the CLI's model catalogue).

## State

- Data folder: `%APPDATA%\MightyCode`, or `--profile`. A default profile without a state file takes the one in
  `%APPDATA%\MightyClaude` or `mighty-claude` (copied once with `--allow-state-write`, read where it is without).
- `workspace-state.json` version 1, read and written as C# does, byte for byte (`json.rs`, `model.rs`, `graph.rs`): property names
  matched without regard to case, an absent field taking the C# initializer's value (a new id, the time now), numbers from
  strings, the lenient converters' damage read as nothing, a wrong type anywhere else failing the load; written in C#'s property
  order, nulls left out, strings escaped as `JavaScriptEncoder.Default` escapes them (every non-ASCII character as `\uXXXX`,
  `+ < > & ' "` too), doubles in .NET's shortest form, times in `"O"`. C# keeps no field it does not model, so neither does this:
  a save drops unknown fields.
- The load is C#'s `LoadAsync` (`store::Store::load`): 8 MB, version 1 as an integer, `workspaces` and `sessions` arrays, the
  remote-reference filter of `KeptWorkspaces`, the once-per-profile `AntigravityMigration`, then the full `Normalize` as a restore.
  A file holding a NUL byte (what a power cut leaves) is moved aside to `workspace-state.json.unreadable-<time>-<id>` and its
  `.bak` loaded (or an empty state), with C#'s notice in the OS's language; any other unreadable file stops loading and every
  save, so it is never overwritten.
- Saving is C#'s `SaveAsync` and `AtomicWriteAsync`: normalized, every workspace one the load or the user approved, files panes
  left out, at most 8 MiB, written to a temporary file flushed to the disk and swapped in with `ReplaceFileW` (the old file kept
  as `.bak`), a refused swap tried 40 times 50 ms apart. A change the user makes is saved at once; a stream of changes waits
  0.22 s and at least 1.5 s after the last save began (`SaveSchedule`); quitting saves once more.

**Writing is off unless `--allow-state-write` is given.** Without it nothing writes the state file, its backup or anything else in
the profile except `tauri-logs/` and the smoke's result (both can be pointed elsewhere with `--log-dir` and `--smoke-out`).

### Parity with C#

`tools/parity/run.ps1` builds the C# tool and compares, byte for byte, what C# and this port save (and send the page) for a
corpus of state files — the synthetic edge cases and the repository's fixtures (`parity make-corpus`: unknown and wrong-typed
fields, NUL files with and without backups, an 8 MiB file, every enum value, non-ASCII, emoji and surrogates, timestamps with 7
and 9 fractional digits, missing and null fields, the delegation records, the remote references, the demo state), plus copies of
the real profile and of `release/perf` and `release/help-capture-final` — and 12,038 doubles, 490 strings and 223 times.
`cargo test` runs the same corpus and vectors when the .NET 10 SDK is there (`tests/parity.rs`; `MIGHTY_PARITY=require` makes a
missing SDK a failure), and `delta.rs`'s tests compare 600 random deltas with C#'s `WebDelta` byte for byte.
`tools/parity/real-write.ps1` runs the app on a copy of the real profile with writing on, changes the theme through the page and
quits, and compares the file with C#'s save of the same change.

What is not reproduced (each a file no app writes): a number literal beyond ±1.8e308 (C# reads infinity; the load fails here);
a lone surrogate escape inside a field C# does not model (C# skips it; the load fails here — inside a modelled field both fail);
an `-0.0` literal where C# wants an integer (read as 0 here; C# refuses it); a date C#'s culture parser reads but ISO 8601 does
not (`11/13/2026`; read as no date here); `"theme"` written twice with a differently cased key in between (the last of each
spelling wins here, the last of all in C#).

## Identity and WebView2

- `identifier` is `dev.mightycode.tauri`, a development identity so this app never collides with the installed C# app. It must
  become the real one before shipping.
- Product name "Mighty Code", main binary `MightyCode.exe` (the 0.4.x updater replaces that name), window 1440×920 with a
  940×650 minimum, as the C# window.
- WebView2 data: `%LOCALAPPDATA%\dev.mightycode.tauri\webview\<default | the profile path as a folder name>` — never the C# app's
  `<profile>\web-profile`, and never inside a profile.
- CSP: the page's build-time meta policy (`vite.config.ts`). `vite build --mode tauri` adds `connect-src 'self' ipc:
  http://ipc.localhost` so Tauri's IPC is not blocked (WebView2 reaches it at `http://ipc.localhost`); the C# build's policy is
  unchanged. `app.security.csp` is null, so Tauri adds no policy of its own. HTML previews' frames come from the `mightyhtml` protocol
  (`http://mightyhtml.h<random>.html.mighty.invalid/…` on Windows, wry's form of a custom scheme; `mightyhtml://h<random>…` on the Mac), so
  the Tauri build's `frame-src` is `http://*.html.mighty.invalid mightyhtml:` where C#'s is `https://*.html.mighty.invalid` (its WebView2
  virtual hosts); a frame's own navigations are held to it as well.

## Smoke

```powershell
.\native\tauri\scripts\smoke.ps1                                   # a new demo profile in %TEMP%
.\native\tauri\scripts\smoke.ps1 -Profile "$env:APPDATA\MightyCode" # the real profile, read-only
```

`MightyCode.exe --smoke-test --smoke-exit --profile <folder>` waits until the page has called `getLocale` and `ready` and the host
has pushed the snapshot and the frame model, then runs a probe in the page: the sidebar's pane titles, the selected pane, the theme,
tabs, groups, header and status bar, then a click on another pane's row (the page must show it and the host's state must take it).
The frame steps follow (`smoke_frame.rs`, C#'s `RunWebShellFrameSmoke`, `checks.frame*`): the sidebar lists the active workspace's
panes with the header and the status bar, a row click selects a pane in the host and the page, the add-pane menu's terminal entry adds a
tab, a tab click and back switch panes (timed, `frame.tabSwitch*Ms`), and the columns preset draws two groups.
Where the platform draws browsers natively, the browser-surface step follows: the host adds a browser pane in a workspace of its
own (in memory), the engine on for the smoke, and makes it active; a second probe types the address of a fixture page this process
serves on `127.0.0.1` into the pane's address field, waits until the native view shows it, checks the view's bounds read back from
Windows (the WebView2 host window in the main window's client coordinates) against the slot in device pixels (±1 px), has the host push
a page dialog and checks the view hides, closes it and checks the view shows again (`checks.browserSurface`); then the address field
shows the history's address (`browserNavigated`), a second page and the page's back button take the engine and the history back
(`browserBack`), a page that calls `window.open` as it loads has its popup refused with no webview added and stays (`browserPopupBlocked`),
and the tab's menu opened over the slot hides the view until Esc closes it (`browserMenuOver`).
Where the page draws terminals (Windows), the terminal step follows (`smoke_terminal.rs`, the terminal part of C#'s
`RunWebPanesSmoke`): the host adds a shell pane in a workspace of its own (a temporary folder, in memory) and lets it start a shell
(without the profile); the probe waits for the shell idle at its prompt (`terminalStarted`), types through the page's xterm a
command that writes a marker in two halves and waits for the joined marker in the terminal's buffer (`terminalEcho`), reads the
footer (`terminalFooter`: the engine and the grid), types a command that runs a while (ping) and waits for the pane's status to
read running and then idle (`terminalStatus`), and closes the pane: its shell ends (`terminalClosed`). Details under `terminal`.
The files steps follow (`smoke_files.rs`, the C# smoke's `RunWebPanesSmoke` files part, `RunWebReferenceSmoke` and `RunWebPictureSmoke`, and
an HTML frame): the host adds a workspace of its own (a temporary folder with fixture files), a Claude pane whose 기록 view holds a reply
naming `src/a.ts:3` and one whose diagram holds a PNG and an SVG picture; the probe opens the workspace's files pane from its add menu
(`filesAddPane`: `addPaneEntry` through `frame::OPEN_FILES`), opens `smoke-folder` in the tree and previews `hello.ts` with line numbers and colour (`filesTree`, `filesFolderOpened`, `filesPreview`), opens
`page/index.html` as a page in the sandboxed frame, which the host must have served with its style sheet and script (`htmlFrame`), clicks
the reply's `src/a.ts:3` where it is drawn and the reference bubble shows the file at line 3 and closes (`referenceBubble`), and both
picture tiles come as PNG data URLs, the first opens the picture viewer with focus inside and Esc closes it (`pictureViewer`).
The history step follows (`smoke_history.rs`, the C# web smoke's work map steps and the resume flow end to end): the host makes a fixture
repository in a temporary folder (a tag and one commit past it), adds it as an active workspace of its own (in memory) and writes a fixture
home whose Claude records belong to it (the history reads that home only); the probe clicks the header's work map button once the folder
reads as a repository (`workMapButton`), waits for the overview's cells (`workMapOverview`) and one card per version with a summarize
button (`workMapVersions`), turns the Mac chip off and on (`workMapChips`); then it opens the workspace's add menu and its Claude entry, and
the folder's earlier session makes the frame's flow ask Start new / Resume… (`resumeChoice`); Resume… opens the list, which shows the
session with its first request and the nested run as hidden, says a search with no match, lists both with "모든 세션 보기"
(`resumeList`); a click on the session's row closes the list and adds a pane that continues it, and the page draws its record's three
requests above in the Mighty diagram, read back from the record (`resumeLoaded`).
With git on PATH the gitCreate step follows: a bare repository in a temporary folder (the fixture's own git), the sheets' location that
folder, then the add menu's "Create from Git…": the address typed, the check reachable, the name `remote`, 생성, `gitProgress` pushed,
and the clone listed as a workspace (`gitCreate*`). The reload step then opens a workspace's rename sheet from the host, loads the
page again, and checks the sheet is drawn again with its name and that its answer (Esc) ends the waiting flow (`dialogResentAfterReload`).
With `--smoke-fake-cli` the ACP step follows (`smoke_acp.rs`, W5-B; C#'s smoke has none): opencode is the stand-in replaying a recorded
session that asks one question (`--fake-acp`, opencode-acp-question); the host adds an opencode pane in a workspace of its own, the probe
sends a request from the composer, the agent's form is drawn as the page's question card with its three options (`acpQuestionCard`), the
probe picks Red and submits, and the answer reaches the agent as the form's content while the card goes (`acpAnswered`), the run completes
and the diagram draws the recording's answer (`acpDiagram`).
The run steps follow (`smoke_runs.rs`, the C# smoke's `RunWebComposerSmoke`, `RunWebCardsSmoke`, `RunWebMightySmoke` and
`RunWebStepsSmoke` over a real child). With `--smoke-fake-cli` (what `smoke.ps1` passes unless `-NoRuns`): the host adds a Claude pane
in a workspace of its own (a temporary folder, in memory) shown in the Mighty diagram; the probe types a request into the composer and
presses Enter (`checks.composerSend`: the draft reached the state, the send started); the stand-in asks one Bash permission, allowed from
the page's card (`permissionCard`), and one plan, approved from the page's plan card (`planCard`: both answers reached the CLI); it then
replays claude-stream-subagents and the diagram draws the request, the sub-agents and the result with the recording's answer
(`mightyDiagram`); a second request is sent, and while its permission waits the progress panel draws a three-step plan on it (1/3: done,
current, pending) and none without it (`progressSteps`); the request is stopped from the composer (`stopRun`: the run ends stopped and its
card goes); then a pi pane in the same workspace sends a request to the stand-in pi, whose `ask_user` question is drawn as the question
card, its first choice is sent and reaches pi as the dialog's value, and the run completes with pi's reply (`piQuestionCard`). Then an Antigravity pane (the stand-in as C#'s fake agy over the smoke's own agy settings beside its logs, never the user's):
its request is refused `echo hi`, the page draws the Antigravity card with the command and its rule (`antigravityCard`), and its 이번만 허용
continues the conversation with the rule in those settings for that run — the stand-in answers "allowed" — and the settings are as they
were once it ended (`antigravityAllowOnce`). With `--smoke-real-claude` (`smoke.ps1 -RealClaude`) the probe sends "Reply with exactly the word: ok" to the installed
`claude` CLI instead (the pane's default permission mode, writes off) and checks the run completed, the result block shows "ok" and the
CLI reported its usage (`real*`).

It writes `smoke-result.json` (`passed`, `checks`, each step's report — `probe`, `frame`, `browser`, `terminal`, `files`, `history`,
`gitCreate`, `reload`, `acp`, `runs` —, the calls it saw, the unimplemented methods) and quits with
0 when it passed, else 1 (Tauri's event loop would end with 0 either way; `main.rs` keeps the code). The demo
profile is made by `cargo run -p mighty-core --example seed_demo -- <folder> [ko|en|zh|ja]` from
`native/contracts/fixtures/demo-profile.json`, as the C# help capture seeds it (without playing the runs). With `-Profile`, the
script compares `workspace-state.json`'s SHA-256 and time before and after and lists any file added to the profile.

## CI

`.github/workflows/native-tauri.yml` runs on pushes to `feat/tauri` and on pull requests touching `native/tauri/**` or the page,
on `windows-latest` and `macos-latest` (arm64): the page's typecheck and unit tests, `cargo fmt --all --check`,
`cargo clippy --workspace --all-targets -- -D warnings`, `cargo test --workspace`, `npm run tauri build -- --debug`, and on Windows
the smoke against the seeded demo profile (its result is kept as the `tauri-smoke-windows` artifact). It is independent of the
0.4.x workflows. The Mac build bundles `assets/icons/mightycode.png` and `MightyCode.icns`, which tauri-codegen needs there.
