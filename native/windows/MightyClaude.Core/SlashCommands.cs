using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

// What choosing a built-in does in the app instead of sending prompt text.
// The CLIs' own commands do not exist in headless mode, so the app performs
// the equivalent itself. SetModel / SetPermission carry their value in ActionArg.
public enum SlashCommandAction { OpenPlugins, NewConversation, ShowUsage, OpenSettings, Rename, Help, SetModel, SetPermission }

// A built-in whose argument the palette completes after /name <space>.
public enum SlashArgument { Model, Permission }

// Where a command came from, decided where it is discovered.
public enum SlashCommandOrigin { App, Project, User, Plugin }

// A skill or custom command the composer can complete after a leading /.
// Invocation is what the CLI expects; the composer inserts /invocation<space>.
// Entries with an Action run in the app and clear the draft instead.
// ActionArg carries the value for SetModel / SetPermission.
public sealed record SlashCommand(
    string Invocation,
    string Description,
    string Source,
    SlashCommandOrigin Origin,
    SlashCommandAction? Action = null,
    SlashArgument? Argument = null,
    string? ActionArg = null)
{
    public string Id => Invocation;
}

// Korean particle: 로/으로 chosen by the final consonant of the preceding word.
public static class KoreanParticle
{
    public static string Ro(string word)
    {
        if (string.IsNullOrEmpty(word)) return "로";
        var offset = word.Length >= 2 && char.IsLowSurrogate(word[^1]) ? 2 : 1;
        var last = char.ConvertToUtf32(word, word.Length - offset);
        if (last < 0xAC00 || last > 0xD7A3) return "로";
        var final = (last - 0xAC00) % 28;
        return final == 0 || final == 8 ? "로" : "으로";
    }
}

// Scans the same places the CLIs read: user and project skills and commands,
// installed Claude plugins, Codex skills. Pure file reads, no CLI calls.
// Caps the list at MaximumCommands = 400.
public static class SlashCommandCatalog
{
    public const int MaximumCommands = 400;
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    // The token being completed: "/ar" → "ar", "/" → "". Null once a space
    // follows the command (arguments are being typed) or draft does not start with /.
    public static string? Query(string draft)
    {
        if (!draft.StartsWith('/')) return null;
        var rest = draft[1..];
        if (rest.Any(char.IsWhiteSpace)) return null;
        if (rest.Length > 80) return null;
        return rest;
    }

    // "/model cla" → ("model","cla"), "/model " → ("model",""). Null unless
    // exactly one space follows a plain command name.
    public static (string Command, string Query)? ArgumentQuery(string draft)
    {
        if (!draft.StartsWith('/') || draft.Length > 160) return null;
        var rest = draft[1..];
        var spaceIdx = rest.IndexOf(' ');
        if (spaceIdx < 0) return null;
        var commandPart = rest[..spaceIdx];
        var queryPart = rest[(spaceIdx + 1)..];
        if (queryPart.Any(char.IsWhiteSpace)) return null;
        var command = ValidName(commandPart);
        if (command == null) return null;
        return (command, queryPart);
    }

    // Built-in app commands using the CLI names each provider's users know.
    // Gemini has no plugin browser; shell sessions have no commands.
    public static SlashCommand[] Builtins(string provider)
    {
        SlashCommand App(string name, string desc, SlashCommandAction? action = null, SlashArgument? argument = null) =>
            new(name, desc, SlashCommandStrings.AppSource, SlashCommandOrigin.App, action, argument);
        var model = App("model", "모델 바꾸기 · 이름을 이어서 고르세요", argument: SlashArgument.Model);
        var rename = App("rename", "실행 창 이름 바꾸기", SlashCommandAction.Rename);
        var help = App("help", "이 실행 창에서 쓸 수 있는 앱 명령 보기", SlashCommandAction.Help);
        return provider switch
        {
            "claude" =>
            [
                App("plugin", "플러그인 마켓플레이스 열기", SlashCommandAction.OpenPlugins), model,
                App("permissions", "작업 권한 바꾸기 · 모드를 이어서 고르세요", argument: SlashArgument.Permission),
                App("clear", "새 대화로 시작 · 다음 입력부터 이전 대화를 잇지 않음", SlashCommandAction.NewConversation),
                App("cost", "이 실행 창의 토큰·비용 보기", SlashCommandAction.ShowUsage),
                App("usage", "이 실행 창의 토큰·비용 보기", SlashCommandAction.ShowUsage),
                App("config", "MightyClaude 설정 열기", SlashCommandAction.OpenSettings), rename, help
            ],
            "codex" =>
            [
                App("plugins", "플러그인 마켓플레이스 열기", SlashCommandAction.OpenPlugins), model,
                App("approvals", "작업 권한 바꾸기 · 모드를 이어서 고르세요", argument: SlashArgument.Permission),
                App("new", "새 대화로 시작 · 다음 입력부터 이전 대화를 잇지 않음", SlashCommandAction.NewConversation),
                App("status", "이 실행 창의 토큰·비용 보기", SlashCommandAction.ShowUsage),
                App("settings", "MightyClaude 설정 열기", SlashCommandAction.OpenSettings), rename, help
            ],
            "gemini" =>
            [
                model,
                App("approval-mode", "작업 권한 바꾸기 · 모드를 이어서 고르세요", argument: SlashArgument.Permission),
                App("clear", "새 대화로 시작 · 다음 입력부터 이전 대화를 잇지 않음", SlashCommandAction.NewConversation),
                App("stats", "이 실행 창의 토큰·비용 보기", SlashCommandAction.ShowUsage),
                App("settings", "MightyClaude 설정 열기", SlashCommandAction.OpenSettings), rename, help
            ],
            _ => []
        };
    }

    // The /help text: one line per built-in.
    public static string HelpText(string provider)
    {
        var lines = Builtins(provider).Select(c => "/" + c.Invocation + " · " + c.Description);
        return "앱 명령 · " + ProviderLabel(provider) + " 실행 창\n"
            + string.Join("\n", lines)
            + "\n그 밖의 /이름은 스킬·사용자 명령·플러그인 명령으로 CLI에 전달됩니다.";
    }

    // Prefix matches first (by invocation), then after-colon prefix matches,
    // then substring matches of invocation or description.
    public static SlashCommand[] Filter(IReadOnlyList<SlashCommand> commands, string query)
    {
        var needle = query.ToLowerInvariant();
        if (needle.Length == 0) return [.. commands];
        var prefix = commands.Where(c => c.Invocation.ToLowerInvariant().StartsWith(needle)).ToList();
        var prefixSet = new HashSet<SlashCommand>(prefix);
        var afterColon = commands.Where(c =>
            !prefixSet.Contains(c) &&
            c.Invocation.Split(':').Skip(1).Any(p => p.ToLowerInvariant().StartsWith(needle))
        ).ToList();
        var colonSet = new HashSet<SlashCommand>(afterColon);
        var contains = commands.Where(c =>
            !prefixSet.Contains(c) && !colonSet.Contains(c) &&
            (c.Invocation.ToLowerInvariant().Contains(needle) || c.Description.ToLowerInvariant().Contains(needle))
        ).ToList();
        return [.. prefix, .. afterColon, .. contains];
    }

    // Everything available to provider for a pane in workspacePath.
    // Project entries shadow user entries with the same invocation.
    // home defaults to the current user's profile directory.
    public static SlashCommand[] Commands(string provider, string? workspacePath, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<SlashCommand>();
        switch (provider)
        {
            case "claude":
                found.AddRange(Skills(Path.Combine(home, ".claude", "skills"), SlashCommandStrings.UserSkillSource, SlashCommandOrigin.User));
                found.AddRange(CommandFiles(Path.Combine(home, ".claude", "commands"), SlashCommandStrings.UserCommandSource, SlashCommandOrigin.User));
                found.AddRange(PluginCommands(home));
                if (workspacePath is not null)
                {
                    found.AddRange(Skills(Path.Combine(workspacePath, ".claude", "skills"), SlashCommandStrings.ProjectSkillSource, SlashCommandOrigin.Project));
                    found.AddRange(CommandFiles(Path.Combine(workspacePath, ".claude", "commands"), SlashCommandStrings.ProjectCommandSource, SlashCommandOrigin.Project));
                }
                break;
            case "codex":
                found.AddRange(Skills(Path.Combine(home, ".codex", "skills"), SlashCommandStrings.CodexSkillSource, SlashCommandOrigin.User));
                if (workspacePath is not null)
                    found.AddRange(Skills(Path.Combine(workspacePath, ".codex", "skills"), SlashCommandStrings.ProjectSkillSource, SlashCommandOrigin.Project));
                break;
        }
        // Later sources (project) win over earlier ones (user, plugins).
        var byInvocation = new Dictionary<string, SlashCommand>(StringComparer.Ordinal);
        foreach (var command in found) byInvocation[command.Invocation] = command;
        return [.. byInvocation.Values
            .OrderBy(c => c.Invocation, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCommands)];
    }

    // <dir>/<name>/SKILL.md; the frontmatter name wins over the folder name.
    private static List<SlashCommand> Skills(string directory, string source, SlashCommandOrigin origin, string prefix = "")
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            var result = new List<SlashCommand>();
            foreach (var folder in Directory.GetDirectories(directory)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Take(MaximumCommands))
            {
                var file = Path.Combine(folder, "SKILL.md");
                if (!ReadFile(file, out var text)) continue;
                var fields = Frontmatter(text);
                var folderName = Path.GetFileName(folder);
                var name = (fields.TryGetValue("name", out var n) ? ValidName(n) : null) ?? ValidName(folderName);
                if (name == null) continue;
                var desc = fields.TryGetValue("description", out var d) ? Clean(d) : "";
                result.Add(new SlashCommand(prefix + name, desc, source, origin));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // <dir>/<name>.md and <dir>/<group>/<name>.md (invoked as group:name).
    private static List<SlashCommand> CommandFiles(string directory, string source, SlashCommandOrigin origin, string prefix = "")
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<SlashCommand>();
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(directory)
                .Where(e => !Path.GetFileName(e).StartsWith('.'))
                .OrderBy(e => Path.GetFileName(e), StringComparer.Ordinal)
                .Take(MaximumCommands))
            {
                var fileName = Path.GetFileName(entry);
                if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(entry))
                {
                    var baseName = ValidName(Path.GetFileNameWithoutExtension(fileName));
                    if (baseName == null) continue;
                    if (!ReadFile(entry, out var text)) continue;
                    var fields = Frontmatter(text);
                    var desc = fields.TryGetValue("description", out var d) ? Clean(d) : FirstLine(text);
                    result.Add(new SlashCommand(prefix + baseName, desc, source, origin));
                }
                else if (Directory.Exists(entry))
                {
                    var groupName = ValidName(fileName);
                    if (groupName == null) continue;
                    result.AddRange(CommandFiles(entry, source, origin, prefix + groupName + ":"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    // Installed Claude plugins (installed_plugins.json): each plugin's
    // skills/ and commands/ are invoked as <plugin>:<name>.
    private static List<SlashCommand> PluginCommands(string home)
    {
        var registry = Path.Combine(home, ".claude", "plugins", "installed_plugins.json");
        if (!File.Exists(registry)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(registry));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
                return [];
            var result = new List<SlashCommand>();
            foreach (var entry in plugins.EnumerateObject().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var plugin = ValidName(entry.Name.Split('@')[0]);
                if (plugin == null) continue;
                if (entry.Value.ValueKind != JsonValueKind.Array) continue;
                string? path = null;
                foreach (var install in entry.Value.EnumerateArray())
                {
                    if (install.TryGetProperty("installPath", out var p) && p.ValueKind == JsonValueKind.String)
                    {
                        var candidate = p.GetString();
                        // macOS uses /path; Windows uses C:\path. Path.IsPathRooted covers both.
                        if (!string.IsNullOrEmpty(candidate) && Path.IsPathRooted(candidate))
                        { path = candidate; break; }
                    }
                }
                if (path == null) continue;
                var source = "플러그인 " + plugin;
                result.AddRange(Skills(Path.Combine(path, "skills"), source, SlashCommandOrigin.Plugin, plugin + ":"));
                result.AddRange(CommandFiles(Path.Combine(path, "commands"), source, SlashCommandOrigin.Plugin, plugin + ":"));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    // ---\nkey: value\n--- frontmatter at the top of the file.
    // Values may be quoted; folded (>) and literal (|) values are skipped.
    public static Dictionary<string, string> Frontmatter(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return [];
        var fields = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1).Take(200))
        {
            if (line.Trim() == "---") break;
            if (line.StartsWith(' ') || line.StartsWith('\t')) continue;
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\''))
                value = value[1..^1];
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value) &&
                !value.StartsWith('>') && !value.StartsWith('|'))
                fields[key] = value;
        }
        return fields;
    }

    private static bool ReadFile(string path, out string content)
    {
        content = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 512 * 1024) return false;
            content = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? ValidName(string? value) =>
        value != null && NameRegex.IsMatch(value) ? value : null;

    private static string Clean(string? value)
    {
        if (value == null) return "";
        var s = value.Replace('\n', ' ').Trim();
        return s.Length > 240 ? s[..240] : s;
    }

    private static string FirstLine(string text)
    {
        var fm = Frontmatter(text);
        string body;
        if (fm.Count == 0)
            body = text;
        else
        {
            var idx = text.IndexOf("\n---", StringComparison.Ordinal);
            body = idx >= 0 ? text[(idx + 4)..] : text;
        }
        var line = body.Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
        return Clean(line ?? "");
    }

    private static string ProviderLabel(string provider) => provider switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "gemini" => "Gemini",
        _ => provider
    };
}

// The effect choosing the highlighted entry has on the draft.
// Insert leaves "/invocation " in the composer, AppAction runs in the app and
// clears it, ArgumentCompletion leaves "/name " so the palette stays open on
// that built-in's choices (AppStore+SlashCommands.swift applyCompletion).
public enum SlashChoiceEffect { Insert, AppAction, ArgumentCompletion }

// What choosing a palette entry does: the draft it leaves behind, and the app
// action to run when there is one. ActionArg carries SetModel / SetPermission.
public sealed record SlashChoice(string Draft, SlashChoiceEffect Effect, SlashCommandAction? Action = null, string? ActionArg = null);

// Pure open/closed state for the completion list above the composer.
// Open when Commands is non-empty; the highlight wraps on MoveUp / MoveDown
// and is clamped when read, exactly as SessionPaneView.swift does.
public sealed record SlashPaletteState(SlashCommand[] Commands, int HighlightedIndex)
{
    public static readonly SlashPaletteState Closed = new([], 0);
    public bool IsOpen => Commands.Length > 0;
    // macOS reads the highlight as min(paletteIndex, count - 1).
    public int SafeIndex => Commands.Length == 0 ? 0 : Math.Min(Math.Max(HighlightedIndex, 0), Commands.Length - 1);
    public SlashCommand? Highlighted => Commands.Length == 0 ? null : Commands[SafeIndex];
    // Up/Down wrap: (index -/+ 1 + count) % count, matching macOS handlePaletteKey.
    public SlashPaletteState MoveUp() => Commands.Length == 0 ? this
        : this with { HighlightedIndex = (SafeIndex - 1 + Commands.Length) % Commands.Length };
    public SlashPaletteState MoveDown() => Commands.Length == 0 ? this
        : this with { HighlightedIndex = (SafeIndex + 1) % Commands.Length };
    // Choosing the highlighted entry. An app action clears the draft; a built-in
    // that takes an argument leaves "/name " and keeps the list open on it.
    public SlashChoice Choose() => Choose(Highlighted);
    public static SlashChoice Choose(SlashCommand? command)
    {
        if (command is null) return new("", SlashChoiceEffect.Insert);
        if (command.Action is { } action) return new("", SlashChoiceEffect.AppAction, action, command.ActionArg);
        return new("/" + command.Invocation + " ",
            command.Argument is null ? SlashChoiceEffect.Insert : SlashChoiceEffect.ArgumentCompletion);
    }
    public static SlashPaletteState Open(SlashCommand[] commands) => new(commands, 0);
}

// Builds the palette state the composer renders. Everything here is pure, so
// Core.Tests proves the screen's behaviour on the Mac without a window.
public static class SlashPalette
{
    // macOS shows at most 60 rows (SessionPaneView.paletteCommands prefix(60)).
    public const int MaximumRows = 60;

    // App actions this client has no screen for yet. Their built-ins are left
    // out of the palette instead of appearing as a row that does nothing.
    // See docs/windows-slash-commands.md.
    public static readonly SlashCommandAction[] UnavailableActions = [SlashCommandAction.OpenPlugins];

    // The built-ins the Windows palette offers: macOS's list minus the entries
    // whose action this client cannot perform.
    public static SlashCommand[] Builtins(string provider) =>
        [.. SlashCommandCatalog.Builtins(provider)
            .Where(c => c.Action is not { } action || !UnavailableActions.Contains(action))];

    // The draft while it is a "/name" being typed or a built-in's "/name arg",
    // or null when the palette should be closed (SessionPaneView.paletteDraft).
    // dismissedFor is the draft Esc closed the list for.
    public static string? Draft(string kind, string draft, string? dismissedFor)
    {
        if (kind == "shell" || dismissedFor == draft) return null;
        return SlashCommandCatalog.Query(draft) is not null || SlashCommandCatalog.ArgumentQuery(draft) is not null
            ? draft : null;
    }

    // What the list shows for draft: built-ins and scanned commands while a name
    // is typed, or a built-in's choices after "/name " (AppStore.slashPalette).
    // A scanned command that shares a built-in's name loses to the built-in.
    public static SlashCommand[] Rows(string provider, string kind, string draft,
        IReadOnlyList<SlashCommand> scanned, Func<SlashArgument, string, SlashCommand[]>? argumentChoices = null)
    {
        if (kind == "shell") return [];
        var builtins = Builtins(provider);
        if (SlashCommandCatalog.Query(draft) is { } query)
        {
            var names = builtins.Select(c => c.Invocation).ToHashSet(StringComparer.Ordinal);
            return SlashCommandCatalog.Filter([.. builtins, .. scanned.Where(c => !names.Contains(c.Invocation))], query);
        }
        if (SlashCommandCatalog.ArgumentQuery(draft) is not { } parsed) return [];
        var argument = builtins.FirstOrDefault(c => c.Invocation == parsed.Command)?.Argument;
        if (argument is not { } kindOfArgument || argumentChoices is null) return [];
        // Choices are invoked as "name value", so the query keeps the name.
        return SlashCommandCatalog.Filter(argumentChoices(kindOfArgument, parsed.Command), parsed.Command + " " + parsed.Query);
    }

    // The whole state for a draft, with the highlight reset to the first row.
    public static SlashPaletteState State(string provider, string kind, string draft, string? dismissedFor,
        IReadOnlyList<SlashCommand> scanned, Func<SlashArgument, string, SlashCommand[]>? argumentChoices = null)
    {
        if (Draft(kind, draft, dismissedFor) is not { } text) return SlashPaletteState.Closed;
        var rows = Rows(provider, kind, text, scanned, argumentChoices);
        if (rows.Length == 0) return SlashPaletteState.Closed;
        return SlashPaletteState.Open(rows.Length > MaximumRows ? rows[..MaximumRows] : rows);
    }

    // The model rows "/model " lists, invoked as "model <value>" so the
    // palette can filter them with the command name still attached.
    public static SlashCommand[] ModelChoices(string command, IEnumerable<(string Value, string DisplayName)> options, string current) =>
        [.. options.Select(option => new SlashCommand(
            command + " " + option.Value,
            option.DisplayName + (option.Value == current ? SlashCommandStrings.PaletteCurrentSuffix : ""),
            SlashCommandStrings.ModelSource, SlashCommandOrigin.App,
            SlashCommandAction.SetModel, null, option.Value))];

    // The permission-mode rows "/permissions " lists. label turns a mode into
    // the same Korean word the composer's permission button shows.
    public static SlashCommand[] PermissionChoices(string command, IEnumerable<string> modes, string current, Func<string, string> label) =>
        [.. modes.Select(mode => new SlashCommand(
            command + " " + mode,
            label(mode) + (mode == current ? SlashCommandStrings.PaletteCurrentSuffix : ""),
            SlashCommandStrings.PermissionSource, SlashCommandOrigin.App,
            SlashCommandAction.SetPermission, null, mode))];

    // A row's description, falling back to macOS's "설명 없음".
    public static string Description(SlashCommand command) =>
        command.Description.Length == 0 ? SlashCommandStrings.PaletteNoDescription : command.Description;

    // The footer's "{count}개".
    public static string CountLabel(int count) =>
        SlashCommandStrings.PaletteCountTemplate.Replace("{count}", count.ToString());
}

// In-memory freshness cache for slash catalogs. The WinUI layer calls IsStale
// to decide when to rescan off the UI thread, mirroring AppStore+SlashCommands.swift
// which rescans when the cache is missing or older than 30 seconds.
public sealed class SlashCatalogCache
{
    private sealed record Entry(SlashCommand[] Commands, DateTimeOffset ScannedAt);
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public bool IsStale(string provider, string? workspacePath) =>
        !cache.TryGetValue(Key(provider, workspacePath), out var e) || DateTimeOffset.UtcNow - e.ScannedAt > Ttl;

    public SlashCommand[]? Get(string provider, string? workspacePath) =>
        cache.TryGetValue(Key(provider, workspacePath), out var e) && DateTimeOffset.UtcNow - e.ScannedAt <= Ttl
            ? e.Commands : null;

    public void Set(string provider, string? workspacePath, SlashCommand[] commands) =>
        cache[Key(provider, workspacePath)] = new Entry(commands, DateTimeOffset.UtcNow);

    private static string Key(string provider, string? workspacePath) => provider + "|" + (workspacePath ?? "");
}
