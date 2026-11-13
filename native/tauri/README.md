# Mighty Code — Rust + Tauri 2 host (skeleton)

The start of the Rust rewrite of the desktop app, Windows first. It opens one window, draws the same React page the C# app draws
(`native/windows/web`), loads the saved state and answers the page's first calls. **It never writes the state file**: every change
the page asks for stays in memory. The C# app (`native/windows/MightyCode.*`) is untouched and still the shipping app.

## Layout

| Path | What |
|---|---|
| `Cargo.toml` | The workspace: `crates/mighty-core`, `crates/mighty-platform`, `crates/mighty-platform-windows`, `crates/mighty-platform-macos`, `src-tauri`. |
| `crates/mighty-core` | No Tauri, no OS code. The state models (`model.rs`, C# `Models.cs`), loading (`store.rs`, C# `StateStore.cs`), the page normalize (`normalize.rs`), the locale catalogues (`locale.rs`, the repo's `locales/*.json` embedded), the pane layout rules (`layout.rs`, C# `PaneLayout.cs`), the frame model (`shell.rs`, C# `MainWindow.WebShell.Shell.cs`), the demo state (`demo.rs`). |
| `crates/mighty-core/examples` | `seed_demo` (a test profile from the help capture's demo), `roundtrip` (checks any state file loads and writes back without loss, read-only). |
| `crates/mighty-platform` | No OS code. The ports (traits) between the shared logic and the OS, each with its contract, a fake of each (`fake.rs`) and `Unsupported` (`unsupported.rs`), and `PlatformProfile`, the deliberate Windows/Mac differences as named values (docs/tauri-architecture.md §2.1). |
| `crates/mighty-platform-windows` | The Windows ports, everything behind `cfg(windows)`: `Paths`, `RegistryPrefs` (HKCU `Software\MightyCode`, reads), `DpapiVault` (C# `DpapiRemoteKeyVault`'s files, read and written both ways), `WebviewSurfaces` (browser panes as WebView2 child webviews), and the process layer (W1-C): `WindowsProcessHost` (C# `ChildProcess`: suspended start, kill-on-close job, hidden UTF-8 console), `command` (C# `WindowsCommand`: PATH × PATHEXT, `.cmd` through `cmd.exe /d /s /c`), `CliRunner`, `discovery`, `WindowsShellEnvironment` (C# `CliEnvironment` + the PowerShell profile capture), `user_environment`, `WindowsPtyHost` (C# `PseudoTerminal`, ConPTY) and `shell_activity`. Its tests run real processes against a stand-in child (`tests/fake/child.rs`, a `[[bin]]`) and compare with C# itself (`tests/parity`, a tiny .NET program over MightyCode.Core). The rest is `Unsupported` for now. |
| `crates/mighty-platform-macos` | The Mac ports in Rust, everything behind `cfg(target_os = "macos")`: `Paths`; the surfaces say terminal and browser are native (Ghostty, CEF/WKWebView) but open nothing yet. |
| `src-tauri` | The app. `main.rs` (window, startup), `host.rs` (the bridge), `methods.rs` (the page methods), `platform.rs` (the one place the OS crate is chosen), `surface.rs` (`SurfaceManager`, the NativeSlot protocol), `smoke.rs` (`--smoke-test`), `args.rs`, `logfile.rs`. |
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
pushes the whole `snapshot` before its own result; a page that loads again starts over. After every call the host flushes a
`delta` (panes replaced whole as `{id, session}`, `removed`, `order` when it changed, `state` when any other field changed) and a
`shell` (the frame model) when it changed.

A method the host does not have answers `ok:false, error:"unimplemented: <method>"`, is logged once to `tauri-logs/host.log`, and
is added to `tauri-logs/unimplemented.json` when the app quits (every run's names together, with the implemented ones): the
porting checklist.

### Implemented methods

`getLocale` (the saved `languagePreference`; "system" reads the OS's preferred UI languages in order, C# `Locale.ChosenLanguage`),
`ready`, `shellState`, `log` (to `tauri-logs/host.log`), `selectSession`, `activatePane`, `selectWorkspace`, `setTheme`,
`setSidebarCollapsed`, `setSidebarWidth`, `toggleDisclosure`, `applyLayoutPreset` — each C#'s rule (`SelectLayoutSession`,
`ActivatePane`, `SelectWorkspace`, `ApplyLayoutPreset`, `WorkspaceDisclosure.Toggle`, ...), on the in-memory state.

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

The frame model (`shell.rs`) ports C# `WebShellModel` for the sidebar (workspaces, disclosure, pane rows with their status word,
tone, agent mark and "· 20 days ago" meta), the header, the layout (saved tree, else the mode's preset, kept stable between models
as C#'s `layoutDefaults`; focus shows the active group) and the status bar. Not yet: tool-permission and question counts, the
delegation tree (children are listed as top-level rows), git info (the work-map button says "not a git repository" as C# does
before its first git read), runtime checks, usage, app update, dashboard, companion.

## State

- Data folder: `%APPDATA%\MightyCode`, or `--profile`. A default profile without a state file reads the one in
  `%APPDATA%\MightyClaude` or `mighty-claude` (C# copies it; this only reads).
- `workspace-state.json` version 1, as C# `Wire.Json` writes it (camelCase, nulls left out; `RunSettings` always writes
  `maxTurns`/`maxBudgetUsd`). Every field this host does not model is kept in a `#[serde(flatten)]` map and known fields stay
  absent when the file left them out, so a load and a write give back what was read; fields C# reads leniently are `Lenient<T>`,
  which keeps a wrong-typed value as it was instead of failing.
- The load is C#'s `ReadAsync` checks (8 MB, version 1 as an integer, `workspaces` and `sessions` arrays, the remote-reference
  filter of `KeptWorkspaces`). A file holding a NUL byte (what a power cut leaves) loads its `.bak`, or starts empty, as C#
  does — but the damaged file is **not** moved aside: the load reports the notice and the name C# would have given it. Any other
  unreadable file starts empty in memory with the reason in the error banner.
- The page gets the state through `normalize::for_page`, the structural part of C#'s `Normalize(restoring: true)` (valid
  workspaces and panes, running panes stopped, automatic titles from the latest request, active ids, layout modes and selections)
  with C#'s defaults filled in.

**Writes are disabled.** Nothing in this host writes the state file, its backup or anything else in the profile except
`tauri-logs/` and the smoke's result (both can be pointed elsewhere with `--log-dir` and `--smoke-out`).

## Identity and WebView2

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
