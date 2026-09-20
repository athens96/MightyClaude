using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyClaude.Core;

// ANSI colour from an SGR code: unset (None), one of the 16 named colours
// (Standard, matching macOS ANSISegment.Color.standard(0-15)), a 256-colour
// palette index (macOS .palette), or 24-bit RGB (macOS .rgb).
public enum AnsiColorKind { None, Standard, Palette, Rgb }

public readonly record struct AnsiColor(AnsiColorKind Kind, int A = 0, int B = 0, int C = 0)
{
    public static readonly AnsiColor Default = default;
    public static readonly AnsiColor Black = new(AnsiColorKind.Standard, 0);
    public static readonly AnsiColor Red = new(AnsiColorKind.Standard, 1);
    public static readonly AnsiColor Green = new(AnsiColorKind.Standard, 2);
    public static readonly AnsiColor Yellow = new(AnsiColorKind.Standard, 3);
    public static readonly AnsiColor Blue = new(AnsiColorKind.Standard, 4);
    public static readonly AnsiColor Magenta = new(AnsiColorKind.Standard, 5);
    public static readonly AnsiColor Cyan = new(AnsiColorKind.Standard, 6);
    public static readonly AnsiColor White = new(AnsiColorKind.Standard, 7);
    public static readonly AnsiColor BrightBlack = new(AnsiColorKind.Standard, 8);
    public static readonly AnsiColor BrightRed = new(AnsiColorKind.Standard, 9);
    public static readonly AnsiColor BrightGreen = new(AnsiColorKind.Standard, 10);
    public static readonly AnsiColor BrightYellow = new(AnsiColorKind.Standard, 11);
    public static readonly AnsiColor BrightBlue = new(AnsiColorKind.Standard, 12);
    public static readonly AnsiColor BrightMagenta = new(AnsiColorKind.Standard, 13);
    public static readonly AnsiColor BrightCyan = new(AnsiColorKind.Standard, 14);
    public static readonly AnsiColor BrightWhite = new(AnsiColorKind.Standard, 15);
    public static AnsiColor Standard(int index) => new(AnsiColorKind.Standard, index);
    // 38;5;n / 48;5;n — index clamped like macOS ANSIText.apply (min(255,max(0,index))).
    public static AnsiColor Palette(int index) => new(AnsiColorKind.Palette, Math.Clamp(index, 0, 255));
    // 38;2;r;g;b / 48;2;r;g;b — each component clamped like macOS ANSIText.clamp.
    public static AnsiColor Rgb(int r, int g, int b) => new(AnsiColorKind.Rgb, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
}

// Pure xterm 256-colour index → RGB conversion (0-15 named, 16-231 the 6x6x6 cube, 232-255 greys).
public static class AnsiPalette
{
    private static readonly (byte R, byte G, byte B)[] Named16 =
    [
        (0, 0, 0), (128, 0, 0), (0, 128, 0), (128, 128, 0), (0, 0, 128), (128, 0, 128), (0, 128, 128), (192, 192, 192),
        (128, 128, 128), (255, 0, 0), (0, 255, 0), (255, 255, 0), (0, 0, 255), (255, 0, 255), (0, 255, 255), (255, 255, 255),
    ];
    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    public static (byte R, byte G, byte B) ToRgb(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16) return Named16[index];
        if (index < 232)
        {
            var i = index - 16;
            return (CubeLevels[i / 36], CubeLevels[i / 6 % 6], CubeLevels[i % 6]);
        }
        var grey = (byte)(8 + (index - 232) * 10);
        return (grey, grey, grey);
    }
}

public sealed record AnsiSegment(string Text, AnsiColor Foreground = default, AnsiColor Background = default, bool Bold = false, bool Dim = false, bool Italic = false, bool Underline = false);

public static class AnsiText
{
    // Parse a string with ANSI SGR escape sequences into styled segments. OSC sequences are stripped.
    public static IReadOnlyList<AnsiSegment> Parse(string text)
    {
        var segments = new List<AnsiSegment>();
        var fg = AnsiColor.Default; var bg = AnsiColor.Default;
        var bold = false; var dim = false; var italic = false; var underline = false;
        var i = 0;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '[') // CSI
                {
                    // flush pending text
                    if (sb.Length > 0) { segments.Add(new(sb.ToString(), fg, bg, bold, dim, italic, underline)); sb.Clear(); }
                    i += 2;
                    var seq = new StringBuilder();
                    while (i < text.Length && text[i] != 'm' && text[i] != 'A' && text[i] != 'B' && text[i] != 'C' && text[i] != 'D' && text[i] != 'H' && text[i] != 'J' && text[i] != 'K')
                        seq.Append(text[i++]);
                    if (i < text.Length && text[i] == 'm')
                    {
                        i++;
                        ApplySgr(seq.ToString(), ref fg, ref bg, ref bold, ref dim, ref italic, ref underline);
                    }
                    else { i++; } // non-SGR CSI — skip final byte
                }
                else if (next == ']') // OSC — skip to ST (ESC \ or BEL)
                {
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '\x07') { i++; break; }
                        if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') { i += 2; break; }
                        i++;
                    }
                }
                else { sb.Append(text[i++]); } // unknown escape — keep raw
            }
            else { sb.Append(text[i++]); }
        }
        if (sb.Length > 0) segments.Add(new(sb.ToString(), fg, bg, bold, dim, italic, underline));
        return segments;
    }

    // Mirrors macOS ANSIText.apply: semicolon params build a flat code list, a colon group
    // (`38:2::r:g:b`, `38:5:n`) collapses to one extended-colour entry in that same list, and
    // 38/48 then consume the following one or three codes for palette/RGB — a malformed or
    // truncated extended colour clears the colour (matches macOS: color stays nil, still assigned).
    private static void ApplySgr(string seq, ref AnsiColor fg, ref AnsiColor bg, ref bool bold, ref bool dim, ref bool italic, ref bool underline)
    {
        var codes = new List<int>();
        foreach (var token in seq.Split(';'))
        {
            if (token.Contains(':'))
            {
                var subs = token.Split(':').Select(s => s.Length == 0 ? (int?)null : int.TryParse(s, out var v) ? v : null).ToArray();
                if (subs.Length < 3 || subs[0] is not { } kind || (kind != 38 && kind != 48) || subs[1] is not { } mode) continue;
                var numbers = subs.Skip(2).Where(n => n.HasValue).Select(n => n!.Value).ToArray();
                if (mode == 2 && numbers.Length >= 3) codes.AddRange(new[] { kind, 2 }.Concat(numbers.Skip(numbers.Length - 3)));
                else if (mode == 5 && numbers.Length > 0) codes.AddRange([kind, 5, numbers[0]]);
            }
            else codes.Add(token.Length == 0 ? 0 : (int.TryParse(token, out var n) ? n : -1));
        }
        if (codes.Count == 0) { Reset(ref fg, ref bg, ref bold, ref dim, ref italic, ref underline); return; }
        var position = 0;
        while (position < codes.Count)
        {
            var code = codes[position]; position++;
            switch (code)
            {
                case 0: Reset(ref fg, ref bg, ref bold, ref dim, ref italic, ref underline); break;
                case 1: bold = true; break;
                case 2: dim = true; break;
                case 3: italic = true; break;
                case 4: underline = true; break;
                case 22: bold = false; dim = false; break;
                case 23: italic = false; break;
                case 24: underline = false; break;
                case >= 30 and <= 37: fg = AnsiColor.Standard(code - 30); break;
                case >= 90 and <= 97: fg = AnsiColor.Standard(code - 90 + 8); break;
                case 39: fg = AnsiColor.Default; break;
                case >= 40 and <= 47: bg = AnsiColor.Standard(code - 40); break;
                case >= 100 and <= 107: bg = AnsiColor.Standard(code - 100 + 8); break;
                case 49: bg = AnsiColor.Default; break;
                case 38 or 48:
                    var color = AnsiColor.Default;
                    if (position < codes.Count && codes[position] == 5 && position + 1 < codes.Count) { color = AnsiColor.Palette(codes[position + 1]); position += 2; }
                    else if (position < codes.Count && codes[position] == 2 && position + 3 < codes.Count) { color = AnsiColor.Rgb(codes[position + 1], codes[position + 2], codes[position + 3]); position += 4; }
                    else position = codes.Count;
                    if (code == 38) fg = color; else bg = color;
                    break;
            }
        }
    }
    private static void Reset(ref AnsiColor fg, ref AnsiColor bg, ref bool bold, ref bool dim, ref bool italic, ref bool underline)
    { fg = AnsiColor.Default; bg = AnsiColor.Default; bold = false; dim = false; italic = false; underline = false; }
}

// A discovered status line config from Claude settings.
public sealed record StatusLineConfig(string Command, int Padding, string Source, bool FromWorkspace, string? OutputStyle = null, bool? ThinkingEnabled = null)
{
    // SHA-256(source + newline + command) as 64-char lowercase hex — matches macOS fingerprint.
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source + "\n" + Command))).ToLowerInvariant();
}

// Both levels Claude consults (macOS StatusLineConfig.Discovery): Workspace is the winning
// .claude/settings.local.json / settings.json entry, User is ~/.claude/settings.json (or
// CLAUDE_CONFIG_DIR). Kept apart so a gated workspace command can still fall back to the user one.
public sealed record StatusLineDiscovery(StatusLineConfig? Workspace, StatusLineConfig? User, bool WorkspaceDisabled = false)
{
    public StatusLineConfig? Preferred => WorkspaceDisabled ? null : (Workspace ?? User);
}

// Session context passed as JSON payload on stdin.
public sealed record StatusLineContext(
    string SessionId,
    string? Cwd,
    string? ProjectDir,
    string? ModelId,
    string? ModelName,
    string? Version,
    double? CostUSD,
    long? DurationMs,
    long? ApiDurationMs,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ContextUsedTokens,
    long? ContextWindowTokens,
    string? Effort,
    bool FastMode,
    IReadOnlyList<SessionRateLimit>? RateLimits,
    string? OutputStyle,
    bool? ThinkingEnabled,
    string? TranscriptPath);

// Result of running the status line command.
public sealed record StatusLineResult(
    IReadOnlyList<IReadOnlyList<AnsiSegment>> Lines,
    string? ErrorText,
    int ExitCode,
    bool TimedOut);

public static class StatusLineSupport
{
    public const int MaximumLines = 6;
    private const int MaximumOutputBytes = 16 * 1024;

    // Compute the transcript path following the macOS slug rule (every non-alphanumeric → '-', first 200 chars).
    public static string TranscriptPath(string configDir, string? cwd, string sessionId)
    {
        var raw = cwd ?? "";
        var slug = new StringBuilder();
        foreach (var c in raw) slug.Append(char.IsLetterOrDigit(c) ? c : '-');
        var s = slug.ToString();
        if (s.Length > 200) s = s[..200];
        return Path.Combine(configDir, "projects", s, sessionId + ".jsonl");
    }

    // Build the JSON payload matching the macOS CLI field names and shape (StatusLineSupport.payload).
    public static string BuildPayload(StatusLineContext ctx)
    {
        var obj = new JsonObject
        {
            ["hook_event_name"] = "Status",
            ["session_id"] = ctx.SessionId,
            ["transcript_path"] = ctx.TranscriptPath,
            ["cwd"] = ctx.Cwd,
            ["model"] = new JsonObject { ["id"] = ctx.ModelId ?? "", ["display_name"] = ctx.ModelName ?? "" },
            ["workspace"] = new JsonObject { ["current_dir"] = ctx.Cwd ?? "", ["project_dir"] = ctx.ProjectDir ?? "" },
            ["version"] = ctx.Version,
            ["cost"] = new JsonObject
            {
                ["total_cost_usd"] = ctx.CostUSD ?? 0.0,
                ["total_duration_ms"] = ctx.DurationMs ?? 0L,
                ["total_api_duration_ms"] = ctx.ApiDurationMs ?? 0L,
                ["total_lines_added"] = 0,
                ["total_lines_removed"] = 0,
            },
            ["fast_mode"] = ctx.FastMode,
        };

        if (ctx.OutputStyle is not null)
            obj["output_style"] = new JsonObject { ["name"] = ctx.OutputStyle };

        if (ctx.ThinkingEnabled.HasValue)
            obj["thinking"] = new JsonObject { ["enabled"] = ctx.ThinkingEnabled.Value };

        var window = new JsonObject
        {
            ["total_input_tokens"] = ctx.InputTokens ?? 0L,
            ["total_output_tokens"] = ctx.OutputTokens ?? 0L,
            ["context_window_size"] = ctx.ContextWindowTokens ?? 200_000L,
        };

        if (ctx.ContextUsedTokens is { } used)
        {
            var size = Math.Max(1L, ctx.ContextWindowTokens ?? 200_000L);
            var percent = Math.Min(100.0, Math.Max(0.0, (double)used / size * 100.0));
            window["current_usage"] = new JsonObject
            {
                ["input_tokens"] = used,
                ["output_tokens"] = 0L,
                ["cache_creation_input_tokens"] = 0L,
                ["cache_read_input_tokens"] = 0L,
            };
            window["used_percentage"] = Math.Round(percent * 10.0) / 10.0;
            window["remaining_percentage"] = Math.Round((100.0 - percent) * 10.0) / 10.0;
            obj["exceeds_200k_tokens"] = used > 200_000;
        }
        else
        {
            window["current_usage"] = (JsonNode?)null;
            window["used_percentage"] = (JsonNode?)null;
            window["remaining_percentage"] = (JsonNode?)null;
            obj["exceeds_200k_tokens"] = false;
        }
        obj["context_window"] = window;

        if (ctx.Effort is { } effort && s_validEfforts.Contains(effort))
            obj["effort"] = new JsonObject { ["level"] = effort };

        if (ctx.RateLimits is { Count: > 0 })
        {
            var limits = new JsonObject();
            foreach (var r in ctx.RateLimits)
            {
                var key = r.Kind switch
                {
                    "five_hour" or "session" or "5h" or "primary" => "five_hour",
                    "seven_day" or "weekly" or "7d" or "secondary" => "seven_day",
                    _ => null,
                };
                if (key is null) continue;
                if (limits[key] is not null) continue;
                if (r.PercentUsed is not { } pct || !double.IsFinite(pct)) continue;
                var entry = new JsonObject { ["used_percentage"] = Math.Min(100.0, Math.Max(0.0, pct)) };
                if (r.ResetsAt is { } resetsAtStr)
                {
                    if (TryParseIso8601(resetsAtStr) is not { } resetsAt || resetsAt <= DateTimeOffset.UtcNow) continue;
                    entry["resets_at"] = resetsAt.ToUnixTimeSeconds();
                }
                limits[key] = entry;
            }
            if (limits.Count > 0) obj["rate_limits"] = limits;
        }

        return obj.ToJsonString();
    }

    private static readonly HashSet<string> s_validEfforts = ["low", "medium", "high", "xhigh", "max"];

    private static DateTimeOffset? TryParseIso8601(string s)
    {
        if (DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) return d;
        return null;
    }

    // Discover both levels Claude consults (macOS StatusLineConfig.discover): workspace-local
    // then workspace at the workspace path (first of the two that has a command wins), and the
    // user-level config from configDir. Kept apart — see StatusLineDiscovery — so a workspace
    // command still waiting on trust does not blank the status line while a user command exists.
    public static StatusLineDiscovery Discover(string? workspacePath, string? homeDir, IDictionary<string, string>? env = null)
    {
        StatusLineConfig? workspace = null;
        var workspaceDisabled = false;
        if (workspacePath is not null)
        {
            foreach (var (path, source) in new[]
            {
                (Path.Combine(workspacePath, ".claude", "settings.local.json"), StatusLineStrings.SourceWorkspaceLocal),
                (Path.Combine(workspacePath, ".claude", "settings.json"), StatusLineStrings.SourceWorkspace),
            })
            {
                var (disabled, config) = ReadEntry(path, fromWorkspace: true, source);
                if (!disabled && config is null) continue;
                if (disabled) workspaceDisabled = true; else workspace = config;
                break;
            }
        }
        var configDir = ConfigDir(homeDir, env);
        var user = configDir is null ? null : ReadEntry(Path.Combine(configDir, "settings.json"), fromWorkspace: false, StatusLineStrings.SourceUser).Config;
        return new StatusLineDiscovery(workspace, user, workspaceDisabled);
    }

    public static string? ConfigDir(string? homeDir, IDictionary<string, string>? env = null)
    {
        if (env is not null && env.TryGetValue("CLAUDE_CONFIG_DIR", out var envDir) && envDir is { Length: > 0 }) return envDir;
        if (homeDir is null) return null;
        return Path.Combine(homeDir, ".claude");
    }

    // Returns (disabled: true, null) when statusLine key exists but is not a valid command entry —
    // this disables the level (macOS: .some(nil)). Returns (false, null) when the key is absent.
    private static (bool Disabled, StatusLineConfig? Config) ReadEntry(string settingsPath, bool fromWorkspace, string source)
    {
        try
        {
            var bytes = File.ReadAllBytes(settingsPath);
            if (bytes.Length > 4 * 1024 * 1024) return (false, null);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("statusLine", out var sl)) return (false, null);
            if (!sl.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "command") return (true, null);
            if (!sl.TryGetProperty("command", out var cmdEl)) return (true, null);
            var command = cmdEl.GetString();
            if (string.IsNullOrWhiteSpace(command) || Encoding.UTF8.GetByteCount(command) > 4096) return (true, null);
            var padding = 0;
            if (sl.TryGetProperty("padding", out var padEl) && padEl.TryGetInt32(out var p)) padding = Math.Clamp(p, 0, 8);
            string? outputStyle = null;
            if (root.TryGetProperty("outputStyle", out var styleEl) && styleEl.GetString() is { Length: > 0 } styleStr && Encoding.UTF8.GetByteCount(styleStr) <= 80)
                outputStyle = styleStr;
            bool? thinkingEnabled = null;
            if (root.TryGetProperty("alwaysThinkingEnabled", out var thinkEl))
            {
                if (thinkEl.ValueKind == JsonValueKind.True) thinkingEnabled = true;
                else if (thinkEl.ValueKind == JsonValueKind.False) thinkingEnabled = false;
            }
            return (false, new StatusLineConfig(command, padding, source, fromWorkspace, outputStyle, thinkingEnabled));
        }
        catch { return (false, null); }
    }

    // The shell Claude Code itself uses for a statusLine command, so a command
    // that works in the CLI works here: /bin/sh elsewhere; on Windows Git Bash
    // when it is installed, PowerShell when it is not. CLAUDE_CODE_GIT_BASH_PATH
    // names a Git Bash outside the usual folders, as it does for the CLI.
    public static (string Binary, string[] Arguments) Shell(string command, bool windows, Func<string, bool>? exists = null, Func<string, string?>? environment = null)
    {
        if (!windows) return ("/bin/sh", ["-c", command]);
        exists ??= File.Exists;
        environment ??= Environment.GetEnvironmentVariable;
        var candidates = new List<string>();
        if (environment("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } named) candidates.Add(named);
        foreach (var root in new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)"), environment("LocalAppData") is { Length: > 0 } local ? local + "\\Programs" : null })
            if (root is { Length: > 0 }) candidates.Add(root + "\\Git\\bin\\bash.exe");
        foreach (var bash in candidates) if (exists(bash)) return (bash, ["-c", command]);
        var system = environment("SystemRoot") is { Length: > 0 } windowsRoot ? windowsRoot : "C:\\Windows";
        return (system + "\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]);
    }

    // Run the status line command. timeout is in seconds (default 8).
    public static async Task<StatusLineResult> RunAsync(StatusLineConfig config, StatusLineContext ctx, double timeout = 8)
    {
        var payload = BuildPayload(ctx);
        var env = new Dictionary<string, string> { ["CLAUDE_CODE_STATUSLINE_HOST"] = "mightyclaude" };

        var (binary, args) = Shell(config.Command, OperatingSystem.IsWindows());
        // The command runs where the CLI would run it: the pane's folder.
        var cwd = ctx.Cwd is { Length: > 0 } folder && Directory.Exists(folder) ? folder : Environment.CurrentDirectory;

        ChildProcess? process = null;
        try
        {
            var info = ChildProcess.StartInfo(binary, args, cwd, env);
            process = ChildProcess.Start(info);
            await process.Input.WriteAsync(payload);
            await process.Input.FlushAsync();
            process.Input.Close();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            int exitCode;
            bool timedOut = false;
            string output;

            try
            {
                // Read output with a byte cap
                var outputTask = ReadCappedAsync(process.Output, MaximumOutputBytes);
                await process.Completion.WaitAsync(cts.Token);
                exitCode = process.Completion.Result;
                // Give a short drain window for background children that hold stdout
                await Task.WhenAny(outputTask, Task.Delay(500));
                output = outputTask.IsCompleted ? outputTask.Result : "";
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                exitCode = -1;
                process.Kill();
                output = "";
                try { await Task.WhenAny(ReadCappedAsync(process.Output, MaximumOutputBytes), Task.Delay(200)); } catch { }
            }

            string? errorText = null;
            if (timedOut) errorText = StatusLineStrings.ErrorTimeout;
            else if (exitCode != 0)
                errorText = StatusLineStrings.ErrorExitTemplate.Replace("{code}", exitCode.ToString());

            var lines = ParseLines(output);
            return new StatusLineResult(lines, errorText, exitCode, timedOut);
        }
        catch (Exception ex)
        {
            return new StatusLineResult([], StatusLineStrings.ErrorStartTemplate.Replace("{reason}", ex.Message), -1, false);
        }
        finally { if (process is not null) await process.DisposeAsync(); }
    }

    private static async Task<string> ReadCappedAsync(System.IO.TextReader reader, int maxBytes)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        var totalBytes = 0;
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buf, 0, buf.Length); } catch { break; }
            if (read == 0) break;
            var chunk = new string(buf, 0, read);
            totalBytes += Encoding.UTF8.GetByteCount(chunk);
            if (totalBytes > maxBytes) { sb.Append(chunk[..Math.Max(0, chunk.Length - (totalBytes - maxBytes) / 2)]); break; }
            sb.Append(chunk);
        }
        return sb.ToString();
    }

    private static IReadOnlyList<IReadOnlyList<AnsiSegment>> ParseLines(string output)
    {
        var rawLines = output.TrimEnd('\n', '\r').Split('\n');
        var result = new List<IReadOnlyList<AnsiSegment>>();
        foreach (var line in rawLines.Take(MaximumLines))
            result.Add(AnsiText.Parse(line.TrimEnd('\r')));
        return result;
    }
}
