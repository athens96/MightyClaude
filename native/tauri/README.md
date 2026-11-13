# Mighty Code — Rust + Tauri 2 host (skeleton)

The start of the Rust rewrite of the desktop app, Windows first. It opens one window, draws the same React page the C# app draws
(`native/windows/web`), loads the saved state and answers the page's first calls. It saves the state byte for byte as the C# app
does, but **only when started with `--allow-state-write`**; without it every change stays in memory. The C# app
(`native/windows/MightyCode.*`) is untouched and still the shipping app.

## Layout

| Path | What |
|---|---|
| `Cargo.toml` | The workspace: `crates/mighty-core`, `crates/mighty-platform`, `crates/mighty-platform-windows`, `crates/mighty-platform-macos`, `crates/mighty-run`, `src-tauri`. |
| `crates/mighty-core` | No Tauri, no OS code. JSON exactly as C# `Wire.Json` reads and writes it (`json.rs`), .NET's times (`time.rs`), the state models (`model.rs`, `graph.rs`: C# `Models.cs`, `MightyGraph.cs`, `ExecutionGraph.cs` and the records they hold), `StateStore.Normalize` with every rule it calls (`normalize.rs`), loading, saving and the save timing (`store.rs`, C# `StateStore.cs`, `SaveSchedule`, `DesktopService`'s save queue), the locale catalogues and `LocaleKeyLeak` (`locale.rs`), the pane layout rules (`layout.rs`, C# `PaneLayout.cs`), model labels (`model_label.rs`), the frame model (`shell.rs`, C# `MainWindow.WebShell.Shell.cs`), the demo state (`demo.rs`). |
| `crates/mighty-core/examples` | `seed_demo` (a test profile from the help capture's demo), `roundtrip` (any state file loaded and saved in memory, timed; read-only), `parity` (the Rust side of `tools/parity`). |
| `tools/parity` | The C# side of the parity checks (`Parity.csproj`, a .NET 10 console over `MightyCode.Core` with `WebDelta.cs` compiled in), `run.ps1` (the corpus and the vectors compared byte for byte), `real-write.ps1` (the app's save of a real profile's copy against C#'s), and `run-replay` (C#'s `DesktopService` running a recorded Claude run through mighty-run's fake CLI, the expectation of `crates/mighty-run/tests/service.rs`). |
| `crates/mighty-platform` | No OS code. The ports (traits) between the shared logic and the OS, each with its contract, a fake of each (`fake.rs`) and `Unsupported` (`unsupported.rs`), and `PlatformProfile`, the deliberate Windows/Mac differences as named values (docs/tauri-architecture.md §2.1). |
| `crates/mighty-platform-windows` | The Windows ports, everything behind `cfg(windows)`: `Paths`, `RegistryPrefs` (HKCU `Software\MightyCode`, reads), `DpapiVault` (C# `DpapiRemoteKeyVault`'s files, read and written both ways), `WebviewSurfaces` (browser panes as WebView2 child webviews), and the process layer (W1-C): `WindowsProcessHost` (C# `ChildProcess`: suspended start, kill-on-close job, hidden UTF-8 console), `command` (C# `WindowsCommand`: PATH × PATHEXT, `.cmd` through `cmd.exe /d /s /c`), `CliRunner`, `discovery`, `WindowsShellEnvironment` (C# `CliEnvironment` + the PowerShell profile capture), `user_environment`, `WindowsPtyHost` (C# `PseudoTerminal`, ConPTY) and `shell_activity`. Its tests run real processes against a stand-in child (`tests/fake/child.rs`, a `[[bin]]`) and compare with C# itself (`tests/parity`, a tiny .NET program over MightyCode.Core). The rest is `Unsupported` for now. |
| `crates/mighty-run` | No Tauri, no OS code: the run engine (W2-A). `manager.rs` (C# `RunManager`: a pane's CLI started through `ProcessHost`, its frames read, its stdin kept for steers and answers), `providers.rs` (C# `ProviderCatalog`: every provider's arguments, Claude's and Codex's model reads, the runtime and discovery), `permissions.rs` and `presentation.rs` (C# `ToolPermissions`: `ProviderInput`, `ClaudePermissionChannel`, the card's copy), `parser.rs` and `stream.rs` (C# `OutputParser` for Claude and Codex, `ClaudeStream`), `plan_mode.rs` (C# `ClaudePlanMode`, the checklist and background-task trackers), `mods.rs` (C# `ModBridge` + `HttpHost` over hyper), `service.rs` (the run half of C# `DesktopService`, its events through a channel), `graph.rs` (`GraphFactory`/`GraphSink`, where mighty-graph's tracker plugs in), and the smaller C# classes each in its own file. Its tests port the C# checks under their names and run a stand-in CLI (`tests/fake/cli.rs`, a `[[bin]]` that replays recordings) through the Windows `ProcessHost`; `tests/service.rs` compares a replayed run's pane with C#'s. |
| `crates/mighty-platform-macos` | The Mac ports in Rust, everything behind `cfg(target_os = "macos")`: `Paths`; the surfaces say terminal and browser are native (Ghostty, CEF/WKWebView) but open nothing yet. |
| `src-tauri` | The app. `main.rs` (window, startup), `host.rs` (the bridge, the state's changes and saves), `delta.rs` (C# `WebBridge.Flush` and `WebDelta`), `methods.rs` (the page methods), `platform.rs` (the one place the OS crate is chosen), `surface.rs` (`SurfaceManager`, the NativeSlot protocol), `smoke.rs` (`--smoke-test`), `args.rs`, `logfile.rs`. |
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
browser pane's `browserOpen`, `browserNavigate` (C# `BrowserAddress.Resolve`), `browserBack`, `browserForward`, `browserReload`.

## Platform ports and native surfaces

The host reaches the OS only through the `mighty-platform` ports; `platform.rs` picks `mighty-platform-windows` or
`mighty-platform-macos` by target (the only place either is named) and hands the rest of the host a `Platform` and a
`SurfaceFactory` for the main window. The data folder and the pre-rename folders come from `Platform::paths`.

Native views over the page (docs/tauri-architecture.md §3): when the page calls `ready`, before the snapshot, the host pushes
`{kind:'host', platform, surfaces:{terminal, browser}}` (Windows: terminal `web`, browser `native`). From then on the page reports
each native slot with `surfaceBounds{paneId, surface, rect (device px), visible, occluded}` and the `SurfaceManager` opens, places,
shows and hides the platform's view there; a page reload hides every view until it is reported again, and a pane that leaves the
state closes its view. On Windows a browser pane is a Tauri child webview of the main window (`unstable` multi-webview) with its own
WebView2 data (`%LOCALAPPDATA%\dev.mightycode.tauri\browser\<profile>`), placed in physical pixels as the slot reports them; the
child webview never reaches the host (`host_post` only listens to the `main` webview, and Tauri refuses app commands from remote
origins). Back/forward state, popups, downloads, per-workspace profiles and the occlusion picture are W4.

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
Where the platform draws browsers natively, the browser-surface step follows: the host adds a browser pane in a workspace of its
own (in memory) and makes it active; a second probe types the address of a fixture page this process serves on `127.0.0.1` into
the pane's address field, waits until the native view shows it, checks the view's bounds read back from Windows (the WebView2
host window in the main window's client coordinates) against the slot in device pixels (±1 px), has the host push a page dialog
and checks the view hides, closes it and checks the view shows again (`checks.browserSurface`, details under `browser`).
It writes `smoke-result.json` (`passed`, `checks`, `probe`, `browser`, the calls it saw, the unimplemented methods) and quits with
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
