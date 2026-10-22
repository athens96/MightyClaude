using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

// Antigravity permission cards (docs/superpowers/plans/2026-10-22-antigravity-permission-cards.md, M/MightyCore/AntigravityPermissions.swift).
// A headless `agy -p` turn cannot ask: what its review mode would have asked about is declined and the turn goes on. The stream says
// exactly what was declined, so once the turn is over the pane asks the way a Claude card does, and an allow becomes a rule in agy's own
// settings (%USERPROFILE%\.gemini\antigravity-cli\settings.json, permissions.allow) for a follow-up turn in the same conversation.

/// <summary>
/// One action a turn was refused. <c>Kind</c> is command, url, file or other (a kind a newer host sends reads as other);
/// <c>Action</c> is agy's permission name (command, write_file, read_file, read_url, execute_url, mcp) or the tool's own name;
/// <c>DisplayName</c> agy's name for it (RunCommand, WriteFile), else the tool's; <c>Target</c> the command line, domain or path when
/// agy said; <c>Rule</c> the permissions.allow rule that allows exactly this, none when no rule can (the card then says to change the
/// permission mode instead).
/// </summary>
public sealed record AntigravityDeniedAction([property: JsonConverter(typeof(AntigravityKindConverter))] string Kind, string Action, string DisplayName, string? Target = null, string? Rule = null)
{
    public static readonly IReadOnlyList<string> Kinds = ["command", "url", "file", "other"];
    /// <summary>A kind this app does not know reads as other.</summary>
    public static string KindOf(string? value) => value is not null && Kinds.Contains(value) ? value : "other";
    /// <summary>Its rule may be allowed only for the follow-up run (<see cref="AntigravityRuleSafety"/>).</summary>
    [JsonIgnore] public bool OnceOnly => Rule is { } rule && AntigravityRuleSafety.Verdict(rule) == AntigravityRuleVerdict.Once;
}

/// <summary>Reads an action's kind as the Mac's decoder does: a string, and one it does not know is <c>other</c>.</summary>
public sealed class AntigravityKindConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? AntigravityDeniedAction.KindOf(reader.GetString()) : throw new JsonException();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>
/// The card one Antigravity turn leaves: what it was refused, together. <c>Id</c> is <c>agy-&lt;uuid&gt;</c>, <c>RunId</c> the refused
/// run's activity id (the phone answers by both), <c>ReceivedAt</c> when the turn ended.
/// </summary>
public sealed record AntigravityPermissionRequest(string Id, string RunId, IReadOnlyList<AntigravityDeniedAction> Actions, string? ReceivedAt = null)
{
    public const int MaximumActions = 16;
    /// <summary>A new card of <paramref name="runId"/>, stamped now as the Mac stamps it (whole seconds).</summary>
    public static AntigravityPermissionRequest Make(string runId, IReadOnlyList<AntigravityDeniedAction> actions) =>
        new("agy-" + Guid.NewGuid().ToString("D"), runId, actions, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    /// <summary>The rules an allow adds, once each, in order (never one <see cref="AntigravityRuleSafety"/> refuses).</summary>
    [JsonIgnore] public IReadOnlyList<string> Rules => Actions.Select(a => a.Rule).OfType<string>().Where(rule => AntigravityRuleSafety.Verdict(rule) != AntigravityRuleVerdict.Refused).Distinct(StringComparer.Ordinal).ToList();
    /// <summary>At least one action can be allowed with a rule.</summary>
    [JsonIgnore] public bool CanAllow => Rules.Count > 0;
    /// <summary>항상 허용 is offered: every rule may be kept (none chains, pipes, redirects or runs a destructive command).</summary>
    [JsonIgnore] public bool CanAllowAlways => CanAllow && Rules.All(rule => AntigravityRuleSafety.Verdict(rule) == AntigravityRuleVerdict.Always);
    /// <summary>What the transcript line and the continuation name: the targets, else agy's names.</summary>
    [JsonIgnore] public IReadOnlyList<string> Names => Actions.Select(a => a.Target ?? a.DisplayName).ToList();
}

/// <summary>The card's three buttons: 이번만 허용, 항상 허용, 거절.</summary>
public enum AntigravityPermissionDecision { Once, Always, Deny }

/// <summary>
/// What the follow-up run of an allowed card carries: the rules to put in place while it runs (<c>Temporary</c>) or for good.
/// <c>RequestId</c> is the card's: a host that runs remote panes adds only rules it reported itself.
/// </summary>
public sealed record AntigravityAllowGrant(string RequestId, IReadOnlyList<string> Rules, bool Temporary)
{
    public const int MaximumRuleBytes = 4_096;
    private static readonly Regex RuleShape = new(@"\A[a-z][a-z_]{0,63}\(.+\)\z", RegexOptions.CultureInvariant);
    /// <summary>A rule is <c>name(…)</c>: a lowercase permission name and a non-empty argument, at most 4 KiB, no NUL, no line break.</summary>
    public static bool ValidRule(string? rule) =>
        rule is not null && Encoding.UTF8.GetByteCount(rule) <= MaximumRuleBytes && !rule.Contains('\0') && !rule.Contains('\n') && !rule.Contains('\r') && RuleShape.IsMatch(rule);
    /// <summary>Well formed, and no rule <see cref="AntigravityRuleSafety"/> refuses; whether it may be kept for good is <see cref="KeepsOnlyAllowedRules"/>.</summary>
    [JsonIgnore] public bool IsValid => Wire.Identifier(RequestId) && Rules is { Count: > 0 and <= AntigravityPermissionRequest.MaximumActions }
        && Rules.All(rule => ValidRule(rule) && AntigravityRuleSafety.Verdict(rule) != AntigravityRuleVerdict.Refused);
    /// <summary>A temporary grant, or one whose every rule may be kept.</summary>
    [JsonIgnore] public bool KeepsOnlyAllowedRules => Temporary || Rules.All(rule => AntigravityRuleSafety.Verdict(rule) == AntigravityRuleVerdict.Always);
    public bool Same(AntigravityAllowGrant? other) => other is not null && other.RequestId == RequestId && other.Temporary == Temporary && other.Rules.SequenceEqual(Rules);
}

/// <summary>Refused: never offered or added. Once: offered and added only for the follow-up run (no 항상 허용). Always: may be kept.</summary>
public enum AntigravityRuleVerdict { Refused, Once, Always }

/// <summary>
/// Which rules an allow may add (M/MightyCore/AntigravityPermissions.swift AntigravityRuleSafety). agy's rules are prefixes (probed on agy
/// 1.4.5: command(echo hi) allows "echo hi there", write_file(&lt;folder&gt;) everything in it, command(*) every command) and a temporary
/// one applies to every agy run on the computer while it is in place, so they are kept narrow: a closed mapping from tool to permission,
/// no wildcard, no whole disk, no parenthesis, and a command that chains, pipes, redirects or runs one of a closed list of destructive
/// programs only once.
/// </summary>
public static class AntigravityRuleSafety
{
    /// <summary>The permission an agy tool's rule names; any other tool gets no rule.</summary>
    public static readonly IReadOnlyDictionary<string, string> ToolPermissions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["run_command"] = "command",
        ["write_file"] = "write_file", ["write_to_file"] = "write_file", ["replace_file_content"] = "write_file", ["multi_replace_file_content"] = "write_file",
        ["sed_file"] = "write_file", ["notebook_edit"] = "write_file",
        ["view_file"] = "read_file", ["read_file"] = "read_file", ["list_dir"] = "read_file",
        ["read_url_content"] = "read_url",
    };
    /// <summary>The permissions a rule may name.</summary>
    public static readonly IReadOnlySet<string> Permissions = ToolPermissions.Values.ToHashSet(StringComparer.Ordinal);
    /// <summary>Programs that destroy data, escalate or end processes (by name in lower case, without a folder or .exe); mkfs… too.</summary>
    public static readonly IReadOnlySet<string> DestructivePrograms = new HashSet<string>(StringComparer.Ordinal)
    {
        "rm", "rmdir", "unlink", "shred", "dd", "sudo", "su", "doas", "kill", "killall", "pkill", "shutdown", "reboot", "halt", "poweroff", "eval",
        "del", "erase", "rd", "format", "remove-item", "taskkill", "stop-process", "stop-computer", "restart-computer",
    };
    /// <summary>Characters that chain, pipe, redirect, background or substitute.</summary>
    public static readonly IReadOnlySet<char> ChainCharacters = new HashSet<char> { ';', '&', '|', '>', '<', '`' };

    /// <summary>The name and the argument of a well-formed rule.</summary>
    public static (string Name, string Argument)? Parts(string rule)
    {
        if (!AntigravityAllowGrant.ValidRule(rule)) return null;
        var open = rule.IndexOf('(');
        return (rule[..open], rule[(open + 1)..^1]);
    }

    public static AntigravityRuleVerdict Verdict(string rule)
    {
        if (Parts(rule) is not { } parts || !Permissions.Contains(parts.Name) || RefusedArgument(parts.Argument)) return AntigravityRuleVerdict.Refused;
        return parts.Name == "command" && Risky(parts.Argument) ? AntigravityRuleVerdict.Once : AntigravityRuleVerdict.Always;
    }

    /// <summary>Empty, the whole disk or home (/, ~, C:\), a wildcard, a parenthesis or a control character.</summary>
    internal static bool RefusedArgument(string argument)
    {
        if (argument.Contains('*') || argument.Contains('(') || argument.Contains(')')) return true;
        if (argument.Any(c => c < 0x20 || c == 0x7F)) return true;
        var core = argument.Trim(' ', '\t').TrimEnd('/', '\\').Trim(' ', '\t');
        if (core.Length == 0 || core == "~") return true;
        return core.Length == 2 && core[1] == ':' && char.IsAsciiLetter(core[0]);
    }

    /// <summary>A command line that may be allowed only once.</summary>
    public static bool Risky(string command)
    {
        if (command.Any(ChainCharacters.Contains) || command.Contains("$(", StringComparison.Ordinal)) return true;
        var words = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(word =>
        {
            var value = word.Trim('"', '\'');
            var slash = value.LastIndexOfAny(['/', '\\']);
            if (slash >= 0) value = value[(slash + 1)..];
            value = value.ToLowerInvariant();
            return value.EndsWith(".exe", StringComparison.Ordinal) ? value[..^4] : value;
        }).ToList();
        if (words.Any(word => DestructivePrograms.Contains(word) || word.StartsWith("mkfs", StringComparison.Ordinal))) return true;
        static bool ShortFlag(string word, char letter) => word.StartsWith('-') && !word.StartsWith("--", StringComparison.Ordinal) && word.Contains(letter);
        if (words.Any(word => word is "chmod" or "chown") && words.Any(word => ShortFlag(word, 'r') || word == "--recursive")) return true;
        if (words.Contains("git"))
        {
            if (words.Contains("push") && words.Any(word => word == "-f" || word.StartsWith("--force", StringComparison.Ordinal) || word.StartsWith('+') || ShortFlag(word, 'f'))) return true;
            if (words.Contains("reset") && words.Contains("--hard")) return true;
            if (words.Contains("clean")) return true;
        }
        return words.Contains("find") && words.Contains("-delete");
    }
}

/// <summary>
/// Collects one turn's refusals from agy's stream-json: each tool step that ended ERROR with a permission denial ("permission check
/// failed … user denied permission …") whose permission the result's denied_actions lists, then what denied_actions lists that no step showed.
/// </summary>
public sealed class AntigravityDenialCollector
{
    private readonly Dictionary<long, AntigravityDeniedAction> steps = [];
    private readonly List<long> order = [];
    private sealed record Reported(string Action, string Name);

    /// <summary>A denial's message, or null when the step's error is something else.</summary>
    public static string? DenialMessage(JsonElement info)
    {
        var error = MetadataJson.Property(info, "error");
        var message = error.ValueKind == JsonValueKind.Object ? error.Text("message") : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        if (message is null || Encoding.UTF8.GetByteCount(message) > 65_536) return null;
        var lowered = message.ToLowerInvariant();
        return lowered.Contains("permission check failed", StringComparison.Ordinal) && lowered.Contains("denied permission", StringComparison.Ordinal) ? message : null;
    }

    /// <summary>One <c>step_update</c> payload of a tool step.</summary>
    public void Observe(JsonElement payload)
    {
        if ((payload.Text("state") ?? "").ToUpperInvariant() != "ERROR" || MetadataJson.Integer(payload, "step_index") is not { } index) return;
        var info = MetadataJson.Property(payload, "tool_info");
        if (DenialMessage(info) is not { } message) return;
        var tool = info.Text("name") ?? payload.Text("tool_name") ?? "tool";
        if (!steps.ContainsKey(index)) order.Add(index);
        steps[index] = Action(Prefix(tool, 128), MetadataJson.Property(info, "parameters"), message);
    }

    /// <summary>The turn's <c>result</c> payload: every refusal, once each. Starts over for the next turn.</summary>
    public IReadOnlyList<AntigravityDeniedAction> Finish(JsonElement payload)
    {
        try
        {
            var rows = MetadataJson.Property(payload, "denied_actions");
            var reported = rows.ValueKind != JsonValueKind.Array ? [] : rows.EnumerateArray().Take(AntigravityPermissionRequest.MaximumActions)
                .Select(row => row.Text("action") is { Length: > 0 } action ? new Reported(Prefix(action, 128), row.Text("display_name") is { } name ? Prefix(name, 128) : Prefix(action, 128)) : null)
                .OfType<Reported>().ToList();
            var result = new List<AntigravityDeniedAction>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(AntigravityDeniedAction value)
            {
                var key = value.Rule ?? value.Kind + "|" + value.Action + "|" + (value.Target ?? "");
                if (result.Count < AntigravityPermissionRequest.MaximumActions && seen.Add(key)) result.Add(value);
            }
            // A step counts only when the result says its permission was refused.
            var listed = reported.Select(row => row.Action).ToHashSet(StringComparer.Ordinal);
            foreach (var index in order)
            {
                if (!steps.TryGetValue(index, out var value) || !listed.Contains(value.Action)) continue;
                if (reported.FirstOrDefault(row => row.Action == value.Action) is { } named) value = value with { DisplayName = named.Name };
                Add(value);
            }
            // A refusal the steps did not show (agy reports each permission once).
            var covered = result.Select(a => a.Action).ToHashSet(StringComparer.Ordinal);
            foreach (var row in reported.Where(row => !covered.Contains(row.Action))) Add(new(Kind(row.Action, row.Action), row.Action, row.Name));
            return result;
        }
        finally { steps.Clear(); order.Clear(); }
    }

    internal static string Kind(string action, string tool)
    {
        var name = action.ToLowerInvariant(); var toolName = tool.ToLowerInvariant();
        if (name == "command" || toolName == "run_command") return "command";
        if (name.EndsWith("_url", StringComparison.Ordinal) || name == "url" || toolName.Contains("url", StringComparison.Ordinal) || toolName.Contains("browser", StringComparison.Ordinal)) return "url";
        if (name.EndsWith("_file", StringComparison.Ordinal) || toolName is "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "sed_file" or "notebook_edit" or "view_file") return "file";
        return "other";
    }

    /// <summary>
    /// What one denied step was. A command is allowed by its whole command line (<c>command(&lt;line&gt;)</c>, a word-prefix rule in agy);
    /// any other denial names its own rule ("user denied permission for write_file(/path)"), used as agy wrote it when it names the tool's
    /// own permission (<see cref="AntigravityRuleSafety.ToolPermissions"/>). A rule the safety rules refuse, or a tool outside the mapping,
    /// leaves the action without one.
    /// </summary>
    internal static AntigravityDeniedAction Action(string tool, JsonElement parameters, string message)
    {
        string? Parameter(params string[] names)
        {
            foreach (var name in names) if (parameters.Text(name) is { Length: > 0 } value) return Prefix(value, AntigravityAllowGrant.MaximumRuleBytes);
            return null;
        }
        var permission = AntigravityRuleSafety.ToolPermissions.GetValueOrDefault(tool);
        if (permission == "command")
        {
            var line = Parameter("CommandLine", "Command", "command");
            var rule = line is null ? null : "command(" + line + ")";
            return new("command", "command", tool, line, rule is not null && AntigravityRuleSafety.Verdict(rule) != AntigravityRuleVerdict.Refused ? rule : null);
        }
        if (permission is not null && NamedRule(message) is { } named && AntigravityRuleSafety.Parts(named) is { } parts && parts.Name == permission
            && AntigravityRuleSafety.Verdict(named) != AntigravityRuleVerdict.Refused)
        {
            var name = parts.Name; var target = parts.Argument;
            var kind = Kind(name, tool);
            // A URL rule names a domain; show the domain, as the rule does.
            if (kind == "url" && Host(target) is { Length: > 0 } host) target = host;
            return new(kind, name, tool, target, named);
        }
        var plain = Kind(permission ?? tool, tool);
        var shown = plain switch
        {
            "url" => Parameter("Url", "URL", "url") is { } url ? Host(url) ?? url : null,
            "file" => Parameter("Path", "TargetFile", "AbsolutePath", "path"),
            "command" => Parameter("CommandLine"),
            _ => null,
        };
        return new(plain, permission ?? tool, tool, shown);
    }

    /// <summary>The rule a denial names: the text after "denied permission for " up to the end of its line, when it is a whole <c>name(…)</c> rule.</summary>
    public static string? NamedRule(string message)
    {
        var at = message.IndexOf("denied permission for ", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = message[(at + "denied permission for ".Length)..];
        var end = rest.IndexOfAny(['\n', '\r']);
        var line = (end < 0 ? rest : rest[..end]).Trim(' ', '\t');
        // "User denied permission for write_file(/x)." ends its sentence.
        var rule = line.EndsWith(").", StringComparison.Ordinal) ? line[..^1] : line;
        return AntigravityAllowGrant.ValidRule(rule) ? rule : null;
    }

    /// <summary>The host of an absolute URL as written (scheme://[user@]host[:port]/…); null for anything else, a bare domain included.</summary>
    internal static string? Host(string value)
    {
        var match = Regex.Match(value, @"\A[A-Za-z][A-Za-z0-9+.\-]*://([^/?#]*)", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var authority = match.Groups[1].Value;
        if (authority.LastIndexOf('@') is var user and >= 0) authority = authority[(user + 1)..];
        if (authority.StartsWith('[')) { var close = authority.IndexOf(']'); return close > 1 ? authority[1..close] : null; }
        var colon = authority.IndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        return host.Length > 0 ? host : null;
    }

    /// <summary>The first <paramref name="count"/> characters, never cutting a surrogate pair.</summary>
    private static string Prefix(string value, int count)
    {
        if (value.Length <= count) return value;
        var cut = char.IsHighSurrogate(value[count - 1]) ? count - 1 : count;
        return value[..cut];
    }
}

/// <summary>
/// The cards of every pane: one per pane, the last turn's. A card stays until it is answered, or until a later run of that pane ends —
/// that run's own card (if any) replaces it, and one without any leaves none. Not thread-safe: its owner locks around it.
/// </summary>
public sealed class AntigravityPermissionBoard
{
    private readonly Dictionary<string, AntigravityPermissionRequest> cards = new(StringComparer.Ordinal);
    /// <summary>Panes whose card came from the run that is still ending.</summary>
    private readonly HashSet<string> fromRunningTurn = new(StringComparer.Ordinal);

    public AntigravityPermissionRequest? this[string sessionId] => cards.GetValueOrDefault(sessionId);
    public IReadOnlyDictionary<string, AntigravityPermissionRequest> Cards => cards;

    /// <summary>A turn ended with refusals (before its run's final status).</summary>
    public void Received(AntigravityPermissionRequest card, string sessionId) { cards[sessionId] = card; fromRunningTurn.Add(sessionId); }
    /// <summary>A run of the pane ended (completed, error or stopped). True when the pane's card went.</summary>
    public bool RunEnded(string sessionId) => !fromRunningTurn.Remove(sessionId) && cards.Remove(sessionId);
    /// <summary>Puts back a card whose answer could not run; the next run's end clears it as usual.</summary>
    public bool Restore(AntigravityPermissionRequest card, string sessionId) => cards.TryAdd(sessionId, card);
    public AntigravityPermissionRequest? Remove(string sessionId)
    {
        fromRunningTurn.Remove(sessionId);
        return cards.Remove(sessionId, out var card) ? card : null;
    }
    public AntigravityPermissionRequest? Pending(string sessionId, string requestId, string runId) =>
        cards.TryGetValue(sessionId, out var card) && card.Id == requestId && card.RunId == runId ? card : null;
}

/// <summary>What an answer does, decided without the app: the grant and the user turn sent on the same conversation.</summary>
public sealed record AntigravityContinuation(AntigravityAllowGrant Grant, string Prompt);

public static class AntigravityPermissionFlow
{
    /// <summary>
    /// Why an allow cannot go ahead: no rule can allow the card, or 항상 허용 on a card with a rule that may be allowed only once. Null for
    /// deny and for an allow that can.
    /// </summary>
    public static string? Refusal(AntigravityPermissionRequest card, AntigravityPermissionDecision decision)
    {
        if (decision == AntigravityPermissionDecision.Deny) return null;
        if (!card.CanAllow) return Locale.Get("antigravity.permission.noRule");
        return decision == AntigravityPermissionDecision.Always && !card.CanAllowAlways ? Locale.Get("remote.error.antigravityOnceOnly") : null;
    }

    /// <summary>Null for deny, and for an allow <see cref="Refusal"/> refuses.</summary>
    public static AntigravityContinuation? Continuation(AntigravityPermissionRequest card, AntigravityPermissionDecision decision)
    {
        if (decision == AntigravityPermissionDecision.Deny || Refusal(card, decision) is not null) return null;
        var allowed = card.Actions.Where(a => a.Rule is not null).Select(a => a.Target ?? a.DisplayName);
        var list = ActivitySupport.Clean(string.Join(", ", allowed), 2_048, true);
        return new(new(card.Id, card.Rules, decision == AntigravityPermissionDecision.Once), Locale.Get("antigravity.permission.continue", new Dictionary<string, string> { ["actions"] = list }));
    }

    /// <summary>The short transcript line a card leaves (what an older phone shows).</summary>
    public static string TranscriptLine(AntigravityPermissionRequest card) =>
        Locale.Get("run.notice.antigravityAsked", new Dictionary<string, string> { ["actions"] = ActivitySupport.Clean(string.Join(", ", card.Names), 512, true) });

    /// <summary>What the card says an action's rule allows: commands starting with it, a file or everything in a folder, a domain.</summary>
    public static string? RuleLine(AntigravityDeniedAction action) => action.Rule is { } rule
        ? Locale.Get(action.Action == "command" ? "antigravity.permission.ruleCommand" : "antigravity.permission.rule", new Dictionary<string, string> { ["rule"] = rule })
        : null;

    /// <summary>The card's label for an action's kind.</summary>
    public static string KindLabel(string kind) => Locale.Get("antigravity.permission.kind." + AntigravityDeniedAction.KindOf(kind));

    /// <summary>The phone's and the desktop's answer to a permission card: allow alone is once, allow with always is always.</summary>
    public static AntigravityPermissionDecision Decision(bool allow, bool always) =>
        allow ? always ? AntigravityPermissionDecision.Always : AntigravityPermissionDecision.Once : AntigravityPermissionDecision.Deny;
}

/// <summary>Why agy's settings were left alone. <c>Kind</c> is the Mac's case name (missingFolder, unreadable, notJSON, notObject,
/// permissionsNotObject, allowNotArray, writeFailed, badRule, onceOnly); the message is the user's words.</summary>
public sealed class AntigravitySettingsException(string kind, string message) : InvalidOperationException(message)
{
    public string Kind { get; } = kind;
    internal static AntigravitySettingsException Of(string kind, string? path = null, string? error = null)
    {
        var values = new Dictionary<string, string>();
        if (path is not null) values["path"] = path;
        if (error is not null) values["error"] = error;
        // 항상 허용 for a rule that may be allowed only once says what the phone and a host say.
        return new(kind, Locale.Get(kind == "onceOnly" ? "remote.error.antigravityOnceOnly" : "antigravity.settings.error." + kind, values));
    }
}

/// <summary>
/// Edits permissions.allow in the bytes of agy's settings, leaving every other byte as it was: other keys, their order and values,
/// unknown fields, whitespace. Rules are added after the last one in the style of the list (or of the file, for a list it has to
/// create, with its line endings), and removing one takes out exactly what adding it put in; <see cref="Pruning"/> then takes out the
/// permissions or allow adding had to create, once empty (M/MightyCore/AntigravityPermissions.swift AntigravitySettingsText).
/// </summary>
public static class AntigravitySettingsText
{
    /// <summary>What adding made that was not there: the file, permissions (with its allow) or allow.</summary>
    public const string CreatedFile = "file", CreatedPermissions = "permissions", CreatedAllow = "allow";

    /// <summary>
    /// The file's rules; throws when it is not an object, permissions is not an object or allow is not an array (non-string entries are
    /// skipped). A UTF-8 byte order mark is not JSON here (agy writes none), as on the Mac.
    /// </summary>
    public static IReadOnlyList<string> Rules(byte[] data, string path)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) throw AntigravitySettingsException.Of("notJSON", path);
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException) { throw AntigravitySettingsException.Of("notJSON", path); }
        using (document)
        {
            var top = document.RootElement;
            if (top.ValueKind != JsonValueKind.Object) throw AntigravitySettingsException.Of("notObject", path);
            if (Last(top, "permissions") is not { } permissions) return [];
            if (permissions.ValueKind != JsonValueKind.Object) throw AntigravitySettingsException.Of("permissionsNotObject", path);
            if (Last(permissions, "allow") is not { } allow) return [];
            if (allow.ValueKind != JsonValueKind.Array) throw AntigravitySettingsException.Of("allowNotArray", path);
            return allow.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
    }

    private static JsonElement? Last(JsonElement value, string key)
    {
        JsonElement? found = null;
        foreach (var member in value.EnumerateObject()) if (member.Name == key) found = member.Value;
        return found;
    }

    /// <summary>A new settings file that holds only these rules.</summary>
    public static byte[] NewFile(IReadOnlyList<string> rules) => Encoding.UTF8.GetBytes("{\n  \"permissions\": {\n    \"allow\": " + Array(rules, "    ", "  ") + "\n  }\n}\n");

    /// <summary><paramref name="rules"/> not in the file yet, added. Returns the bytes, what was added and what had to be created for it.</summary>
    public static (byte[] Data, IReadOnlyList<string> Added, IReadOnlyList<string> Created) Adding(IReadOnlyList<string> rules, byte[] data, string path)
    {
        var existing = Rules(data, path).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = rules.Where(rule => !existing.Contains(rule) && seen.Add(rule)).ToList();
        if (missing.Count == 0) return (data, [], []);
        var b = new List<byte>(data);
        var newline = data.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        var scan = new Scanner(data, path);
        var top = scan.SkipWhitespace(0);
        if (top >= data.Length || data[top] != (byte)'{') throw AntigravitySettingsException.Of("notObject", path);
        var topObject = scan.Object(top);
        var unit = scan.LineIndent(topObject.Members.Count > 0 ? topObject.Members[0].KeyStart : -1) is { Length: > 0 } found ? found : "  ";
        if (topObject.Members.LastOrDefault(m => m.Key == "permissions") is not { } permissions)
        {
            InsertMember(b, scan, topObject, "", unit, newline, indent => "\"permissions\": " + AllowObject(missing, indent, unit, newline));
            return (b.ToArray(), missing, [CreatedPermissions]);
        }
        var permissionsIndent = scan.LineIndent(permissions.KeyStart);
        var section = scan.Object(permissions.ValueStart);
        if (section.Members.LastOrDefault(m => m.Key == "allow") is not { } allow)
        {
            InsertMember(b, scan, section, permissionsIndent, unit, newline, indent => "\"allow\": " + Array(missing, indent, unit, newline));
            return (b.ToArray(), missing, [CreatedAllow]);
        }
        var list = scan.Array(allow.ValueStart);
        if (list.Elements.Count > 0)
        {
            var last = list.Elements[^1];
            var from = scan.SeparatorEnd(last.Start, list.Elements.Count > 1 ? list.Elements[^2].End : list.Open);
            var separator = data[from..last.Start];
            var inserted = new List<byte>();
            foreach (var rule in missing) { inserted.Add((byte)','); inserted.AddRange(separator); inserted.AddRange(Encoded(rule)); }
            b.InsertRange(last.End, inserted);
        }
        else
        {
            var rendered = Encoding.UTF8.GetBytes(Array(missing, scan.LineIndent(allow.KeyStart), unit, newline));
            b.RemoveRange(list.Open, list.Close - list.Open + 1);
            b.InsertRange(list.Open, rendered);
        }
        return (b.ToArray(), missing, []);
    }

    /// <summary>One occurrence of each of <paramref name="rules"/> taken out of permissions.allow (the last one). Returns the bytes and what was removed.</summary>
    public static (byte[] Data, IReadOnlyList<string> Removed) Removing(IReadOnlyList<string> rules, byte[] data, string path)
    {
        _ = Rules(data, path);
        var b = data;
        var removed = new List<string>();
        foreach (var rule in rules.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal))
        {
            var scan = new Scanner(b, path);
            var topObject = scan.Object(scan.SkipWhitespace(0));
            if (topObject.Members.LastOrDefault(m => m.Key == "permissions") is not { } permissions) break;
            var section = scan.Object(permissions.ValueStart);
            if (section.Members.LastOrDefault(m => m.Key == "allow") is not { } allow) break;
            var list = scan.Array(allow.ValueStart);
            var elements = list.Elements;
            var index = -1;
            for (var at = elements.Count - 1; at >= 0; at--) if (scan.String(elements[at]) == rule) { index = at; break; }
            if (index < 0) continue;
            var next = new List<byte>(b);
            if (elements.Count == 1) next.RemoveRange(list.Open + 1, list.Close - list.Open - 1);
            else if (index == elements.Count - 1) next.RemoveRange(elements[index - 1].End, elements[index].End - elements[index - 1].End);
            else next.RemoveRange(elements[index].Start, elements[index + 1].Start - elements[index].Start);
            b = next.ToArray();
            removed.Add(rule);
        }
        // Whatever is left must still read as the same kind of file.
        _ = Rules(b, path);
        return (b, removed);
    }

    /// <summary>
    /// Takes out what adding created once it is empty: permissions when it holds nothing but an empty allow (permissions, file), else an
    /// empty allow (allow). <c>Empty</c>: the file is now <c>{}</c>, which a created file is deleted for.
    /// </summary>
    public static (byte[] Data, bool Empty) Pruning(IReadOnlySet<string> created, byte[] data, string path)
    {
        _ = Rules(data, path);
        var b = new List<byte>(data);
        var scan = new Scanner(data, path);
        var top = scan.Object(scan.SkipWhitespace(0));
        var permissionMembers = Enumerable.Range(0, top.Members.Count).Where(i => top.Members[i].Key == "permissions").ToList();
        if (permissionMembers.Count == 1)
        {
            var index = permissionMembers[0];
            var section = scan.Object(top.Members[index].ValueStart);
            var allows = Enumerable.Range(0, section.Members.Count).Where(i => section.Members[i].Key == "allow").ToList();
            var emptyAllow = allows.Count == 1 && scan.Array(section.Members[allows[0]].ValueStart).Elements.Count == 0;
            if (created.Contains(CreatedPermissions) || created.Contains(CreatedFile))
            {
                if (section.Members.Count == 0 || section.Members.Count == 1 && emptyAllow) RemoveMember(b, top, index);
            }
            else if (created.Contains(CreatedAllow) && emptyAllow) RemoveMember(b, section, allows[0]);
        }
        var bytes = b.ToArray();
        var after = new Scanner(bytes, path);
        var result = after.Object(after.SkipWhitespace(0));
        _ = Rules(bytes, path);
        return (bytes, result.Members.Count == 0);
    }

    /// <summary>Takes a member out with the separator that adding put before it; the only member leaves <c>{}</c>.</summary>
    private static void RemoveMember(List<byte> b, Scanner.ObjectSpan value, int index)
    {
        var members = value.Members;
        if (members.Count == 1) { b.RemoveRange(value.Open, value.Close - value.Open + 1); b.InsertRange(value.Open, "{}"u8.ToArray()); }
        else if (index == members.Count - 1) b.RemoveRange(members[index - 1].ValueEnd, members[index].ValueEnd - members[index - 1].ValueEnd);
        else b.RemoveRange(members[index].KeyStart, members[index + 1].KeyStart - members[index].KeyStart);
    }

    // Rendering

    /// <summary>A rule as a JSON string, as the Mac writes it: non-ASCII and slashes as they are.</summary>
    internal static byte[] Encoded(string rule)
    {
        var text = new StringBuilder("\"");
        foreach (var c in rule)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else text.Append(c);
                    break;
            }
        }
        return Encoding.UTF8.GetBytes(text.Append('"').ToString());
    }

    /// <summary>A list on its own lines under <paramref name="indent"/>, or on one line when the place it goes is one line (null).</summary>
    internal static string Array(IReadOnlyList<string> rules, string? indent, string unit, string newline = "\n")
    {
        var items = rules.Select(rule => Encoding.UTF8.GetString(Encoded(rule)));
        if (indent is null) return "[" + string.Join(", ", items) + "]";
        return "[" + newline + string.Join("," + newline, items.Select(item => indent + unit + item)) + newline + indent + "]";
    }

    internal static string AllowObject(IReadOnlyList<string> rules, string? indent, string unit, string newline = "\n") => indent is null
        ? "{\"allow\": " + Array(rules, null, unit) + "}"
        : "{" + newline + indent + unit + "\"allow\": " + Array(rules, indent + unit, unit, newline) + newline + indent + "}";

    /// <summary>
    /// Adds a member to <paramref name="value"/>: after its last member, in that member's style, or as the only member of an empty
    /// object. <paramref name="render"/> gets the member's indent (null when it goes on one line).
    /// </summary>
    private static void InsertMember(List<byte> b, Scanner scan, Scanner.ObjectSpan value, string? indentOfObject, string unit, string newline, Func<string?, string> render)
    {
        if (value.Members.Count > 0)
        {
            var last = value.Members[^1];
            var previousEnd = value.Members.Count > 1 ? value.Members[^2].ValueEnd : value.Open;
            var separator = scan.Bytes[scan.SeparatorEnd(last.KeyStart, previousEnd)..last.KeyStart];
            var inserted = new List<byte> { (byte)',' };
            inserted.AddRange(separator); inserted.AddRange(Encoding.UTF8.GetBytes(render(scan.LineIndent(last.KeyStart))));
            b.InsertRange(last.ValueEnd, inserted);
            return;
        }
        var text = indentOfObject is { } outer ? "{" + newline + outer + unit + render(outer + unit) + newline + outer + "}" : "{" + render(null) + "}";
        b.RemoveRange(value.Open, value.Close - value.Open + 1);
        b.InsertRange(value.Open, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Just enough of a JSON reader to find where things are. The bytes were already read by JsonDocument; anything unexpected is notJSON.</summary>
    internal sealed class Scanner(byte[] bytes, string path)
    {
        internal sealed record Member(string Key, int KeyStart, int ValueStart, int ValueEnd);
        internal sealed record ObjectSpan(int Open, int Close, IReadOnlyList<Member> Members);
        internal readonly record struct Element(int Start, int End);
        internal sealed record ListSpan(int Open, int Close, IReadOnlyList<Element> Elements);
        internal byte[] Bytes => bytes;

        private AntigravitySettingsException Fail() => AntigravitySettingsException.Of("notJSON", path);
        private static bool Space(byte value) => value is 0x20 or 0x09 or 0x0A or 0x0D;
        internal int SkipWhitespace(int start)
        {
            var i = start;
            while (i < bytes.Length && Space(bytes[i])) i++;
            return i;
        }
        /// <summary>Index after the closing quote of the string at <paramref name="start"/>.</summary>
        private int StringEnd(int start)
        {
            if (start >= bytes.Length || bytes[start] != (byte)'"') throw Fail();
            var i = start + 1;
            while (i < bytes.Length)
            {
                if (bytes[i] == (byte)'\\') { i += 2; continue; }
                if (bytes[i] == (byte)'"') return i + 1;
                i++;
            }
            throw Fail();
        }
        internal string? String(Element range) => Decode(range.Start, range.End);
        private string? Decode(int start, int end)
        {
            if (start >= bytes.Length || bytes[start] != (byte)'"') return null;
            try { using var document = JsonDocument.Parse(bytes.AsMemory(start, end - start)); return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null; }
            catch (JsonException) { return null; }
        }
        private int ValueEnd(int start)
        {
            if (start >= bytes.Length) throw Fail();
            switch (bytes[start])
            {
                case (byte)'"': return StringEnd(start);
                case (byte)'{': return Object(start).Close + 1;
                case (byte)'[': return Array(start).Close + 1;
                default:
                    var i = start;
                    while (i < bytes.Length && bytes[i] is not ((byte)',' or (byte)']' or (byte)'}') && !Space(bytes[i])) i++;
                    if (i <= start) throw Fail();
                    return i;
            }
        }
        internal ObjectSpan Object(int open)
        {
            if (open >= bytes.Length || bytes[open] != (byte)'{') throw Fail();
            var members = new List<Member>();
            var i = SkipWhitespace(open + 1);
            if (i < bytes.Length && bytes[i] == (byte)'}') return new(open, i, members);
            while (true)
            {
                var keyStart = i;
                var keyEnd = StringEnd(keyStart);
                var key = Decode(keyStart, keyEnd) ?? throw Fail();
                i = SkipWhitespace(keyEnd);
                if (i >= bytes.Length || bytes[i] != (byte)':') throw Fail();
                var valueStart = SkipWhitespace(i + 1);
                var end = ValueEnd(valueStart);
                members.Add(new(key, keyStart, valueStart, end));
                i = SkipWhitespace(end);
                if (i >= bytes.Length) throw Fail();
                if (bytes[i] == (byte)'}') return new(open, i, members);
                if (bytes[i] != (byte)',') throw Fail();
                i = SkipWhitespace(i + 1);
            }
        }
        internal ListSpan Array(int open)
        {
            if (open >= bytes.Length || bytes[open] != (byte)'[') throw AntigravitySettingsException.Of("allowNotArray", path);
            var elements = new List<Element>();
            var i = SkipWhitespace(open + 1);
            if (i < bytes.Length && bytes[i] == (byte)']') return new(open, i, elements);
            while (true)
            {
                var end = ValueEnd(i);
                elements.Add(new(i, end));
                i = SkipWhitespace(end);
                if (i >= bytes.Length) throw Fail();
                if (bytes[i] == (byte)']') return new(open, i, elements);
                if (bytes[i] != (byte)',') throw Fail();
                i = SkipWhitespace(i + 1);
            }
        }
        /// <summary>Where the whitespace before <paramref name="position"/> starts, after the comma (or bracket) that follows <paramref name="after"/>.</summary>
        internal int SeparatorEnd(int position, int after)
        {
            var i = position;
            while (i > after + 1 && Space(bytes[i - 1])) i--;
            return i;
        }
        /// <summary>The spaces and tabs before <paramref name="position"/> when nothing else is on its line before it.</summary>
        internal string? LineIndent(int position)
        {
            if (position < 0 || position > bytes.Length) return null;
            var i = position;
            while (i > 0 && bytes[i - 1] is 0x20 or 0x09) i--;
            if (i != 0 && bytes[i - 1] != 0x0A) return null;
            return Encoding.UTF8.GetString(bytes, i, position - i);
        }
    }
}

/// <summary>
/// Puts rules in agy's settings and takes them out again (M/MightyCore/AntigravityPermissions.swift AntigravityRuleLeases). One per app:
/// the panes on this PC and the runs it hosts for other computers share it, so a rule two of them hold at once stays until the last is
/// done. Every rule it adds for a while is written to its ledger first, and taken out of it only once the settings are seen without it a
/// little after it was removed (a running agy that rewrites its settings could bring it back); a removal that fails (File.Replace meets a
/// file agy has open) is tried again with growing pauses, then again on the next apply or release and at the next launch
/// (<see cref="RemoveLeftovers"/>). A rule the settings already had is never removed, and what adding had to create (permissions, allow,
/// the file) goes again once it is empty. Calls are serialized; each reads and writes files and may pause, so callers keep them off the
/// UI thread.
/// </summary>
public sealed class AntigravityRuleLeases
{
    /// <summary>The rules one run holds a count on (the ones the app added); released once.</summary>
    public sealed class Lease
    {
        internal Lease(IReadOnlyList<string> rules) { Rules = rules; }
        public IReadOnlyList<string> Rules { get; }
    }
    private sealed record LedgerEntry(string Settings, string Rule);
    private sealed record CreatedEntry(string Settings, string Part);
    private sealed record Ledger(int Version, List<LedgerEntry> Entries, List<CreatedEntry>? Created = null)
    {
        public bool IsEmpty => Entries.Count == 0 && (Created ?? []).Count == 0;
    }

    public string SettingsPath { get; }
    private readonly string? ledgerPath;
    private readonly IReadOnlyList<TimeSpan> retryDelays;
    private readonly TimeSpan recheckDelay;
    private readonly object gate = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly HashSet<Lease> leases = [];
    /// <summary>Rules removed and waiting to be looked for once more.</summary>
    private readonly HashSet<string> awaitingRecheck = new(StringComparer.Ordinal);
    private readonly List<Task> rechecks = [];
    /// <summary>Quit released everything: nothing is added any more.</summary>
    private bool closed;

    /// <summary>%USERPROFILE%\.gemini\antigravity-cli\settings.json (on a Mac, ~/.gemini/antigravity-cli/settings.json).</summary>
    public static string DefaultSettingsPath(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");

    /// <param name="ledgerPath">Where the rules added for a while are recorded (the app's data folder); null keeps no record.</param>
    /// <param name="retryDelays">The pauses before each new try of a failed removal; null is 250 ms, 1 s, 3 s.</param>
    /// <param name="recheckDelay">How long after a removal the settings are looked at again; null is 5 s.</param>
    public AntigravityRuleLeases(string settingsPath, string? ledgerPath, IReadOnlyList<TimeSpan>? retryDelays = null, TimeSpan? recheckDelay = null)
    {
        SettingsPath = settingsPath; this.ledgerPath = ledgerPath;
        this.retryDelays = retryDelays ?? [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
        this.recheckDelay = recheckDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The rules the app holds for a while now, with their counts.</summary>
    public IReadOnlyDictionary<string, int> Held() { lock (gate) return new Dictionary<string, int>(counts, StringComparer.Ordinal); }

    /// <summary>Reads the settings the way adding would, and changes nothing.</summary>
    public void Check() { lock (gate) _ = Read(); }

    /// <summary>Waits for the look-agains removals started (checks).</summary>
    public async Task Settled()
    {
        while (true)
        {
            Task[] pending; lock (gate) { rechecks.RemoveAll(task => task.IsCompleted); pending = rechecks.ToArray(); }
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// Puts <paramref name="grant"/>'s rules in place. A temporary grant returns the lease to <see cref="Release"/> when its run ends; one
    /// for good returns null. After quit's <see cref="ReleaseAll"/> it throws <see cref="OperationCanceledException"/> and adds nothing.
    /// </summary>
    public Lease? Apply(AntigravityAllowGrant grant)
    {
        lock (gate)
        {
            if (closed) throw new OperationCanceledException();
            if (!grant.IsValid) throw AntigravitySettingsException.Of("badRule");
            if (!grant.KeepsOnlyAllowedRules) throw AntigravitySettingsException.Of("onceOnly");
            RetryPending([]);
            var current = Read();
            var existing = current.Rules.ToHashSet(StringComparer.Ordinal);
            var rules = grant.Rules.Distinct(StringComparer.Ordinal).ToList();
            if (!grant.Temporary)
            {
                // Kept for good: a rule the app holds or held for a while is no longer its to remove.
                var missing = rules.Where(rule => !existing.Contains(rule)).ToList();
                if (missing.Count > 0) Save(Changed(current, missing).Data);
                foreach (var rule in rules) { counts.Remove(rule); awaitingRecheck.Remove(rule); }
                try { Forget(rules); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { }
                return null;
            }
            // Still in the settings from an earlier hold whose removal is pending: held again.
            var pending = LedgerRules().ToHashSet(StringComparer.Ordinal);
            var incremented = new List<string>(); var reclaimed = new List<string>(); var added = new List<string>();
            foreach (var rule in rules)
            {
                if (counts.TryGetValue(rule, out var count)) { counts[rule] = count + 1; incremented.Add(rule); }
                else if (!existing.Contains(rule)) added.Add(rule);
                else if (pending.Contains(rule)) { counts[rule] = 1; awaitingRecheck.Remove(rule); reclaimed.Add(rule); }
            }
            if (added.Count > 0)
            {
                try
                {
                    var change = Changed(current, added);
                    Record(added, change.Created);
                    Save(change.Data);
                }
                catch
                {
                    try { Forget(added); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { }
                    foreach (var rule in incremented) counts[rule] = counts.GetValueOrDefault(rule, 1) - 1;
                    foreach (var rule in reclaimed) counts.Remove(rule);
                    throw;
                }
                foreach (var rule in added) counts[rule] = 1;
            }
            var lease = new Lease([.. incremented, .. reclaimed, .. added]);
            leases.Add(lease);
            return lease;
        }
    }

    /// <summary>The lease's run ended: a rule nobody holds any more leaves the settings.</summary>
    public void Release(Lease lease)
    {
        List<string> gone = [];
        lock (gate)
        {
            if (!leases.Remove(lease)) return;
            foreach (var rule in lease.Rules)
            {
                if (!counts.TryGetValue(rule, out var count)) continue;
                if (count > 1) counts[rule] = count - 1; else { counts.Remove(rule); gone.Add(rule); }
            }
            RetryPending(gone);
        }
        Remove(gone);
    }

    /// <summary>Quit: every rule held for a while leaves now, and nothing is added after.</summary>
    public void ReleaseAll()
    {
        List<string> gone;
        lock (gate)
        {
            closed = true;
            gone = counts.Keys.Concat(LedgerRules().Where(rule => !awaitingRecheck.Contains(rule))).Distinct(StringComparer.Ordinal).ToList();
            counts.Clear(); leases.Clear();
        }
        Remove(gone);
    }

    /// <summary>Launch: what a previous run of the app added for a while and could not take out.</summary>
    public void RemoveLeftovers()
    {
        List<string> left;
        lock (gate)
        {
            left = LedgerRules().Where(rule => !counts.ContainsKey(rule) && !awaitingRecheck.Contains(rule)).ToList();
            if (left.Count == 0) { PruneIfDone(); return; }
        }
        Remove(left);
    }

    // Removing

    /// <summary>Takes <paramref name="rules"/> out of the settings, trying again after each of the retry pauses; then looks again after the recheck delay. A rule held again meanwhile is left in.</summary>
    private void Remove(IReadOnlyList<string> rules)
    {
        if (rules.Count == 0) return;
        if (RemoveWithRetries(rules)) lock (gate) ScheduleRecheck(rules.Where(rule => !counts.ContainsKey(rule)).ToList());
    }

    /// <summary>Each try under the lock, each pause outside it.</summary>
    private bool RemoveWithRetries(IReadOnlyList<string> rules)
    {
        for (var attempt = 0; ; attempt++)
        {
            lock (gate)
            {
                var still = rules.Where(rule => !counts.ContainsKey(rule)).ToList();
                if (still.Count == 0) return true;
                try { RemoveOnce(still); return true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { if (attempt >= retryDelays.Count) return false; }
            }
            Thread.Sleep(retryDelays[attempt]);
        }
    }

    private void RemoveOnce(IReadOnlyList<string> rules)
    {
        var current = Read();
        if (current.Data is not { } data) return;
        var result = AntigravitySettingsText.Removing(rules, data, SettingsPath);
        if (result.Removed.Count > 0) Save(result.Data);
    }

    /// <summary>Under <c>gate</c>.</summary>
    private void ScheduleRecheck(IReadOnlyList<string> rules)
    {
        if (rules.Count == 0) return;
        foreach (var rule in rules) awaitingRecheck.Add(rule);
        var delay = recheckDelay;
        rechecks.Add(Task.Run(async () => { await Task.Delay(delay); Recheck(rules); }));
    }

    /// <summary>A little after a removal: a rule agy wrote back is taken out again; then the ledger lets go of it.</summary>
    private void Recheck(IReadOnlyList<string> rules)
    {
        List<string> still;
        lock (gate)
        {
            var ledger = LedgerRules().ToHashSet(StringComparer.Ordinal);
            still = rules.Where(rule => !counts.ContainsKey(rule) && awaitingRecheck.Contains(rule) && ledger.Contains(rule)).ToList();
            foreach (var rule in rules.Where(rule => !counts.ContainsKey(rule))) awaitingRecheck.Remove(rule);
            if (still.Count == 0) return;
        }
        if (!RemoveWithRetries(still)) return;
        lock (gate)
        {
            try { Forget(still.Where(rule => !counts.ContainsKey(rule)).ToList()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { return; }
            PruneIfDone();
        }
    }

    /// <summary>Under <c>gate</c>: one try, without waiting, at what the ledger still holds that nobody holds now (an earlier removal that failed).</summary>
    private void RetryPending(IReadOnlyList<string> except)
    {
        var skip = except.ToHashSet(StringComparer.Ordinal);
        var pending = LedgerRules().Where(rule => !counts.ContainsKey(rule) && !awaitingRecheck.Contains(rule) && !skip.Contains(rule)).ToList();
        if (pending.Count == 0) return;
        try { RemoveOnce(pending); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { return; }
        ScheduleRecheck(pending);
    }

    /// <summary>Under <c>gate</c>: nothing held and nothing left in the ledger, so what adding created goes once empty.</summary>
    private void PruneIfDone()
    {
        if (counts.Count > 0 || LedgerRules().Count > 0 || ReadLedger() is not { } ledger) return;
        var created = (ledger.Created ?? []).Where(e => e.Settings == SettingsPath).Select(e => e.Part).ToHashSet(StringComparer.Ordinal);
        if (created.Count == 0) return;
        try
        {
            var current = Read();
            if (current.Data is { } data)
            {
                var result = AntigravitySettingsText.Pruning(created, data, SettingsPath);
                if (result.Empty && created.Contains(AntigravitySettingsText.CreatedFile) && new FileInfo(SettingsPath).LinkTarget is null) File.Delete(SettingsPath);
                else if (!result.Data.AsSpan().SequenceEqual(data)) Save(result.Data);
            }
            WriteLedger(ledger with { Created = (ledger.Created ?? []).Where(e => e.Settings != SettingsPath).ToList() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { }
    }

    // Files

    private sealed record Current(byte[]? Data, IReadOnlyList<string> Rules);

    /// <summary>The settings file itself: a symbolic link is followed, so a linked file stays linked.</summary>
    private string Target()
    {
        try { if (new FileInfo(SettingsPath) is { LinkTarget: not null } link && link.ResolveLinkTarget(true) is { } final) return final.FullName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return SettingsPath;
    }

    private Current Read()
    {
        // A link to nothing is not replaced by a new file.
        try { if (new FileInfo(SettingsPath) is { LinkTarget: not null } link && !(link.ResolveLinkTarget(true)?.Exists ?? false)) throw AntigravitySettingsException.Of("unreadable", SettingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw AntigravitySettingsException.Of("unreadable", SettingsPath); }
        var target = Target();
        var folder = Path.GetDirectoryName(target);
        if (folder is null || !Directory.Exists(folder)) throw AntigravitySettingsException.Of("missingFolder", folder ?? target);
        if (Directory.Exists(target)) throw AntigravitySettingsException.Of("unreadable", SettingsPath);
        if (!File.Exists(target)) return new(null, []);
        byte[] data;
        try { data = File.ReadAllBytes(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw AntigravitySettingsException.Of("unreadable", SettingsPath); }
        return new(data, AntigravitySettingsText.Rules(data, SettingsPath));
    }

    /// <summary>The settings with <paramref name="rules"/> added, and what that created.</summary>
    private (byte[] Data, IReadOnlyList<string> Created) Changed(Current current, IReadOnlyList<string> rules)
    {
        if (current.Data is not { } data) return (AntigravitySettingsText.NewFile(rules), [AntigravitySettingsText.CreatedFile]);
        var result = AntigravitySettingsText.Adding(rules, data, SettingsPath);
        return (result.Data, result.Created);
    }

    /// <summary>
    /// Writes beside the file and puts it in the file's place. On Windows File.Replace keeps the file's ACL, attributes and creation
    /// time; elsewhere its mode is copied. A symbolic link keeps pointing where it did.
    /// </summary>
    private void Save(byte[] data)
    {
        var target = Target();
        var folder = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(folder, "." + Path.GetFileName(target) + ".mighty-" + Guid.NewGuid().ToString("D") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            var exists = File.Exists(target);
            if (OperatingSystem.IsWindows())
            {
                if (exists) File.Replace(temporary, target, null, ignoreMetadataErrors: true); else File.Move(temporary, target);
            }
            else
            {
                File.SetUnixFileMode(temporary, exists ? File.GetUnixFileMode(target) : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.Move(temporary, target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw AntigravitySettingsException.Of("writeFailed", SettingsPath, ex.Message);
        }
    }

    private Ledger? ReadLedger()
    {
        if (ledgerPath is null || !File.Exists(ledgerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<Ledger>(File.ReadAllBytes(ledgerPath), Wire.Json) is { Entries: not null } ledger
                ? ledger with { Entries = ledger.Entries.Where(e => e is { Settings: not null, Rule: not null }).ToList(), Created = ledger.Created?.Where(e => e is { Settings: not null, Part: not null }).ToList() }
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>The rules the ledger holds for these settings.</summary>
    private List<string> LedgerRules() => (ReadLedger()?.Entries ?? []).Where(e => e.Settings == SettingsPath).Select(e => e.Rule).Distinct(StringComparer.Ordinal).ToList();

    private void WriteLedger(Ledger ledger)
    {
        if (ledgerPath is null) return;
        if (ledger.IsEmpty) { if (File.Exists(ledgerPath)) File.Delete(ledgerPath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        var temporary = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(ledger, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
            File.Move(temporary, ledgerPath, overwrite: true);
        }
        catch { try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { } throw; }
    }

    private void Record(IReadOnlyList<string> rules, IReadOnlyList<string> created)
    {
        if (ledgerPath is null) return;
        var ledger = ReadLedger() ?? new(1, []);
        foreach (var rule in rules) if (!ledger.Entries.Contains(new(SettingsPath, rule))) ledger.Entries.Add(new(SettingsPath, rule));
        var parts = ledger.Created ?? [];
        foreach (var part in created) if (!parts.Contains(new(SettingsPath, part))) parts.Add(new(SettingsPath, part));
        try { WriteLedger(ledger with { Created = parts.Count == 0 ? null : parts }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw AntigravitySettingsException.Of("writeFailed", ledgerPath, ex.Message); }
    }

    private void Forget(IReadOnlyList<string> rules)
    {
        if (ledgerPath is null || rules.Count == 0 || ReadLedger() is not { } ledger) return;
        var gone = rules.ToHashSet(StringComparer.Ordinal);
        ledger.Entries.RemoveAll(e => e.Settings == SettingsPath && gone.Contains(e.Rule));
        WriteLedger(ledger);
    }
}
