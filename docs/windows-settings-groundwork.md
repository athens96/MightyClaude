# Windows Settings Groundwork

Describes the three shared foundations that the Settings screen and the CLI update
feature introduce. The six features that follow reuse them without a review gap,
so every decision is recorded here.

---

## Foundation 1 — CLI Runner (`ICliRunner`)

**Interface** (`MightyClaude.Core/CliRunner.cs`):

```csharp
public interface ICliRunner
{
    Task<CliRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null);
}

public sealed record CliRunResult(int ExitCode, string Output, string Error, bool TimedOut);
```

**Rules**:
- Never builds a shell command line from text — always passes `executable` and
  `arguments` as separate values.
- Caps captured output at `CliRunner.OutputCapBytes` (1 MiB) per stream before
  returning; the caller is not exposed to unbounded strings.
- On timeout or cancellation, kills the entire Windows Job Object process group so
  no child process is left behind.
- One redaction point: tests inject a `FakeRunner` that records calls without
  starting a real process, so arguments and output never reach a log unredacted in
  production unless the caller chooses to log them.
- The public surface of `ICliRunner` contains no CLI-update type; it is
  feature-agnostic.

---

## Foundation 2 — Saved Preferences

Every new `AppSnapshot` field that stores a user preference follows these rules:

| Rule | Rationale |
|------|-----------|
| Default is the off/null/zero value | A file written before the field existed loads cleanly with the field absent. |
| `Version` stays 1 | `StateStore` resets all saved state when `Version` is not 1; additive fields avoid that. |
| A malformed JSON token falls back to null, never throws | `LenientNullableBoolConverter` implements this for `bool?` fields. |
| Sessions and workspaces survive a malformed new field | The decoder reads all fields independently; one bad field cannot corrupt the rest. |

**Fields added by this work** (all in `AppSnapshot`, `MightyClaude.Core/Models.cs`):

| Field | Type | Default | Serialised as |
|-------|------|---------|---------------|
| `AutoUpdateCLIs` | `bool?` | `null` (off) | `"autoUpdateCLIs"` — absent when null |

---

## Foundation 3 — Section Registration

**Shape** (`MightyClaude.WinUI/MainWindow.Settings.cs`):

```csharp
internal sealed record SettingsSection(string Title, Func<StackPanel> Build);
```

**How to register** — add one line to the collection literal in `GetSettingsSections()`:

```csharp
internal List<SettingsSection> GetSettingsSections() =>
[
    new("화면",                       BuildDisplaySection),
    new(CliUpdateStrings.SectionTitle, BuildCliUpdateSectionFromState),
    new("이 PC의 CLI",                 BuildProvidersSection),
    new("앱 정보",                     BuildAppInfoSection),
    // ← add new section here, in the macOS slot order
];
```

`Build` is called each time Settings opens; return a fresh `StackPanel` with controls
wired to `service.UpdateAsync`. The registration does not touch any other section.

**Section order** — Windows shows sections in the same relative order as macOS
(`SettingsViews.swift`). Sections whose feature is not yet on Windows are simply
absent; they are added here when their feature arrives.

**OS-bound substitution** (recorded here, not a 보류 row):

| Windows | macOS | Reason |
|---------|-------|--------|
| `"이 PC의 CLI"` | `"이 Mac의 CLI"` | Windows devices are not Macs. |

This mirrors the `SectionDescription` substitution in `CliUpdateStrings` (`이 PC에`
for `이 Mac에`), which is the only other OS-bound substitution in the CLI update
copy.
