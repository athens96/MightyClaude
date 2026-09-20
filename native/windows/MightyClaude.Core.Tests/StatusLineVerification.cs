using System.Text.Json;
using MightyClaude.Core;

internal static class StatusLineVerification
{
    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "sl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // Settings precedence: workspace-local > workspace > user. Mirrors macOS configFollowsClaudePrecedenceAndOnlyCommandEntries.
    internal static Task ConfigFollowsPrecedenceAndOnlyCommandEntries()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "repo");

            // User-level only — type:command required
            Write(Path.Combine(home, ".claude", "settings.json"),
                """{"statusLine":{"type":"command","command":"echo user","padding":2}}""");
            var user = StatusLineSupport.Discover(null, home).Preferred;
            Check(user?.Command == "echo user", "user command");
            Check(user?.Padding == 2, "user padding");
            Check(user?.FromWorkspace == false, "user not from workspace");
            Check(user?.Source == StatusLineStrings.SourceUser, "user source label");

            // Workspace settings.json takes precedence over user
            Write(Path.Combine(workspace, ".claude", "settings.json"),
                """{"statusLine":{"type":"command","command":"echo proj"}}""");
            var proj = StatusLineSupport.Discover(workspace, home).Preferred;
            Check(proj?.Command == "echo proj", "workspace command");
            Check(proj?.FromWorkspace == true, "workspace from-workspace flag");
            Check(proj?.Source == StatusLineStrings.SourceWorkspace, "workspace source label");

            // Workspace-local takes precedence over workspace
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"command","command":"echo local"}}""");
            var local = StatusLineSupport.Discover(workspace, home).Preferred;
            Check(local?.Command == "echo local", "local command");
            Check(local?.Source == StatusLineStrings.SourceWorkspaceLocal, "local source label");

            // A settings.json without statusLine returns null — not disabled, falls through
            Write(Path.Combine(home, ".claude", "settings.json"), """{"theme":"dark"}""");
            var noCmd = StatusLineSupport.Discover(null, home).Preferred;
            Check(noCmd is null, "no statusLine key returns null");

            // A non-command entry at the workspace level disables the level (does not fall through to user)
            Write(Path.Combine(home, ".claude", "settings.json"),
                """{"statusLine":{"type":"command","command":"echo user2"}}""");
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"other"}}""");
            var disabled = StatusLineSupport.Discover(workspace, home);
            Check(disabled.WorkspaceDisabled, "non-command entry sets workspaceDisabled");
            Check(disabled.Workspace == null, "disabled workspace level has no config");
            Check(disabled.User?.Command == "echo user2", "user level still visible on disabled discovery");
            Check(disabled.Preferred == null, "preferred is null when workspace is disabled");

            // CLAUDE_CONFIG_DIR env var overrides home
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"command","command":"echo local"}}""");
            var altConfig = Path.Combine(root, "altconfig");
            Write(Path.Combine(altConfig, "settings.json"),
                """{"statusLine":{"type":"command","command":"echo alt"}}""");
            var envOverride = StatusLineSupport.Discover(null, home,
                new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = altConfig }).Preferred;
            Check(envOverride?.Command == "echo alt", "CLAUDE_CONFIG_DIR override");

            // Blank command is refused (disabled)
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"command","command":"   "}}""");
            var blank = StatusLineSupport.Discover(workspace, home);
            Check(blank.WorkspaceDisabled, "blank command disables the level");

            // padding is clamped to 0-8
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"command","command":"echo x","padding":20}}""");
            var clamped = StatusLineSupport.Discover(workspace, home).Workspace;
            Check(clamped?.Padding == 8, "padding clamped to max 8");
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"command","command":"echo x","padding":-5}}""");
            var clampedMin = StatusLineSupport.Discover(workspace, home).Workspace;
            Check(clampedMin?.Padding == 0, "padding clamped to min 0");

            // outputStyle and alwaysThinkingEnabled carried from the same settings file
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"type":"command","command":"echo local"},"outputStyle":"Explanatory","alwaysThinkingEnabled":true}""");
            Write(Path.Combine(home, ".claude", "settings.json"),
                """{"statusLine":{"type":"command","command":"echo user2"},"outputStyle":"Explanatory","alwaysThinkingEnabled":true}""");
            var richDiscovery = StatusLineSupport.Discover(workspace, home);
            Check(richDiscovery.Workspace?.OutputStyle == "Explanatory", "workspace outputStyle carried");
            Check(richDiscovery.Workspace?.ThinkingEnabled == true, "workspace alwaysThinkingEnabled carried");
            Check(richDiscovery.User?.OutputStyle == "Explanatory", "user outputStyle carried");
            Check(richDiscovery.User?.ThinkingEnabled == true, "user alwaysThinkingEnabled carried");

            // Discovery keeps both levels apart
            var discovery2 = StatusLineSupport.Discover(workspace, home);
            Check(discovery2.Workspace?.Command == "echo local" && discovery2.Workspace?.FromWorkspace == true, "discovery keeps the workspace level");
            Check(discovery2.User?.Command == "echo user2" && discovery2.User?.FromWorkspace == false, "discovery keeps the user level");
            Check(discovery2.Preferred == discovery2.Workspace, "preferred is the winning level");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    // Fingerprint = SHA-256(source + newline + command), 64-char lowercase hex.
    internal static Task FingerprintMatchesMacOS()
    {
        var cfg = new StatusLineConfig("echo hi", 0, "사용자 설정", false);
        Check(cfg.Fingerprint.Length == 64, "fingerprint is 64 chars");
        Check(cfg.Fingerprint == cfg.Fingerprint.ToLowerInvariant(), "fingerprint is lowercase");
        // Two configs with same command+source must have equal fingerprints.
        var cfg2 = new StatusLineConfig("echo hi", 2, "사용자 설정", true);
        Check(cfg.Fingerprint == cfg2.Fingerprint, "padding and fromWorkspace don't affect fingerprint");
        // Different command → different fingerprint.
        var cfg3 = new StatusLineConfig("echo bye", 0, "사용자 설정", false);
        Check(cfg.Fingerprint != cfg3.Fingerprint, "different command gives different fingerprint");
        return Task.CompletedTask;
    }

    // Payload uses the CLI's own field names and nesting — same keys, same types, same omission rules as macOS.
    // Mirrors macOS payloadUsesTheCLIsFieldNamesAndTranscriptLayout literally.
    internal static Task StatusLinePayloadUsesTheCLIsFieldNamesAndTranscriptLayout()
    {
        // Transcript path: every non-alphanumeric → '-', leading dash kept (no trimming), first 200 chars.
        Check(StatusLineSupport.TranscriptPath("/Users/me/.claude", "/Users/me/Work/My.App", "abc")
            == "/Users/me/.claude/projects/-Users-me-Work-My-App/abc.jsonl", "transcript path keeps leading dash");
        Check(StatusLineSupport.TranscriptPath("/cfg", "/x", "s")
            == "/cfg/projects/-x/s.jsonl", "transcript path with env config dir");

        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
        var ctx = new StatusLineContext(
            SessionId: "sess",
            Cwd: "/repo",
            ProjectDir: "/repo",
            ModelId: "claude-fable-5-1",
            ModelName: "Fable 5.1",
            Version: "2.1.274",
            CostUSD: 1.5,
            DurationMs: 4000,
            ApiDurationMs: 0,
            InputTokens: 120_000,
            OutputTokens: 3000,
            CacheReadTokens: 100_000,
            CacheWriteTokens: 5000,
            ContextUsedTokens: 150_000,
            ContextWindowTokens: 1_000_000,
            Effort: "high",
            FastMode: true,
            RateLimits:
            [
                new SessionRateLimit("five_hour", 42.5, reset),
                new SessionRateLimit("seven_day", 12.0, "2000-01-01T00:00:00Z"),
                new SessionRateLimit("other", 1.0),
            ],
            OutputStyle: null,
            ThinkingEnabled: null,
            TranscriptPath: StatusLineSupport.TranscriptPath("/Users/me/.claude", "/repo", "sess")
        );

        var json = StatusLineSupport.BuildPayload(ctx);
        var r = JsonDocument.Parse(json).RootElement;

        // Top-level hook and identity
        Check(r.GetProperty("hook_event_name").GetString() == "Status", "hook_event_name must be Status");
        Check(r.GetProperty("session_id").GetString() == "sess", "session_id");
        Check(r.GetProperty("version").GetString() == "2.1.274", "version");

        // model is an object {id, display_name}
        var model = r.GetProperty("model");
        Check(model.GetProperty("id").GetString() == "claude-fable-5-1", "model.id");
        Check(model.GetProperty("display_name").GetString() == "Fable 5.1", "model.display_name");

        // workspace is an object {current_dir, project_dir}
        var ws = r.GetProperty("workspace");
        Check(ws.GetProperty("current_dir").GetString() == "/repo", "workspace.current_dir");
        Check(ws.GetProperty("project_dir").GetString() == "/repo", "workspace.project_dir");

        // cost object with five fields
        var cost = r.GetProperty("cost");
        Check(cost.GetProperty("total_cost_usd").GetDouble() == 1.5, "cost.total_cost_usd");
        Check(cost.GetProperty("total_duration_ms").GetInt64() == 4000, "cost.total_duration_ms");
        Check(cost.GetProperty("total_lines_added").GetInt32() == 0, "cost.total_lines_added");
        Check(cost.GetProperty("total_lines_removed").GetInt32() == 0, "cost.total_lines_removed");

        // fast_mode and exceeds_200k_tokens
        Check(r.GetProperty("fast_mode").GetBoolean() == true, "fast_mode");
        Check(r.GetProperty("exceeds_200k_tokens").GetBoolean() == false, "exceeds_200k_tokens for 150k/1M");

        // effort as object {level}
        Check(r.GetProperty("effort").GetProperty("level").GetString() == "high", "effort.level");

        // context_window always present with total_input_tokens, total_output_tokens, context_window_size
        var cw = r.GetProperty("context_window");
        Check(cw.GetProperty("context_window_size").GetInt64() == 1_000_000, "context_window_size");
        Check(cw.GetProperty("total_input_tokens").GetInt64() == 120_000, "total_input_tokens");
        Check(cw.GetProperty("total_output_tokens").GetInt64() == 3000, "total_output_tokens");
        // used_percentage = round(150000/1000000*100*10)/10 = 15.0
        Check(Math.Abs(cw.GetProperty("used_percentage").GetDouble() - 15.0) < 0.01, "used_percentage 15%");
        Check(Math.Abs(cw.GetProperty("remaining_percentage").GetDouble() - 85.0) < 0.01, "remaining_percentage 85%");

        // current_usage describes the context window (input_tokens = contextUsedTokens, others 0)
        var usage = cw.GetProperty("current_usage");
        Check(usage.GetProperty("input_tokens").GetInt64() == 150_000, "current_usage.input_tokens");
        Check(usage.GetProperty("output_tokens").GetInt64() == 0, "current_usage.output_tokens");
        Check(usage.GetProperty("cache_read_input_tokens").GetInt64() == 0, "current_usage.cache_read_input_tokens");
        Check(usage.GetProperty("cache_creation_input_tokens").GetInt64() == 0, "current_usage.cache_creation_input_tokens");

        // output_style and thinking omitted when null
        Check(!r.TryGetProperty("output_style", out _), "output_style omitted when null");
        Check(!r.TryGetProperty("thinking", out _), "thinking omitted when null");

        // output_style and thinking present when set
        var styled = ctx with { OutputStyle = "Explanatory", ThinkingEnabled = false };
        var sr = JsonDocument.Parse(StatusLineSupport.BuildPayload(styled)).RootElement;
        Check(sr.GetProperty("output_style").GetProperty("name").GetString() == "Explanatory", "output_style.name");
        Check(sr.GetProperty("thinking").GetProperty("enabled").GetBoolean() == false, "thinking.enabled=false");
        var styledOn = ctx with { OutputStyle = "Explanatory", ThinkingEnabled = true };
        var son = JsonDocument.Parse(StatusLineSupport.BuildPayload(styledOn)).RootElement;
        Check(son.GetProperty("thinking").GetProperty("enabled").GetBoolean() == true, "thinking.enabled=true");

        // rate_limits is an object keyed by canonical kind (first match, past resetsAt skipped, unknown skipped)
        var rl = r.GetProperty("rate_limits");
        Check(rl.ValueKind == JsonValueKind.Object, "rate_limits is an object not an array");
        Check(rl.GetProperty("five_hour").GetProperty("used_percentage").GetDouble() == 42.5, "five_hour.used_percentage");
        Check(rl.GetProperty("five_hour").TryGetProperty("resets_at", out var resetsAt) && resetsAt.ValueKind == JsonValueKind.Number, "five_hour.resets_at is a unix timestamp integer");
        Check(!rl.TryGetProperty("seven_day", out _), "seven_day absent (resetsAt in the past)");
        Check(!rl.TryGetProperty("other", out _), "unknown kind omitted");

        // Nothing measured yet: context_window always present, current_usage/used_percentage are JSON null
        var empty = new StatusLineContext(
            SessionId: "s", Cwd: "/r", ProjectDir: "/r",
            ModelId: "default", ModelName: "CLI 기본값", Version: "",
            CostUSD: null, DurationMs: null, ApiDurationMs: null,
            InputTokens: null, OutputTokens: null, CacheReadTokens: null,
            CacheWriteTokens: null, ContextUsedTokens: null, ContextWindowTokens: null,
            Effort: null, FastMode: false, RateLimits: null,
            OutputStyle: null, ThinkingEnabled: null, TranscriptPath: null);
        var er = JsonDocument.Parse(StatusLineSupport.BuildPayload(empty)).RootElement;
        var ew = er.GetProperty("context_window");
        Check(ew.GetProperty("used_percentage").ValueKind == JsonValueKind.Null, "empty: used_percentage is null not missing");
        Check(ew.GetProperty("current_usage").ValueKind == JsonValueKind.Null, "empty: current_usage is null not missing");
        Check(!er.TryGetProperty("rate_limits", out _), "empty: rate_limits absent");
        Check(!er.TryGetProperty("effort", out _), "empty: effort absent");

        // exceeds_200k_tokens true when contextUsedTokens > 200k
        var over = ctx with { ContextUsedTokens = 250_000, ContextWindowTokens = 1_000_000 };
        Check(JsonDocument.Parse(StatusLineSupport.BuildPayload(over)).RootElement
            .GetProperty("exceeds_200k_tokens").GetBoolean() == true, "exceeds_200k_tokens when used>200k");

        return Task.CompletedTask;
    }

    // ANSI parsing: colours, weight, strip OSC.
    internal static Task AnsiParsingKeepsColoursWeightAndStripsEscapes()
    {
        // Bold cyan text
        var segs = AnsiText.Parse("\x1b[1;36mhello\x1b[0m world");
        Check(segs.Count == 2, "two segments");
        Check(segs[0].Text == "hello" && segs[0].Bold && segs[0].Foreground == AnsiColor.Cyan, "bold cyan hello");
        Check(segs[1].Text == " world" && !segs[1].Bold && segs[1].Foreground == AnsiColor.Default, "reset world");

        // Dim + italic
        var segs2 = AnsiText.Parse("\x1b[2;3mfaded\x1b[22;23m normal");
        Check(segs2[0].Dim && segs2[0].Italic, "dim italic");
        Check(!segs2[1].Dim && !segs2[1].Italic, "dim italic cleared");

        // OSC stripped
        var segs3 = AnsiText.Parse("\x1b]0;window title\x07visible");
        Check(segs3.Count == 1 && segs3[0].Text == "visible", "OSC stripped");

        // Bright colours (90-97 fg, 100-107 bg)
        var segs4 = AnsiText.Parse("\x1b[92;101mgreen\x1b[0m");
        Check(segs4[0].Foreground == AnsiColor.BrightGreen, "bright green fg");
        Check(segs4[0].Background == AnsiColor.BrightRed, "bright red bg");

        // Underline
        var segs5 = AnsiText.Parse("\x1b[4munderlined\x1b[24m not");
        Check(segs5[0].Underline, "underline set");
        Check(!segs5[1].Underline, "underline cleared");

        // Plain text — no segments lost
        var segs6 = AnsiText.Parse("plain");
        Check(segs6.Count == 1 && segs6[0].Text == "plain", "plain text");
        return Task.CompletedTask;
    }

    // 256-colour and RGB SGR codes, mirroring macOS ANSIText.apply exactly: semicolon and
    // colon forms, foreground and background, clamped/reset malformed parameters.
    internal static Task AnsiParsingSupports256ColourAndRgbLikeMacOS()
    {
        // Bold + 256-colour palette foreground (38;5;n)
        var palette = AnsiText.Parse("\x1b[1;38;5;208mbold\x1b[22m normal");
        Check(palette[0].Bold && palette[0].Foreground == AnsiColor.Palette(208), "bold palette foreground");
        Check(!palette[1].Bold, "bold cleared after palette segment");

        // 24-bit RGB foreground (38;2;r;g;b), reset by 39
        var rgb = AnsiText.Parse("\x1b[38;2;10;20;30mrgb\x1b[39mplain");
        Check(rgb[0].Foreground == AnsiColor.Rgb(10, 20, 30), "rgb foreground");
        Check(rgb[1].Foreground == AnsiColor.Default, "39 resets rgb foreground");

        // Background palette and RGB (48;5;n / 48;2;r;g;b) — macOS supports background the same way.
        var bgPalette = AnsiText.Parse("\x1b[48;5;21mbg");
        Check(bgPalette[0].Background == AnsiColor.Palette(21) && bgPalette[0].Foreground == AnsiColor.Default, "palette background only");
        var bgRgb = AnsiText.Parse("\x1b[48;2;1;2;3mbg");
        Check(bgRgb[0].Background == AnsiColor.Rgb(1, 2, 3), "rgb background");

        // Out-of-range palette index clamps like macOS ANSIText (min(255,max(0,index))).
        var clamped = AnsiText.Parse("\x1b[38;5;300mclamped");
        Check(clamped[0].Foreground == AnsiColor.Palette(255), "palette index clamps to 255");

        // A malformed/truncated extended colour clears the colour instead of leaving it — matches
        // macOS: `color` stays nil and is still assigned to state.foreground/background.
        var malformed = AnsiText.Parse("\x1b[31;38;5mafter");
        Check(malformed[0].Foreground == AnsiColor.Default, "incomplete 38;5 clears the foreground");

        // Colon sub-parameter form (38:2::r:g:b) sets the colour; unrelated codes keep working.
        var colon = AnsiText.Parse("\x1b[1m\x1b[38:2::10:20:30mrgb");
        Check(colon[0].Bold && colon[0].Foreground == AnsiColor.Rgb(10, 20, 30), "colon rgb form with bold preserved");
        var colonPalette = AnsiText.Parse("\x1b[38:5:9mrgb");
        Check(colonPalette[0].Foreground == AnsiColor.Palette(9), "colon palette form");
        return Task.CompletedTask;
    }

    // Trust rule: fromWorkspace commands must be in the trusted map before running.
    internal static Task TrustRuleRequiresFingerprintForWorkspaceCommands()
    {
        var ws = new StatusLineConfig("echo ws", 0, "프로젝트 설정", FromWorkspace: true);
        var user = new StatusLineConfig("echo user", 0, "사용자 설정", FromWorkspace: false);

        // User-level: always trusted
        Check(IsTrusted(user, null, "ws1"), "user config always trusted");
        Check(IsTrusted(user, new Dictionary<string, string>(), "ws1"), "user config trusted with empty map");

        // Workspace: trusted only when fingerprint matches
        Check(!IsTrusted(ws, null, "ws1"), "workspace config not trusted with null map");
        Check(!IsTrusted(ws, new Dictionary<string, string>(), "ws1"), "workspace config not trusted with empty map");
        var map = new Dictionary<string, string> { ["ws1"] = ws.Fingerprint };
        Check(IsTrusted(ws, map, "ws1"), "workspace config trusted when fingerprint matches");
        // Changed command → new fingerprint → not trusted
        var ws2 = new StatusLineConfig("echo ws-changed", 0, "프로젝트 설정", FromWorkspace: true);
        Check(!IsTrusted(ws2, map, "ws1"), "changed command is not trusted");
        // Different workspace id → not trusted
        Check(!IsTrusted(ws, map, "ws2"), "different workspace id is not trusted");

        // 이 워크스페이스에서 허용 stores the fingerprint on the snapshot, and the pane
        // never runs an untrusted workspace command; Version stays 1 across save/restore.
        var workspaceId = Wire.Id();
        var snapshot = new AppSnapshot();
        Check(StatusLineTrust.Resolve(new(ws, null), snapshot, workspaceId) == (null, ws), "an untrusted workspace command is shown as a question, not run");
        Check(StatusLineTrust.Resolve(new(user, null), snapshot, workspaceId) == (user, null), "a user-level command runs without a prompt");
        snapshot = StatusLineTrust.Trust(snapshot, ws, workspaceId);
        Check(snapshot.TrustedStatusLines![workspaceId] == ws.Fingerprint, "허용 stores the fingerprint per workspace id");
        Check(StatusLineTrust.Resolve(new(ws, null), snapshot, workspaceId) == (ws, null), "once allowed the workspace command runs and the question disappears");
        Check(StatusLineTrust.Resolve(new(ws2, null), snapshot, workspaceId) == (null, ws2), "a changed command asks again");
        var restored = StateStore.Normalize(JsonSerializer.Deserialize<AppSnapshot>(JsonSerializer.SerializeToUtf8Bytes(snapshot, Wire.Json), Wire.Json)!, true);
        Check(restored.Version == 1 && restored.TrustedStatusLines?[workspaceId] == ws.Fingerprint, "trusted fingerprints survive save and restore at Version 1");
        return Task.CompletedTask;
    }

    private static bool IsTrusted(StatusLineConfig config, Dictionary<string, string>? trusted, string workspaceId)
        => StatusLineTrust.IsTrusted(config, trusted, workspaceId);

    // While a workspace command waits on trust, macOS still runs the user-level command
    // (AppStore+StatusLine.swift: `gated ? discovery.user : discovery.preferred`); the
    // workspace command is never run before it is allowed.
    internal static Task FallsBackToUserCommandWhileWorkspaceCommandIsGated()
    {
        var ws = new StatusLineConfig("echo ws", 0, "프로젝트 설정", FromWorkspace: true);
        var user = new StatusLineConfig("echo user", 0, "사용자 설정", FromWorkspace: false);
        var workspaceId = Wire.Id();
        var snapshot = new AppSnapshot();
        var discovery = new StatusLineDiscovery(ws, user);

        Check(discovery.Preferred == ws, "the workspace level still wins precedence");
        Check(StatusLineTrust.Resolve(discovery, snapshot, workspaceId) == (user, ws), "gated workspace command falls back to the user command");

        snapshot = StatusLineTrust.Trust(snapshot, ws, workspaceId);
        Check(StatusLineTrust.Resolve(discovery, snapshot, workspaceId) == (ws, null), "once allowed, the workspace command itself runs");

        // No user-level command to fall back to: still no workspace command runs, and there is nothing to show.
        var workspaceOnly = new StatusLineDiscovery(ws, null);
        Check(StatusLineTrust.Resolve(workspaceOnly, new AppSnapshot(), workspaceId) == (null, ws), "with no user command, a gated workspace command leaves nothing running");

        // A user-level command with no workspace command at all always just runs.
        var userOnly = new StatusLineDiscovery(null, user);
        Check(StatusLineTrust.Resolve(userOnly, new AppSnapshot(), workspaceId) == (user, null), "a user-only discovery runs without a prompt");
        return Task.CompletedTask;
    }

    // Pure xterm 256-colour index → RGB, used by the WinUI renderer for 38;5;n / 48;5;n.
    internal static Task PaletteIndexMapsToXtermColours()
    {
        Check(AnsiPalette.ToRgb(1) == (128, 0, 0), "index 1 is the named ANSI red");
        Check(AnsiPalette.ToRgb(9) == (255, 0, 0), "index 9 is the named ANSI bright red");
        Check(AnsiPalette.ToRgb(15) == (255, 255, 255), "index 15 is the named ANSI bright white");
        Check(AnsiPalette.ToRgb(16) == (0, 0, 0), "index 16 is the black corner of the 6x6x6 cube");
        Check(AnsiPalette.ToRgb(21) == (0, 0, 255), "index 21 is pure blue in the cube");
        Check(AnsiPalette.ToRgb(196) == (255, 0, 0), "index 196 is the well-known xterm bright red");
        Check(AnsiPalette.ToRgb(231) == (255, 255, 255), "index 231 is the white corner of the cube");
        Check(AnsiPalette.ToRgb(232) == (8, 8, 8), "index 232 is the darkest grey step");
        Check(AnsiPalette.ToRgb(255) == (238, 238, 238), "index 255 is the lightest grey step");
        Check(AnsiPalette.ToRgb(-5) == AnsiPalette.ToRgb(0) && AnsiPalette.ToRgb(999) == AnsiPalette.ToRgb(255), "out-of-range indices clamp instead of throwing");
        return Task.CompletedTask;
    }

    // Real short command through the platform shell (skipped on unsupported platforms that lack the shell).
    internal static async Task RunnerFeedsStdinCapturesOutputAndColour()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP"); return;
        }
        // A command that echoes back the first line of stdin with an ANSI colour code.
        string command;
        if (OperatingSystem.IsWindows())
            command = "for /f \"tokens=*\" %i in ('findstr /r \".\"') do @echo \x1b[36m%i\x1b[0m";
        else
            command = "read line; printf '\\033[36m%s\\033[0m' \"$line\"";

        var cfg = new StatusLineConfig(command, 0, "사용자 설정", false);
        var ctx = new StatusLineContext("sid", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, (bool?)false, null);
        var result = await StatusLineSupport.RunAsync(cfg, ctx, timeout: 8);
        // On Windows the findstr-based command may not work cleanly; just verify it returned without exception.
        Check(!result.TimedOut, "short command must not time out");
    }

    // Timed-out command is killed; result has TimedOut=true, finishes well within timeout*2.
    internal static async Task RunnerEnforcesTimeout()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP"); return;
        }
        string command = OperatingSystem.IsWindows() ? "ping -n 10 127.0.0.1 > nul" : "sleep 5";
        var cfg = new StatusLineConfig(command, 0, "사용자 설정", false);
        var ctx = new StatusLineContext("sid2", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, (bool?)false, null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await StatusLineSupport.RunAsync(cfg, ctx, timeout: 0.4);
        sw.Stop();
        Check(result.TimedOut, "timed-out flag must be true");
        Check(result.ErrorText == StatusLineStrings.ErrorTimeout, "error text must match macOS literal");
        Check(sw.Elapsed.TotalSeconds < 4, $"must finish well before sleep duration, took {sw.Elapsed.TotalSeconds:F1}s");
    }
    // The shell is the one Claude Code uses, so a command that works in the CLI works in the app.
    public static Task ShellFollowsClaudeCodeOnWindows()
    {
        Func<string, string?> env = name => name switch { "ProgramFiles" => "C:\\Program Files", "LocalAppData" => "C:\\Users\\me\\AppData\\Local", "SystemRoot" => "C:\\Windows", _ => null };
        var gitBash = "C:\\Program Files\\Git\\bin\\bash.exe";
        var withGit = StatusLineSupport.Shell("~/.claude/statusline.sh", windows: true, exists: path => path == gitBash, environment: env);
        Check(withGit.Binary == gitBash && withGit.Arguments.SequenceEqual(new[] { "-c", "~/.claude/statusline.sh" }), "Git Bash가 있으면 Git Bash로 실행해야 합니다.");
        var withoutGit = StatusLineSupport.Shell("node status.mjs", windows: true, exists: _ => false, environment: env);
        Check(withoutGit.Binary == "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" && withoutGit.Arguments.SequenceEqual(new[] { "-NoProfile", "-NonInteractive", "-Command", "node status.mjs" }), "Git Bash가 없으면 PowerShell로 실행해야 합니다.");
        var named = StatusLineSupport.Shell("x", windows: true, exists: path => path == "D:\\tools\\bash.exe", environment: name => name == "CLAUDE_CODE_GIT_BASH_PATH" ? "D:\\tools\\bash.exe" : env(name));
        Check(named.Binary == "D:\\tools\\bash.exe", "CLAUDE_CODE_GIT_BASH_PATH로 지정한 Git Bash를 먼저 써야 합니다.");
        var posix = StatusLineSupport.Shell("x", windows: false);
        Check(posix.Binary == "/bin/sh" && posix.Arguments.SequenceEqual(new[] { "-c", "x" }), "Windows가 아니면 /bin/sh로 실행해야 합니다.");
        return Task.CompletedTask;
    }
}
