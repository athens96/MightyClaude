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
public sealed record StatusLineConfig(string Command, int Padding, string Source, bool FromWorkspace)
{
    // SHA-256(source + newline + command) as 64-char lowercase hex — matches macOS fingerprint.
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source + "\n" + Command))).ToLowerInvariant();
}

// Both levels Claude consults (macOS StatusLineConfig.Discovery): Workspace is the winning
// .claude/settings.local.json / settings.json entry, User is ~/.claude/settings.json (or
// CLAUDE_CONFIG_DIR). Kept apart so a gated workspace command can still fall back to the user one.
public sealed record StatusLineDiscovery(StatusLineConfig? Workspace, StatusLineConfig? User)
{
    // Claude's own precedence for callers that already trust the workspace level.
    public StatusLineConfig? Preferred => Workspace ?? User;
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
    bool ThinkingEnabled,
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

    // Compute the transcript path following the macOS slug rule.
    public static string TranscriptPath(string configDir, string? projectDir, string sessionId)
    {
        var raw = projectDir ?? "";
        var slug = new StringBuilder();
        foreach (var c in raw) slug.Append(char.IsLetterOrDigit(c) ? c : '-');
        var s = slug.ToString().TrimStart('-');
        if (s.Length > 200) s = s[..200];
        if (s.Length == 0) s = "default";
        return Path.Combine(configDir, "projects", s, sessionId + ".jsonl");
    }

    // Build the JSON payload matching the macOS CLI field names.
    public static string BuildPayload(StatusLineContext ctx)
    {
        var obj = new JsonObject
        {
            ["hook_event_name"] = "StatusLineUpdate",
            ["session_id"] = ctx.SessionId,
            ["transcript_path"] = ctx.TranscriptPath,
            ["cwd"] = ctx.Cwd,
            ["model"] = ctx.ModelId,
            ["model_name"] = ctx.ModelName,
            ["version"] = ctx.Version,
            ["cost_usd"] = ctx.CostUSD,
            ["duration_ms"] = ctx.DurationMs,
            ["api_duration_ms"] = ctx.ApiDurationMs,
            ["output_style"] = ctx.OutputStyle,
            ["thinking_enabled"] = ctx.ThinkingEnabled,
            ["fast_mode"] = ctx.FastMode,
            ["workspace"] = ctx.ProjectDir,
        };
        if (ctx.InputTokens.HasValue || ctx.OutputTokens.HasValue || ctx.ContextUsedTokens.HasValue)
        {
            obj["context_window"] = new JsonObject
            {
                ["input_tokens"] = ctx.InputTokens,
                ["output_tokens"] = ctx.OutputTokens,
                ["cache_read_tokens"] = ctx.CacheReadTokens,
                ["cache_write_tokens"] = ctx.CacheWriteTokens,
                ["context_tokens_used"] = ctx.ContextUsedTokens,
                ["context_window_size"] = ctx.ContextWindowTokens,
            };
        }
        else { obj["context_window"] = null; }
        if (ctx.RateLimits is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var r in ctx.RateLimits)
            {
                var item = new JsonObject { ["kind"] = r.Kind };
                if (r.PercentUsed.HasValue) item["percent_used"] = r.PercentUsed.Value;
                if (r.ResetsAt is not null) item["resets_at"] = r.ResetsAt;
                arr.Add(item);
            }
            obj["rate_limits"] = arr;
        }
        else { obj["rate_limits"] = null; }
        return obj.ToJsonString();
    }

    // Discover both levels Claude consults (macOS StatusLineConfig.discover): workspace-local
    // then workspace at the workspace path (first of the two that has a command wins), and the
    // user-level config from configDir. Kept apart — see StatusLineDiscovery — so a workspace
    // command still waiting on trust does not blank the status line while a user command exists.
    public static StatusLineDiscovery Discover(string? workspacePath, string? homeDir, IDictionary<string, string>? env = null)
    {
        StatusLineConfig? workspace = null;
        if (workspacePath is not null)
            workspace = TryReadCommand(Path.Combine(workspacePath, ".claude", "settings.local.json"), fromWorkspace: true, StatusLineStrings.SourceWorkspaceLocal)
                     ?? TryReadCommand(Path.Combine(workspacePath, ".claude", "settings.json"), fromWorkspace: true, StatusLineStrings.SourceWorkspace);
        var configDir = ConfigDir(homeDir, env);
        var user = configDir is null ? null : TryReadCommand(Path.Combine(configDir, "settings.json"), fromWorkspace: false, StatusLineStrings.SourceUser);
        return new StatusLineDiscovery(workspace, user);
    }

    public static string? ConfigDir(string? homeDir, IDictionary<string, string>? env = null)
    {
        if (env is not null && env.TryGetValue("CLAUDE_CONFIG_DIR", out var envDir) && envDir is { Length: > 0 }) return envDir;
        if (homeDir is null) return null;
        return Path.Combine(homeDir, ".claude");
    }

    private static StatusLineConfig? TryReadCommand(string settingsPath, bool fromWorkspace, string source)
    {
        try
        {
            var text = File.ReadAllText(settingsPath);
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("statusLine", out var sl) &&
                sl.TryGetProperty("command", out var cmdEl) &&
                cmdEl.GetString() is { Length: > 0 } command)
            {
                var padding = sl.TryGetProperty("padding", out var padEl) && padEl.TryGetInt32(out var p) ? p : 0;
                return new StatusLineConfig(command, padding, source, fromWorkspace);
            }
        }
        catch { }
        return null;
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
