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

**Where the order lives** (`MightyClaude.Core/SettingsSections.cs`):

```csharp
public sealed record SettingsSectionSlot(string Id, string? WindowsTitle)
{
    public bool OnWindows => WindowsTitle is not null;
}

public static class SettingsSections
{
    public static IReadOnlyList<SettingsSectionSlot> MacOrder { get; }      // every macOS slot, in order
    public static IReadOnlyList<SettingsSectionSlot> Windows { get; }       // MacOrder.Where(OnWindows)
    public static IReadOnlyList<string> WindowsTitles { get; }              // the headings, in order
}
```

The slot list is in Core, not in WinUI, so the macOS order is a plain fact a
Mac-side check can read — `settings sections …` in `Core.Tests` proves the order,
the omissions and the smoke rules without building WinUI.

**Where the controls live** (`MightyClaude.WinUI/MainWindow.Settings.cs`):

```csharp
internal sealed record SettingsSection(string Title, Func<StackPanel> Build);

internal List<SettingsSection> GetSettingsSections() =>
    [.. SettingsSections.Windows.Select(slot => new SettingsSection(slot.WindowsTitle!, BuilderFor(slot.Id)))];

private Func<StackPanel> BuilderFor(string slotId) => slotId switch
{
    SettingsSections.Display   => BuildDisplaySection,
    SettingsSections.CliUpdate => BuildCliUpdateSectionFromState,
    SettingsSections.Providers => BuildProvidersSection,
    SettingsSections.AppInfo   => BuildAppInfoSection,
    _ => throw new InvalidOperationException("no Settings builder registered for slot " + slotId),
};
```

**How to register a section** — two small edits, neither of which touches another
section:

1. In Core, give the slot a `WindowsTitle` in `MacOrder` (it is already listed
   with `null`). Its position in the macOS order is already correct.
2. In WinUI, add one arm to `BuilderFor` returning the builder for its controls.

`Build` is called each time Settings opens; return a fresh `StackPanel` with
controls wired to `service.UpdateAsync`.

**Sections shown today** (the rest of `MacOrder` carries `null` until its feature
arrives on Windows):

| Slot | Windows title | macOS |
|------|---------------|-------|
| `display` | `화면` | `Section("화면")` |
| `cliUpdate` | `CLI 업데이트` (`CliUpdateStrings.SectionTitle`) | `CLIUpdateSettingsSection()` |
| `providers` | `이 PC의 CLI` | the unnamed provider `Section` |
| `appInfo` | `앱 정보` | `Section("앱 정보")` |

Absent until their feature arrives: `remoteConnection`, `styles`, `components`,
`mobileRemote`, `companion`, `cliAccounts`, `claudeMods`, `appUpdate`.

**Smoke** — `SettingsSectionsSmoke.RunAsync` (Core) takes the headings and the
result-row statuses the screen actually built, refuses anything that is not the
registered order or drops a fixture row, flips the CLI auto-update switch, reads
it back from saved state and always puts the original value back. WinUI only
builds the screen and hands Core what it rendered, under the smoke key
`settingsSections`.

**OS-bound substitution** (recorded here, not a 보류 row):

| Windows | macOS | Reason |
|---------|-------|--------|
| `"이 PC의 CLI"` | `"이 Mac의 CLI"` | Windows devices are not Macs. |

This mirrors the `SectionDescription` substitution in `CliUpdateStrings` (`이 PC에`
for `이 Mac에`), which is the only other OS-bound substitution in the CLI update
copy.
