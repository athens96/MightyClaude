# Mighty Code — Rust + Tauri 2 host (skeleton)

The start of the Rust rewrite of the desktop app, Windows first. It opens one window, draws the same React page the C# app draws
(`native/windows/web`), loads the saved state and answers the page's first calls. It saves the state byte for byte as the C# app
does, but **only when started with `--allow-state-write`**; without it every change stays in memory. The C# app
(`native/windows/MightyCode.*`) is untouched and still the shipping app.

## Layout

| Path | What |
|---|---|
| `Cargo.toml` | The workspace: `crates/mighty-core`, `crates/mighty-composer`, `crates/mighty-graph`, `crates/mighty-platform`, `crates/mighty-platform-windows`, `crates/mighty-platform-macos`, `crates/mighty-run`, `src-tauri`. |
| `crates/mighty-core` | No Tauri, no OS code. JSON exactly as C# `Wire.Json` reads and writes it (`json.rs`), .NET's times (`time.rs`), the state models (`model.rs`, `graph.rs`: C# `Models.cs`, `MightyGraph.cs`, `ExecutionGraph.cs` and the records they hold), `StateStore.Normalize` with every rule it calls (`normalize.rs`), loading, saving and the save timing (`store.rs`, C# `StateStore.cs`, `SaveSchedule`, `DesktopService`'s save queue), the locale catalogues and `LocaleKeyLeak` (`locale.rs`), the pane layout rules (`layout.rs`, C# `PaneLayout.cs`), model labels (`model_label.rs`), the frame model (`shell.rs`, C# `MainWindow.WebShell.Shell.cs`), the demo state (`demo.rs`), the add-pane menu and a pane's place (`add_pane.rs`, C# `AddPaneMenu`, `SessionTemplate`, `AddToLayout`), renames and pane titles (`rename.rs`, `RenameSupport`, `PaneTitle`), the workspace create rules (`workspace_create.rs`, `WorkspaceCreate`), the browser pane's rules (`browser.rs`, `BrowserHistory`, `BrowserProfile`, `BrowserEngineService`), the text setting (`text_appearance.rs`). |
| `crates/mighty-composer` | No Tauri, no OS-specific code: the composer's C# Core classes and what the WinUI host builds from them for the page (W2-B). `slash` (`SlashCommands.cs`, `SlashCommandStrings.cs`; the scan reads files through `SlashFiles`), `next_actions`, `ask_targets`, `queue` (the second send, 바로 실행, `QueuedInputBuffer`, `RunBlock`, `BackgroundQueuePolicy`), `pending` (`PendingDisplay`, `SubmitResponse`, `SubmitReceipt`, `SubmitScroll`), `attachments` (`AttachmentSupport`; staging is the run engine's), `images` (`AgentImages.cs`: header sniffing with TIFF, the SHA-256 cache, the path rule, Markdown pictures, the RTF picture), `svg` (`SafeSvgDocument`, the SVG rules of `FilePreviewClassifier`), `html` (`LocalHtmlDocument`), `text` and `files` (the decoding and path rules they read). Then the page's models as pure functions: `composer::composer_state` (`WebComposerState`), `menus::composer_menu`/`menu_action`/`run_settings_info`/`run_settings_from`/`slash_catalog`, `cards::cards` (`WebCards`) with `presentation` (`ToolPermissionPresentation`, `UserQuestionnaire`, `PlanCardSupport`). What later stages give them is typed (`composer::WindowRules`, `cards::CardInputs`) and listed (`composer::PENDING`, `cards::PENDING`). Its tests port the C# checks, read the shared fixtures, check the page's field names against `src/composer/*.ts` and `src/cards/types.ts`, and compare 35,597 vectors with C# itself (`tools/parity` `composer`). |
| `crates/mighty-core/examples` | `seed_demo` (a test profile from the help capture's demo), `roundtrip` (any state file loaded and saved in memory, timed; read-only), `parity` (the Rust side of `tools/parity`). |
| `tools/parity` | The C# side of the parity checks (`Parity.csproj`, a .NET 10 console over `MightyCode.Core` with `WebDelta.cs` compiled in; `Composer.cs` answers mighty-composer's vectors), `run.ps1` (the corpus and the vectors compared byte for byte), `real-write.ps1` (the app's save of a real profile's copy against C#'s), and `run-replay` (C#'s `DesktopService` running a recorded Claude run through mighty-run's fake CLI, the expectation of `crates/mighty-run/tests/service.rs`). |
| `crates/mighty-platform` | No OS code. The ports (traits) between the shared logic and the OS, each with its contract, a fake of each (`fake.rs`) and `Unsupported` (`unsupported.rs`), and `PlatformProfile`, the deliberate Windows/Mac differences as named values (docs/tauri-architecture.md §2.1). |
| `crates/mighty-platform-windows` | The Windows ports, everything behind `cfg(windows)`: `Paths`, `RegistryPrefs` (HKCU `Software\MightyCode`, reads), `DpapiVault` (C# `DpapiRemoteKeyVault`'s files, read and written both ways), `WebviewSurfaces` (browser panes as WebView2 child webviews), and the process layer (W1-C): `WindowsProcessHost` (C# `ChildProcess`: suspended start, kill-on-close job, hidden UTF-8 console), `command` (C# `WindowsCommand`: PATH × PATHEXT, `.cmd` through `cmd.exe /d /s /c`), `CliRunner`, `discovery`, `WindowsShellEnvironment` (C# `CliEnvironment` + the PowerShell profile capture), `user_environment`, `WindowsPtyHost` (C# `PseudoTerminal`, ConPTY) and `shell_activity`. Its tests run real processes against a stand-in child (`tests/fake/child.rs`, a `[[bin]]`) and compare with C# itself (`tests/parity`, a tiny .NET program over MightyCode.Core). The rest is `Unsupported` for now. |
| `crates/mighty-run` | No Tauri, no OS code: the run engine (W2-A). `manager.rs` (C# `RunManager`: a pane's CLI started through `ProcessHost`, its frames read, its stdin kept for steers and answers), `providers.rs` (C# `ProviderCatalog`: every provider's arguments, Claude's and Codex's model reads, the runtime and discovery), `permissions.rs` and `presentation.rs` (C# `ToolPermissions`: `ProviderInput`, `ClaudePermissionChannel`, the card's copy), `parser.rs` and `stream.rs` (C# `OutputParser` for Claude and Codex, `ClaudeStream`), `plan_mode.rs` (C# `ClaudePlanMode`, the checklist and background-task trackers), `mods.rs` (C# `ModBridge` + `HttpHost` over hyper), `service.rs` (the run half of C# `DesktopService`, its events through a channel, with `UpdateDraft` and the window's `ClearToolPermissions`), `graph.rs` (`GraphFactory`/`GraphSink`, where mighty-graph's tracker plugs in), and the smaller C# classes each in its own file. Its tests port the C# checks under their names and run a stand-in CLI (`tests/fake/cli.rs`, a `[[bin]]` that replays recordings) through the Windows `ProcessHost`; `tests/service.rs` compares a replayed run's pane with C#'s. |
| `crates/mighty-graph` | No Tauri, no OS code: the execution graph (W3). `tracker/` (C# `ExecutionGraphTracker`: a run's stream as graph blocks and graph runs, notice runs, Codex and Antigravity sessions), `run.rs` (the tracker as mighty-run's `GraphFactory`/`GraphSink`, `TrackerFactory`; mighty-graph reads mighty-run, never the other way round), the rules every Mighty view reads (`support`, `notice`), the views' models (`layout`, `camera`, `flow/` role view, `timeline`, `structure/`, `view_model`) and the host's `mighty` push (`view.rs`: `MightyPane`, `Sources`, `project`, `MightySent`, `MightyPushes`). Its tests port the graph suites under their names and read the shared fixtures; the checks that need the run engine replay recordings through mighty-run's stream parser (`tests/view_support` `replay`); what is still left is `DEFERRED.md`. |
| `crates/mighty-platform-macos` | The Mac ports in Rust, everything behind `cfg(target_os = "macos")`: `Paths`; the surfaces say terminal and browser are native (Ghostty, CEF/WKWebView) but open nothing yet. |
| `src-tauri` | The app. `main.rs` (window, startup), `host.rs` (the bridge, the state's changes and saves, the run service's events), `delta.rs` (C# `WebBridge.Flush` and `WebDelta`), `methods.rs` (the page methods), `runs.rs` (the run engine: one `RunService` with mighty-graph's tracker and the picture cache, the CLIs' runtime), `panes.rs` (the never-shown pane views: composer and cards models and pushes), `composer.rs`, `cards.rs`, `mighty.rs` (their page methods; `mighty.rs` also the `mighty` push), `platform.rs` (the one place the OS crate is chosen), `surface.rs` (`SurfaceManager`, the NativeSlot protocol and the browser pane), `frame.rs` (the frame's remaining methods: close, focus, rename, remove, the add menu, panes, the folder picker, Explorer, the text size, the resume flow), `dialogs.rs` (the page-drawn dialogs), `workspace_create.rs` (the create sheets, git through `ProcessHost`), `smoke.rs`, `smoke_frame.rs` and `smoke_runs.rs` (`--smoke-test`), `args.rs`, `logfile.rs`. |
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
| `--smoke-fake-cli <exe>` | The smoke's run steps against mighty-run's stand-in CLI (`mighty-fake-cli --replay-cli claude-stream-subagents`); every other provider reads as missing. Needs `--smoke-test`. |
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

`getLocale` (the saved `languagePreference`; "system" reads the OS's preferred UI languages in order, C# `Locale.ChosenLanguage`),
`ready`, `shellState`, `log` (to `tauri-logs/host.log`), `selectSession`, `activatePane`, `selectWorkspace`, `setTheme`,
`setSidebarCollapsed`, `setSidebarWidth`, `toggleDisclosure`, `applyLayoutPreset`, `resizeSplit`, `resetSplit`, `dockSession`,
`showDashboard`, `hideDashboard`, `dismissError`, `renameCheck` — each C#'s rule (`SelectLayoutSession`, `ActivatePane`,
`SelectWorkspace`, `ApplyLayoutPreset`, `ResizeSplit`, `DockSession`, `WorkspaceDisclosure.Toggle`, `RenameSupport`, ...). Every
change is C#'s `UpdateAsync`: normalized, then queued for saving.

The NativeSlot protocol (`surface.rs`, registered from `methods.rs` with one line): `surfaceBounds`, `surfaceAction`, and the
browser pane's `browserOpen`, `browserNavigate` (C# `BrowserAddress.Resolve`), `browserBack`, `browserForward`, `browserReload`,
`browserInstall` and C#'s `browserBounds`. A browser pane is C#'s in full (MainWindow.WebShell.Panes.cs, MainWindow.Browser.cs): nothing
starts while the setting `browserEngineEnabled` is off (read once per launch: `disabled`), the install offer without the WebView2 runtime
(`missing`; `browserInstall` downloads Microsoft's bootstrapper only then, checks its Authenticode signer and runs it), else its view opens
over the slot in its workspace's web profile (`<local>\browser\<profile>\browser-profiles\<workspaceProfileKey or workspace id>`); the
history (`BrowserHistory`) follows the engine's own navigations and every state is pushed (`browser{paneId, state}`); back and forward
are the engine's; popups are refused and downloads cancelled; script dialogs never show; a press in the page makes its pane the active one;
the picture shown while a page overlay hides the view is WebView2's `CapturePreview`.

The frame's remaining methods (`frame.rs`, one line in `methods.rs`; C# MainWindow.WebShell.Shell.cs, MainWindow.Navigation.cs,
MainWindow.ResumeSession.cs, MainWindow.SmallParity.cs): `closeSession` (the run stopped and ended, then the pane gone), `focusPane`
(`toggle` back to custom), `textSize` (Ctrl+= / Ctrl+- / Ctrl+0 over the saved setting, pushed as `text`; not saved: W6), `dismissResource`,
`renameSession` and `renameWorkspace` (the page's rename sheet; Automatic for an agent pane), `removeWorkspace` (the page's danger
confirmation, every pane's run stopped), `showInExplorer` (the folder's real path, `explorer.exe`), `pickFolder` (the OS's folder picker
owned by the main window; the folder approved for saving and added, a Claude pane in a workspace without one), `addMenu` (the entries in the
Mac's words, the remote menu, Connect), `addPaneEntry` (terminal, browser, files through `frame::OPEN_FILES`, agents with the resume flow,
the three ways to add a workspace, another workspace's entry made active first), `newPane`, and `dialogAnswer`. Every action is C#'s `Act`:
the banner cleared first, what it fails with shown there.

The page-drawn dialogs (`dialogs.rs`): a flow pushes `dialog{id, dialog}` and waits for `dialogAnswer`; the host closes one
(`dialogClose`) or updates a resume choice's checking state (`dialogUpdate`). Every dialog still open is sent again, oldest first and as it
stands, when the page calls `ready` (C# never sends one again after a reload, and its flow then waits for ever). The resume sheets
(`resumeChoice`, `resumeList`) are drawn and answered here; the folder's sessions come from `frame::RESUME` (the session history module's
`ResumeSource`: the head-only look-up, the pick from the list `resumeRows` showed, `RememberSession`), and `resumeRows` reads the open list
from `frame::open_resume`. Until it is set, an agent pane starts new at once.

Workspace create (`workspace_create.rs`; C# MainWindow.WorkspaceCreate.cs, `GitTool`, `GitCloneJob`; the rules are mighty-core's
`workspace_create`, held to native/contracts/fixtures/workspace-create.json): `workspaceCreate`, `createCheck`, `pickLocation`,
`makeFolder` (`git init` when asked), `repoName`, `gitProbe` (`git ls-remote -- <url> HEAD`, a newer one stops the older), `gitCreate`
(`git clone --progress`, pushed as `gitProgress`; a second press answers `busy`) and `gitCreateCancel` (git and everything it started
ended, what it wrote removed). git is the user's own, through `ProcessHost`, never prompting (an empty `GIT_ASKPASS`, `GCM_INTERACTIVE=never`,
BatchMode ssh unless the user has an ssh command); the sheets start at the last location, else the active workspace's parent, else home.

The composer (`composer.rs`, C# MainWindow.WebShell.Composer.cs over the never-shown `PaneView`): `composerState`, `updateDraft`
(`DesktopService.UpdateDraft`: the pane alone, saved on the schedule), `send` (`PaneView.Submit`: a busy pane queues the draft, an idle
one takes the draft and its attachments and starts the request through the run service, a start that did not happen puts them back;
answered with `SubmitReceipt.SendAnswer`: outcome, user entry, graph run, the page's client id, the composer state), `stop`
(`StopActiveRun`: the queue cancelled unless the turn only waits on background work), `queueRemove`, `queueRunNow` (바로 실행: `Here`
starts the row now, `Steer` hands it to the running turn), `queueRunNowMenu`, `installCli`, `pickAttachments` (the OS's picker through
Tauri's dialog plugin), `addAttachments`, `removeAttachment`, `dismissAttachmentError`, `attachmentPreview`, `slashCommands` (the built-ins
and the scanned catalogue), `slashAction` (new conversation, help, model, permission), `composerMenu`, `composerMenuChoose` (the row the
page showed: provider, model, effort, permission, Fast, models read again, new conversation), `runSettings`, `applyRunSettings`,
`toggleFast`, `composerToggle` (status line), `newConversation`, `setAskTarget`, `dismissAskNotice`.

The cards (`cards.rs`, C# MainWindow.WebShell.Cards.cs): `cards`, `permissionAnswer` (Allow once / Deny for the request the card shows),
`questionnaireAnswer` (`QuestionnaireAnswers`, `ValidateAnswers`; the refusal's words back), `planAnswer` (`AnswerPlanCard`: one answer
at a time, a revise Core would refuse never leaves the card), `nextActionFill`, `backgroundWork`, `statusLineTrust`.

The Mighty views (`mighty.rs`, C# MainWindow.WebShell.Mighty.cs): `mightyVisible`, `mightySetPresentation`, `mightySaveSize`,
`mightyExpand`, `mightyResultFiles`, `mightyLoadOlder`, `mightyImages` (a step's tiles as data URLs), `mightyTimelineFlip`,
`mightyLogCollapsed`, `mightyOpenDashboard`. And `imageData` (a log picture from the cache by its hash; an SVG goes as it is, where C#
draws it to PNG first) and `openUrl` (http, https, mailto through `ShellExecuteW`, as C#'s `UseShellExecute`).

### The run engine in the app

One `RunService` (mighty-run) owns the state once the host has started it (`runs.rs`): `View::update` goes through
`RunService::update` (normalized under the service's lock, then saved), and each `ServiceEvent` takes the page's copy from the service
again. Its runner makes each request's graph with mighty-graph's tracker (`TrackerFactory`: the graph run published every second and at
the end, with the notice runs) and keeps tool pictures in the picture cache (the profile's `image-cache` with `--allow-state-write`, else
`%LOCALAPPDATA%\dev.mightycode.tauri\image-cache\<profile>`). Claude runs load the mighty-bridge plugin (`claude-mods` beside the exe, else
the repository's `mods/mighty-bridge`). The CLIs are read once at start (`ProviderCatalog::get_runtime`) and a pane's provider again when
its models are asked for; until then the composer says it is checking the runtime.

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
| A typed built-in (`SubmitTypedBuiltin`), the slash palette's usage, settings, plugins and rename actions | sent as text; those actions do nothing | W6, W4 |
| The "send to" targets (`AskTargetsFor`: the installed CLIs and the ask skill) | none offered; `setAskTarget` takes only null | W5 |
| The model list's notice (`ModelListNotices.Notice`), the custom model dialog, the plugin window | none | W5, W6 |
| Background strip rows (`PlanCardSupport.BackgroundStripRows`) | the strip's line without rows | W8 |
| The Antigravity card, a terminal prompt, web-open prompts, the sign-in card, the status line | none | W5, W4, W6 |
| The session record's older requests and history words (`mightyLoadOlder`), the Ouroboros dashboard, background output tails, custom agents' definitions | empty; `mightyLoadOlder` and `mightyOpenDashboard` only mark the view | W4, W9 |
| `sessionInfo`, `guidedAction` | unimplemented | W6, W9 |

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
spelling wins here, the last of all in C#).## Identity and WebView2

- `identifier` is `dev.mightycode.tauri`, a development identity so this app never collides with the installed C# app. It must
  become the real one before shipping.
- Product name "Mighty Code", main binary `MightyCode.exe` (the 0.4.x updater replaces that name), window 1440×920 with a
  940×650 minimum, as the C# window.
- WebView2 data: `%LOCALAPPDATA%\dev.mightycode.tauri\webview\<default | the profile path as a folder name>` — never the C# app's
  `<profile>\web-profile`, and never inside a profile.
- CSP: the page's build-time meta policy (`vite.config.ts`). `vite build --mode tauri` adds `connect-src 'self' ipc:
  http://ipc.localhost` so Tauri's IPC is not blocked (WebView2 reaches it at `http://ipc.localhost`); the C# build's policy is
  unchanged. `app.security.csp` is null, so Tauri adds no policy of its own. The page's `frame-src https://*.html.mighty.invalid`
  (HTML previews) has no Tauri counterpart yet.

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
With git on PATH the gitCreate step follows: a bare repository in a temporary folder (the fixture's own git), the sheets' location that
folder, then the add menu's "Create from Git…": the address typed, the check reachable, the name `remote`, 생성, `gitProgress` pushed,
and the clone listed as a workspace (`gitCreate*`). The reload step then opens a workspace's rename sheet from the host, loads the
page again, and checks the sheet is drawn again with its name and that its answer (Esc) ends the waiting flow (`dialogResentAfterReload`).
The run steps follow (`smoke_runs.rs`, the C# smoke's `RunWebComposerSmoke`, `RunWebCardsSmoke`, `RunWebMightySmoke` and
`RunWebStepsSmoke` over a real child). With `--smoke-fake-cli` (what `smoke.ps1` passes unless `-NoRuns`): the host adds a Claude pane
in a workspace of its own (a temporary folder, in memory) shown in the Mighty diagram; the probe types a request into the composer and
presses Enter (`checks.composerSend`: the draft reached the state, the send started); the stand-in asks one Bash permission, allowed from
the page's card (`permissionCard`), and one plan, approved from the page's plan card (`planCard`: both answers reached the CLI); it then
replays claude-stream-subagents and the diagram draws the request, the sub-agents and the result with the recording's answer
(`mightyDiagram`); a second request is sent, and while its permission waits the progress panel draws a three-step plan on it (1/3: done,
current, pending) and none without it (`progressSteps`); the request is stopped from the composer (`stopRun`: the run ends stopped and its
card goes). With `--smoke-real-claude` (`smoke.ps1 -RealClaude`) the probe sends "Reply with exactly the word: ok" to the installed
`claude` CLI instead (the pane's default permission mode, writes off) and checks the run completed, the result block shows "ok" and the
CLI reported its usage (`real*`).

It writes `smoke-result.json` (`passed`, `checks`, `probe`, `browser`, `runs`, the calls it saw, the unimplemented methods) and quits with
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
