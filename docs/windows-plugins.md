# Windows Claude plugin list: model shapes and decided differences

Read-only view of a workspace's installed and available Claude plugins, shown
in a window opened from the run-pane menu and from the `/plugin` slash command.
Nothing here installs, removes, enables, disables or updates a plugin, and
nothing adds or refreshes a marketplace.

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

The full sentence (`PluginStrings.DetailRemote`) reads:
> 원격 워크스페이스의 플러그인은 해당 호스트에서 관리하세요. **이 PC의** 설치는 변경하지 않습니다.

The macOS original reads "이 Mac의". The substitution is recorded here and the
`Claude 플러그인 목록` row in `docs/windows-parity.md` is marked `확인 필요` for it.
It is a word substitution, not a behaviour change, so it is not a `보류` row.

The two sentences a remote workspace shows are the macOS originals, unchanged:
`원격 워크스페이스에서는 관리할 수 없습니다.` and
`원격 컴퓨터의 MightyClaude에서 플러그인을 관리하세요.`

---

## Codex `/plugins`

Codex `/plugins` (`SlashCommandAction.OpenPlugins` for the Codex provider) is
filtered out of the Windows slash palette by `SlashPalette.Builtins` until the
Codex plugin screen is built. `SlashCommandCatalog.Builtins` keeps the macOS
entry; only the Windows palette hides it. The `claude plugin palette …` Core
check verifies both conditions.


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

The GUI smoke run records `claudePluginList`: it drives the real dialog with a
fixture snapshot through the tabs, the marketplace filter, the search box and a
reload, reads back the sentence a missing CLI produces and the two sentences a
remote workspace shows, and puts back the two hooks it set.

New saved-state fields: none. The snapshot `Version` stays 1.
