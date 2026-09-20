# Windows plugin list: model shapes and decided differences

Plugin window for a workspace's installed and available plugins, shared by both
providers: Claude (run-pane menu · `/plugin`) and Codex (run-pane menu ·
`/plugins`). The only two mutations available are the two macOS has: install one
plugin from the catalog, and refresh the registered marketplaces.

**Install**: Claude offers a three-scope picker (local · project · user, macOS
labels) and an install button per catalog row. Codex installs at user level only
and offers no scope choice. Arguments are passed as a list through the shared
runner; the plugin id and scope are validated against the values the browser just
returned before they are used. A remote workspace never runs any operation.

**Marketplace refresh**: refreshes all registered Git-backed marketplaces (Codex:
Git-only; Claude: all registered sources) that the current filter selects, one at
a time. The button is disabled when nothing refreshable is registered.

**While an operation runs**: the dialog shows a progress label and a cancel
button, and the close button is disabled — exactly as macOS disables it. At most
one operation runs at a time. After the operation the list is reloaded.

**No uninstall, enable, disable or marketplace-add**: those mutations are not in
the macOS app, so they are not added here either.

OS-bound difference: the scope picker is a WinUI ComboBox instead of a Picker.
No difference in copy, visible behaviour or scope — macOS words used as-is.

---

## Model types

These types live in `MightyClaude.Core` and are shared unchanged by the Codex
plugin feature and the marketplace feature. Their public surface must not change
when those features are added; they extend the types with new methods, not new
fields.

### `ClaudeInstalledPlugin`

| Field | Type | Notes |
|-------|------|-------|
| `PluginId` | `string` | `"name@marketplace"` — validated as two identifiers |
| `Name` | `string` | The part before `@` |
| `Marketplace` | `string?` | The part after `@`; null for a direct install |
| `Version` | `string?` | Bounded to `VersionCap` characters |
| `Scope` | `string` | `user` / `project` / `local` / `managed` / `session` |
| `Enabled` | `bool?` | Null when the CLI did not report the flag |
| `ProjectPath` | `string?` | Present for `project` and `local` scopes |
| `Description` | `string` | Filled from the catalog when the CLI row omits it |
| `Errors` | `IReadOnlyList<string>` | Bounded to 16 messages × `MessageCap` chars each |
| `Notes` | `IReadOnlyList<string>` | Same bounds |
| `Id` | `string` (computed) | Length-prefixed `PluginId\|Scope\|ProjectPath` — no field can forge another row's key |

### `ClaudeCatalogPlugin`

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `string` | `"name@marketplace"` |
| `Name` | `string` | |
| `Description` | `string` | |
| `Marketplace` | `string` | Must match a registered marketplace |
| `Version` | `string?` | |
| `SourceKind` | `string` | `github` / `git` / `npm` / `url` / `directory` / … |

### `ClaudePluginMarketplace`

```csharp
public sealed record ClaudePluginMarketplace(string Name, string SourceKind = "unknown");
```

### `ClaudePluginSnapshot`

| Field | Type | Notes |
|-------|------|-------|
| `Status` | `string` | `ready` / `missing` / `unsupported` / `failed` / `cancelled` / `remote` |
| `Detail` | `string` | The macOS sentence for the status |
| `CliVersion` | `string?` | Shown in the window header |
| `Installed` | `IReadOnlyList<ClaudeInstalledPlugin>` | |
| `Available` | `IReadOnlyList<ClaudeCatalogPlugin>` | |
| `Marketplaces` | `IReadOnlyList<ClaudePluginMarketplace>` | |
| `UpdatedAt` | `string?` | ISO-8601 timestamp |
| `DiagnosticOutput` | `string` | Bounded CLI output behind the diagnostics disclosure |

Default `Status` is `"failed"` so a zero-value snapshot is never silently ready.

### `ClaudePluginOperationResult`

```csharp
public sealed record ClaudePluginOperationResult(string Status, string Detail, string Output = "");
```

Nothing in this feature produces one. The type exists so the marketplace and
Codex features can add their install/remove/enable/disable methods without
reshaping the shared models.

---

## OS-bound substitutions

| Windows | macOS | Reason |
|---------|-------|--------|
| `"이 PC의 설치는 변경하지 않습니다."` | `"이 Mac의 설치는 변경하지 않습니다."` | Windows devices are not Macs. |
| `"이 PC의 Codex 설치 목록과 마켓플레이스 목록입니다."` | `"이 Mac의 Codex 설치 목록과 마켓플레이스 목록입니다."` | The same word, the same reason, in `CodexPluginStrings.FooterNote`. |

The full sentence (`PluginStrings.DetailRemote`) reads:
> 원격 워크스페이스의 플러그인은 해당 호스트에서 관리하세요. **이 PC의** 설치는 변경하지 않습니다.

The macOS original reads "이 Mac의". The substitution is recorded here and the
`Claude 플러그인 목록` row in `docs/windows-parity.md` is marked `확인 필요` for it.
It is a word substitution, not a behaviour change, so it is not a `보류` row.

The two sentences a remote workspace shows are the macOS originals, unchanged:
`원격 워크스페이스에서는 관리할 수 없습니다.` and
`원격 컴퓨터의 MightyClaude에서 플러그인을 관리하세요.`

---

## Codex plugins

The same plugin window serves both Claude and Codex. The provider string
`"codex"` selects a `CodexPluginReader` in `ShowPluginBrowser`; the reader
implements `IPluginReader` the same way `ClaudePluginReader` does and returns
the same `ClaudePluginSnapshot` type. `CodexPluginStrings` holds only what Codex
says differently; everything both providers say stays in `PluginStrings` and is
reused unchanged, so the Claude list behaves exactly as before.

`/plugins` reaches this window from the Codex palette and the run pane menu.
`SlashPalette.Builtins` no longer filters anything and
`SlashPalette.UnavailableActions` stays empty, so no app action is left out of
the Windows palette (`docs/windows-slash-commands.md`).

### How the Codex list is read

| # | Command | Timeout | Output cap |
|---|---------|---------|------------|
| 1 | `codex --version` | 4s | 160 displayed characters |
| 2 | `codex plugin list --help` | 4s | probe: must name `--json` and `--available` |
| 3 | `codex plugin add --help` | 4s | probe: must name `--json` |
| 4 | `codex plugin marketplace list --help` | 4s | probe: must name `--json` |
| 5 | `codex plugin marketplace upgrade --help` | 4s | probe: must name `--json` |
| 6 | `codex plugin list --json --available` | 20s | 8 MiB |
| 7 | `codex plugin marketplace list --json` | 20s | 512 KiB |

Reading `--help` changes nothing, and the read path itself only ever runs
commands 6 and 7. The Codex CLI does not export the many disable variables
Claude does; only `GIT_TERMINAL_PROMPT=0` is set, exactly as macOS does, so the
user's own `CODEX_HOME` still decides which user-level registry is read.

Codex plugins are user level only. The parser records every installed row as
`user`, and `ClaudePluginBrowser.SupportedScopes` keeps a row claiming another
scope off the Codex window.

### Codex answer shapes

`ClaudePluginSupport.ParseCodexSnapshot` mirrors `CodexPluginService.parseSnapshot`
and differs from the Claude parser in four ways, all of them the CLI's own shape:

- the marketplace answer is `{"marketplaces": [...]}`, not a bare array;
- a marketplace's kind comes from `marketplaceSource.sourceType`, and the Codex
  whitelist is `local` / `remote` / `github` / `git` / `directory`;
- an available row is identified by `pluginId` + `name` + `marketplaceName` +
  `installed` + `enabled`, and a row whose `installPolicy` is neither
  `AVAILABLE` nor `INSTALLED_BY_DEFAULT` is left out and counted in
  `DetailRestrictedSuffix`;
- an installed row must carry `"installed": true`.

Unlike the Claude parser, a row the Codex CLI cannot describe is a `failed`
status rather than a skipped row, because macOS throws there. A malformed or
oversized answer is never a ready-but-empty list. A CLI that still answered but
warned on stderr keeps its warning and gains `DetailWarningSuffix`.

### Capability probe

Before running either list command, the reader probes the `--help` output of all
four plugin subcommands for the flags this screen and the later marketplace
feature would use, in the order `CodexPluginService.command` probes them. A
probe that fails, times out or does not name a flag becomes
`ClaudePluginStatus.Unsupported` with `CodexPluginStrings.DetailUnsupported`,
and the list commands are never run against that build.

The version number itself never gates the screen: this CLI feature is still
moving, so the help output is the honest answer to "does this build support
it". Probing all four — including the two the marketplace feature will use —
keeps a Windows verdict identical to the macOS one. (The Claude reader is
different: Claude's plugin JSON has a known first release, so it gates on
`>= 2.1.268` exactly as `ClaudePluginService` does.)

### Codex status to sentence

| Status | When | `CodexPluginStrings` |
|--------|------|----------------------|
| `ready` | both commands answered | `DetailReady` (always shown, even when ready) |
| `ready` | marketplace list empty | `DetailNoMarketplaces` |
| `missing` | no `codex` found on PATH | `DetailMissingCli` |
| `unsupported` | a probe flag is absent | `DetailUnsupported` |
| `failed` | the list command failed | `DetailListingFailed` |
| `failed` | version could not be read | `DetailUnknownVersion` |
| `failed` | the marketplace command failed | `PluginStrings.DetailMarketplacesFailed` |
| `failed` | a run timed out, or the runner threw | `PluginStrings.DetailIncomplete` |
| `failed` | malformed JSON or past a cap | `PluginStrings.DetailMalformed` |
| `failed` | workspace path invalid / gone | `PluginStrings.DetailInvalidWorkspace` / `DetailMissingWorkspace` |
| `cancelled` | a read requested after the window closed | `PluginStrings.DetailCancelled` |
| `remote` | remote workspace | `PluginStrings.DetailRemote` |

Install, `plugin marketplace add` and `plugin marketplace upgrade` belong to the
marketplace feature and have no copy and no control here.

### OS-bound string substitution

`CodexPluginStrings.FooterNote` reads `이 PC의` where the macOS string reads
`이 Mac의`. This substitution is noted in `docs/windows-parity.md` and is the
only word changed from the macOS copy (`ClaudePluginView.swift`). Every other
`CodexPluginStrings` constant is the macOS literal, verified by
`CodexPluginVerification.StringsMatchMacOS`.

### What changes between Claude and Codex in the shared window

| Item | Claude | Codex |
|------|--------|-------|
| Title | `Claude 플러그인` | `Codex 플러그인` |
| Scopes shown | local, project, user, managed, session | user only |
| Footer sentence | `PluginStrings.FooterNote` | `CodexPluginStrings.FooterNote` |
| Marketplace help | docs link | `CodexPluginStrings.MarketplaceHelp` sentence |
| Status line when ready | hidden | `CodexPluginStrings.DetailReady` always shown |
| CLI environment | 8 disable vars + `GIT_TERMINAL_PROMPT=0` | `GIT_TERMINAL_PROMPT=0` only |
| CLI version gate | `>= 2.1.268` | capability probe (help-output flags) |

---

## How the list is read

Only the installed Claude CLI's own plugin subcommands, run in the workspace
folder through the shared one-shot runner (`ICliRunner`,
`docs/windows-settings-groundwork.md`). None of them changes anything.

| # | Command | Timeout | Output cap |
|---|---------|---------|------------|
| 1 | `claude --version` | 4s (`min(4, read timeout)`) | 160 displayed characters |
| 2 | `claude plugin list --json --available` | 20s | 8 MiB |
| 3 | `claude plugin marketplace list --json` | 20s | 512 KiB |

The environment macOS forces is forced here too: `DISABLE_AUTOUPDATER`,
`DISABLE_TELEMETRY`, `DISABLE_ERROR_REPORTING`,
`CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL`,
`CLAUDE_CODE_DISABLE_BACKGROUND_TASKS`, `CLAUDE_CODE_SKIP_PROMPT_HISTORY` and
`GIT_TERMINAL_PROMPT=0`. A caller's `FORCE_AUTOUPDATE_PLUGINS` is removed, so
reading the list can never update a plugin behind the user's back.

## Status to sentence

| Status | When | `PluginStrings` |
|--------|------|-----------------|
| `ready` | all three answered and parsed | `DetailReady`, or `DetailNoMarketplaces` when none is registered |
| `missing` | no `claude` found on PATH | `DetailMissingCli` |
| `unsupported` | CLI older than 2.1.268 | `DetailUnsupported` |
| `failed` | the list command failed | `DetailListingFailed` |
| `failed` | the marketplace command failed | `DetailMarketplacesFailed` |
| `failed` | a run timed out, or the runner threw | `DetailIncomplete` |
| `failed` | the version could not be read | `DetailUnknownVersion` |
| `failed` | malformed JSON or past a cap | `DetailMalformed` |
| `failed` | workspace path invalid / gone | `DetailInvalidWorkspace` / `DetailMissingWorkspace` |
| `cancelled` | a read requested after the window closed | `DetailCancelled` |
| `remote` | remote workspace | `DetailRemote` |

A malformed or oversized answer never becomes a ready-but-empty list: it is
`failed`, and the window shows the reload copy.

## Decided differences in OS-bound mechanism

Each was decided without asking; the reason is the last column.

| Item | macOS | Windows | Reason |
|------|-------|---------|--------|
| A second request while one read runs | `busy` status and `다른 플러그인 작업이 진행 중입니다.` | joins the running read and gets the same answer | A read changes nothing, so there is no reason to refuse the second caller. This is a visible difference, so it also has a `보류` row in `docs/windows-parity.md`. |
| Finding the CLI | PATH entry + `claude` | PATH entry (max 64) + `claude.cmd`, `claude.exe`, `claude.bat`, no extension | An npm install on Windows is `claude.cmd`. |
| Window kind | separate 760×620 sheet | `ContentDialog`, body 700 wide, list 380 tall | WinUI has no sheet; this matches the rename dialog. |
| Diagnostics output | SwiftUI `DisclosureGroup` | a button with the same copy that folds the text away | WinUI has no `DisclosureGroup`. |
| Monospaced text | `.monospaced` | `FontFamily("Consolas")` | The same choice the other Windows screens make. |
| Finding the Codex CLI | PATH entry + `codex` | PATH entry (max 64) + `codex.cmd`, `codex.exe`, `codex.bat`, no extension | An npm install on Windows is `codex.cmd`. |
| The Codex empty-marketplace sentence | a SwiftUI `Text` under the empty copy | a `TextBlock` with the automation id `codex-plugin-marketplace-help` | WinUI needs an id for the smoke run to read it the way a user reads the screen. |

## What the window shows

Title `Claude 플러그인`, then the workspace name, its path and the CLI version.
Two tabs, `설치됨 {count}` and `마켓플레이스 {count}`. A search box over name and
description, and a marketplace filter whose first entry is `전체`. A
`목록 새로고침` button that runs the same three reads again. Installed rows carry
the name, version, `활성`/`비활성`/`상태 미확인`, description,
`{marketplace or 직접 설치} · {scope label}`, the project path, and any errors and
notes. Catalog rows carry the name, version, description (or
`설명이 제공되지 않았습니다.`) and `{marketplace} · {source kind}`. Scope labels are
`로컬 · 나만`, `프로젝트 · 공유`, `사용자 · 전체`, `관리자 관리`. The window closes
with `닫기`.

There is no install button, no scope picker and no marketplace refresh: those
belong to the marketplace feature.

## Wired into the running app

| Opened from | Code |
|-------------|------|
| the run pane's `···` menu entry `Claude 플러그인` | `MainWindow.cs` `MoreMenu` → `OpenPluginBrowser` |
| the `/plugin` slash command | `MainWindow.SlashPalette.cs` `PerformSlashAction` → `OpenPluginBrowser` |
| the run pane's `···` menu entry `Codex 플러그인` | `MainWindow.cs` `MoreMenu` → `OpenPluginBrowser` (the entry is built for `claude` and `codex`) |
| the `/plugins` slash command | `MainWindow.SlashPalette.cs` `PerformSlashAction` → `OpenPluginBrowser(pane.Provider)` |

The two mutations are not wired through the smoke harness. `MainWindow` owns one
`PluginOperations` (Core, `PluginOperations.cs`) as a field, built at app start
by its parameterless constructor — the one that carries the real shared
`CliRunner` with the macOS 8 MiB listing cap. `ShowPluginBrowser` takes both the
runner it builds the provider's reader from and the two mutation calls from that
field, so `설치` and `마켓플레이스 새로고침` really start the installed CLI's own
plugin command in the running app. That object also keeps the app-wide
one-at-a-time gate: with the Claude and the Codex window both open, a second
operation is refused with `다른 플러그인 작업이 진행 중입니다.` instead of starting a
second CLI run, the way one macOS service serialises them.

The smoke run does not replace it — it swaps only the *reader*
(`smokeReaderFactory`) for `FakeMarketplaceReader`, so the fake never reaches a
process and the object the real app owns is left exactly as it is.

## Checks

`ClaudePluginVerification.cs` holds the `claude plugin …` checks. They drive a
fake `ICliRunner` over temporary folders: no real claude, codex, gemini, npm or
winget process starts, no network is touched and the real user profile is never
read.

Core also decides what counts as a changing control.
`ClaudePluginSupport.AutomationId` builds the window's automation ids and
`ClaudePluginSupport.NamesAChange` reads an id one dash-separated word at a time,
calling it a change when a word is `install`, `uninstall`, `enable`, `disable`,
`update`, `scope`, `refresh`, `remove` or `add`. Reading whole words is what
keeps the installed tab (`tab-installed`) from being mistaken for an install
button. A Core check runs every id in the window's source through that rule, so
a control added later is judged too.

`CodexPluginVerification.cs` holds the `codex plugin …` checks, driven by a fake
`ICliRunner` that answers `--version`, the four `--help` probes and the two
`--json` list commands. It asserts that no call without `--help` ever names
`add`, `upgrade`, `install`, `remove`, `enable`, `disable` or `update`: reading
the help of `plugin add` is still only reading.

`ClaudePluginSupport.NamesAChange` gained `upgrade`, the word the Codex
marketplace feature will use (`codex plugin marketplace upgrade`).

The GUI smoke run also records `codexPluginList`: it drives the same real dialog
under the Codex title with a Codex fixture through the tabs, the marketplace
filter, the search box and two reloads, reads back the sentence an empty
registry shows and the sentence a CLI without the JSON plugin commands produces,
and puts back the two hooks it set.

The GUI smoke run records `claudePluginList`: it drives the real dialog with a
fixture snapshot through the tabs, the marketplace filter, the search box and a
reload, reads back the sentence a missing CLI produces and the two sentences a
remote workspace shows, and puts back the two hooks it set.

New saved-state fields: none. The snapshot `Version` stays 1.

---

## Where this stands

### Claude list

Done and on `main`. Unchanged by the Codex work apart from two shared pieces it
gained: `IPluginReader`, which `ClaudePluginReader` now implements so the window
can hold either reader, and `ClaudePluginSupport.NamesAChange`, which gained the
word `upgrade`. Its own Core checks still pass, so the list behaves as before.

### Codex list

Done and on `main` (`ae5625a` read path, `0cea2a3` window, `9ad3c73` tab count).

Present: the shared window under the title `Codex 플러그인`, the two tabs, the
search box, the marketplace filter, `목록 새로고침`, the diagnostics fold, the
`codex --version` line, the user-only scope, `CodexPluginStrings.FooterNote`,
`CodexPluginStrings.MarketplaceHelp`, and the status sentence that stays visible
even when the read succeeded. Reached from the run pane's `···` menu and from
`/plugins`, on the real events, not only under `--smoke-test`.

Absent, and deliberately so — this screen is read-only: no install, remove,
enable or disable button, no install-scope picker, and no
`codex plugin marketplace add` or `upgrade`. Those belong to the marketplace
feature and have rows 23 and 24 in `docs/windows-parity.md` as `보류`.

### What has been verified, and where

| Claim | How it was checked |
|-------|--------------------|
| The read changes nothing | `CodexPluginVerification.cs` asserts that no call without `--help` ever names `add`, `upgrade`, `install`, `remove`, `enable`, `disable` or `update` |
| A CLI without the flags is explained, not used | the capability-probe check drives a fake `--help` that omits `--json` |
| Malformed, oversized, failed and timed-out answers become statuses | four separate checks; none of them yields a ready-but-empty list |
| The Korean copy is the macOS copy | `CodexPluginVerification.StringsMatchMacOS`, with the one recorded `이 PC의` substitution |
| The window is wired into the running app, not just the smoke check | `codex plugin window is a real WinUI surface wired into the app` reads `MainWindow.cs` and `MainWindow.SlashPalette.cs` |
| No app action is left out of the palette | `SlashPalette.UnavailableActions` is asserted empty |

13 `codex plugin …` checks, inside a Core suite of 205 that passes on this Mac
with `dotnet run --project native/windows/MightyClaude.Core.Tests
--artifacts-path /tmp/mc-artifacts`. No real `claude`, `codex`, `gemini`, `npm`
or `winget` process starts, no network is touched, and the real user profile is
never read: every check uses the fake `ICliRunner` over a temporary folder.

### Saved state and the freeze

New saved-state fields: none. The snapshot `Version` stays 1, so `StateStore`
never resets a user's state over this feature.

Every file this feature touched is inside `native/windows/**` or
`docs/windows-*.md`, and `scripts/check-style-freeze.sh` passes. The CI job's
own freeze step (`Check the manifest-only freeze after the tag`) passes too.

One CI note, so nobody reads it as this feature's doing: the `macos` job is red
at the `Test Swift core and loopback remote execution` step. It is red the same
way at `ecbfb86`, the commit before any Codex plugin work, and at a commit that
changed nothing but `.md` files. The cause is in `native/macos/**`, which this
Seed's allow-list does not let this work touch.

### Still open

The on-device pass. Every screen item is marked `기기 미확인` in
`docs/windows-screen-checklist.md`, because no Windows machine has run this
build; the Core checks and the GUI smoke run are what stands behind it so far.
The `완료` mark on the `Codex 플러그인` parity row is set by the main session
once it has read both Windows CI jobs.
