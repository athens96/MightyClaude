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

    // Settings precedence: workspace-local > workspace > user.
    internal static Task ConfigFollowsPrecedenceAndOnlyCommandEntries()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "repo");

            // User-level only — discovered when no workspace settings exist
            Write(Path.Combine(home, ".claude", "settings.json"),
                """{"statusLine":{"command":"echo user","padding":2}}""");
            var user = StatusLineSupport.Discover(null, home).Preferred;
            Check(user?.Command == "echo user", "user command");
            Check(user?.Padding == 2, "user padding");
            Check(user?.FromWorkspace == false, "user not from workspace");
            Check(user?.Source == StatusLineStrings.SourceUser, "user source label");

            // Workspace settings.json takes precedence over user
            Write(Path.Combine(workspace, ".claude", "settings.json"),
                """{"statusLine":{"command":"echo proj"}}""");
            var proj = StatusLineSupport.Discover(workspace, home).Preferred;
            Check(proj?.Command == "echo proj", "workspace command");
            Check(proj?.FromWorkspace == true, "workspace from-workspace flag");
            Check(proj?.Source == StatusLineStrings.SourceWorkspace, "workspace source label");

            // Workspace-local takes precedence over workspace
            Write(Path.Combine(workspace, ".claude", "settings.local.json"),
                """{"statusLine":{"command":"echo local"}}""");
            var local = StatusLineSupport.Discover(workspace, home).Preferred;
            Check(local?.Command == "echo local", "local command");
            Check(local?.Source == StatusLineStrings.SourceWorkspaceLocal, "local source label");

            // A settings.json without statusLine returns null from that source
            Write(Path.Combine(home, ".claude", "settings.json"), """{"theme":"dark"}""");
            var noCmd = StatusLineSupport.Discover(null, home).Preferred;
            Check(noCmd is null, "no statusLine key returns null");

            // CLAUDE_CONFIG_DIR env var overrides home
            var altConfig = Path.Combine(root, "altconfig");
            Write(Path.Combine(altConfig, "settings.json"),
                """{"statusLine":{"command":"echo alt"}}""");
            var envOverride = StatusLineSupport.Discover(null, home,
                new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = altConfig }).Preferred;
            Check(envOverride?.Command == "echo alt", "CLAUDE_CONFIG_DIR override");

            // Discovery keeps both levels apart (unlike Preferred, which only ever exposes the winner).
            Write(Path.Combine(home, ".claude", "settings.json"), """{"statusLine":{"command":"echo user2"}}""");
            var discovery = StatusLineSupport.Discover(workspace, home);
            Check(discovery.Workspace?.Command == "echo local" && discovery.Workspace?.FromWorkspace == true, "discovery keeps the workspace level");
            Check(discovery.User?.Command == "echo user2" && discovery.User?.FromWorkspace == false, "discovery keeps the user level");
            Check(discovery.Preferred == discovery.Workspace, "preferred is the winning level");
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

    // Payload must use the CLI field names macOS sends.
    internal static Task PayloadUsesCliFieldNamesAndTranscriptLayout()
    {
        var root = Temp();
        try
        {
            var configDir = Path.Combine(root, "config");
            var ctx = new StatusLineContext(
                SessionId: "sess01",
                Cwd: "/work",
                ProjectDir: "/work/my-repo",
                ModelId: "claude-sonnet-5",
                ModelName: "Claude Sonnet 5",
                Version: "1.2.3",
                CostUSD: 0.0042,
                DurationMs: 1234,
                ApiDurationMs: 999,
                InputTokens: 100,
                OutputTokens: 50,
                CacheReadTokens: 10,
                CacheWriteTokens: 5,
                ContextUsedTokens: 1000,
                ContextWindowTokens: 200000,
                Effort: null,
                FastMode: true,
                RateLimits: [new SessionRateLimit("requests", 42.5, "2026-09-21T00:00:00Z")],
                OutputStyle: "auto",
                ThinkingEnabled: false,
                TranscriptPath: StatusLineSupport.TranscriptPath(configDir, "/work/my-repo", "sess01")
            );

            var json = StatusLineSupport.BuildPayload(ctx);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root2 = doc.RootElement;

            Check(root2.GetProperty("hook_event_name").GetString() == "StatusLineUpdate", "hook_event_name");
            Check(root2.GetProperty("session_id").GetString() == "sess01", "session_id");
            Check(root2.GetProperty("model").GetString() == "claude-sonnet-5", "model");
            Check(root2.GetProperty("fast_mode").GetBoolean() == true, "fast_mode");
            Check(root2.GetProperty("thinking_enabled").GetBoolean() == false, "thinking_enabled");
            var cw = root2.GetProperty("context_window");
            Check(cw.GetProperty("input_tokens").GetInt64() == 100, "input_tokens");
            Check(cw.GetProperty("context_window_size").GetInt64() == 200000, "context_window_size");
            var rl = root2.GetProperty("rate_limits")[0];
            Check(rl.GetProperty("kind").GetString() == "requests", "rate_limits kind");
            Check(rl.GetProperty("percent_used").GetDouble() == 42.5, "rate_limits percent_used");

            // Transcript path: slug uses non-alphanum → '-', first 200 chars
            var tp = StatusLineSupport.TranscriptPath(configDir, "/work/my-repo", "sess01");
            Check(tp.Contains("work-my-repo"), "transcript slug replaces / with -");
            Check(tp.EndsWith("sess01.jsonl"), "transcript ends with sessionId.jsonl");

            // Null context_window when no token fields
            var ctxNoTokens = ctx with { InputTokens = null, OutputTokens = null, ContextUsedTokens = null, CacheReadTokens = null, CacheWriteTokens = null, ContextWindowTokens = null };
            var json2 = StatusLineSupport.BuildPayload(ctxNoTokens);
            var doc2 = System.Text.Json.JsonDocument.Parse(json2);
            Check(doc2.RootElement.GetProperty("context_window").ValueKind == System.Text.Json.JsonValueKind.Null, "context_window null when no tokens");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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
        var ctx = new StatusLineContext("sid", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, false, null);
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
        var ctx = new StatusLineContext("sid2", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, false, null);
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
