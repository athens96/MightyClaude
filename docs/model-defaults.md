# Model Defaults Contract

Cross-platform field names and resolution rules for per-provider per-permission-mode default models and registered custom model names.

Both the macOS (`MightyCore`) and Windows (`MightyClaude.Core`) implementations
follow this contract. Tests on both platforms prove the same rules.

## Field names

### AppSnapshot

| Field | Type | Default | Description |
|---|---|---|---|
| `modelDefaults` | `ModelDefaultsConfig?` | `null` | App-level defaults. `null` → all modes use CLI default ("default"). |

### Workspace

| Field | Type | Default | Description |
|---|---|---|---|
| `modelDefaults` | `ModelDefaultsConfig?` | `null` | Workspace-level override. `null` → no workspace-level override. |

### ModelDefaultsConfig

| Field | Type | Description |
|---|---|---|
| `claude` | `ProviderModeDefaults` | Per-mode defaults for Claude. |
| `codex` | `ProviderModeDefaults` | Per-mode defaults for Codex. |

### ProviderModeDefaults

| Field | Type | Description |
|---|---|---|
| `modeDefaults` | `{ [mode]: string }` | Map from permission mode → model name. Absent key or value `"default"` means use CLI default. |
| `registeredModels` | `RegisteredModelEntry[]` | User-registered custom model names for this provider. |

### RegisteredModelEntry

| Field | Type | Default | Description |
|---|---|---|---|
| `name` | `string` | — | Registered model name (validated by CoreValidation.model rule). |
| `supportsEffort` | `bool` | `false` | Whether this model supports effort levels. |
| `supportedEffortLevels` | `string[]` | `[]` | Subset of the provider's known effort levels. |

## Resolution rules

```
resolve(sessionModel, provider, permissionMode, workspaceDefaults, appDefaults):
  1. if sessionModel ≠ "default"  →  return sessionModel
  2. for source in [workspaceDefaults, appDefaults]:
       name = source?.claude_or_codex.modeDefaults[permissionMode]
       if name ≠ null and name ≠ "default"  →  return name
  3. return "default"   (CLI decides)
```

Priority summary: **explicit selection > workspace default > app default > CLI default**

A model selected with `/model` persists across permission-mode changes. Changing the
permission mode or the model default takes effect on the next request.

## Mode menu label

`modeMenuLabel(provider, mode, workspace, app)` = `resolve("default", provider, mode, workspace, app)`

The permission-mode button shows the resolved model name for that mode (or `"default"`
when nothing is configured).

## Graph node / phone block label

```
nodeModelLabel(cliReportedModel, configuredModel):
  1. if cliReportedModel is non-empty  →  return cliReportedModel          (actual)
  2. if configuredModel ≠ "default"   →  return configuredModel + " · 설정" (configured, unconfirmed)
  3. return nil                        (no label; CLI decided, model unknown)
```

The phone block list projects the same label that the Mac attaches to the graph request
node (`nodeModelLabel`). There is no separate phone-side model concept.

## Registered name validation

Before a name is saved or passed to the CLI, the following rules apply in order:

1. **Trim** leading and trailing whitespace (applied silently before the checks below).
2. **Non-empty** — reject the trimmed name if it is empty.
3. **Not reserved** — reject the name `"default"` (case-sensitive).
4. **Character set** — name must match `CoreValidation.model`:
   - max 200 characters
   - pattern: `^[a-zA-Z0-9][a-zA-Z0-9._:/@\[\]-]*$`
5. **No provider-duplicate** — reject if another entry with the same name already exists
   under the same provider.

Registration rejects with an explicit error message; it never silently normalises or
substitutes a different name.

When a registered name is deleted:
- Every mode row (app default, workspace override) for that provider that referenced
  the deleted name is reverted to `"default"`.
- The number of reverted rows is reported to the caller.
- Open run sessions that had the name set keep it; they will see a CLI error on the
  next run.

## Effort format for registered models

`RegisteredModelEntry` carries the same effort shape as a `ModelOption` from the
catalog:

| Field | Type | Default | Description |
|---|---|---|---|
| `supportsEffort` | `bool` | `false` | Whether this model accepts an effort argument. |
| `supportedEffortLevels` | `string[]` | `[]` | Supported effort values; must be a subset of the provider's known effort levels. Empty means no effort restriction beyond `supportsEffort`. |

At run time, `ProviderOptions.effortLevels` checks registered entries when the
catalog does not contain the model name, so the registered effort shape feeds the
same effort-validation path used by catalog models.

If the CLI later adds the same model name to its catalog, the catalog entry takes
precedence over the registered entry.

## Where the rules are applied

The following call sites in production code use the resolution logic and validation rules.

### macOS (`native/macos/Sources/MightyClaude/`)

| File | Location | Rule applied |
|---|---|---|
| `AppStore.swift` | `start(_:)` ~line 671 | `ModelDefaultsResolution.resolve` — computes the configured model for the new request from the session explicit model + workspace/app `providerModeDefaults`; result passed to `beginGraphRun(configuredModel:)` |
| `AppStore.swift` | `start(_:)` ~line 692, 700 | `ModelDefaultsResolution.resolve` — second resolve call for the `StartRunRequest.model` field sent to the CLI; `["--model", name]` is omitted when the result is `"default"` |
| `AppStore.swift` | `start(_:)` ~line 704 | `CoreValidation.validateSelection(request, catalog:, registeredModels:)` — validates the resolved model name, passing provider registered models so custom names pass the official-name gate |
| `AppStore.swift` | effort-filter loop ~line 497 | `ProviderOptions.effortLevels(provider:model:catalog:registeredModels:)` — resets effort when the stored effort is no longer valid for the new model |
| `AppStore.swift` | Codex effort probe ~line 963 | `ProviderOptions.effortLevels(provider:model:catalog:registeredModels:)` — probes effort support for the Codex default model |
| `AppStore+ModelDefaults.swift` | `removeRegisteredModel(_:provider:)` ~line 27 | `ModelDefaultsResolution.removeRegisteredModel` — reverts all mode rows that referenced the deleted name and returns the revert count |
| `SessionPaneView.swift` | permission-mode menu ~line 303 | `ModelDefaultsResolution.modeMenuLabel` — resolves the display model for each mode row and pill without an explicit session model |
| `SessionPaneView.swift` | effort picker ~line 25 | `ProviderOptions.effortLevels(provider:model:catalog:registeredModels:)` — feeds the effort picker with valid levels for the selected model including registered custom names |

### macOS (`native/macos/Sources/MightyCore/`)

| File | Location | Rule applied |
|---|---|---|
| `MightyGraph.swift` | `beginGraphRun(input:id:configuredModel:)` ~line 283 | `ModelDefaultsResolution.nodeModelLabel(cliReportedModel: nil, configured:)` — sets the initial graph node label from the configured (resolved) model |
| `MightyGraph.swift` | `recordGraph(_:)` ~line 299 | `ModelDefaultsResolution.nodeModelLabel(cliReportedModel: reported, configured:)` — updates the label with the model name the CLI actually reports |
| `ProcessRunner.swift` | run loop ~line 281 | `CoreValidation.validateSelection(request, catalog:, registeredModels:)` — validates on the real run path so registered custom names are accepted by the runner |
| `Remote/MobileRemoteSupport.swift` | phone block projection ~line 211 | `run.nodeModelLabel` — projects the graph node label to the phone block list |

### Windows (`native/windows/MightyClaude.Core/`)

The Windows Core resolves defaults via the same `ModelDefaultsResolution` type and exposes section rows and mutation methods through the section model; WinUI renders them in `BuildModelDefaultsSection`.

## Backward compatibility

- `AppSnapshot.version` stays `1`. Old snapshots without the `modelDefaults` field
  decode with `modelDefaults = null`, meaning all modes resolve to `"default"`.
- Same for `Workspace.modelDefaults`.
- No existing run-session behaviour changes when `modelDefaults` is `null`.
- `ProviderModeDefaults` fields `modeDefaults` and `registeredModels` both default to
  empty when absent, so partial payloads are safe.

## Scope

Only Claude and Codex are in scope. Gemini has no per-mode model configuration.
Windows has no execution graph, so `nodeModelLabel` is only exercised on macOS and
is not part of the Windows settings or snapshot contract.

## 기기 미확인 항목 (manual verification)

The following items require human visual confirmation on a real device:

- **설정 화면 모델 기본값 섹션**: 앱 설정에 "모델 기본값" 섹션이 표시되고 권한 모드별로 모델을 선택할 수 있음
- **권한 모드 메뉴 모델 이름 표시**: 권한 모드 버튼에 해당 모드의 기본 모델 이름이 함께 표시됨
- **직접 등록 모델 이름 등록 UI**: 목록에 없는 모델 이름을 직접 입력해 등록할 수 있음
- **CLI 거부 오류 표시**: 등록된 이름을 CLI가 거부할 때 오류 메시지가 그대로 표시됨 (조용히 대체되지 않음)
- **그래프 노드 모델 라벨** (macOS only): 실행 그래프의 요청 노드에 실제 사용 모델 이름이 표시됨
- **폰 블록 모델 라벨**: 폰 블록 목록에 모델 이름 라벨이 표시됨
