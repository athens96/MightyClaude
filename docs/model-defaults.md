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

## Mode menu label

`modeMenuLabel(provider, mode, workspace, app)` = `resolve("default", provider, mode, workspace, app)`

The permission-mode button shows the resolved model name for that mode (or "default"
when nothing is configured).

## Backward compatibility

- `AppSnapshot.version` stays `1`. Old snapshots without the `modelDefaults` field
  decode with `modelDefaults = null`, meaning all modes resolve to `"default"`.
- Same for `Workspace.modelDefaults`.
- No existing run-session behaviour changes when `modelDefaults` is `null`.

## Scope

Only Claude and Codex are in scope. Gemini has no per-mode model configuration.

## 기기 미확인 항목 (manual verification)

The following items require human visual confirmation on a real device:

- **설정 화면 모델 기본값 섹션**: 앱 설정에 "모델 기본값" 섹션이 표시되고 권한 모드별로 모델을 선택할 수 있음
- **권한 모드 메뉴 모델 이름 표시**: 권한 모드 버튼에 해당 모드의 기본 모델 이름이 함께 표시됨
- **직접 등록 모델 이름 등록 UI**: 목록에 없는 모델 이름을 직접 입력해 등록할 수 있음
- **CLI 거부 오류 표시**: 등록된 이름을 CLI가 거부할 때 오류 메시지가 그대로 표시됨 (조용히 대체되지 않음)
- **그래프 노드 모델 라벨** (macOS only): 실행 그래프의 요청 노드에 실제 사용 모델 이름이 표시됨
- **폰 블록 모델 라벨**: 폰 블록 목록에 모델 이름 라벨이 표시됨
