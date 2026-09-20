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

The macOS original reads "이 Mac의". The substitution is recorded here; no
`보류` row is needed because the reason is mechanical.

---

## Codex `/plugins`

Codex `/plugins` (`SlashCommandAction.OpenPlugins` for the Codex provider) is
filtered out of the Windows slash palette by `SlashPalette.Builtins` until the
Codex plugin screen is built. `SlashCommandCatalog.Builtins` keeps the macOS
entry; only the Windows palette hides it. The `claude plugin palette …` Core
check verifies both conditions.
