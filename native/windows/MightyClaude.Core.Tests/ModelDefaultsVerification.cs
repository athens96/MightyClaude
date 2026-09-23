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

    // ── Section model rows ──────────────────────────────────────────────────

    internal static Task ModelDefaultsRows()
    {
        // With null config all modes resolve to "default"
        var nullRows = ModelDefaultsResolution.SectionRows(null);
        Check(nullRows.All(r => r.CurrentValue == "default"), "null config must yield all 'default' rows");
        Check(nullRows.All(r => r.Choices.Single() == "default"), "null config rows must offer only 'default' as choice");
        // Check row counts: claude=5 modes (plan,manual,acceptEdits,auto,fullAccess), codex=3
        var claudeRows = nullRows.Where(r => r.Provider == "claude").ToList();
        var codexRows = nullRows.Where(r => r.Provider == "codex").ToList();
        Check(claudeRows.Count == 5, "claude must have 5 mode rows");
        Check(codexRows.Count == 3, "codex must have 3 mode rows");

        // With config the current value reflects mode defaults
        var config = new ModelDefaultsConfig
        {
            Claude = new() { ModeDefaults = new() { ["manual"] = "claude-sonnet-5" }, RegisteredModels = [new("my-claude")] },
            Codex = new() { ModeDefaults = new() { ["acceptEdits"] = "gpt-6-astra" } }
        };
        var rows = ModelDefaultsResolution.SectionRows(config);
        Check(rows.Single(r => r.Provider == "claude" && r.Mode == "manual").CurrentValue == "claude-sonnet-5", "claude manual row must reflect config");
        Check(rows.Single(r => r.Provider == "codex" && r.Mode == "acceptEdits").CurrentValue == "gpt-6-astra", "codex acceptEdits row must reflect config");
        // Registered model appears in choices
        Check(rows.Where(r => r.Provider == "claude").All(r => r.Choices.Contains("my-claude")), "registered model must appear in every claude row's choices");
        Check(rows.Where(r => r.Provider == "codex").All(r => !r.Choices.Contains("my-claude")), "claude's registered model must not appear in codex choices");
        return Task.CompletedTask;
    }

    // ── AddRegisteredModel ─────────────────────────────────────────────────

    internal static Task ModelDefaultsAdd()
    {
        var config = new ModelDefaultsConfig();
        // Success: valid name
        var err = ModelDefaultsResolution.AddRegisteredModel("acme/custom-v2", "claude", ref config);
        Check(err is null, "adding a valid name must return null error");
        Check(config.Claude.RegisteredModels.Any(m => m.Name == "acme/custom-v2"), "registered model must appear in config");
        // Duplicate: same provider
        var dupErr = ModelDefaultsResolution.AddRegisteredModel("acme/custom-v2", "claude", ref config);
        Check(dupErr is not null, "adding a duplicate name must return an error");
        // Codex is independent
        var codexErr = ModelDefaultsResolution.AddRegisteredModel("acme/custom-v2", "codex", ref config);
        Check(codexErr is null, "same name under a different provider must succeed");
        // Whitespace is trimmed
        var trimConfig = new ModelDefaultsConfig();
        Check(ModelDefaultsResolution.AddRegisteredModel("  trimmed-name  ", "claude", ref trimConfig) is null, "whitespace must be trimmed before registration");
        Check(trimConfig.Claude.RegisteredModels[0].Name == "trimmed-name", "stored name must be trimmed");
        return Task.CompletedTask;
    }

    internal static Task ModelDefaultsInvalidName()
    {
        var config = new ModelDefaultsConfig();
        Check(ModelDefaultsResolution.AddRegisteredModel("", "claude", ref config) is not null, "empty name must be rejected");
        Check(ModelDefaultsResolution.AddRegisteredModel("   ", "claude", ref config) is not null, "whitespace-only name must be rejected");
        Check(ModelDefaultsResolution.AddRegisteredModel("default", "claude", ref config) is not null, "'default' must be rejected as reserved");
        Check(ModelDefaultsResolution.AddRegisteredModel(new string('a', 201), "claude", ref config) is not null, "201-char name must be rejected");
        Check(ModelDefaultsResolution.AddRegisteredModel("bad name with spaces", "claude", ref config) is not null, "name with spaces must be rejected");
        Check(config.Claude.RegisteredModels.Count == 0, "no name must be registered after all rejections");
        return Task.CompletedTask;
    }

    // ── RemoveRegisteredModel ──────────────────────────────────────────────

    internal static Task ModelDefaultsRemoveReverts()
    {
        var config = new ModelDefaultsConfig
        {
            Claude = new()
            {
                ModeDefaults = new() { ["manual"] = "my-model", ["auto"] = "my-model", ["plan"] = "claude-sonnet-5" },
                RegisteredModels = [new("my-model"), new("other-model")]
            }
        };
        var reverted = ModelDefaultsResolution.RemoveRegisteredModel("my-model", "claude", ref config);
        Check(reverted == 2, "removing a name referenced by two modes must revert 2 rows");
        Check(!config.Claude.RegisteredModels.Any(m => m.Name == "my-model"), "removed model must not remain in registered list");
        Check(config.Claude.RegisteredModels.Any(m => m.Name == "other-model"), "other model must remain in registered list");
        Check(config.Claude.ModeDefaults["manual"] == "default", "manual row must revert to 'default'");
        Check(config.Claude.ModeDefaults["auto"] == "default", "auto row must revert to 'default'");
        Check(config.Claude.ModeDefaults["plan"] == "claude-sonnet-5", "plan row (pointing to different model) must remain unchanged");
        // Removing an absent name reverts 0 rows
        var noop = ModelDefaultsResolution.RemoveRegisteredModel("nonexistent", "claude", ref config);
        Check(noop == 0, "removing an absent name must revert 0 rows");
        // Codex is unaffected
        var codexConfig = new ModelDefaultsConfig { Codex = new() { ModeDefaults = new() { ["manual"] = "my-model" }, RegisteredModels = [new("my-model")] } };
        ModelDefaultsResolution.RemoveRegisteredModel("my-model", "claude", ref codexConfig);
        Check(codexConfig.Codex.RegisteredModels.Any(m => m.Name == "my-model"), "removing from claude must not touch codex");
        return Task.CompletedTask;
    }

    internal static Task ModelDefaultsOldSnapshot()
    {
        // A snapshot without ModelDefaults must decode as null (already proven in AppSnapshotModelDefaultsRoundTrips)
        // and SectionRows must handle null gracefully with all "default" values
        var rows = ModelDefaultsResolution.SectionRows(null);
        Check(rows.Count > 0, "SectionRows must return rows even for null config");
        Check(rows.All(r => r.CurrentValue == "default"), "all rows must be 'default' for null config");
        // Removing from null-based config must return 0 without crashing
        var config = new ModelDefaultsConfig();
        var reverted = ModelDefaultsResolution.RemoveRegisteredModel("ghost", "claude", ref config);
        Check(reverted == 0, "removing from empty config must return 0");
        return Task.CompletedTask;
    }

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
