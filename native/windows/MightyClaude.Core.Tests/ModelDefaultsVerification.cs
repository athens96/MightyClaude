using System.Text.Json;
using MightyClaude.Core;

/// Windows counterpart to macOS ModelDefaultsTests.swift.
/// Proves the same resolution rules documented in docs/model-defaults.md.
internal static class ModelDefaultsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // ── Resolution rule: explicit model > "default" → mode default ─────────

    internal static Task ExplicitModelWinsOverModeDefault()
    {
        var appDefaults = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5" } }
        };
        var result = ModelDefaultsResolution.Resolve("claude-opus-5", "claude", "manual", null, appDefaults);
        Check(result == "claude-opus-5", "explicit model must win over mode default");
        return Task.CompletedTask;
    }

    internal static Task ExplicitModelIgnoresWorkspaceAndAppDefaults()
    {
        var workspace = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["auto"] = "claude-sonnet-5" } }
        };
        var app = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["auto"] = "claude-fable-5-1" } }
        };
        var result = ModelDefaultsResolution.Resolve("my-company/claude-custom", "claude", "auto", workspace, app);
        Check(result == "my-company/claude-custom", "explicit model must ignore workspace and app defaults");
        return Task.CompletedTask;
    }

    // ── Resolution rule: "default" session → app mode default ──────────────

    internal static Task DefaultSessionUsesAppModeDefault()
    {
        var app = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5", ["auto"] = "claude-opus-5", ["plan"] = "claude-fable-5-1" } }
        };
        Check(ModelDefaultsResolution.Resolve("default", "claude", "manual", null, app) == "claude-sonnet-5", "manual default");
        Check(ModelDefaultsResolution.Resolve("default", "claude", "auto", null, app) == "claude-opus-5", "auto default");
        Check(ModelDefaultsResolution.Resolve("default", "claude", "plan", null, app) == "claude-fable-5-1", "plan default");
        return Task.CompletedTask;
    }

    internal static Task NoDefaultsReturnDefault()
    {
        foreach (var mode in new[] { "manual", "plan", "acceptEdits", "auto", "fullAccess" })
            Check(ModelDefaultsResolution.Resolve("default", "claude", mode, null, null) == "default", "claude " + mode + " with no defaults must return 'default'");
        foreach (var mode in new[] { "manual", "acceptEdits", "onRequest", "fullAccess" })
            Check(ModelDefaultsResolution.Resolve("default", "codex", mode, null, null) == "default", "codex " + mode + " with no defaults must return 'default'");
        return Task.CompletedTask;
    }

    // ── Resolution rule: workspace override > app default ──────────────────

    internal static Task WorkspaceOverrideBeatsAppDefault()
    {
        var app = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5" } } };
        var workspace = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["manual"] = "claude-opus-5" } } };
        Check(ModelDefaultsResolution.Resolve("default", "claude", "manual", workspace, app) == "claude-opus-5", "workspace must beat app default");
        return Task.CompletedTask;
    }

    internal static Task WorkspaceDefaultValueFallsThroughToAppDefault()
    {
        var app = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5" } } };
        // workspace explicitly records "default" for "manual" → falls through to app
        var workspace = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["manual"] = "default" } } };
        Check(ModelDefaultsResolution.Resolve("default", "claude", "manual", workspace, app) == "claude-sonnet-5", "workspace 'default' must fall through to app default");
        return Task.CompletedTask;
    }

    internal static Task WorkspaceOverrideOnlyAppliesToItsMode()
    {
        var app = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["plan"] = "claude-sonnet-5" } } };
        var workspace = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["auto"] = "claude-opus-5" } } };
        Check(ModelDefaultsResolution.Resolve("default", "claude", "auto", workspace, app) == "claude-opus-5", "workspace auto");
        Check(ModelDefaultsResolution.Resolve("default", "claude", "plan", workspace, app) == "claude-sonnet-5", "app plan");
        Check(ModelDefaultsResolution.Resolve("default", "claude", "manual", workspace, app) == "default", "neither sets manual");
        return Task.CompletedTask;
    }

    // ── Mode menu label = resolved model for that mode ──────────────────────

    internal static Task ModeMenuLabelIsResolvedForMode()
    {
        var app = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5", ["auto"] = "claude-opus-5" } },
            Codex = new() { ModeDefaults = new() { ["manual"] = "gpt-6-astra" } }
        };
        Check(ModelDefaultsResolution.ModeMenuLabel("claude", "manual", null, app) == "claude-sonnet-5", "claude manual label");
        Check(ModelDefaultsResolution.ModeMenuLabel("claude", "auto", null, app) == "claude-opus-5", "claude auto label");
        Check(ModelDefaultsResolution.ModeMenuLabel("codex", "manual", null, app) == "gpt-6-astra", "codex manual label");
        Check(ModelDefaultsResolution.ModeMenuLabel("claude", "plan", null, app) == "default", "unset mode label is 'default'");
        return Task.CompletedTask;
    }

    internal static Task ModeMenuLabelRespectsWorkspaceOverApp()
    {
        var app = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5" } } };
        var workspace = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["manual"] = "claude-opus-5" } } };
        Check(ModelDefaultsResolution.ModeMenuLabel("claude", "manual", workspace, app) == "claude-opus-5", "mode menu label must respect workspace over app");
        return Task.CompletedTask;
    }

    internal static Task ExplicitSelectionSurvivesPermissionModeChanges()
    {
        var app = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5", ["plan"] = "claude-fable-5-1", ["acceptEdits"] = "claude-opus-5", ["auto"] = "claude-opus-5", ["fullAccess"] = "claude-sonnet-5" } }
        };
        foreach (var mode in new[] { "manual", "plan", "acceptEdits", "auto", "fullAccess" })
        {
            Check(ModelDefaultsResolution.Resolve("claude-haiku-4-5-20251001", "claude", mode, null, app) == "claude-haiku-4-5-20251001",
                "explicit model must survive mode change for " + mode);
            Check(ModelDefaultsResolution.ModeMenuLabel("claude", mode, null, app) == app.Claude.ModeDefaults[mode],
                "mode menu label for " + mode + " must reflect that mode's own default");
        }
        return Task.CompletedTask;
    }

    // ── Provider isolation: Codex ≠ Claude ─────────────────────────────────

    internal static Task CodexProviderUsesCodexDefaults()
    {
        var app = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5" } },
            Codex = new() { ModeDefaults = new() { ["manual"] = "gpt-6-astra" } }
        };
        Check(ModelDefaultsResolution.Resolve("default", "codex", "manual", null, app) == "gpt-6-astra", "codex must use codex defaults");
        Check(ModelDefaultsResolution.Resolve("default", "claude", "manual", null, app) == "claude-sonnet-5", "claude must use claude defaults");
        return Task.CompletedTask;
    }

    // ── AppSnapshot carries ModelDefaults field; old snapshot stays Version 1 ─

    internal static Task AppSnapshotModelDefaultsRoundTrips()
    {
        var config = new ModelDefaultsConfig
        {
            Claude = new()
            {
                ModeDefaults = new() { ["manual"] = "claude-sonnet-5", ["auto"] = "claude-opus-5" },
                RegisteredModels = [new("my-claude", true, ["high", "max"])]
            },
            Codex = new() { ModeDefaults = new() { ["manual"] = "gpt-6-astra" } }
        };
        var snapshot = new AppSnapshot { ModelDefaults = config };
        var json = JsonSerializer.Serialize(snapshot, Wire.Json);
        var decoded = JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)!;
        Check(decoded.ModelDefaults is not null, "ModelDefaults must survive round-trip");
        Check(decoded.ModelDefaults!.Claude.ModeDefaults["manual"] == "claude-sonnet-5", "claude manual after round-trip");
        Check(decoded.ModelDefaults.Claude.RegisteredModels[0].Name == "my-claude", "registered model name after round-trip");
        Check(decoded.ModelDefaults.Claude.RegisteredModels[0].SupportsEffort, "supportsEffort after round-trip");
        Check(decoded.ModelDefaults.Codex.ModeDefaults["manual"] == "gpt-6-astra", "codex manual after round-trip");
        return Task.CompletedTask;
    }

    internal static Task OldSnapshotWithoutModelDefaultsDecodesAsNullVersionStays1()
    {
        var json = """{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":252}""";
        var snapshot = JsonSerializer.Deserialize<AppSnapshot>(json, Wire.Json)!;
        Check(snapshot.ModelDefaults is null, "old snapshot must decode ModelDefaults as null");
        Check(snapshot.Version == 1, "old snapshot version must stay 1");
        // Null ModelDefaults means all modes resolve to "default"
        foreach (var mode in new[] { "manual", "plan", "acceptEdits", "auto", "fullAccess" })
            Check(ModelDefaultsResolution.Resolve("default", "claude", mode, null, null) == "default",
                "null ModelDefaults must resolve all modes to 'default'");
        return Task.CompletedTask;
    }

    internal static Task WorkspaceModelDefaultsRoundTrips()
    {
        var config = new ModelDefaultsConfig { Claude = new() { ModeDefaults = new() { ["plan"] = "claude-fable-5-1" } } };
        var workspace = new Workspace { ModelDefaults = config };
        var json = JsonSerializer.Serialize(workspace, Wire.Json);
        var decoded = JsonSerializer.Deserialize<Workspace>(json, Wire.Json)!;
        Check(decoded.ModelDefaults is not null, "workspace ModelDefaults must survive round-trip");
        Check(decoded.ModelDefaults!.Claude.ModeDefaults["plan"] == "claude-fable-5-1", "workspace claude plan after round-trip");
        return Task.CompletedTask;
    }

    internal static Task OldWorkspaceWithoutModelDefaultsDecodesAsNull()
    {
        var json = """{"id":"abc","name":"test","path":"/tmp/test","createdAt":"2026-09-22T00:00:00Z"}""";
        var workspace = JsonSerializer.Deserialize<Workspace>(json, Wire.Json)!;
        Check(workspace.ModelDefaults is null, "old workspace must decode ModelDefaults as null");
        return Task.CompletedTask;
    }

    // ── Settings section is registered ─────────────────────────────────────

    internal static Task ModelDefaultsSectionIsRegistered()
    {
        var slot = SettingsSections.MacOrder.SingleOrDefault(s => s.Id == SettingsSections.ModelDefaults);
        Check(slot is not null, "ModelDefaults slot must exist in MacOrder");
        Check(slot!.OnWindows, "ModelDefaults must be shown on Windows");
        Check(SettingsSections.Windows.Any(s => s.Id == SettingsSections.ModelDefaults),
            "ModelDefaults must appear in the Windows list");
        // The title is non-empty and comes from the locale
        Check(!string.IsNullOrEmpty(ModelDefaultsStrings.SectionTitle), "ModelDefaults section title must be non-empty");
        // ModelDefaults slot sits after Providers in MacOrder
        var macIds = SettingsSections.MacOrder.Select(s => s.Id).ToList();
        Check(macIds.IndexOf(SettingsSections.ModelDefaults) > macIds.IndexOf(SettingsSections.Providers),
            "ModelDefaults slot must appear after Providers in MacOrder");
        return Task.CompletedTask;
    }
}
