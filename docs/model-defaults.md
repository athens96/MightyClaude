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

| File | Location | Rule applied |
|---|---|---|
| `ModelDefaultsResolution.cs` | `BuildPaneRequest(pane, workspace, appDefaults, input)` | Resolves the effective model from `pane.Model`, provider, permission mode, workspace and app defaults; attaches the provider's registered models to `StartRunRequest.RegisteredModels` |
| `RunManager.cs` | `ExecuteAsync` effort check | `ProviderCatalog.Efforts(provider, model, catalog, request.RegisteredModels)` — validates the effort setting against the catalog or a registered entry's saved levels |
| `MainWindow.Composer.cs` | `AddOverflowSettings` 추론 강도 submenu | `ProviderCatalog.Efforts(provider, model, catalog, registeredModels)` — feeds the overflow effort submenu with valid levels including registered custom names |
| `MainWindow.cs` | `RefreshMenus` 권한 submenu and current-mode text | `ModelDefaultsResolution.ModeMenuLabel` — each mode item shows `"<label> · <model>"` when the mode resolves to a non-default model |

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

The Settings sections on both platforms edit **app-level defaults only**; workspace-level
override editing is deferred (the `Workspace.modelDefaults` field is still resolved at
run time).

## Model and token display on the graph

macOS only. The execution graph shows which models each block used and how many tokens.

### Attribution rules

- **Block capsule** (`ModelUsageFormat.blockCapsule`): shows the block total with the models that produced it, e.g. `"12.3K · Opus 5.5"` or `"12.3K · Opus 5.5 +1"` for multiple models. Before the first response arrives the capsule shows the configured model label (with the `graph.nodeModel.configuredSuffix` locale key).
- **Block capsule tooltip** (`ModelUsageFormat.blockCapsuleHelp`): per-model token breakdown with input / output / cache detail.
- **Activity line suffix** (`ModelUsageFormat.activitySuffix`): the model short name and compact token count of the response that called the activity. When one response called several activities, only the **first** activity line in that response shows the model and token count; the other lines show a locale-keyed "same response" marker (`usage.modelUsage.sameResponse`) with no numbers. Summing the numbers shown on activity lines and responses that called no activity equals the block total exactly.
- **Task/Agent activity line**: shows the calling response's model and tokens first (same first-line rule as any other activity line), then the subagent block total and its models, e.g. "Opus 5.5 · 150 · sub 3.0K · Sonnet 4.6".
- **Codex responses**: use the run's configured model, marked as configured (`markedAsConfigured = true` in `GraphResponseRecord`).
- **Re-sent response**: a response resent with the same message id replaces the prior record; tokens are never double-counted.

### Short model names

Short display names come from the CLI model catalog (`ModelOption.displayName`) when the model id matches `ModelOption.value` or `ModelOption.resolvedModel`; otherwise the raw model id is used verbatim. On screen, every `ModelUsageFormat` call receives the session provider catalog (from `providerRuntime(session.provider, workspaceId:).modelCatalog.models`), so catalog short names are used wherever a model id is rendered.

### Formatter

`ModelUsageFormat` (in `MightyCore`) is the single source of every string drawn. All user-visible copy goes through `L()` locale keys (`usage.modelUsage.*`); no Korean literals appear in `ModelUsageFormat` itself.

## 기기 미확인 항목 (manual verification)

The following items require human visual confirmation on a real device:

- **설정 화면 모델 기본값 섹션**: 앱 설정에 "모델 기본값" 섹션이 표시되고 권한 모드별로 모델을 선택할 수 있음
- **권한 모드 메뉴 모델 이름 표시**: 권한 모드 버튼에 해당 모드의 기본 모델 이름이 함께 표시됨
- **직접 등록 모델 이름 등록 UI**: 목록에 없는 모델 이름을 직접 입력해 등록할 수 있음
- **등록 모델 effort 지원 토글** (macOS/Windows): 모델 등록 행에 effort 지원 스위치(macOS: Toggle, Windows: CheckBox/ToggleSwitch)가 있고, 켜면 제공자의 알려진 effort 수준 목록이 나타남
- **등록 모델 effort 수준 선택** (macOS/Windows): effort 지원이 켜진 상태에서 수준을 하나 이상 선택해야 등록이 되고, 수준을 하나도 고르지 않으면 오류가 표시됨; effort 지원을 끄면 수준 목록 없이 저장됨
- **CLI 거부 오류 표시**: 등록된 이름을 CLI가 거부할 때 오류 메시지가 그대로 표시됨 (조용히 대체되지 않음)
- **그래프 노드 모델 라벨** (macOS only): 실행 그래프의 요청 노드에 실제 사용 모델 이름이 표시됨
- **폰 블록 모델 라벨**: 폰 블록 목록에 모델 이름 라벨이 표시됨
- **블록 캡슐 모델 표시** (macOS only): 블록 헤더의 토큰 캡슐이 토큰 합계와 함께 모델 이름을 표시함 (예: "2.5K · Opus 5.5 +1")
- **활동 줄 모델·토큰 표시** (macOS only): 각 활동 줄에 해당 응답의 모델 이름과 토큰이 표시됨
- **같은 응답 마커** (macOS only): 같은 응답이 여러 활동을 호출한 경우, 첫 번째 활동 줄만 모델과 토큰을 표시하고 나머지는 "same response" 마커를 표시함
- **서브에이전트 활동 줄** (macOS only): Task/Agent 활동 줄은 해당 응답의 모델·토큰을 먼저 표시하고, 이어서 서브에이전트 블록 합계와 모델을 표시함 (예: "Opus 5.5 · 150 · 하위 3.0K · Sonnet 4.6")
- **화면의 모델 짧은 이름** (macOS only): 활동 줄·블록 캡슐·툴팁에 표시되는 모델 이름이 세션 제공자의 카탈로그 짧은 이름으로 나오는지 확인; 카탈로그에 없는 id는 원본 id를 그대로 표시함
